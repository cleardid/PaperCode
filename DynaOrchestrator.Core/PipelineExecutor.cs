using DynaOrchestrator.Core.Batch;
using DynaOrchestrator.Core.Models;
using DynaOrchestrator.Core.PostProcessing;
using DynaOrchestrator.Core.PreProcessing;
using DynaOrchestrator.Core.Solver;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;

namespace DynaOrchestrator.Core
{
    /// <summary>
    /// 单工况执行器。
    /// 将原先 Program.cs 中的单 case 流程抽取出来，供批处理与单工况模式复用。
    /// </summary>
    internal static class PipelineExecutor
    {
        /// <summary>
        /// 执行单个工况的完整流程。
        /// 注意：
        /// 1. 传入的 appConfig 应保持配置文件原始单位；
        /// 2. 方法内部会统一调用 NormalizeUnits()；
        /// 3. optimizeHardwareResources=true 时，会自动覆盖 Ncpu / Memory。
        /// </summary>
        public static void Execute(
            AppConfig appConfig,
            BatchCaseRecord record,
            bool optimizeHardwareResources = false,
            Action<string>? logger = null,
            CancellationToken cancellationToken = default)
        {
            if (appConfig == null)
                throw new ArgumentNullException(nameof(appConfig));

            var config = appConfig.Pipeline;
            var exp = appConfig.Explosive;
            var other = appConfig.Other;

            other.NormalizeUnits();

            if (optimizeHardwareResources)
            {
                OptimizeHardwareResources(config, logger);
            }

            logger?.Invoke($"[Config] 已加载配置. 爆源( mm): ({exp.Xc}, {exp.Yc}, {exp.Zc}), 当量: {exp.W} kg");

            // [Phase 1] 前处理 (如果连网格自适应也不需要，也可以加开关屏蔽)
            RunPreProcessing(config, other, exp, logger);

            // [Phase 2] LS-DYNA 求解 (核心保留)
            string actualTrhistPath = RunSimulation(config, cancellationToken, logger);

            // [Phase 3] 后处理与图构建 (按需执行)
            if (config.EnableGraphPostProcessing)
            {
                RunPostProcessing(config, exp, other, record, actualTrhistPath, logger);
            }
            else
            {
                logger?.Invoke("\n[Phase 3] 已配置为跳过数据构建，工况执行结束。");
            }
        }

        /// <summary>
        /// 从配置文件加载并解析 AppConfig 对象。
        /// </summary>
        public static AppConfig LoadConfig(string configFile)
        {
            if (!File.Exists(configFile))
                throw new FileNotFoundException($"未找到配置文件: {configFile}");

            string jsonString = File.ReadAllText(configFile);
            if (string.IsNullOrWhiteSpace(jsonString))
                throw new Exception("配置文件为空");

            AppConfig? appConfig = JsonSerializer.Deserialize<AppConfig>(jsonString);
            if (appConfig == null)
                throw new Exception("配置文件解析失败");

            return appConfig;
        }

        /// <summary>
        /// 进行几何解析与自适应网格生成，输出新的 K 文件。
        /// </summary>
        private static void RunPreProcessing(PipelineConfig config, OtherConfig other, ExplosiveParams exp, Action<string>? logger)
        {
            logger?.Invoke("\n[Phase 1] 几何解析与自适应网格生成...");

            string? outDir = Path.GetDirectoryName(Path.GetFullPath(config.OutputKFile));
            if (outDir == null)
                throw new Exception("无法获取输出文件目录");

            if (!Directory.Exists(outDir))
                Directory.CreateDirectory(outDir);

            var stlMesh = STLParser.ParseBinarySTL(config.StlFile, logger);
            AdaptiveMeshGenerator.ProcessAndGenerate(config.BaseKFile, config.OutputKFile, stlMesh, other, exp, logger);
        }

