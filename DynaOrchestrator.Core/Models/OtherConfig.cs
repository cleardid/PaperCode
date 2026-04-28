
namespace DynaOrchestrator.Core.Models
{
    /// <summary>
    /// 从配置文件读取的其他参数，单位为 mm 和 kg
    /// 需要在程序入口进行单位转换，统一为 m 和 kg
    /// </summary>
    public class OtherConfig
    {
        /// <summary>
        /// 截断半径 单位 m
        /// 用于将房间尺寸均等划分为若干网格
        /// </summary>
        public float Rc { get; set; } = 0.75f;

        /// <summary>
        /// 图边权的空间邻近性启发式衰减系数
        /// 用于构造 graph connectivity prior，不表示物理传播系数
        /// $w = e^{-\alpha \cdot d}$
        /// </summary>
        public float Alpha { get; set; } = 6.0f;

        // ================= 空间网格采样参数 =================
        /// <summary>
        /// 基础体素采样尺寸 (mm)
        /// 用于生成自适应测点
        /// </summary>
        public double DlDense { get; set; } = 50.0;

        /// <summary>
        /// 远场网格稀疏化倍率
        /// 用于生成自适应测点
        /// </summary>
        public int SparseFactor { get; set; } = 4;

        /// <summary>
        /// 爆心致密区半径倍数
        /// 用于生成自适应测点
        /// </summary>
        public double CoreRadiusMultiplier { get; set; } = 4.0;

        /// <summary>
        /// 边界层加密裕度 (mm) 
        /// 用于生成自适应测点 和 提取边界语义先验信息
        /// </summary>
        public double WallMargin { get; set; } = 60.0;

        // ================= 时间流场采样参数 =================
        /// <summary>
        /// LS-DYNA Trhist 数据输出间隔 (s)
        /// </summary>
        public double TrhistDt { get; set; } = 0.00001;

        /// <summary>
        /// 对外接口：从配置文件读取的数值为 mm ，内部转换为 m
        /// </summary>
        public void NormalizeUnits()
        {
            DlDense *= 0.001f; // mm -> m
            WallMargin *= 0.001f; // mm -> m
        }
    }
}
