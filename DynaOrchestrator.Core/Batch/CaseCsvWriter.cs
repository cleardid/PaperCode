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
                var records = CaseCsvReader.Read(csvPath);
                WriteAll(csvPath, records);
            }
        }

        public static void MarkRunning(string csvPath, string caseId)
        {
            UpdateRecord(csvPath, caseId, r =>
            {
                r.Completed = "0";
                r.Status = "Running";
                r.LastRunTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            });
        }

        public static void MarkSuccess(string csvPath, string caseId)
        {
            UpdateRecord(csvPath, caseId, r =>
            {
                r.Completed = "1";
                r.Status = "Success";
                r.LastRunTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            });
        }

        public static void MarkFailed(string csvPath, string caseId)
        {
            UpdateRecord(csvPath, caseId, r =>
            {
                r.Completed = "0";
                r.Status = "Failed";
                r.LastRunTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            });
        }

        private static void UpdateRecord(string csvPath, string caseId, Action<BatchCaseRecord> updater)
        {
            lock (CsvUpdateLock)
            {
                var records = CaseCsvReader.Read(csvPath);
                var record = records.FirstOrDefault(r => string.Equals(r.CaseId, caseId, StringComparison.OrdinalIgnoreCase));

                if (record == null)
                    throw new Exception($"CSV 中未找到待更新 CaseId: {caseId}");

                updater(record);
                WriteAll(csvPath, records);
            }
        }

        private static void WriteAll(string csvPath, List<BatchCaseRecord> records)
        {
            var lines = new List<string>
            {
                "CaseId,GeomType,L,W,H,PositionType,X,Y,Z,ChargeLevel,ChargeMass,ChargeDensity,DatasetStage,Completed,Status,LastRunTime"
            };

            foreach (var r in records)
            {
                lines.Add(string.Join(",",
                    EscapeCsv(r.CaseId),
                    EscapeCsv(r.GeomType),
                    EscapeCsv(r.L.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    EscapeCsv(r.W.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    EscapeCsv(r.H.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    EscapeCsv(r.PositionType),
                    EscapeCsv(r.X.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    EscapeCsv(r.Y.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    EscapeCsv(r.Z.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    EscapeCsv(r.ChargeLevel),
                    EscapeCsv(r.ChargeMass.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    EscapeCsv(r.ChargeDensity.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    EscapeCsv(r.DatasetStage),
                    EscapeCsv(r.Completed),
                    EscapeCsv(r.Status),
                    EscapeCsv(r.LastRunTime)));
            }

            File.WriteAllLines(csvPath, lines, Encoding.UTF8);
        }

        private static string EscapeCsv(string? value)
        {
            value ??= string.Empty;
            bool mustQuote = value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r');
            if (!mustQuote)
                return value;

            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }
}