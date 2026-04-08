using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DynaOrchestrator.Core.Batch
{
    /// <summary>
    /// 批处理中的单条工况记录。
    /// 该对象直接对应 cases/*.csv 中的一行。
    /// </summary>
    public sealed class BatchCaseRecord : INotifyPropertyChanged
    {
        /// <summary>
        /// 工况唯一标识，例如 G1_P1_C1
        /// </summary>
        public string CaseId { get; set; } = string.Empty;

        /// <summary>
        /// 数据阶段，例如 train
        /// </summary>
        public string DatasetStage { get; set; } = string.Empty;

        /// <summary>
        /// 几何类型，例如 G1 / G2 / G3
        /// </summary>
        public string GeomType { get; set; } = string.Empty;

        /// <summary>
        /// 房间长度，单位 m
        /// </summary>
        public double L { get; set; }

        /// <summary>
        /// 房间宽度，单位 m
        /// </summary>
        public double W { get; set; }

        /// <summary>
        /// 房间高度，单位 m
        /// </summary>
        public double H { get; set; }

        /// <summary>
        /// 爆点位置类型，例如 center / near_x_wall
        /// </summary>
        public string PositionType { get; set; } = string.Empty;

        /// <summary>
        /// 爆点坐标 x，单位 mm
        /// </summary>
        public double X { get; set; }

        /// <summary>
        /// 爆点坐标 y，单位 mm
        /// </summary>
        public double Y { get; set; }

        /// <summary>
        /// 爆点坐标 z，单位 mm
        /// </summary>
        public double Z { get; set; }

        /// <summary>
        /// 装药等级，例如 C1 / C2 / C3
        /// </summary>
        public string ChargeLevel { get; set; } = string.Empty;

        /// <summary>
        /// 装药密度 kg/m3
        /// 用于计算装药质量，用于计算爆炸参数中的半径
        /// </summary>
        public double ChargeDensity { get; set; }

        /// <summary>
        /// 装药质量，单位 kg
        /// </summary>
        public double ChargeMass { get; set; }

        // 断点续跑状态列
        // --- 运行时动态状态属性 ---
        private string _completed = "0";
        public string Completed
        {
            get => _completed;
            set { if (_completed != value) { _completed = value; OnPropertyChanged(); } }
        }

        private string _status = "Pending";
        public string Status
        {
            get => _status;
            set { if (_status != value) { _status = value; OnPropertyChanged(); } }
        }

        private string _lastRunTime = string.Empty;
        public string LastRunTime
        {
            get => _lastRunTime;
            set { if (_lastRunTime != value) { _lastRunTime = value; OnPropertyChanged(); } }
        }

        /// <summary>
        /// 是否已经完成
        /// </summary>
        public bool IsCompleted =>
            string.Equals((Completed ?? string.Empty).Trim(), "1", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 用于 UI 界面显示位置信息
        /// </summary>
        public string AbsPositionDisplay => $"({X:F2}, {Y:F2}, {Z:F2}) mm";

        /// <summary>
        /// 当前工况对应的基础模型目录名。
        /// 当前规则下直接使用 GeomType 映射 base_models/G1、G2、G3。
        /// </summary>
        public string BaseModelKey => GeomType;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// 用于日志输出的简要描述。
        /// </summary>
        public override string ToString()
        {
            return $"{CaseId} | {GeomType} | Pos=({X:F3},{Y:F3},{Z:F3}) | " +
                   $"Abs=({X:F2}, {Y:F2}, {Z:F2}) mm | Density={ChargeDensity:F4} kg/m3 | Charge={ChargeLevel}/{ChargeMass:F4} kg | " +
                   $"Completed={Completed} | Status={Status}";
        }
    }
}