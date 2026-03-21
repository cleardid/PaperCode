using System;
using System.IO;

namespace DynaOrchestrator.Core.Batch
{
    /// <summary>
    /// 单个工况对应的标准目录与文件路径定义。
    /// 目录规划：
    /// 1. input/  : 基础输入文件与自动生成的 config.json
    /// 2. run/    : LS-DYNA 实际运行目录，存放 model_out.k、trhist 及其他原始求解输出
    /// 3. output/ : 后处理最终结果目录，存放 npz、quality_report、case_metadata 等
    /// </summary>
    public sealed class BatchCasePaths
    {
        /// <summary>
        /// 批处理根目录，例如 D:\Project\BatchRoot
        /// </summary>
        public string BatchRoot { get; }

        /// <summary>
        /// 数据阶段，例如 pilot_54
        /// </summary>
        public string DatasetStage { get; }

        /// <summary>
        /// 工况 ID，例如 G1_P1_C1
        /// </summary>
        public string CaseId { get; }

        /// <summary>
        /// base_models/<GeomType> 目录
        /// </summary>
        public string BaseModelDir { get; }

        /// <summary>
        /// 原始基础 K 文件路径
        /// </summary>
        public string SourceBaseKFile { get; }

        /// <summary>
        /// 原始基础 STL 文件路径
        /// </summary>
        public string SourceStlFile { get; }

        /// <summary>
        /// runs/<DatasetStage>/<CaseId>/
        /// </summary>
        public string CaseRootDir { get; }

        /// <summary>
        /// runs/<DatasetStage>/<CaseId>/input/
        /// </summary>
        public string InputDir { get; }

        /// <summary>
        /// runs/<DatasetStage>/<CaseId>/run/
        /// LS-DYNA 的实际运行目录
        /// </summary>
        public string RunDir { get; }

        /// <summary>
        /// runs/<DatasetStage>/<CaseId>/output/
        /// 后处理最终结果目录
        /// </summary>
        public string OutputDir { get; }

        /// <summary>
        /// 复制后的 input/base.k
        /// </summary>
        public string LocalBaseKFile { get; }

        /// <summary>
        /// 复制后的 input/room.stl
        /// </summary>
        public string LocalStlFile { get; }

        /// <summary>
        /// 自动生成的 input/config.json
        /// </summary>
        public string LocalConfigFile { get; }

        /// <summary>
        /// 前处理输出的 run/model_out.k
        /// 注意：该文件是最终送入 LS-DYNA 的 K 文件
        /// </summary>
        public string LocalOutputKFile { get; }

        /// <summary>
        /// LS-DYNA 输出的 trhist 最终目标路径：run/trhist
        /// 注意：
        /// 当前系统中，通常通过 OutputKFile 所在目录 + TrhistFile 文件名
        /// 来确定实际 trhist 输出位置，因此这里仅作为标准目标路径定义。
        /// </summary>
        public string LocalTrhistFile { get; }

        /// <summary>
        /// 最终输出的 NPZ 文件：output/dataset_01.npz
        /// </summary>
        public string LocalNpzFile { get; }

        /// <summary>
        /// 可选：单工况元信息文件
        /// </summary>
        public string LocalCaseMetadataFile { get; }

        /// <summary>
        /// 可选：单工况质量报告文件
        /// </summary>
        public string LocalQualityReportFile { get; }

        public BatchCasePaths(string batchRoot, BatchCaseRecord record)
        {
            if (string.IsNullOrWhiteSpace(batchRoot))
                throw new ArgumentException("batchRoot 不能为空。", nameof(batchRoot));

            if (record == null)
                throw new ArgumentNullException(nameof(record));

            BatchRoot = Path.GetFullPath(batchRoot);
            DatasetStage = record.DatasetStage;
            CaseId = record.CaseId;

            BaseModelDir = Path.Combine(BatchRoot, "base_models", record.BaseModelKey);
            SourceBaseKFile = Path.Combine(BaseModelDir, "base.k");
            SourceStlFile = Path.Combine(BaseModelDir, "room.stl");

            CaseRootDir = Path.Combine(BatchRoot, "runs", DatasetStage, CaseId);

            InputDir = Path.Combine(CaseRootDir, "input");
            RunDir = Path.Combine(CaseRootDir, "run");
            OutputDir = Path.Combine(CaseRootDir, "output");

            LocalBaseKFile = Path.Combine(InputDir, "base.k");
            LocalStlFile = Path.Combine(InputDir, "room.stl");
            LocalConfigFile = Path.Combine(InputDir, "config.json");

            LocalOutputKFile = Path.Combine(RunDir, "model_out.k");
            LocalTrhistFile = Path.Combine(RunDir, "trhist");

            LocalNpzFile = Path.Combine(OutputDir, "dataset_01.npz");
            LocalCaseMetadataFile = Path.Combine(OutputDir, "case_metadata.json");
            LocalQualityReportFile = Path.Combine(OutputDir, "quality_report.json");
        }

        /// <summary>
        /// 创建 case 所需目录。
        /// </summary>
        public void EnsureDirectories()
        {
            Directory.CreateDirectory(CaseRootDir);
            Directory.CreateDirectory(InputDir);
            Directory.CreateDirectory(RunDir);
            Directory.CreateDirectory(OutputDir);
        }

        /// <summary>
        /// 检查基础模型文件是否存在。
        /// </summary>
        public void ValidateBaseModelFiles()
        {
            if (!File.Exists(SourceBaseKFile))
                throw new FileNotFoundException($"未找到基础 K 文件: {SourceBaseKFile}");

            if (!File.Exists(SourceStlFile))
                throw new FileNotFoundException($"未找到基础 STL 文件: {SourceStlFile}");
        }

        /// <summary>
        /// 复制 base_models 中的基础文件到当前 case 的 input 目录。
        /// overwrite=true，便于重复调试同一 case。
        /// </summary>
        public void CopyBaseFilesToInput(bool overwrite = true)
        {
            ValidateBaseModelFiles();
            EnsureDirectories();

            File.Copy(SourceBaseKFile, LocalBaseKFile, overwrite);
            File.Copy(SourceStlFile, LocalStlFile, overwrite);
        }
    }
}