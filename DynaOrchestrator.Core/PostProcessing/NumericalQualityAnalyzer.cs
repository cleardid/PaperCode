using System.Text.Json;

namespace DynaOrchestrator.Core.PostProcessing
{
    /// <summary>
    /// 数值质量诊断器：
    /// 1. 检查时间轴单调性与 dt 统计
    /// 2. 检查 features / attrs / edge_weight 的 NaN / Inf
    /// 3. 检查图拓扑质量（孤立点、自环、越界边、负权重、零权重）
    /// 4. 检查工程标签可用性
    /// 5. 输出统一质量报告 JSON
    /// </summary>
    public static class NumericalQualityAnalyzer
    {
        /// <summary>
        /// 生成质量诊断报告
        /// </summary>
        public static QualityReport Analyze(
            int[] rows,
            int[] cols,
            float[] weights,
            float[] features,
            float[] attrs,
            float[] time,
            EngineeringLabels labels,
            int numNodes,
            int numEdges,
            int timeSteps,
            int featureDim,
            int attrDim,
            float arrivalThresholdPa)
        {
            if (rows == null) throw new ArgumentNullException(nameof(rows));
            if (cols == null) throw new ArgumentNullException(nameof(cols));
            if (weights == null) throw new ArgumentNullException(nameof(weights));
            if (features == null) throw new ArgumentNullException(nameof(features));
            if (attrs == null) throw new ArgumentNullException(nameof(attrs));
            if (time == null) throw new ArgumentNullException(nameof(time));
            if (labels == null) throw new ArgumentNullException(nameof(labels));

            var warnings = new List<string>();
            var errors = new List<string>();

            var timeAxis = AnalyzeTimeAxis(time);
            if (!timeAxis.is_strictly_increasing)
                errors.Add("time axis is not strictly increasing");

            if (attrs.Length != numNodes * attrDim)
                errors.Add("node_attr length does not match num_nodes * attr_dim");

            var featureStats = AnalyzeFloatArray(features);
            var attrStats = AnalyzeFloatArray(attrs);
            var edgeWeightStats = AnalyzeFloatArray(weights);

            if (featureStats.nan_count > 0 || featureStats.inf_count > 0)
                errors.Add("features contain NaN or Inf");

            if (attrStats.nan_count > 0 || attrStats.inf_count > 0)
                errors.Add("node_attr contains NaN or Inf");

            if (edgeWeightStats.nan_count > 0 || edgeWeightStats.inf_count > 0)
                errors.Add("edge_weight contains NaN or Inf");

            var graphStats = AnalyzeGraph(rows, cols, weights, numNodes, numEdges);
            if (graphStats.out_of_range_edge_index_count > 0)
                errors.Add("graph contains out-of-range edge indices");

            if (graphStats.isolated_node_count > 0)
                warnings.Add("graph contains isolated nodes");

            if (graphStats.negative_edge_weight_count > 0)
                warnings.Add("graph contains negative edge weights");

            var labelStats = AnalyzeLabels(labels, arrivalThresholdPa);
            if (labelStats.negative_impulse_count > 0)
                errors.Add("positive_impulse contains negative values");

            if (labelStats.negative_duration_count > 0)
                errors.Add("positive_duration contains negative values");

            if (labelStats.arrival_missing_ratio > 0.0)
                warnings.Add("some nodes never reach arrival threshold");

            if (labelStats.peak_less_than_threshold_count > 0)
                warnings.Add("some nodes have peak overpressure below arrival threshold");

            var featureRanges = AnalyzeFeatureRanges(features, numNodes, timeSteps, featureDim);

            string status = errors.Count > 0 ? "fail" : (warnings.Count > 0 ? "warning" : "pass");

            return new QualityReport
            {
                report_name = "quality_report",
                report_version = "1.0",
                status = status,
                warnings = warnings,
                errors = errors,
                time_axis = timeAxis,
                arrays = new ArrayQualitySection
                {
                    features = featureStats,
                    node_attr = attrStats,
                    edge_weight = edgeWeightStats
                },
                graph_topology = graphStats,
                labels = labelStats,
                feature_ranges = featureRanges
            };
        }

