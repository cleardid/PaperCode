using System;

namespace DynaOrchestrator.Core.Batch
{
    /// <summary>
    /// 单个工况在批处理中的运行结果。
    /// 用于后续生成 run_summary.csv / success_cases.csv / failed_cases.csv。
    /// </summary>
    public sealed class BatchRunResult
    {
        /// <summary>
        /// 工况 ID
        /// </summary>
        public string CaseId { get; set; } = string.Empty;

        /// <summary>
        /// 几何类型
        /// </summary>
        public string GeomType { get; set; } = string.Empty;

        /// <summary>
        /// 数据阶段，例如 pilot_54
        /// </summary>
        public string DatasetStage { get; set; } = string.Empty;

        /// <summary>
        /// 运行状态：success / failed
        /// </summary>
        public string Status { get; set; } = "unknown";

        /// <summary>
        /// 错误信息或补充说明
        /// </summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// 输入目录绝对路径
        /// </summary>
        public string InputDir { get; set; } = string.Empty;

        /// <summary>
        /// 输出目录绝对路径
        /// </summary>
        public string OutputDir { get; set; } = string.Empty;

        /// <summary>
        /// 程序记录的开始时间
        /// </summary>
        public DateTime StartTime { get; set; }

        /// <summary>
        /// 程序记录的结束时间
        /// </summary>
        public DateTime EndTime { get; set; }

        /// <summary>
        /// 本工况总耗时，单位秒
        /// </summary>
        public double DurationSeconds => (EndTime - StartTime).TotalSeconds;

        /// <summary>
        /// 预留字段：若后续需要记录仿真返回码，可直接使用。
        /// 当前阶段不强依赖。
        /// </summary>
        public int ExitCode { get; set; } = 0;
    }
}