
namespace DynaOrchestrator.Core.Models
{
    public class PipelineConfig
    {
        public string BaseKFile { get; set; } = "base.k";
        public string StlFile { get; set; } = "room.stl";
        public string OutputKFile { get; set; } = "model_out.k";
        public string TrhistFile { get; set; } = "trhist";
        public string NpzOutputFile { get; set; } = "dataset_01.npz";

        // LS-DYNA 求解器路径
        public string LsDynaPath { get; set; } = @"D:\Program Files\ANSYS Inc\v231\ansys\bin\winx64\lsdyna_dp.exe";

        /// <summary>
        /// 阶段 1：是否执行几何解析与网格生成
        /// </summary>
        public bool EnablePreProcessing { get; set; } = true;

        /// <summary>
        /// 阶段 2：是否启动 LS-DYNA 求解器
        /// </summary>
        public bool EnableSimulation { get; set; } = true;

        /// <summary>
        /// 阶段 3：是否执行 C++ 引擎图提取与 NPZ 序列化
        /// </summary>
        public bool EnablePostProcessing { get; set; } = true;
    }
}
