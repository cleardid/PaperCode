
namespace DynaOrchestrator.Core.Models
{
    public class PipelineConfig
    {
        public string BaseKFile { get; set; } = "room_base.k";
        public string StlFile { get; set; } = "1.stl";
        public string OutputKFile { get; set; } = "room_run.k";
        public string TrhistFile { get; set; } = "trhist";
        public string NpzOutputFile { get; set; } = "dataset_01.npz";

        // LS-DYNA 求解器路径，请根据您的 Windows 实际路径修改
        public string LsDynaPath { get; set; } = @"C:\LSDYNA\program\ls-dyna_smp_d_R13_0_winx64.exe";
        public int Ncpu { get; set; } = 8;
        public string Memory { get; set; } = "200m";

        // 样本标识
        public string CaseId { get; set; } = "case_0001";
        // 数据集版本
        public string DatasetVersion { get; set; } = "v1.0";
        
        // 新增：是否执行特征后处理与图构建（默认为 true 保持兼容）
        public bool EnableGraphPostProcessing { get; set; } = true;
    }
}
