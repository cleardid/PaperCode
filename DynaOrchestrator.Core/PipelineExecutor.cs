using DynaOrchestrator.Core.Batch;
using DynaOrchestrator.Core.Models;
using DynaOrchestrator.Core.PostProcessing;
using DynaOrchestrator.Core.PreProcessing;
using DynaOrchestrator.Core.Solver;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Collections.Generic;
using System.Linq;

namespace DynaOrchestrator.Core
{
    /// <summary>
    /// 单工况执行器。
    /// 负责串联前处理（自适应网格生成）、LS-DYNA求解，以及基于非托管内存的高性能图特征提取与序列化。
    /// 在批处理模式下，该类被 BatchRunner 并发调用。
    /// </summary>
    internal static class PipelineExecutor
    {

        /// <summary>
        /// 用于隔离 C++ 端 OpenMP 的多线程全核加速，防止与 C# 端的并发任务发生线程冲突
        /// </summary>
        private static readonly SemaphoreSlim _postProcessGate = new(1, 1);

        /// <summary>
        /// 执行单个工况的完整流程。
        /// </summary>
        /// <param name="appConfig">包含该工况所有配置信息的对象（注意其单位为原始输入单位）</param>
        /// <param name="record">当前执行的工况记录元数据</param>
        /// <param name="logger">UI 线程的日志回调委托</param>
        /// <param name="cancellationToken">任务取消令牌，用于随时安全中断底层求解器</param>
        public static void Execute(
            AppConfig appConfig,
            BatchCaseRecord record,
            Action<string>? logger = null,
            CancellationToken cancellationToken = default)
        {
            if (appConfig == null) throw new ArgumentNullException(nameof(appConfig));

            var config = appConfig.Pipeline;
            var exp = appConfig.Explosive;
            var other = appConfig.Other;

            // 统一在主流程起点将配置文件中的 mm 转换为 m，防止后续计算出现量纲错误
            other.NormalizeUnits();

            logger?.Invoke($"[Config] 已加载配置. 爆源(mm): ({exp.Xc}, {exp.Yc}, {exp.Zc}), 当量: {exp.W} kg");

            // 预先推导共用的文件路径
            string absoluteOutputKFile = Path.GetFullPath(config.OutputKFile);
            string absoluteStlFile = Path.GetFullPath(config.StlFile);
            string outDir = Path.GetDirectoryName(absoluteOutputKFile) ?? "";
            string actualTrhistPath = Path.Combine(outDir, config.TrhistFile);

            // 如果需要执行图特征提取，必须确保 C++ 动态链接库物理存在
            if (config.EnablePostProcessing)
            {
                string dllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DynaOrchestrator.Native.dll");
                if (!File.Exists(dllPath))
                {
                    throw new FileNotFoundException($"系统缺失核心计算引擎组件：未找到 C++ 动态链接库 ({dllPath})。请确保 Native 项目已编译且输出至该目录。");
                }

                GraphEngineAPI.EnsureAvailable();
            }

            // 如果跳过了第一阶段（前处理）
            if (!config.EnablePreProcessing)
            {
                // 且要求执行第二阶段：必须有 K 文件
                if (config.EnableSimulation && !File.Exists(absoluteOutputKFile))
                    throw new FileNotFoundException($"依赖缺失：已跳过网格生成，但未找到供 LS-DYNA 求解的基础模型 ({absoluteOutputKFile})。如果是全新工况，请务必勾选「前处理」。");

                // 且要求执行第三阶段：必须有 STL 文件作为几何先验
                if (config.EnablePostProcessing && !File.Exists(absoluteStlFile))
                    throw new FileNotFoundException($"依赖缺失：已跳过网格生成，但未找到供图引擎做几何遮挡计算的 STL 文件 ({absoluteStlFile})。");
            }

            // 如果跳过了第二阶段（仿真求解）
            if (!config.EnableSimulation)
            {
                // 且要求执行第三阶段：必须有前置求解产出的 trhist 文件
                if (config.EnablePostProcessing && !File.Exists(actualTrhistPath))
                    throw new FileNotFoundException($"依赖缺失：已跳过 LS-DYNA 求解，但未在目录中找到供图特征提取的历史流场文件 ({actualTrhistPath})。");
            }

            // 如果全部开关都关了，直接记录并返回
            if (!config.EnablePreProcessing && !config.EnableSimulation && !config.EnablePostProcessing)
            {
                logger?.Invoke("[Info] 所有执行阶段均未勾选，该工况直接略过。");
                return;
            }

            // [Phase 1] 前处理：基于 STL 与包围盒生成追踪点网格
            if (config.EnablePreProcessing)
            {
                RunPreProcessing(config, other, exp, logger);
            }
            else
            {
                logger?.Invoke("\n[Phase 1] 已配置为跳过前处理阶段。");
            }

            // [阶段 2] LS-DYNA 求解：拉起外部求解器进程，并阻塞等待正常终止
            if (config.EnableSimulation)
            {
                actualTrhistPath = RunSimulation(config, appConfig, cancellationToken, logger);
            }
            else
            {
                logger?.Invoke("\n[Phase 2] 已配置为跳过 LS-DYNA 求解阶段。");
                if (config.EnablePostProcessing && !File.Exists(actualTrhistPath))
                {
                    throw new FileNotFoundException($"跳过了求解，但未在目录中找到供后处理使用的历史文件: {actualTrhistPath}");
                }
            }

            // [阶段 3] 后处理与图构建：调用 C++ 图引擎，并通过 Span<T> 实现内存零拷贝提取
            if (config.EnablePostProcessing)
            {
                logger?.Invoke("\n[Phase 3] 准备进行图特征提取...");
                logger?.Invoke("[排队] 正在等待全局 C++ 引擎锁，以防 OpenMP 线程冲突...");

                _postProcessGate.Wait(cancellationToken);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    logger?.Invoke("[获得锁] 开始执行非托管图提取与数据集生成...");
                    RunPostProcessing(config, exp, other, record, actualTrhistPath, logger);
                }
                finally
                {
                    _postProcessGate.Release();
                }
            }
            else
            {
                logger?.Invoke("\n[Phase 3] 已配置为跳过后处理数据构建，工况执行结束。");
            }
        }

