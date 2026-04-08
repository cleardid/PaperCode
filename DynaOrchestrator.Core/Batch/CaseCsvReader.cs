using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace DynaOrchestrator.Core.Batch
{
    public static class CaseCsvReader
    {
        public static List<BatchCaseRecord> Read(string csvPath)
        {
            if (string.IsNullOrWhiteSpace(csvPath))
                throw new ArgumentException("CSV 路径不能为空。", nameof(csvPath));

            if (!File.Exists(csvPath))
                throw new FileNotFoundException($"未找到工况 CSV 文件: {csvPath}");

            var lines = File.ReadAllLines(csvPath);
            if (lines.Length == 0)
                throw new Exception("工况 CSV 文件为空。");

            int headerLineIndex = FindFirstNonEmptyLine(lines);
            if (headerLineIndex < 0)
                throw new Exception("工况 CSV 文件中未找到有效表头。");

            string headerLine = lines[headerLineIndex];
            var headers = SplitCsvLine(headerLine);
            if (headers.Count == 0)
                throw new Exception("CSV 表头解析失败。");

            var headerMap = BuildHeaderMap(headers);
            ValidateRequiredHeaders(headerMap, csvPath);

            var result = new List<BatchCaseRecord>();
            var caseIdSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = headerLineIndex + 1; i < lines.Length; i++)
            {
                string rawLine = lines[i];
                if (string.IsNullOrWhiteSpace(rawLine))
                    continue;

                var cells = SplitCsvLine(rawLine);
                if (cells.All(string.IsNullOrWhiteSpace))
                    continue;

                var record = ParseRecord(cells, headerMap, i + 1);

                if (!caseIdSet.Add(record.CaseId))
                    throw new Exception($"CSV 中存在重复 CaseId: {record.CaseId}（第 {i + 1} 行）");

                ValidateRecord(record, i + 1);
                result.Add(record);
            }

            if (result.Count == 0)
                throw new Exception("CSV 中未解析到任何有效工况记录。");

            return result;
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

        private static Dictionary<string, int> BuildHeaderMap(IReadOnlyList<string> headers)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < headers.Count; i++)
            {
                string key = headers[i].Trim();
                if (!string.IsNullOrWhiteSpace(key) && !map.ContainsKey(key))
                    map[key] = i;
            }
            return map;
        }

        private static void ValidateRequiredHeaders(Dictionary<string, int> headerMap, string csvPath)
        {
            string[] requiredHeaders =
            {
                "CaseId",
                "GeomType",
                "L",
                "W",
                "H",
                "PositionType",
                "X",
                "Y",
                "Z",
                "ChargeLevel",
                "ChargeMass",
                "ChargeDensity",
                "DatasetStage"
            };

            foreach (var header in requiredHeaders)
            {
                if (!headerMap.ContainsKey(header))
                    throw new Exception($"CSV 缺少必须字段: {header}。文件: {csvPath}");
            }
        }

        private static BatchCaseRecord ParseRecord(
            IReadOnlyList<string> cells,
            Dictionary<string, int> headerMap,
            int lineNumber)
        {
            return new BatchCaseRecord
            {
                CaseId = GetRequiredString(cells, headerMap, "CaseId", lineNumber),
                GeomType = GetRequiredString(cells, headerMap, "GeomType", lineNumber),
                L = GetRequiredDouble(cells, headerMap, "L", lineNumber),
                W = GetRequiredDouble(cells, headerMap, "W", lineNumber),
                H = GetRequiredDouble(cells, headerMap, "H", lineNumber),
                PositionType = GetRequiredString(cells, headerMap, "PositionType", lineNumber),
                X = GetRequiredDouble(cells, headerMap, "X", lineNumber),
                Y = GetRequiredDouble(cells, headerMap, "Y", lineNumber),
                Z = GetRequiredDouble(cells, headerMap, "Z", lineNumber),
                ChargeLevel = GetRequiredString(cells, headerMap, "ChargeLevel", lineNumber),
                ChargeMass = GetRequiredDouble(cells, headerMap, "ChargeMass", lineNumber),
                ChargeDensity = GetRequiredDouble(cells, headerMap, "ChargeDensity", lineNumber),
                DatasetStage = GetRequiredString(cells, headerMap, "DatasetStage", lineNumber),

                Completed = GetOptionalString(cells, headerMap, "Completed", "0"),
                Status = GetOptionalString(cells, headerMap, "Status", "Pending"),
                LastRunTime = GetOptionalString(cells, headerMap, "LastRunTime", string.Empty)
            };
        }

        private static void ValidateRecord(BatchCaseRecord record, int lineNumber)
        {
            if (string.IsNullOrWhiteSpace(record.CaseId))
                throw new Exception($"第 {lineNumber} 行 CaseId 为空。");

            if (string.IsNullOrWhiteSpace(record.GeomType))
                throw new Exception($"第 {lineNumber} 行 GeomType 为空。");

            if (string.IsNullOrWhiteSpace(record.PositionType))
                throw new Exception($"第 {lineNumber} 行 PositionType 为空。");

            if (string.IsNullOrWhiteSpace(record.ChargeLevel))
                throw new Exception($"第 {lineNumber} 行 ChargeLevel 为空。");

            if (string.IsNullOrWhiteSpace(record.DatasetStage))
                throw new Exception($"第 {lineNumber} 行 DatasetStage 为空。");

            if (record.L <= 0 || record.W <= 0 || record.H <= 0)
                throw new Exception($"第 {lineNumber} 行房间尺寸必须大于 0。");

            if (record.ChargeMass <= 0)
                throw new Exception($"第 {lineNumber} 行 ChargeMass 必须大于 0。");

            double roomLmm = record.L * 1000.0;
            double roomWmm = record.W * 1000.0;
            double roomHmm = record.H * 1000.0;

            // X/Y/Z 始终存储绝对坐标，单位 mm。
            // 这里要求爆点必须位于房间包围盒范围内。
            if (record.X < 0 || record.X > roomLmm ||
                record.Y < 0 || record.Y > roomWmm ||
                record.Z < 0 || record.Z > roomHmm)
            {
                throw new Exception(
                    $"第 {lineNumber} 行绝对坐标超出房间范围。 " +
                    $"当前坐标(mm)=({record.X}, {record.Y}, {record.Z})，" +
                    $"房间范围(mm)=([0,{roomLmm}], [0,{roomWmm}], [0,{roomHmm}])。");
            }
        }

        private static string GetRequiredString(
            IReadOnlyList<string> cells,
            Dictionary<string, int> headerMap,
            string columnName,
            int lineNumber)
        {
            int index = headerMap[columnName];
            if (index < 0 || index >= cells.Count)
                throw new Exception($"第 {lineNumber} 行缺少字段 {columnName}。");

            string value = cells[index].Trim();
            if (string.IsNullOrWhiteSpace(value))
                throw new Exception($"第 {lineNumber} 行字段 {columnName} 为空。");

            return value;
        }

        private static string GetOptionalString(
            IReadOnlyList<string> cells,
            Dictionary<string, int> headerMap,
            string columnName,
            string defaultValue)
        {
            if (!headerMap.TryGetValue(columnName, out int index))
                return defaultValue;

            if (index < 0 || index >= cells.Count)
                return defaultValue;

            string value = cells[index].Trim();
            return string.IsNullOrWhiteSpace(value) ? defaultValue : value;
        }

        private static double GetRequiredDouble(
            IReadOnlyList<string> cells,
            Dictionary<string, int> headerMap,
            string columnName,
            int lineNumber)
        {
            string raw = GetRequiredString(cells, headerMap, columnName, lineNumber);

            if (!double.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double value))
                throw new Exception($"第 {lineNumber} 行字段 {columnName} 不是有效数字: {raw}");

            return value;
        }

        private static List<string> SplitCsvLine(string line)
        {
            var result = new List<string>();
            if (line == null)
                return result;

            bool inQuotes = false;
            var current = new System.Text.StringBuilder();

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
    }
}