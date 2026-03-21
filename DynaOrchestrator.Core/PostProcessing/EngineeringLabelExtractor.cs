using System.Globalization;
using System.Text.Json;

namespace DynaOrchestrator.Core.PostProcessing
{
    /// <summary>
    /// 从时空动态图场中提取工程标签：
    /// - time[T]
    /// - p_max[N]                  : 全时程峰值超压
    /// - t_arrival[N]              : 首次跨越阈值的到达时刻（线性插值）
    /// - positive_impulse[N]       : 全时程累计正相比冲（对 max(p-threshold, 0) 做时间积分）
    /// - positive_duration[N]      : 全时程累计正压持续时间（按线性插值精确累计）
    ///
    /// features 布局为 (N, T, D)，且第 5 维 [index=4] 已经是 overpressure。
    /// </summary>
    public static class EngineeringLabelExtractor
    {
        public static EngineeringLabels Extract(
            string trhistPath,
            float[] features,
            int numNodes,
            int timeSteps,
            int featureDim,
            Action<string>? logger,
            int pressureDimIndex = 4,
            float arrivalThreshold = 1e-7f)
        {
            if (features == null)
                throw new ArgumentNullException(nameof(features));

            if (features.Length != numNodes * timeSteps * featureDim)
                throw new InvalidOperationException("features 长度与 (N,T,D) 不匹配。");

            if (pressureDimIndex < 0 || pressureDimIndex >= featureDim)
                throw new ArgumentOutOfRangeException(nameof(pressureDimIndex));

            float[] time = ReadTimeAxisFromTrhist(trhistPath, timeSteps);

            float[] pMax = new float[numNodes];
            float[] tArrival = new float[numNodes];
            float[] positiveImpulse = new float[numNodes];
            float[] positiveDuration = new float[numNodes];

            for (int i = 0; i < numNodes; i++)
                tArrival[i] = -1.0f;

            for (int node = 0; node < numNodes; node++)
            {
                float localMax = float.NegativeInfinity;
                double impulse = 0.0;
                double duration = 0.0;
                bool arrived = false;

                // 1) 峰值超压 + 到达时刻（线性插值）
                for (int t = 0; t < timeSteps; t++)
                {
                    int idx = node * (timeSteps * featureDim) + t * featureDim + pressureDimIndex;
                    float p = features[idx];

                    if (p > localMax)
                        localMax = p;

                    if (!arrived)
                    {
                        if (t == 0 && p >= arrivalThreshold)
                        {
                            tArrival[node] = time[0];
                            arrived = true;
                        }
                        else if (t > 0)
                        {
                            int prevIdx = node * (timeSteps * featureDim) + (t - 1) * featureDim + pressureDimIndex;
                            float pPrev = features[prevIdx];

                            if (pPrev < arrivalThreshold && p >= arrivalThreshold)
                            {
                                float t0 = time[t - 1];
                                float t1 = time[t];

                                float ta = InterpolateCrossingTime(t0, t1, pPrev, p, arrivalThreshold);
                                tArrival[node] = ta;
                                arrived = true;
                            }
                        }
                    }
                }

                // 2) 全时程累计正相比冲 + 全时程累计正压持续时间
                for (int t = 0; t < timeSteps - 1; t++)
                {
                    int idx0 = node * (timeSteps * featureDim) + t * featureDim + pressureDimIndex;
                    int idx1 = node * (timeSteps * featureDim) + (t + 1) * featureDim + pressureDimIndex;

                    float p0Raw = features[idx0];
                    float p1Raw = features[idx1];

                    float t0 = time[t];
                    float t1 = time[t + 1];
                    float dt = t1 - t0;

                    if (dt < 0)
                        throw new InvalidOperationException("时间轴不是单调递增，无法计算工程标签。");

                    // 与阈值比较后的“有效正压”
                    float q0 = p0Raw - arrivalThreshold;
                    float q1 = p1Raw - arrivalThreshold;

                    impulse += IntegratePositivePartLinear(q0, q1, dt);
                    duration += ComputePositiveDurationLinear(q0, q1, dt);
                }

                pMax[node] = localMax;
                positiveImpulse[node] = (float)impulse;
                positiveDuration[node] = (float)duration;
            }

            var metadata = new EngineeringLabelMetadata
            {
                PressureDimIndex = pressureDimIndex,
                ArrivalThreshold = arrivalThreshold,
                PeakOverpressureDefinition = "max over full overpressure time history",
                ArrivalTimeDefinition = "first threshold crossing time using linear interpolation",
                PositiveImpulseDefinition = "time integral of max(overpressure - arrival_threshold_pa, 0) over full time history",
                PositiveDurationDefinition = "total duration where overpressure >= arrival_threshold_pa using piecewise-linear crossing",
                PositivePhaseRule = "overpressure >= arrival_threshold_pa",
                TimeSource = "parsed from trhist time-step headers"
            };

            logger?.Invoke("[后处理] 已提取工程标签：p_max, t_arrival, positive_impulse, positive_duration");
            logger?.Invoke($"[后处理] 到达/正相阈值 arrivalThreshold = {arrivalThreshold:E6}");

            return new EngineeringLabels
            {
                Time = time,
                PeakOverpressure = pMax,
                ArrivalTime = tArrival,
                PositiveImpulse = positiveImpulse,
                PositiveDuration = positiveDuration,
                Metadata = metadata
            };
        }