        /// <summary>
        /// 执行 LS-DYNA 求解，输出 TRHIST 文件。
        /// 当前逻辑保持不变：
        /// 实际 trhist 路径 = OutputKFile 所在目录 + TrhistFile 文件名
        /// </summary>
        private static string RunSimulation(PipelineConfig config, CancellationToken cancellationToken, Action<string>? logger)
        {
            logger?.Invoke("\n[Phase 2] 启动 LS-DYNA 求解器...");

            string? runDir = Path.GetDirectoryName(Path.GetFullPath(config.OutputKFile));
            if (runDir == null)
                throw new Exception("无法获取 trhist 目录");

            string actualTrhistPath = Path.Combine(runDir, config.TrhistFile);

            bool ok = LsDynaOrchestrator.Run(config, cancellationToken, logger);

            // 若是用户主动停止导致的终止，直接按取消抛出，不归类为普通失败
            cancellationToken.ThrowIfCancellationRequested();

            if (!ok || !File.Exists(actualTrhistPath))
                throw new Exception($"仿真执行中断或未找到文件: {actualTrhistPath}");

            return actualTrhistPath;
        }

        /// <summary>
        /// 执行后处理图引擎，提取物理图特征与静态属性，并保存为 NPZ 文件。
        /// 注意：当前 DLL 原生返回 11 维静态属性：
        /// [x,y,z,d,nx,ny,nz,W^(1/3), d_wall, d_edge, d_corner]
        /// </summary>
        private static void RunPostProcessing(PipelineConfig config, ExplosiveParams exp, OtherConfig other, BatchCaseRecord record, string actualTrhistPath, Action<string>? logger)
        {
            logger?.Invoke("\n[Phase 3] 提取物理图特征与静态属性...");

            // 向 C++ 注入托管回调
            GraphEngineAPI.InitializeLogger(logger);

            string absoluteStlPath = Path.GetFullPath(config.StlFile);

            IntPtr ptr = GraphEngineAPI.GenerateGraph(
                actualTrhistPath,
                absoluteStlPath,
                other.Rc,
                other.Alpha,
                (float)exp.Xc,
                (float)exp.Yc,
                (float)exp.Zc,
                (float)exp.W);

            if (ptr == IntPtr.Zero)
                throw new Exception("C++ 图引擎返回空指针");

            try
            {
                GraphData data = Marshal.PtrToStructure<GraphData>(ptr);

                var (rows, cols, weights, features, attrs11) = ExtractManagedGraphData(data);

                if (data.feature_dim != 5)
                    throw new Exception($"DLL 返回的 x 特征维度不是 5，而是 {data.feature_dim}");

                if (data.attr_dim != 11)
                    throw new Exception($"DLL 返回的 node_attr 维度不是 11，而是 {data.attr_dim}");

                int attrDim = data.attr_dim;

                const float arrivalThresholdPa = 1e-7f;

                // 提取工程响应标签。当前采用方案 B：保留 4 个 engineering head 标签，便于后续扩展。
                var labels = EngineeringLabelExtractor.Extract(
                    actualTrhistPath,
                    features,
                    data.num_nodes,
                    data.time_steps,
                    data.feature_dim,
                    logger,
                    pressureDimIndex: 4,
                    arrivalThreshold: arrivalThresholdPa);

                // 近壁 / 近棱 / 近角语义标签
                float[] nearWallFlag = BuildBinarySemanticFlag(
                    attrs11, data.num_nodes, attrDim, attrIndex: 8, threshold: (float)other.WallMargin);

                float[] nearEdgeFlag = BuildBinarySemanticFlag(
                    attrs11, data.num_nodes, attrDim, attrIndex: 9, threshold: (float)other.WallMargin);

                float[] nearCornerFlag = BuildBinarySemanticFlag(
                    attrs11, data.num_nodes, attrDim, attrIndex: 10, threshold: (float)other.WallMargin);

                int[] samplingRegionId = BuildSamplingRegionId(
                    nearWallFlag,
                    nearEdgeFlag,
                    nearCornerFlag,
                    data.num_nodes);

                // 直接导出 case 级条件向量
                float[] caseCond = BuildCaseConditionVector(record);

                // 严格执行 STGNS-v1 当前训练链路所需的一致性校验
                ValidateStgnsV1Export(
                    rows,
                    cols,
                    weights,
                    features,
                    attrs11,
                    labels.PeakOverpressure,
                    labels.ArrivalTime,
                    labels.PositiveImpulse,
                    labels.PositiveDuration,
                    nearWallFlag,
                    nearEdgeFlag,
                    nearCornerFlag,
                    samplingRegionId,
                    caseCond,
                    data.num_nodes,
                    data.time_steps,
                    data.feature_dim,
                    attrDim);

                // 保存 NPZ：只保留 STGNS-v1 主训练链路真正需要的数组文件
                NpzWriter.Save(
                    config.NpzOutputFile,
                    rows,
                    cols,
                    weights,
                    features,
                    attrs11,
                    labels.PeakOverpressure,
                    labels.ArrivalTime,
                    labels.PositiveImpulse,
                    labels.PositiveDuration,
                    nearWallFlag,
                    nearEdgeFlag,
                    nearCornerFlag,
                    samplingRegionId,
                    caseCond,
                    data.num_nodes,
                    data.time_steps,
                    data.feature_dim,
                    attrDim,
                    logger);
            }
            finally
            {
                GraphEngineAPI.FreeGraphData(ptr);
                logger?.Invoke("[Info] 非托管图内存已安全释放。");
            }
        }