        /// <summary>
        /// 将质量报告对象序列化为 JSON
        /// </summary>
        public static string BuildJson(QualityReport report)
        {
            return JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true
            });
        }

        /// <summary>
        /// 检查时间轴质量
        /// </summary>
        private static TimeAxisQuality AnalyzeTimeAxis(float[] time)
        {
            bool strictlyIncreasing = true;
            double dtMin = double.PositiveInfinity;
            double dtMax = double.NegativeInfinity;
            double dtSum = 0.0;
            int dtCount = 0;

            for (int i = 1; i < time.Length; i++)
            {
                double dt = time[i] - time[i - 1];
                if (dt <= 0.0)
                    strictlyIncreasing = false;

                dtMin = Math.Min(dtMin, dt);
                dtMax = Math.Max(dtMax, dt);
                dtSum += dt;
                dtCount++;
            }

            if (dtCount == 0)
            {
                dtMin = 0.0;
                dtMax = 0.0;
            }

            return new TimeAxisQuality
            {
                length = time.Length,
                start_time_s = time.Length > 0 ? time[0] : 0.0,
                end_time_s = time.Length > 0 ? time[^1] : 0.0,
                is_strictly_increasing = strictlyIncreasing,
                dt_min_s = dtMin,
                dt_max_s = dtMax,
                dt_mean_s = dtCount > 0 ? dtSum / dtCount : 0.0
            };
        }

        /// <summary>
        /// 统计浮点数组中的 NaN / Inf / 极值
        /// </summary>
        private static FloatArrayQuality AnalyzeFloatArray(float[] data)
        {
            int nanCount = 0;
            int posInfCount = 0;
            int negInfCount = 0;
            int finiteCount = 0;

            double minVal = double.PositiveInfinity;
            double maxVal = double.NegativeInfinity;

            foreach (float v in data)
            {
                if (float.IsNaN(v))
                {
                    nanCount++;
                    continue;
                }

                if (float.IsPositiveInfinity(v))
                {
                    posInfCount++;
                    continue;
                }

                if (float.IsNegativeInfinity(v))
                {
                    negInfCount++;
                    continue;
                }

                finiteCount++;
                minVal = Math.Min(minVal, v);
                maxVal = Math.Max(maxVal, v);
            }

            if (finiteCount == 0)
            {
                minVal = 0.0;
                maxVal = 0.0;
            }

            return new FloatArrayQuality
            {
                length = data.Length,
                nan_count = nanCount,
                inf_count = posInfCount + negInfCount,
                finite_count = finiteCount,
                min = minVal,
                max = maxVal
            };
        }

        /// <summary>
        /// 检查图拓扑质量
        /// </summary>
        private static GraphTopologyQuality AnalyzeGraph(
            int[] rows,
            int[] cols,
            float[] weights,
            int numNodes,
            int numEdges)
        {
            int selfLoopCount = 0;
            int outOfRangeCount = 0;
            int negativeWeightCount = 0;
            int zeroWeightCount = 0;

            int[] degree = new int[numNodes];

            int edgeCount = numEdges;

            if (rows.Length < numEdges || cols.Length < numEdges || weights.Length < numEdges)
                throw new InvalidOperationException("graph edge array length is smaller than numEdges");
            for (int i = 0; i < edgeCount; i++)
            {
                int r = rows[i];
                int c = cols[i];
                float w = weights[i];

                bool rowValid = r >= 0 && r < numNodes;
                bool colValid = c >= 0 && c < numNodes;

                if (!rowValid || !colValid)
                {
                    outOfRangeCount++;
                    continue;
                }

                if (r == c)
                    selfLoopCount++;

                degree[r]++;
                degree[c]++;

                if (!float.IsNaN(w) && !float.IsInfinity(w))
                {
                    if (w < 0f) negativeWeightCount++;
                    if (w == 0f) zeroWeightCount++;
                }
            }

            int isolatedNodeCount = 0;
            for (int i = 0; i < numNodes; i++)
            {
                if (degree[i] == 0)
                    isolatedNodeCount++;
            }

            return new GraphTopologyQuality
            {
                edge_count_directed = edgeCount,
                isolated_node_count = isolatedNodeCount,
                isolated_node_ratio = numNodes > 0 ? (double)isolatedNodeCount / numNodes : 0.0,
                self_loop_count = selfLoopCount,
                out_of_range_edge_index_count = outOfRangeCount,
                negative_edge_weight_count = negativeWeightCount,
                zero_edge_weight_count = zeroWeightCount
            };
        }

        /// <summary>
        /// 检查工程标签质量
        /// </summary>
        private static LabelQuality AnalyzeLabels(EngineeringLabels labels, float arrivalThresholdPa)
        {
            int arrivalValidCount = 0;
            int arrivalMissingCount = 0;
            int negativeImpulseCount = 0;
            int negativeDurationCount = 0;
            int peakLessThanThresholdCount = 0;

            int count = labels.PeakOverpressure.Length;

            for (int i = 0; i < count; i++)
            {
                if (labels.ArrivalTime[i] >= 0f) arrivalValidCount++;
                else arrivalMissingCount++;

                if (labels.PositiveImpulse[i] < 0f) negativeImpulseCount++;
                if (labels.PositiveDuration[i] < 0f) negativeDurationCount++;
                if (labels.PeakOverpressure[i] < arrivalThresholdPa) peakLessThanThresholdCount++;
            }

            return new LabelQuality
            {
                arrival_valid_count = arrivalValidCount,
                arrival_missing_count = arrivalMissingCount,
                arrival_missing_ratio = count > 0 ? (double)arrivalMissingCount / count : 0.0,
                negative_impulse_count = negativeImpulseCount,
                negative_duration_count = negativeDurationCount,
                peak_less_than_threshold_count = peakLessThanThresholdCount
            };
        }

        /// <summary>
        /// 提取动态图 5 个特征的全局范围
        /// 特征布局固定为:
        /// 0=rho, 1=vx, 2=vy, 3=vz, 4=overpressure
        /// </summary>
        private static FeatureRangeSection AnalyzeFeatureRanges(float[] features, int numNodes, int timeSteps, int featureDim)
        {
            if (featureDim < 5)
                throw new InvalidOperationException($"feature_dim={featureDim} 小于 5，无法按既定布局分析范围。");

            var rho = InitRange();
            var vx = InitRange();
            var vy = InitRange();
            var vz = InitRange();
            var overpressure = InitRange();

            for (int n = 0; n < numNodes; n++)
            {
                for (int t = 0; t < timeSteps; t++)
                {
                    int baseIdx = n * (timeSteps * featureDim) + t * featureDim;

                    UpdateRange(ref rho, features[baseIdx + 0]);
                    UpdateRange(ref vx, features[baseIdx + 1]);
                    UpdateRange(ref vy, features[baseIdx + 2]);
                    UpdateRange(ref vz, features[baseIdx + 3]);
                    UpdateRange(ref overpressure, features[baseIdx + 4]);
                }
            }

            return new FeatureRangeSection
            {
                rho = rho,
                vx = vx,
                vy = vy,
                vz = vz,
                overpressure = overpressure
            };
        }

        /// <summary>
        /// 初始化范围对象
        /// </summary>
        private static ScalarRange InitRange()
        {
            return new ScalarRange
            {
                min = double.PositiveInfinity,
                max = double.NegativeInfinity
            };
        }

        /// <summary>
        /// 更新范围对象
        /// </summary>
        private static void UpdateRange(ref ScalarRange range, float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                return;

            range.min = Math.Min(range.min, value);
            range.max = Math.Max(range.max, value);

            if (double.IsPositiveInfinity(range.min)) range.min = 0.0;
            if (double.IsNegativeInfinity(range.max)) range.max = 0.0;
        }
    }

    /// <summary>
    /// 质量报告根对象
    /// </summary>
    public sealed class QualityReport
    {
        public required string report_name { get; init; }
        public required string report_version { get; init; }
        public required string status { get; init; }
        public required List<string> warnings { get; init; }
        public required List<string> errors { get; init; }
        public required TimeAxisQuality time_axis { get; init; }
        public required ArrayQualitySection arrays { get; init; }
        public required GraphTopologyQuality graph_topology { get; init; }
        public required LabelQuality labels { get; init; }
        public required FeatureRangeSection feature_ranges { get; init; }
    }

    /// <summary>
    /// 时间轴质量
    /// </summary>
    public sealed class TimeAxisQuality
    {
        public required int length { get; init; }
        public required double start_time_s { get; init; }
        public required double end_time_s { get; init; }
        public required bool is_strictly_increasing { get; init; }
        public required double dt_min_s { get; init; }
        public required double dt_max_s { get; init; }
        public required double dt_mean_s { get; init; }
    }

    /// <summary>
    /// 数组质量汇总
    /// </summary>
    public sealed class ArrayQualitySection
    {
        public required FloatArrayQuality features { get; init; }
        public required FloatArrayQuality node_attr { get; init; }
        public required FloatArrayQuality edge_weight { get; init; }
    }

    /// <summary>
    /// 通用浮点数组质量
    /// </summary>
    public sealed class FloatArrayQuality
    {
        public required int length { get; init; }
        public required int nan_count { get; init; }
        public required int inf_count { get; init; }
        public required int finite_count { get; init; }
        public required double min { get; init; }
        public required double max { get; init; }
    }

    /// <summary>
    /// 图拓扑质量
    /// </summary>
    public sealed class GraphTopologyQuality
    {
        public required int edge_count_directed { get; init; }
        public required int isolated_node_count { get; init; }
        public required double isolated_node_ratio { get; init; }
        public required int self_loop_count { get; init; }
        public required int out_of_range_edge_index_count { get; init; }
        public required int negative_edge_weight_count { get; init; }
        public required int zero_edge_weight_count { get; init; }
    }

    /// <summary>
    /// 标签质量
    /// </summary>
    public sealed class LabelQuality
    {
        public required int arrival_valid_count { get; init; }
        public required int arrival_missing_count { get; init; }
        public required double arrival_missing_ratio { get; init; }
        public required int negative_impulse_count { get; init; }
        public required int negative_duration_count { get; init; }
        public required int peak_less_than_threshold_count { get; init; }
    }

    /// <summary>
    /// 动态特征全局范围
    /// </summary>
    public sealed class FeatureRangeSection
    {
        public required ScalarRange rho { get; init; }
        public required ScalarRange vx { get; init; }
        public required ScalarRange vy { get; init; }
        public required ScalarRange vz { get; init; }
        public required ScalarRange overpressure { get; init; }
    }

    /// <summary>
    /// 单标量范围
    /// </summary>
    public sealed class ScalarRange
    {
        public required double min { get; set; }
        public required double max { get; set; }
    }
}