using DynaOrchestrator.Core.Models;
using DynaOrchestrator.Core.PostProcessing;
using DynaOrchestrator.Core.PreProcessing;
using DynaOrchestrator.Core.Solver;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;

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
        public static void Execute(AppConfig appConfig, bool optimizeHardwareResources = false, Action<string>? logger = null)
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
            string actualTrhistPath = RunSimulation(config, logger);
    
            // [Phase 3] 后处理与图构建 (按需执行)
            if (config.EnableGraphPostProcessing)
            {
                RunPostProcessing(config, exp, other, actualTrhistPath, logger);
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
        private static string RunSimulation(PipelineConfig config, Action<string>? logger)
        {
            logger?.Invoke("\n[Phase 2] 启动 LS-DYNA 求解器...");

            string? runDir = Path.GetDirectoryName(Path.GetFullPath(config.OutputKFile));
            if (runDir == null)
                throw new Exception("无法获取 trhist 目录");

            string actualTrhistPath = Path.Combine(runDir, config.TrhistFile);

            if (!LsDynaOrchestrator.Run(config) || !File.Exists(actualTrhistPath))
                throw new Exception($"仿真执行中断或未找到文件: {actualTrhistPath}");

            return actualTrhistPath;
        }

        /// <summary>
        /// 执行后处理图引擎，提取物理图特征与静态属性，并保存为 NPZ 文件。
        /// 注意：当前 DLL 原生返回 11 维静态属性：
        /// [x,y,z,d,nx,ny,nz,W^(1/3), d_wall, d_edge, d_corner]
        /// </summary>
        private static void RunPostProcessing(PipelineConfig config, ExplosiveParams exp, OtherConfig other, string actualTrhistPath, Action<string>? logger)
        {
            logger?.Invoke("\n[Phase 3] 提取物理图特征与静态属性...");
            
            // 向 C++ 注入托管回调
            GraphEngineAPI.InitializeLogger(logger);

            string absoluteStlPath = Path.GetFullPath(config.StlFile);

            //logger?.Invoke($"[C#] Rc before GenerateGraph = {other.Rc}");
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

                if (data.attr_dim != 11)
                    throw new Exception($"DLL 返回的 node_attr 维度不是 11，而是 {data.attr_dim}");

                int newAttrDim = data.attr_dim;

                const float arrivalThresholdPa = 1e-7f;

                // 提取工程响应标签
                var labels = EngineeringLabelExtractor.Extract(
                    actualTrhistPath,
                    features,
                    data.num_nodes,
                    data.time_steps,
                    data.feature_dim,
                    logger,
                    pressureDimIndex: 4,
                    arrivalThreshold: arrivalThresholdPa);

                string labelsMetaJson = EngineeringLabelExtractor.BuildMetadataJson(labels.Metadata);

                // 近壁 / 近棱 / 近角语义标签
                float[] nearWallFlag = BuildBinarySemanticFlag(
                    attrs11, data.num_nodes, newAttrDim, attrIndex: 8, threshold: (float)other.WallMargin);

                float[] nearEdgeFlag = BuildBinarySemanticFlag(
                    attrs11, data.num_nodes, newAttrDim, attrIndex: 9, threshold: (float)other.WallMargin);

                float[] nearCornerFlag = BuildBinarySemanticFlag(
                    attrs11, data.num_nodes, newAttrDim, attrIndex: 10, threshold: (float)other.WallMargin);

                int[] samplingRegionId = BuildSamplingRegionId(
                    nearWallFlag,
                    nearEdgeFlag,
                    nearCornerFlag,
                    data.num_nodes);

                // case 级统计摘要
                double peakMax = labels.PeakOverpressure.Length > 0 ? labels.PeakOverpressure.Max() : 0.0;
                double peakMean = labels.PeakOverpressure.Length > 0 ? labels.PeakOverpressure.Average() : 0.0;

                var validArrivalTimes = labels.ArrivalTime.Where(t => t >= 0.0f).ToArray();

                double arrivalMin = validArrivalTimes.Length > 0 ? validArrivalTimes.Min() : -1.0;
                double arrivalMax = validArrivalTimes.Length > 0 ? validArrivalTimes.Max() : -1.0;

                double impulseMax = labels.PositiveImpulse.Length > 0 ? labels.PositiveImpulse.Max() : 0.0;
                double durationMax = labels.PositiveDuration.Length > 0 ? labels.PositiveDuration.Max() : 0.0;

                int noArrivalNodeCount = labels.ArrivalTime.Count(t => t < 0.0f);
                double noArrivalNodeRatio = data.num_nodes > 0 ? (double)noArrivalNodeCount / data.num_nodes : 0.0;

                string caseMetadataJson = BuildCaseMetadataJson(
                    config,
                    exp,
                    other,
                    actualTrhistPath,
                    data.num_nodes,
                    data.num_edges,
                    data.time_steps,
                    data.feature_dim,
                    newAttrDim,
                    peakMax,
                    peakMean,
                    arrivalMin,
                    arrivalMax,
                    impulseMax,
                    durationMax,
                    arrivalThresholdPa,
                    noArrivalNodeCount,
                    noArrivalNodeRatio);

                string featureMetaJson = BuildFeatureMetaJson(data.feature_dim);

                string configSnapshotJson = JsonSerializer.Serialize(
                    new AppConfig
                    {
                        Pipeline = config,
                        Explosive = exp,
                        Other = other
                    },
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    });

                string graphMetaJson = NpzWriter.BuildGraphMetaJson(data.num_edges, other.Rc, other.Alpha);

                // 数值质量诊断
                var qualityReport = NumericalQualityAnalyzer.Analyze(
                    rows,
                    cols,
                    weights,
                    features,
                    attrs11,
                    labels.Time,
                    labels,
                    data.num_nodes,
                    data.num_edges,
                    data.time_steps,
                    data.feature_dim,
                    newAttrDim,
                    arrivalThresholdPa);

                string qualityReportJson = NumericalQualityAnalyzer.BuildJson(qualityReport);

                // 保存 NPZ
                NpzWriter.Save(
                    config.NpzOutputFile,
                    rows,
                    cols,
                    weights,
                    features,
                    attrs11,
                    labels.Time,
                    labels.PeakOverpressure,
                    labels.ArrivalTime,
                    labels.PositiveImpulse,
                    labels.PositiveDuration,
                    nearWallFlag,
                    nearEdgeFlag,
                    nearCornerFlag,
                    samplingRegionId,
                    labelsMetaJson,
                    caseMetadataJson,
                    featureMetaJson,
                    configSnapshotJson,
                    graphMetaJson,
                    qualityReportJson,
                    (float)other.WallMargin,
                    data.num_nodes,
                    data.time_steps,
                    data.feature_dim,
                    newAttrDim,
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

        private static string BuildFeatureMetaJson(int featureDim)
        {
            if (featureDim != 5)
                throw new InvalidOperationException($"当前仅支持 5 维动态图特征元数据，实际 feature_dim={featureDim}");

            var meta = new
            {
                name = "x",
                shape = new[] { "num_nodes", "time_steps", "5" },
                fields = new object[]
                {
                    new { index = 0, name = "rho", unit = "kg/m^3", description = "air density" },
                    new { index = 1, name = "vx", unit = "m/s", description = "x velocity" },
                    new { index = 2, name = "vy", unit = "m/s", description = "y velocity" },
                    new { index = 3, name = "vz", unit = "m/s", description = "z velocity" },
                    new { index = 4, name = "overpressure", unit = "Pa", description = "overpressure = raw_pressure - P0" }
                }
            };

            return JsonSerializer.Serialize(meta, new JsonSerializerOptions
            {
                WriteIndented = true
            });
        }

        private static string BuildCaseMetadataJson(
            PipelineConfig config,
            ExplosiveParams exp,
            OtherConfig other,
            string actualTrhistPath,
            int numNodes,
            int numEdges,
            int timeSteps,
            int featureDim,
            int attrDim,
            double peakMax,
            double peakMean,
            double arrivalMin,
            double arrivalMax,
            double impulseMax,
            double durationMax,
            float arrivalThresholdPa,
            int noArrivalNodeCount,
            double noArrivalNodeRatio)
        {
            var meta = new
            {
                dataset_spec = new
                {
                    case_id = config.CaseId,
                    dataset_version = config.DatasetVersion,
                    generator = "DynaOrchestrator.Core",
                    generation_time_utc = DateTime.UtcNow.ToString("O"),
                    graph_feature_layout = new[] { "rho", "vx", "vy", "vz", "overpressure" },
                    node_attr_layout = new[] { "x", "y", "z", "d", "nx", "ny", "nz", "W_cbrt", "d_wall", "d_edge", "d_corner" },
                    engineering_label_names = new[] { "p_max", "t_arrival", "positive_impulse", "positive_duration" },

                    dataset_layers = new
                    {
                        graph_topology = new[]
                        {
                            "edge_index_row.npy",
                            "edge_index_col.npy",
                            "edge_weight.npy",
                            "graph_meta.json"
                        },
                        dynamic_field = new[]
                        {
                            "x.npy",
                            "time.npy",
                            "feature_meta.json"
                        },
                        static_prior = new[]
                        {
                            "node_attr.npy",
                            "node_attr_meta.json",
                            "near_wall_flag.npy",
                            "near_edge_flag.npy",
                            "near_corner_flag.npy",
                            "sampling_region_id.npy",
                            "sampling_semantics_meta.json"
                        },
                        engineering_labels = new[]
                        {
                            "p_max.npy",
                            "t_arrival.npy",
                            "positive_impulse.npy",
                            "positive_duration.npy",
                            "engineering_labels_meta.json"
                        },
                        case_metadata = new[]
                        {
                            "case_metadata.json",
                            "config_snapshot.json",
                            "quality_report.json"
                        }
                    }
                },

                source_files = new
                {
                    base_k_file = config.BaseKFile,
                    stl_file = config.StlFile,
                    output_k_file = config.OutputKFile,
                    trhist_file = actualTrhistPath,
                    npz_output_file = config.NpzOutputFile
                },

                scenario = new
                {
                    charge_center_mm = new[] { exp.Xc, exp.Yc, exp.Zc },
                    charge_center_m = new[] { exp.Xc * 0.001, exp.Yc * 0.001, exp.Zc * 0.001 },
                    charge_radius_mm = exp.Radius,
                    charge_mass_kg = exp.W,
                    rc_m = other.Rc,
                    alpha = other.Alpha,
                    dl_dense_m = other.DlDense,
                    sparse_factor = other.SparseFactor,
                    core_radius_multiplier = other.CoreRadiusMultiplier,
                    wall_margin_m = other.WallMargin
                },

                sampling_semantics = new
                {
                    wall_margin_m = other.WallMargin,
                    near_wall_rule = "d_wall <= wall_margin_m",
                    near_edge_rule = "d_edge <= wall_margin_m",
                    near_corner_rule = "d_corner <= wall_margin_m",
                    sampling_region_id_definition = new
                    {
                        regular = 0,
                        near_wall = 1,
                        near_edge = 2,
                        near_corner = 3
                    }
                },

                design_factors = new
                {
                    room_geometry_type = "sealed_rigid_rectangular_room",
                    charge_position_type = "user_defined",
                    charge_mass_kg = exp.W,
                    charge_radius_mm = exp.Radius,
                    graph_radius_m = other.Rc,
                    sampling_dense_step_m = other.DlDense,
                    sparse_factor = other.SparseFactor,
                    wall_margin_m = other.WallMargin,
                    core_radius_multiplier = other.CoreRadiusMultiplier
                },

                graph_construction = new
                {
                    graph_builder = "radius graph with LoS visibility constraint",
                    edge_storage = "directed COO",
                    edge_count_semantics = "num_edges counts directed edges actually stored in edge_index_row/col",
                    edge_weight_definition = "heuristic spatial proximity weight: exp(-alpha * distance)",
                    edge_weight_role = "graph connectivity prior, not a physical propagation coefficient",

                    stl_role = new[]
                    {
                        "closed-domain identification",
                        "wall-distance recovery",
                        "sampling near wall"
                    },

                    los_role = "graph edge visibility constraint",
                    research_positioning = "auxiliary geometric preprocessing rather than core methodological contribution",

                    parameters = new
                    {
                        rc_m = other.Rc,
                        alpha = other.Alpha
                    }
                },

                numerical_reliability = new
                {
                    trhist_dt_s = other.TrhistDt,
                    lsdyna_ncpu = config.Ncpu,
                    lsdyna_memory = config.Memory,
                    pressure_feature_dim_index = 4,
                    arrival_threshold_pa = arrivalThresholdPa,
                    overpressure_definition = "overpressure = raw_pressure - P0",
                    positive_phase_rule = "overpressure >= arrival_threshold_pa",
                    impulse_rule = "time_integral_of_max(overpressure - arrival_threshold_pa, 0)",
                    duration_rule = "time_duration_where_overpressure >= arrival_threshold_pa_using_piecewise_linear_crossing"
                },

                units = new
                {
                    coordinate = "m",
                    distance = "m",
                    velocity = "m/s",
                    density = "kg/m^3",
                    time = "s",
                    pressure = "Pa",
                    mass = "kg",
                    edge_weight = "1"
                },

                case_statistics = new
                {
                    num_nodes = numNodes,
                    num_edges = numEdges,
                    time_steps = timeSteps,
                    feature_dim = featureDim,
                    attr_dim = attrDim,
                    peak_overpressure_global_max = peakMax,
                    peak_overpressure_global_mean = peakMean,
                    arrival_time_min = arrivalMin,
                    arrival_time_max = arrivalMax,
                    no_arrival_node_count = noArrivalNodeCount,
                    no_arrival_node_ratio = noArrivalNodeRatio,
                    positive_impulse_global_max = impulseMax,
                    positive_duration_global_max = durationMax
                }
            };

            return JsonSerializer.Serialize(meta, new JsonSerializerOptions
            {
                WriteIndented = true
            });
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