        /// <summary>
        /// 从磁盘加载 JSON 格式的工况独立配置文件。
        /// </summary>
        public static AppConfig LoadConfig(string configFile)
        {
            if (!File.Exists(configFile)) throw new FileNotFoundException($"未找到配置文件: {configFile}");
            string jsonString = File.ReadAllText(configFile);
            return JsonSerializer.Deserialize<AppConfig>(jsonString) ?? throw new Exception("配置文件解析失败");
        }

        /// <summary>
        /// 执行前处理流程：解析 STL 并生成自适应 Tracer 网格。
        /// </summary>
        private static void RunPreProcessing(PipelineConfig config, OtherConfig other, ExplosiveParams exp, Action<string>? logger)
        {
            logger?.Invoke("\n[Phase 1] 几何解析与自适应网格生成...");
            string outDir = Path.GetDirectoryName(Path.GetFullPath(config.OutputKFile)) ?? throw new Exception("无法获取输出目录");

            if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);

            // 解析二进制 STL，并提取体素化包围盒
            var stlMesh = STLParser.ParseBinarySTL(config.StlFile, logger);

            // 生成新测点并覆盖写入 model_out.k
            AdaptiveMeshGenerator.ProcessAndGenerate(config.BaseKFile, config.OutputKFile, stlMesh, other, exp, logger);
        }

        /// <summary>
        /// 执行 LS-DYNA 求解，监控输出并处理中止信号。
        /// </summary>
        private static string RunSimulation(PipelineConfig config, AppConfig appConfig, CancellationToken cancellationToken, Action<string>? logger)
        {
            logger?.Invoke("\n[Phase 2] 启动 LS-DYNA 求解器...");
            string runDir = Path.GetDirectoryName(Path.GetFullPath(config.OutputKFile)) ?? throw new Exception("无法获取 trhist 目录");
            string actualTrhistPath = Path.Combine(runDir, config.TrhistFile);

            // 核心调度：使用批处理配置统一下发的硬件资源（NcpuPerCase / MemoryPerCase）
            bool ok = LsDynaOrchestrator.Run(config, appConfig.Workspace.NcpuPerCase, appConfig.Workspace.MemoryPerCase, cancellationToken, logger);

            // 响应用户界面的取消请求
            cancellationToken.ThrowIfCancellationRequested();

            if (!ok || !File.Exists(actualTrhistPath))
                throw new Exception($"仿真执行中断或未生成结果文件: {actualTrhistPath}");

            return actualTrhistPath;
        }

        /// <summary>
        /// 执行图引擎后处理，提取物理特征并序列化为 NPZ 数据集。
        /// 此方法深入使用了非托管内存的零拷贝技术，大幅降低了大数据量下的内存峰值（OOM 风险）。
        /// </summary>
        private static void RunPostProcessing(PipelineConfig config, ExplosiveParams exp, OtherConfig other, BatchCaseRecord record, string actualTrhistPath, Action<string>? logger)
        {
            logger?.Invoke("\n[Phase 3] 提取物理图特征与静态属性...");

            // 注册 C# 回调到 C++，用于将 C++ 引擎的计算进度输出到 WPF 界面
            GraphEngineAPI.InitializeLogger(logger);

            // 调用 C++ 动态链接库进行空间搜索与图构建
            IntPtr ptr = GraphEngineAPI.GenerateGraph(
                actualTrhistPath, Path.GetFullPath(config.StlFile),
                other.Rc, other.Alpha, (float)exp.Xc, (float)exp.Yc, (float)exp.Zc, (float)exp.W);

            if (ptr == IntPtr.Zero) throw new Exception("C++ 图引擎返回空指针，构建失败。");

            try
            {
                // 将返回的 IntPtr 映射为 C# 结构体（仅映射元数据与指针，不拷贝数组实体）
                GraphData data = Marshal.PtrToStructure<GraphData>(ptr);
                ValidateGraphData(data); // 内存安全前置校验

                // 【核心性能优化区】：启用 unsafe 指针包装
                // 原逻辑使用 Marshal.Copy 会在 C# 托管堆分配等大的 float[]，导致内存翻倍。
                // 现逻辑使用 ReadOnlySpan<T> 直接在 C++ 分配的非托管内存上进行视图包装，实现真正零拷贝。
                unsafe
                {
                    // 构建图拓扑 Span
                    var rowsSpan = new ReadOnlySpan<int>(data.coo_rows.ToPointer(), data.num_edges);
                    var colsSpan = new ReadOnlySpan<int>(data.coo_cols.ToPointer(), data.num_edges);
                    var weightsSpan = new ReadOnlySpan<float>(data.coo_weights.ToPointer(), data.num_edges);

                    // 构建节点特征与静态属性 Span
                    var featuresSpan = new ReadOnlySpan<float>(data.node_features.ToPointer(), data.num_nodes * data.time_steps * data.feature_dim);
                    var attrsSpan = new ReadOnlySpan<float>(data.node_attrs.ToPointer(), data.num_nodes * data.attr_dim);

                    if (data.feature_dim != 5) throw new Exception($"DLL 返回的 x 维度异常，预期 5，实际为 {data.feature_dim}");
                    if (data.attr_dim != 11) throw new Exception($"DLL 返回的 node_attr 维度异常，预期 11，实际为 {data.attr_dim}");

                    // 基于非托管内存计算工程响应标签（到达时间、比冲、超压等）
                    var labels = EngineeringLabelExtractor.Extract(
                        actualTrhistPath, featuresSpan, data.num_nodes, data.time_steps, data.feature_dim, logger, 4, 1e-7f);

                    // 提取边界语义先验信息
                    float[] nearWallFlag = BuildBinarySemanticFlag(attrsSpan, data.num_nodes, data.attr_dim, 8, (float)other.WallMargin);
                    float[] nearEdgeFlag = BuildBinarySemanticFlag(attrsSpan, data.num_nodes, data.attr_dim, 9, (float)other.WallMargin);
                    float[] nearCornerFlag = BuildBinarySemanticFlag(attrsSpan, data.num_nodes, data.attr_dim, 10, (float)other.WallMargin);
                    int[] samplingRegionId = BuildSamplingRegionId(nearWallFlag, nearEdgeFlag, nearCornerFlag, data.num_nodes);

                    // 生成该工况的宏观条件向量
                    float[] caseCond = BuildCaseConditionVector(record);

                    // 序列化前执行 STGNS-v1 算法模型要求的对齐与合法性校验
                    ValidateStgnsV1Export(rowsSpan, colsSpan, weightsSpan, featuresSpan, attrsSpan, labels.PeakOverpressure, labels.ArrivalTime, labels.PositiveImpulse, labels.PositiveDuration, nearWallFlag, nearEdgeFlag, nearCornerFlag, samplingRegionId, caseCond, data.num_nodes, data.time_steps, data.feature_dim, data.attr_dim);

                    // 将 Span 数据通过 MemoryMarshal 映射为 byte 后直接写入磁盘压缩流
                    NpzWriter.Save(config.NpzOutputFile, rowsSpan, colsSpan, weightsSpan, featuresSpan, attrsSpan, labels.PeakOverpressure, labels.ArrivalTime, labels.PositiveImpulse, labels.PositiveDuration, nearWallFlag, nearEdgeFlag, nearCornerFlag, samplingRegionId, caseCond, data.num_nodes, data.time_steps, data.feature_dim, data.attr_dim, logger);

                    logger?.Invoke("[后处理] NPZ 写出完成。");

                    NpzWriter.ValidateBasicStructure(config.NpzOutputFile, logger);

                    CleanupLargeSolverOutputsAfterSuccessfulNpz(
                        config: config,
                        trhistPathToKeep: actualTrhistPath,
                        kFilePathToKeep: config.OutputKFile,
                        logger: logger);
                }
            }
            finally
            {
                // 无论 C# 侧发生何种异常，必须保证 C++ 堆内存被安全释放
                GraphEngineAPI.FreeGraphData(ptr);
                logger?.Invoke("[Info] 非托管图内存已安全释放。");
            }
        }

        /// <summary>
        /// 校验从 C++ 获取的 GraphData 结构指针的合法性，防止后续引发内存访问越界(Access Violation)。
        /// </summary>
        private static void ValidateGraphData(GraphData data)
        {
            if (data.num_nodes <= 0) throw new Exception("GraphData.num_nodes 非法");
            if (data.num_edges < 0 || data.time_steps <= 0 || data.feature_dim <= 0 || data.attr_dim != 11) throw new Exception("GraphData 维度字段非法");
            if (data.num_edges > 0 && (data.coo_rows == IntPtr.Zero || data.coo_cols == IntPtr.Zero || data.coo_weights == IntPtr.Zero)) throw new Exception("图拓扑指针非法，返回为空");
            if (data.node_features == IntPtr.Zero || data.node_attrs == IntPtr.Zero) throw new Exception("节点特征或属性指针非法，返回为空");
        }

        /// <summary>
        /// 构建 STGNS-v1 模型要求输入的 7 维全局条件特征向量
        /// </summary>
        private static float[] BuildCaseConditionVector(BatchCaseRecord record) => new[]
        {
            (float)(record.X * 0.001), (float)(record.Y * 0.001), (float)(record.Z * 0.001), // 相对位置 m
            (float)record.L, (float)record.W, (float)record.H, // 房间长宽高 m
            (float)Math.Pow(record.ChargeMass, 1.0 / 3.0) // 当量特征 W^(1/3)
        };

        /// <summary>
        /// 基于静态属性数组，抽取二值化的空间语义标志（如是否贴壁）。
        /// 支持对 C++ 非托管内存 (ReadOnlySpan) 直接进行安全寻址。
        /// </summary>
        private static float[] BuildBinarySemanticFlag(ReadOnlySpan<float> attrs, int numNodes, int attrDim, int attrIndex, float threshold)
        {
            var flags = new float[numNodes];
            for (int i = 0; i < numNodes; i++) flags[i] = attrs[i * attrDim + attrIndex] <= threshold ? 1.0f : 0.0f;
            return flags;
        }

        /// <summary>
        /// 为各节点分配语义归属 ID，优先级：角区 > 棱边区 > 近壁区 > 内部流场。
        /// </summary>
        private static int[] BuildSamplingRegionId(float[] nearWallFlag, float[] nearEdgeFlag, float[] nearCornerFlag, int numNodes)
        {
            var regionId = new int[numNodes];
            for (int i = 0; i < numNodes; i++)
            {
                if (nearCornerFlag[i] > 0.5f) regionId[i] = 3;
                else if (nearEdgeFlag[i] > 0.5f) regionId[i] = 2;
                else if (nearWallFlag[i] > 0.5f) regionId[i] = 1;
                else regionId[i] = 0;
            }
            return regionId;
        }

        /// <summary>
        /// 输出前进行最终的一致性检查，防止产生带有脏数据的模型训练集。
        /// </summary>
        private static void ValidateStgnsV1Export(
            ReadOnlySpan<int> rows, ReadOnlySpan<int> cols, ReadOnlySpan<float> weights,
            ReadOnlySpan<float> features, ReadOnlySpan<float> attrs, ReadOnlySpan<float> pMax,
            ReadOnlySpan<float> tArrival, ReadOnlySpan<float> positiveImpulse, ReadOnlySpan<float> positiveDuration,
            ReadOnlySpan<float> nearWallFlag, ReadOnlySpan<float> nearEdgeFlag, ReadOnlySpan<float> nearCornerFlag,
            ReadOnlySpan<int> samplingRegionId, ReadOnlySpan<float> caseCond,
            int numNodes, int timeSteps, int featureDim, int attrDim)
        {
            EnsureEdgeIndicesInRange(rows, cols, numNodes);

            EnsureFinite(weights, nameof(weights));
            EnsureFinite(features, nameof(features));
            EnsureFinite(attrs, nameof(attrs));
            EnsureFinite(pMax, nameof(pMax));
            EnsureFinite(tArrival, nameof(tArrival));
            EnsureFinite(positiveImpulse, nameof(positiveImpulse));
            EnsureFinite(positiveDuration, nameof(positiveDuration));
            EnsureFinite(nearWallFlag, nameof(nearWallFlag));
            EnsureFinite(nearEdgeFlag, nameof(nearEdgeFlag));
            EnsureFinite(nearCornerFlag, nameof(nearCornerFlag));
            EnsureFinite(caseCond, nameof(caseCond));
        }

        /// <summary>
        /// 校验 COO 图拓扑索引是否严格映射在当前节点规模内，防止训练框架崩溃。
        /// </summary>
        private static void EnsureEdgeIndicesInRange(ReadOnlySpan<int> rows, ReadOnlySpan<int> cols, int numNodes)
        {
            for (int i = 0; i < rows.Length; i++)
            {
                if (rows[i] < 0 || rows[i] >= numNodes) throw new InvalidOperationException($"edge_index_row[{i}] 越界，预期范围 [0, {numNodes})，实际值为 {rows[i]}");
                if (cols[i] < 0 || cols[i] >= numNodes) throw new InvalidOperationException($"edge_index_col[{i}] 越界，预期范围 [0, {numNodes})，实际值为 {cols[i]}");
            }
        }

        /// <summary>
        /// 扫描并剔除包含 NaN 或是无限大值的脏特征列。
        /// </summary>
        private static void EnsureFinite(ReadOnlySpan<float> values, string name)
        {
            for (int i = 0; i < values.Length; i++)
            {
                if (float.IsNaN(values[i]) || float.IsInfinity(values[i])) throw new InvalidOperationException($"{name}[{i}] 存在 NaN或Inf等无效数值");
            }
        }

        private static void CleanupLargeSolverOutputsAfterSuccessfulNpz(
    PipelineConfig config,
    string trhistPathToKeep,
    string kFilePathToKeep,
    Action<string>? logger)
        {
            if (!config.CleanupLargeSolverFilesAfterSuccessfulNpz)
            {
                logger?.Invoke("[Cleanup] 已关闭大文件自动清理。");
                return;
            }

            string workDir = Path.GetDirectoryName(Path.GetFullPath(kFilePathToKeep))
                             ?? throw new InvalidOperationException("无法确定求解结果目录。");

            if (!Directory.Exists(workDir))
            {
                logger?.Invoke($"[Cleanup] 结果目录不存在，跳过清理：{workDir}");
                return;
            }

            var keepSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Path.GetFullPath(trhistPathToKeep),
                Path.GetFullPath(kFilePathToKeep)
            };

            int deletedCount = 0;
            long deletedBytes = 0;

            foreach (string pattern in config.SolverCleanupGlobs ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(pattern))
                    continue;

                IEnumerable<string> matchedFiles;
                try
                {
                    matchedFiles = Directory.EnumerateFiles(workDir, pattern, SearchOption.TopDirectoryOnly);
                }
                catch (Exception ex)
                {
                    logger?.Invoke($"[Cleanup][Warning] 枚举文件失败，Pattern={pattern}, Dir={workDir}, Error={ex.Message}");
                    continue;
                }

                foreach (string file in matchedFiles)
                {
                    string fullPath = Path.GetFullPath(file);

                    if (keepSet.Contains(fullPath))
                    {
                        logger?.Invoke($"[Cleanup] 保留文件：{Path.GetFileName(fullPath)}");
                        continue;
                    }

                    try
                    {
                        var fi = new FileInfo(fullPath);
                        long size = fi.Exists ? fi.Length : 0;

                        File.Delete(fullPath);

                        deletedCount++;
                        deletedBytes += size;

                        logger?.Invoke($"[Cleanup] 已删除：{fi.Name} ({FormatBytes(size)})");
                    }
                    catch (Exception ex)
                    {
                        logger?.Invoke($"[Cleanup][Warning] 删除失败：{fullPath}，Error={ex.Message}");
                    }
                }
            }

            logger?.Invoke($"[Cleanup] 清理完成，共删除 {deletedCount} 个文件，释放 {FormatBytes(deletedBytes)}。");
        }

        private static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double size = bytes;
            int unit = 0;

            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }

            return $"{size:0.##} {units[unit]}";
        }
    }
}