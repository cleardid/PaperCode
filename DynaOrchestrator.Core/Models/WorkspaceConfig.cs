namespace DynaOrchestrator.Core.Models
{
    public class WorkspaceConfig
    {
        /// <summary>
        /// 工作区根目录。默认程序所在目录。
        /// </summary>
        public string RootDir { get; set; } = "./experiments";

        /// <summary>
        /// 多任务 CSV 相对路径（相对于 RootDir）。
        /// </summary>
        public string CasesCsv { get; set; } = "cases/cases.csv";

        /// <summary>
        /// 多任务最大并发 case 数。
        /// </summary>
        public int MaxParallelCases { get; set; } = 2;

        /// <summary>
        /// 每个 case 分配给 LS-DYNA 的 CPU 数。
        /// </summary>
        public int NcpuPerCase { get; set; } = 4;

        /// <summary>
        /// 每个 case 分配给 LS-DYNA 的内存参数。
        /// </summary>
        public string MemoryPerCase { get; set; } = "400m";
    }
}