        public static string BuildMetadataJson(EngineeringLabelMetadata metadata)
        {
            return JsonSerializer.Serialize(metadata, new JsonSerializerOptions
            {
                WriteIndented = true
            });
        }

        private static float InterpolateCrossingTime(float t0, float t1, float y0, float y1, float threshold)
        {
            float denom = y1 - y0;
            if (Math.Abs(denom) < 1e-20f)
                return t1;

            float ratio = (threshold - y0) / denom;
            ratio = Math.Clamp(ratio, 0.0f, 1.0f);
            return t0 + ratio * (t1 - t0);
        }

        /// <summary>
        /// 对线性段 q(t) 的正部分 max(q, 0) 做精确积分。
        /// 这里 q = overpressure - threshold。
        /// </summary>
        private static double IntegratePositivePartLinear(float q0, float q1, float dt)
        {
            if (dt <= 0f)
                return 0.0;

            bool pos0 = q0 > 0f;
            bool pos1 = q1 > 0f;

            if (pos0 && pos1)
            {
                return 0.5 * (q0 + q1) * dt;
            }

            if (!pos0 && !pos1)
            {
                return 0.0;
            }

            float absDenom = Math.Abs(q1 - q0);
            if (absDenom < 1e-20f)
                return 0.0;

            if (pos0 && !pos1)
            {
                double dtPos = dt * (q0 / (q0 - q1));
                return 0.5 * q0 * dtPos;
            }

            // !pos0 && pos1
            {
                double dtPos = dt * (q1 / (q1 - q0));
                return 0.5 * q1 * dtPos;
            }
        }

        /// <summary>
        /// 计算线性段 q(t) > 0 的持续时间。
        /// 这里 q = overpressure - threshold。
        /// </summary>
        private static double ComputePositiveDurationLinear(float q0, float q1, float dt)
        {
            if (dt <= 0f)
                return 0.0;

            bool pos0 = q0 >= 0f;
            bool pos1 = q1 >= 0f;

            if (pos0 && pos1)
                return dt;

            if (!pos0 && !pos1)
                return 0.0;

            float absDenom = Math.Abs(q1 - q0);
            if (absDenom < 1e-20f)
                return 0.0;

            if (pos0 && !pos1)
            {
                return dt * (q0 / (q0 - q1));
            }

            // !pos0 && pos1
            return dt * (q1 / (q1 - q0));
        }

        /// <summary>
        /// 从 trhist 文件读取真实时间轴
        /// </summary>
        private static float[] ReadTimeAxisFromTrhist(string trhistPath, int expectedTimeSteps)
        {
            if (!File.Exists(trhistPath))
                throw new FileNotFoundException("找不到 trhist 文件", trhistPath);

            var times = new List<float>();

            using var sr = new StreamReader(trhistPath);

            sr.ReadLine();
            string? line2 = sr.ReadLine();
            if (line2 == null)
                throw new InvalidOperationException("trhist 文件头损坏。");

            var parts = line2.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                throw new InvalidOperationException("无法解析 trhist 的节点数和变量数。");

            int numNodes = int.Parse(parts[0], CultureInfo.InvariantCulture);

            sr.ReadLine();
            sr.ReadLine();
            sr.ReadLine();

            while (!sr.EndOfStream)
            {
                string? line = sr.ReadLine();
                if (line == null) break;

                line = line.Trim();
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                if (!float.TryParse(line, NumberStyles.Float, CultureInfo.InvariantCulture, out float t))
                    break;

                times.Add(t);

                for (int i = 0; i < numNodes * 3; i++)
                {
                    if (sr.EndOfStream)
                        throw new InvalidOperationException("trhist 文件在时间步数据中意外结束。");
                    sr.ReadLine();
                }
            }

            if (times.Count != expectedTimeSteps)
            {
                throw new InvalidOperationException(
                    $"从 trhist 读出的时间步数 {times.Count} 与 features 中的时间步数 {expectedTimeSteps} 不一致。");
            }

            return times.ToArray();
        }
    }

    public sealed class EngineeringLabels
    {
        public required float[] Time { get; init; }
        public required float[] PeakOverpressure { get; init; }
        public required float[] ArrivalTime { get; init; }
        public required float[] PositiveImpulse { get; init; }
        public required float[] PositiveDuration { get; init; }
        public required EngineeringLabelMetadata Metadata { get; init; }
    }

    public sealed class EngineeringLabelMetadata
    {
        public required int PressureDimIndex { get; init; }
        public required float ArrivalThreshold { get; init; }
        public required string PeakOverpressureDefinition { get; init; }
        public required string ArrivalTimeDefinition { get; init; }
        public required string PositiveImpulseDefinition { get; init; }
        public required string PositiveDurationDefinition { get; init; }
        public required string PositivePhaseRule { get; init; }
        public required string TimeSource { get; init; }
    }
}