        /// <summary>
        /// 验证并提取托管侧图数据。
        /// </summary>
        private static (int[] rows, int[] cols, float[] weights, float[] features, float[] attrs)
            ExtractManagedGraphData(GraphData data)
        {
            ValidateGraphData(data);

            int edgeCount = data.num_edges;
            int featureCount = checked(data.num_nodes * data.time_steps * data.feature_dim);
            int attrCount = checked(data.num_nodes * data.attr_dim);

            int[] rows = new int[edgeCount];
            int[] cols = new int[edgeCount];
            float[] weights = new float[edgeCount];
            float[] features = new float[featureCount];
            float[] attrs = new float[attrCount];

            if (edgeCount > 0)
            {
                Marshal.Copy(data.coo_rows, rows, 0, edgeCount);
                Marshal.Copy(data.coo_cols, cols, 0, edgeCount);
                Marshal.Copy(data.coo_weights, weights, 0, edgeCount);
            }

            Marshal.Copy(data.node_features, features, 0, featureCount);
            Marshal.Copy(data.node_attrs, attrs, 0, attrCount);

            return (rows, cols, weights, features, attrs);
        }

        /// <summary>
        /// 验证 GraphData 结构体内容是否合法。
        /// </summary>
        private static void ValidateGraphData(GraphData data)
        {
            if (data.num_nodes <= 0)
                throw new Exception("GraphData.num_nodes 非法");

            if (data.num_edges < 0 || data.time_steps <= 0 || data.feature_dim <= 0 || data.attr_dim <= 0)
                throw new Exception("GraphData 维度字段非法");

            if (data.attr_dim != 11)
                throw new Exception($"GraphData.attr_dim 非法，当前为 {data.attr_dim}，预期为 11");

            if (data.num_edges > 0)
            {
                if (data.coo_rows == IntPtr.Zero) throw new Exception("coo_rows 空指针");
                if (data.coo_cols == IntPtr.Zero) throw new Exception("coo_cols 空指针");
                if (data.coo_weights == IntPtr.Zero) throw new Exception("coo_weights 空指针");
            }

            if (data.node_features == IntPtr.Zero)
                throw new Exception("node_features 空指针");

            if (data.node_attrs == IntPtr.Zero)
                throw new Exception("node_attrs 空指针");
        }

        /// <summary>
        /// 构建 STGNS-v1 当前训练链路使用的 case 条件向量。
        /// 顺序固定为：
        /// [charge_x_m, charge_y_m, charge_z_m, room_L_m, room_W_m, room_H_m, charge_scale]
        ///
        /// 这里的 charge_scale 明确定义为 W^(1/3)，单位 kg^(1/3)，与 node_attr 中的 W_cbrt 保持一致。
        /// record.X / Y / Z 采用绝对坐标，单位 mm；导出到 case_cond 时统一转换为 m。
        /// </summary>
        private static float[] BuildCaseConditionVector(BatchCaseRecord record)
        {
            return new[]
            {
                (float)(record.X * 0.001),
                (float)(record.Y * 0.001),
                (float)(record.Z * 0.001),
                (float)record.L,
                (float)record.W,
                (float)record.H,
                (float)Math.Pow(record.ChargeMass, 1.0 / 3.0)};
        }

