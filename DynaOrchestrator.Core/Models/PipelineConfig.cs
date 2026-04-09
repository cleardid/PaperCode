
namespace DynaOrchestrator.Core.Models
{
    public class PipelineConfig
    {
        public string BaseKFile { get; set; } = "base.k";
        public string StlFile { get; set; } = "room.stl";
        public string OutputKFile { get; set; } = "model_out.k";
        public string TrhistFile { get; set; } = "trhist";
        public string NpzOutputFile { get; set; } = "dataset_01.npz";

        // LS-DYNA 求解器路径，请根据您的 Windows 实际路径修改
        public string LsDynaPath { get; set; } = @"D:\Program Files\ANSYS Inc\v231\ansys\bin\winx64\lsdyna_dp.exe";

        // 是否执行特征后处理与图构建（默认为 true 保持兼容）
        public bool EnableGraphPostProcessing { get; set; } = true;
    }
}
