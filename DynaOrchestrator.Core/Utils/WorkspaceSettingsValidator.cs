using System.Text.RegularExpressions;

namespace DynaOrchestrator.Core.Utils
{
    /// <summary>
    /// 对批处理资源参数做统一校验与标准化，避免 UI、调度器、求解器各自维护一套口径。
    /// </summary>
    public static class WorkspaceSettingsValidator
    {
        private static readonly Regex MemoryPattern = new(@"^\d+[kKmMgGtT]$", RegexOptions.Compiled);

        public static void ValidateBatchSettings(int maxParallelCases, int ncpuPerCase, string memoryPerCase)
        {
            if (maxParallelCases <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxParallelCases), "MaxParallelCases 必须大于 0。");

            if (ncpuPerCase <= 0)
                throw new ArgumentOutOfRangeException(nameof(ncpuPerCase), "NcpuPerCase 必须大于 0。");

            _ = NormalizeMemoryPerCase(memoryPerCase);
        }

        /// <summary>
        /// 将内存参数标准化为小写紧凑形式，例如 900m。
        /// </summary>
        public static string NormalizeMemoryPerCase(string memoryPerCase)
        {
            if (string.IsNullOrWhiteSpace(memoryPerCase))
                throw new ArgumentException("MemoryPerCase 不能为空，格式应类似 900m。", nameof(memoryPerCase));

            string normalized = memoryPerCase.Trim();

            if (!MemoryPattern.IsMatch(normalized))
                throw new ArgumentException("MemoryPerCase 格式非法，应为类似 900m 的形式。", nameof(memoryPerCase));

            return normalized.ToLowerInvariant();
        }
    }
}