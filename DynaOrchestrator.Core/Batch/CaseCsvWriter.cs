using System.Text;

namespace DynaOrchestrator.Core.Batch
{
    public static class CaseCsvWriter
    {
        private static readonly object CsvUpdateLock = new object();

        public static void EnsureStatusColumns(string csvPath)
        {
            lock (CsvUpdateLock)
            {
                var table = ReadTable(csvPath);
                bool changed = false;

                changed |= EnsureColumn(table, "Completed", "0");
                changed |= EnsureColumn(table, "Status", "Pending");
                changed |= EnsureColumn(table, "LastRunTime", string.Empty);

                if (changed)
                    WriteTable(csvPath, table);
            }
        }

        public static void MarkRunning(string csvPath, string caseId)
        {
            UpdateRecord(csvPath, caseId, "0", "Running");
        }

        public static void MarkSuccess(string csvPath, string caseId)
        {
            UpdateRecord(csvPath, caseId, "1", "Success");
        }

        public static void MarkFailed(string csvPath, string caseId)
        {
            UpdateRecord(csvPath, caseId, "0", "Failed");
        }

        public static void MarkCanceled(string csvPath, string caseId)
        {
            UpdateRecord(csvPath, caseId, "0", "Canceled");
        }

        private static void UpdateRecord(string csvPath, string caseId, string completed, string status)
        {
            lock (CsvUpdateLock)
            {
                var table = ReadTable(csvPath);

                EnsureColumn(table, "Completed", "0");
                EnsureColumn(table, "Status", "Pending");
                EnsureColumn(table, "LastRunTime", string.Empty);

                int caseIdIndex = GetRequiredColumnIndex(table.Headers, "CaseId");
                int completedIndex = GetRequiredColumnIndex(table.Headers, "Completed");
                int statusIndex = GetRequiredColumnIndex(table.Headers, "Status");
                int lastRunTimeIndex = GetRequiredColumnIndex(table.Headers, "LastRunTime");

                var row = table.Rows.FirstOrDefault(r =>
                    string.Equals(GetCell(r, caseIdIndex), caseId, StringComparison.OrdinalIgnoreCase));

                if (row == null)
                    throw new Exception($"CSV 中未找到待更新 CaseId: {caseId}");

                SetCell(row, completedIndex, completed);
                SetCell(row, statusIndex, status);
                SetCell(row, lastRunTimeIndex, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

                WriteTable(csvPath, table);
            }
        }

        private static CsvTable ReadTable(string csvPath)
        {
            if (!File.Exists(csvPath))
                throw new FileNotFoundException($"未找到 CSV 文件: {csvPath}");

            var lines = File.ReadAllLines(csvPath);
            if (lines.Length == 0)
                throw new Exception("CSV 文件为空。");

            int headerLineIndex = FindFirstNonEmptyLine(lines);
            if (headerLineIndex < 0)
                throw new Exception("CSV 中未找到有效表头。");

            var headers = SplitCsvLine(lines[headerLineIndex]);
            if (headers.Count == 0)
                throw new Exception("CSV 表头解析失败。");

            var rows = new List<List<string>>();
            for (int i = headerLineIndex + 1; i < lines.Length; i++)
            {
                string raw = lines[i];
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                var row = SplitCsvLine(raw);
                while (row.Count < headers.Count)
                    row.Add(string.Empty);

                rows.Add(row);
            }

            return new CsvTable
            {
                Headers = headers,
                Rows = rows
            };
        }

        private static void WriteTable(string csvPath, CsvTable table)
        {
            var lines = new List<string>
            {
                string.Join(",", table.Headers.Select(EscapeCsv))
            };

            foreach (var row in table.Rows)
            {
                while (row.Count < table.Headers.Count)
                    row.Add(string.Empty);

                lines.Add(string.Join(",", row.Take(table.Headers.Count).Select(EscapeCsv)));
            }

            File.WriteAllLines(csvPath, lines, Encoding.UTF8);
        }

        private static bool EnsureColumn(CsvTable table, string columnName, string defaultValue)
        {
            if (table.Headers.Any(h => string.Equals(h, columnName, StringComparison.OrdinalIgnoreCase)))
                return false;

            table.Headers.Add(columnName);
            foreach (var row in table.Rows)
                row.Add(defaultValue);

            return true;
        }

        private static int GetRequiredColumnIndex(List<string> headers, string columnName)
        {
            for (int i = 0; i < headers.Count; i++)
            {
                if (string.Equals(headers[i], columnName, StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            throw new Exception($"CSV 缺少字段: {columnName}");
        }

        private static string GetCell(List<string> row, int index)
        {
            return index >= 0 && index < row.Count ? row[index] : string.Empty;
        }

        private static void SetCell(List<string> row, int index, string value)
        {
            while (row.Count <= index)
                row.Add(string.Empty);

            row[index] = value;
        }

        private static int FindFirstNonEmptyLine(string[] lines)
        {
            for (int i = 0; i < lines.Length; i++)
            {
                if (!string.IsNullOrWhiteSpace(lines[i]))
                    return i;
            }
            return -1;
        }

        private static List<string> SplitCsvLine(string line)
        {
            var result = new List<string>();
            bool inQuotes = false;
            var current = new StringBuilder();

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];

                if (c == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else if (c == ',' && !inQuotes)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }

            result.Add(current.ToString());
            return result;
        }

        private static string EscapeCsv(string? value)
        {
            value ??= string.Empty;
            bool mustQuote = value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r');
            if (!mustQuote)
                return value;

            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private sealed class CsvTable
        {
            public List<string> Headers { get; set; } = new();
            public List<List<string>> Rows { get; set; } = new();
        }
    }
}