        /// <summary>
        /// 对导出的最小化 NPZ 结构执行严格校验，确保与 STGNS-v1 Python 主训练链路一致。
        /// </summary>
        private static void ValidateStgnsV1Export(
            int[] rows,
            int[] cols,
            float[] weights,
            float[] features,
            float[] attrs,
            float[] pMax,
            float[] tArrival,
            float[] positiveImpulse,
            float[] positiveDuration,
            float[] nearWallFlag,
            float[] nearEdgeFlag,
            float[] nearCornerFlag,
            int[] samplingRegionId,
            float[] caseCond,
            int numNodes,
            int timeSteps,
            int featureDim,
            int attrDim)
        {
            if (rows.Length != cols.Length || rows.Length != weights.Length)
                throw new InvalidOperationException("边数组长度不一致：edge_index_row / edge_index_col / edge_weight 必须同长。");

            if (features.Length != numNodes * timeSteps * featureDim)
                throw new InvalidOperationException("x.npy 数据长度与 (N,T,D) 不匹配。");

            if (attrs.Length != numNodes * attrDim)
                throw new InvalidOperationException("node_attr.npy 数据长度与 (N,attr_dim) 不匹配。");

            if (featureDim != 5)
                throw new InvalidOperationException($"x.npy 的最后一维必须恒为 5，当前为 {featureDim}。");

            if (attrDim != 11)
                throw new InvalidOperationException($"node_attr.npy 的最后一维必须恒为 11，当前为 {attrDim}。");

            ValidateNodeArrayLength(nearWallFlag, numNodes, nameof(nearWallFlag));
            ValidateNodeArrayLength(nearEdgeFlag, numNodes, nameof(nearEdgeFlag));
            ValidateNodeArrayLength(nearCornerFlag, numNodes, nameof(nearCornerFlag));
            ValidateNodeArrayLength(samplingRegionId, numNodes, nameof(samplingRegionId));
            ValidateNodeArrayLength(pMax, numNodes, nameof(pMax));
            ValidateNodeArrayLength(tArrival, numNodes, nameof(tArrival));
            ValidateNodeArrayLength(positiveImpulse, numNodes, nameof(positiveImpulse));
            ValidateNodeArrayLength(positiveDuration, numNodes, nameof(positiveDuration));

            if (caseCond.Length != 7)
                throw new InvalidOperationException("case_cond.npy 的长度必须恒为 7。");

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

            for (int i = 0; i < numNodes; i++)
            {
                bool isCorner = nearCornerFlag[i] > 0.5f;
                bool isEdge = nearEdgeFlag[i] > 0.5f;
                bool isWall = nearWallFlag[i] > 0.5f;

                int expectedRegionId = isCorner ? 3 : isEdge ? 2 : isWall ? 1 : 0;
                int actualRegionId = samplingRegionId[i];

                if (actualRegionId < 0 || actualRegionId > 3)
                    throw new InvalidOperationException($"sampling_region_id[{i}] 超出合法范围 [0,3]，当前值={actualRegionId}。");

                if (actualRegionId != expectedRegionId)
                    throw new InvalidOperationException($"sampling_region_id 与 near_*_flag 语义不一致，节点 {i} 期望 {expectedRegionId}，实际 {actualRegionId}。");
            }
        }

        private static void ValidateNodeArrayLength<T>(T[] values, int numNodes, string name)
        {
            if (values.Length != numNodes)
                throw new InvalidOperationException($"{name} 长度必须等于 num_nodes，当前 {values.Length} != {numNodes}。");
        }

        private static void EnsureEdgeIndicesInRange(int[] rows, int[] cols, int numNodes)
        {
            for (int i = 0; i < rows.Length; i++)
            {
                if (rows[i] < 0 || rows[i] >= numNodes)
                    throw new InvalidOperationException($"edge_index_row[{i}] 越界，当前值={rows[i]}，num_nodes={numNodes}。");

                if (cols[i] < 0 || cols[i] >= numNodes)
                    throw new InvalidOperationException($"edge_index_col[{i}] 越界，当前值={cols[i]}，num_nodes={numNodes}。");
            }
        }

