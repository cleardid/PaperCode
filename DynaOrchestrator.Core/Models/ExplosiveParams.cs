
namespace DynaOrchestrator.Core.Models
{
    /// <summary>
    /// 从配置文件读取的爆炸参数，单位为 mm 和 kg
    /// </summary>
    public class ExplosiveParams
    {
        public double Xc { get; set; } // 爆心 X (mm)
        public double Yc { get; set; } // 爆心 Y (mm)
        public double Zc { get; set; } // 爆心 Z (mm)
        public double Radius { get; set; } // 用于几何自适应网格生成的初始半径 单位 mm
        public double W { get; set; }      // TNT 当量 (kg)，用于物理特征注入
    }
}
