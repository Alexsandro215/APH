using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Hud.App.Services
{
    public sealed record ReportSessionRecord(
        int SessionNumber,
        string PdfPath,
        DateTime CreatedAt,
        DateTime? StartedAt,
        DateTime? ClosedAt,
        int TableCount,
        TimeSpan Duration,
        string Hero,
        string ResultLabel,
        int HandCount = 0,
        string HeroStatsLabel = "-",
        IReadOnlyList<string>? TableSourcePaths = null);

    public static class ReportSessionIndexService
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        public static string DefaultReportsFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "APH Reports");

        public static string GetReportsFolder()
        {
            var settings = AppSettingsService.Load();
            return string.IsNullOrWhiteSpace(settings.ReportsFolder)
                ? DefaultReportsFolder
                : settings.ReportsFolder!;
        }

        public static void SaveMetadata(ReportSessionRecord record)
        {
            try
            {
                var metaPath = GetMetadataPath(record.PdfPath);
                var json = JsonSerializer.Serialize(record, JsonOptions);
                File.WriteAllText(metaPath, json);
            }
            catch
            {
                // Metadata should never block report creation.
            }
        }

        public static IReadOnlyList<ReportSessionRecord> LoadSessions()
        {
            var folder = GetReportsFolder();
            if (!Directory.Exists(folder))
                return Array.Empty<ReportSessionRecord>();

            var records = Directory.EnumerateFiles(folder, "*.pdf", SearchOption.TopDirectoryOnly)
                .Select(ReadRecord)
                .OrderByDescending(record => record.CreatedAt)
                .ThenByDescending(record => record.PdfPath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            for (var i = 0; i < records.Count; i++)
                records[i] = records[i] with { SessionNumber = records.Count - i };

            return records;
        }

        private static ReportSessionRecord ReadRecord(string pdfPath)
        {
            var metaPath = GetMetadataPath(pdfPath);
            if (File.Exists(metaPath))
            {
                try
                {
                    var json = File.ReadAllText(metaPath);
                    var record = JsonSerializer.Deserialize<ReportSessionRecord>(json, JsonOptions);
                    if (record is not null)
                        return record with
                        {
                            PdfPath = pdfPath,
                            CreatedAt = File.GetCreationTime(pdfPath)
                        };
                }
                catch { }
            }

            var created = File.GetCreationTime(pdfPath);
            return new ReportSessionRecord(
                0,
                pdfPath,
                created,
                TryParseFromFileName(pdfPath),
                null,
                0,
                TimeSpan.Zero,
                "-",
                "-");
        }

        private static DateTime? TryParseFromFileName(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var marker = "APH_RT_Session_";
            var index = name.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                return null;

            var value = name[(index + marker.Length)..];
            return DateTime.TryParseExact(
                value,
                "yyyyMMdd_HHmmss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date)
                    ? date
                    : null;
        }

        private static string GetMetadataPath(string pdfPath) =>
            Path.ChangeExtension(pdfPath, ".session.json");
    }
}