        private static void EnsureFinite(float[] values, string name)
        {
            for (int i = 0; i < values.Length; i++)
            {
                if (float.IsNaN(values[i]) || float.IsInfinity(values[i]))
                    throw new InvalidOperationException($"{name}[{i}] 存在 NaN/Inf。");
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private class MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;

            public MEMORYSTATUSEX()
            {
                dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        /// <summary>
        /// 动态计算并覆盖 LS-DYNA 的运行参数。
        /// 批处理模式下通常建议关闭该逻辑，避免覆盖外层统一分配的 ncpu/memory。
        /// </summary>
        private static void OptimizeHardwareResources(PipelineConfig config, Action<string>? logger)
        {
            // CPU：保留 1 个逻辑核给系统
            int totalCores = Environment.ProcessorCount;
            config.Ncpu = Math.Max(1, totalCores - 1);

            // 内存：按当前剩余物理内存的 85% 分配给 LS-DYNA
            MEMORYSTATUSEX memStatus = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(memStatus))
            {
                ulong availableMemoryBytes = memStatus.ullAvailPhys;
                double targetMemoryBytes = availableMemoryBytes * 0.85;

                // 转换为 LS-DYNA 所需 Mega-Words（1 Word = 8 Bytes）
                long memoryInMegaWords = (long)(targetMemoryBytes / 8.0 / 1000000.0);

                // 至少给 20m
                memoryInMegaWords = Math.Max(20, memoryInMegaWords);
                config.Memory = $"{memoryInMegaWords}m";

                double availableGb = availableMemoryBytes / 1024.0 / 1024.0 / 1024.0;
                double targetGb = targetMemoryBytes / 1024.0 / 1024.0 / 1024.0;

                logger?.Invoke($"[硬件探测] CPU 逻辑核心: {totalCores}，分配给 DYNA: {config.Ncpu} 核");
                logger?.Invoke($"[硬件探测] 系统当前剩余可用内存: {availableGb:F2} GB");
                logger?.Invoke($"[硬件探测] 提取 85% 分配给 DYNA: {config.Memory} (约 {targetGb:F2} GB)");
            }
            else
            {
                logger?.Invoke("[Warning] 无法读取系统内存状态，将使用配置文件默认值。");
            }
        }

        private static float[] BuildBinarySemanticFlag(
            float[] attrs,
            int numNodes,
            int attrDim,
            int attrIndex,
            float threshold)
        {
            if (attrs == null)
                throw new ArgumentNullException(nameof(attrs));

            if (attrDim <= attrIndex)
                throw new ArgumentOutOfRangeException(nameof(attrIndex), $"attrIndex={attrIndex} 超出 attrDim={attrDim}");

            if (attrs.Length != numNodes * attrDim)
                throw new InvalidOperationException("attrs 长度与 numNodes * attrDim 不匹配。");

            var flags = new float[numNodes];

            for (int i = 0; i < numNodes; i++)
            {
                int baseIdx = i * attrDim;
                float value = attrs[baseIdx + attrIndex];
                flags[i] = value <= threshold ? 1.0f : 0.0f;
            }

            return flags;
        }

        private static int[] BuildSamplingRegionId(
            float[] nearWallFlag,
            float[] nearEdgeFlag,
            float[] nearCornerFlag,
            int numNodes)
        {
            if (nearWallFlag.Length != numNodes ||
                nearEdgeFlag.Length != numNodes ||
                nearCornerFlag.Length != numNodes)
            {
                throw new InvalidOperationException("区域语义数组长度不一致。");
            }

            var regionId = new int[numNodes];

            for (int i = 0; i < numNodes; i++)
            {
                bool isCorner = nearCornerFlag[i] > 0.5f;
                bool isEdge = nearEdgeFlag[i] > 0.5f;
                bool isWall = nearWallFlag[i] > 0.5f;

                // 优先级：角区 > 棱边区 > 近壁区 > 常规区
                if (isCorner) regionId[i] = 3;
                else if (isEdge) regionId[i] = 2;
                else if (isWall) regionId[i] = 1;
                else regionId[i] = 0;
            }

            return regionId;
        }
    }
}