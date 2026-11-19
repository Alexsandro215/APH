using System.IO;
using System.Text.Json;
using System.Windows.Media;

namespace Hud.App.Services
{
    /// <summary>Nota y color de un jugador ("farolea en el river", rojo).</summary>
    public sealed record PlayerNote(string Note, string Color);

    /// <summary>
    /// Notas y etiquetas de color por jugador, guardadas en %APPDATA%\APH\player-notes.json. Se ven en
    /// su cuadro del HUD de la mesa y se editan desde Tiempo real (boton "Nota" de cada jugador).
    /// </summary>
    public static class PlayerNotes
    {
        /// <summary>Colores de etiqueta: clave guardada y color en pantalla.</summary>
        public static readonly (string Key, string LabelKey, Color Color)[] Palette =
        {
            ("red", "Notes.Red", Color.FromRgb(220, 64, 74)),
            ("orange", "Notes.Orange", Color.FromRgb(232, 130, 40)),
            ("yellow", "Notes.Yellow", Color.FromRgb(230, 196, 50)),
            ("green", "Notes.Green", Color.FromRgb(56, 168, 92)),
            ("blue", "Notes.Blue", Color.FromRgb(60, 132, 214)),
            ("purple", "Notes.Purple", Color.FromRgb(150, 92, 210)),
        };

        private static readonly object Sync = new();
        private static Dictionary<string, PlayerNote>? _notes;

        /// <summary>Sube con cada cambio (el HUD redibuja los cuadros).</summary>
        public static int Version { get; private set; }

        public static string FilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "APH", "player-notes.json");

        public static PlayerNote? Get(string name)
        {
            lock (Sync)
                return Notes().TryGetValue(Key(name), out var note) ? note : null;
        }

        /// <summary>Guarda la nota y el color (los dos vacios borran al jugador).</summary>
        public static void Set(string name, string note, string color)
        {
            lock (Sync)
            {
                var notes = Notes();
                note = note.Trim();
                if (note.Length == 0 && string.IsNullOrWhiteSpace(color))
                    notes.Remove(Key(name));
                else
                    notes[Key(name)] = new PlayerNote(note, color ?? "");
                Version++;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                    File.WriteAllText(FilePath, JsonSerializer.Serialize(notes, new JsonSerializerOptions { WriteIndented = true }));
                }
                catch (Exception ex)
                {
                    AppLog.Warn("No se pudieron guardar las notas de jugadores", ex);
                }
            }
        }

        public static Color? ColorOf(string? key)
        {
            foreach (var item in Palette)
                if (item.Key == key)
                    return item.Color;
            return null;
        }

        private static string Key(string name) => PokerStarsHandHistory.NormalizeName(name).Trim();

        private static Dictionary<string, PlayerNote> Notes()
        {
            if (_notes is not null)
                return _notes;
            try
            {
                if (File.Exists(FilePath))
                    _notes = JsonSerializer.Deserialize<Dictionary<string, PlayerNote>>(File.ReadAllText(FilePath));
            }
            catch (Exception ex)
            {
                AppLog.Warn("No se pudieron leer las notas de jugadores", ex);
            }
            _notes ??= new Dictionary<string, PlayerNote>(StringComparer.Ordinal);
            return _notes;
        }
    }
}
