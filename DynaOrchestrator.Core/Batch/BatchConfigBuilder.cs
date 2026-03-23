using System;
using System.IO;
using System.Text.Json;
using DynaOrchestrator.Core.Models;

namespace DynaOrchestrator.Core.Batch
{
    /// <summary>
    /// 根据基础 AppConfig 与单条工况记录，构建该 case 的专属配置。
    /// 注意：
    /// 1. AppConfig.Explosive 的坐标单位是 mm；
    /// 2. AppConfig.Other 仍保持原始配置单位（mm / kg），不要在这里调用 NormalizeUnits；
    /// 3. NormalizeUnits 仍应只在现有主流程真正执行前调用一次；
    /// 4. TrhistFile 继续保持为“文件名”，由 RunSimulation 按 OutputKFile 所在目录拼接，
    ///    从而保证 trhist 与 model_out.k 位于同一 run 目录。
    /// </summary>
    public static class BatchConfigBuilder
    {
        /// <summary>
        /// 根据基础配置和 case 信息构建派生配置。
        /// </summary>
        /// <param name="baseConfig">基础配置（原始单位）</param>
        /// <param name="record">单条工况记录，绝对坐标单位为 m</param>
        /// <param name="paths">该 case 的标准路径</param>
        /// <param name="ncpuPerCase">单工况分配 CPU 数</param>
        /// <param name="memoryPerCase">单工况分配内存，例如 200m</param>
        /// <returns>派生后的 AppConfig</returns>
        public static AppConfig Build(
            AppConfig baseConfig,
            BatchCaseRecord record,
            BatchCasePaths paths,
            int ncpuPerCase,
            string memoryPerCase,
            Action<string> logger)
        {
            if (baseConfig == null)
                throw new ArgumentNullException(nameof(baseConfig));
            if (record == null)
                throw new ArgumentNullException(nameof(record));
            if (paths == null)
                throw new ArgumentNullException(nameof(paths));

            // 深拷贝，避免多个 case 共用同一配置实例
            AppConfig config = DeepClone(baseConfig);

            // ---------------- PipelineConfig ----------------
            config.Pipeline.BaseKFile = paths.LocalBaseKFile;
            config.Pipeline.StlFile = paths.LocalStlFile;

            // 前处理生成的真正求解 K 文件，放在 run/ 目录
            config.Pipeline.OutputKFile = paths.LocalOutputKFile;

            // 保持原有语义：这里只写文件名，由 RunSimulation 使用 OutputKFile 所在目录拼接
            // 最终落到 run/trhist
            config.Pipeline.TrhistFile = "trhist";

            // 最终后处理结果输出到 output/ 目录
            config.Pipeline.NpzOutputFile = paths.LocalNpzFile;

            // 单工况资源控制：由外层批处理统一设定
            config.Pipeline.Ncpu = ncpuPerCase;
            config.Pipeline.Memory = memoryPerCase;

            // 样本标识
            config.Pipeline.CaseId = record.CaseId;
            config.Pipeline.DatasetVersion = record.DatasetStage;

            // LsDynaPath 保持基础配置不变

            // ---------------- ExplosiveParams ----------------
            config.Explosive.Xc = record.X;
            config.Explosive.Yc = record.Y;
            config.Explosive.Zc = record.Z;

            // W 单位 kg
            config.Explosive.W = record.ChargeMass;

            // 根据 ChargeMass 和 ChargeDensity 显式推导 Radius(mm)
            config.Explosive.Radius = CalculateRadiusMm(record.ChargeMass, record.ChargeDensity);

            logger?.Invoke($"计算的爆炸半径为 {config.Explosive.Radius} mm。");

            // ---------------- OtherConfig ----------------
            // 直接深拷贝原始配置，不做单位换算。
            // 统一由现有执行主流程中的 NormalizeUnits() 处理一次。
            config.Other = DeepCloneOther(baseConfig.Other);

            return config;
        }

        /// <summary>
        /// 根据装药质量和装药密度计算爆炸半径，单位 mm。
        /// </summary>
        /// <param name="chargeMass">装药质量，单位 kg</param>
        /// <param name="chargeDensity">装药密度，单位 kg/m3</param>
        /// <returns>半径，单位 mm</returns>
        private static double CalculateRadiusMm(double chargeMass, double chargeDensity)
        {
            return Math.Pow(3 * chargeMass / (4 * Math.PI * chargeDensity), 1.0 / 3) * 1000;
        }

        /// <summary>
        /// 将派生后的 AppConfig 写入 input/config.json。
        /// </summary>
        public static void WriteConfig(AppConfig config, string configPath)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));
            if (string.IsNullOrWhiteSpace(configPath))
                throw new ArgumentException("configPath 不能为空。", nameof(configPath));

            string? dir = Path.GetDirectoryName(configPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            var options = new JsonSerializerOptions
            {
                WriteIndented = true
            };

            string json = JsonSerializer.Serialize(config, options);
            File.WriteAllText(configPath, json);
        }

        /// <summary>
        /// 深拷贝 AppConfig。
        /// </summary>
        private static AppConfig DeepClone(AppConfig config)
        {
            var options = new JsonSerializerOptions();
            string json = JsonSerializer.Serialize(config, options);

            return JsonSerializer.Deserialize<AppConfig>(json, options)
                   ?? throw new Exception("AppConfig 深拷贝失败。");
        }

        /// <summary>
        /// 单独深拷贝 OtherConfig，主要为了强调其原始单位口径保持不变。
        /// </summary>
        private static OtherConfig DeepCloneOther(OtherConfig other)
        {
            var options = new JsonSerializerOptions();
            string json = JsonSerializer.Serialize(other, options);

            return JsonSerializer.Deserialize<OtherConfig>(json, options)
                   ?? throw new Exception("OtherConfig 深拷贝失败。");
        }
    }
}