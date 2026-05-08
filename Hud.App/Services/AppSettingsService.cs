using System.IO;
using System.Text.Json;

namespace Hud.App.Services
{
    public sealed class AppSettings
    {
        public string? PokerStarsHandHistoryFolder { get; set; }
        public string Language { get; set; } = "Espanol";
        public string Palette { get; set; } = ThemePaletteManager.DefaultPaletteKey;
        public bool CloudSyncEnabled { get; set; }
        public bool GoogleSyncEnabled { get; set; }
        public bool EmailSyncEnabled { get; set; }
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
                return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
            catch
            {
                return new AppSettings();
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
    }
}

