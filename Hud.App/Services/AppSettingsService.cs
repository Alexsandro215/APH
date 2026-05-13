using System.IO;
using System.Text.Json;

namespace Hud.App.Services
{
    public sealed class AppSettings
    {
        public string? PokerStarsHandHistoryFolder { get; set; }
        public Dictionary<string, string> PokerRoomFolders { get; set; } = new();
        public string SelectedPokerRoom { get; set; } = "PokerStars";
        public string Language { get; set; } = "Espanol";
        public string Palette { get; set; } = ThemePaletteManager.DefaultPaletteKey;
        public string? ReportsFolder { get; set; }
        public bool ProtectReportsWithPassword { get; set; }
        public string? ReportPasswordSalt { get; set; }
        public string? ReportPasswordHash { get; set; }
        public string? EncryptedReportPassword { get; set; }
        public string? LocalAppPasswordSalt { get; set; }
        public string? LocalAppPasswordHash { get; set; }
        public string? GoogleAccountEmail { get; set; }
        public bool CloudSyncEnabled { get; set; }
        public bool GoogleSyncEnabled { get; set; }
        public bool EmailSyncEnabled { get; set; }
        public string? LastIrtSession { get; set; }
    }

    public static class AppSettingsService
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        public static string SettingsDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HudPoker");

        public static string SettingsPath =>
            Path.Combine(SettingsDirectory, "appsettings.json");

        public static AppSettings Load()
        {
            try
            {
                if (!File.Exists(SettingsPath))
                    return new AppSettings();

                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
                Normalize(settings);
                return settings;
            }
            catch
            {
                var settings = new AppSettings();
                Normalize(settings);
                return settings;
            }
        }

        public static void Save(AppSettings settings)
        {
            try
            {
                Directory.CreateDirectory(SettingsDirectory);
                var json = JsonSerializer.Serialize(settings, JsonOptions);
                File.WriteAllText(SettingsPath, json);
            }
            catch
            {
                // Settings must never block the app.
            }
        }

        private static void Normalize(AppSettings settings)
        {
            settings.PokerRoomFolders ??= new Dictionary<string, string>();

            if (string.IsNullOrWhiteSpace(settings.SelectedPokerRoom))
                settings.SelectedPokerRoom = "PokerStars";

            if (!string.IsNullOrWhiteSpace(settings.PokerStarsHandHistoryFolder) &&
                !settings.PokerRoomFolders.ContainsKey("PokerStars"))
            {
                settings.PokerRoomFolders["PokerStars"] = settings.PokerStarsHandHistoryFolder;
            }
        }
    }
}

