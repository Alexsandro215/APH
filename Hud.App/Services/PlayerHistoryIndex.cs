using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using HandReader.Core.Models;
using HandReader.Core.Parsing;
using HandReader.Core.Stats;

namespace Hud.App.Services
{
    /// <summary>
    /// Lo que el heroe ya jugo con cada rival en sus OTRAS mesas: todos los historiales de la carpeta
    /// de la mesa, menos el de la propia mesa. El HUD sobre la mesa lo suma a la mesa en vivo, que ya
    /// lee su archivo desde el principio: asi ninguna mano se cuenta dos veces. Se refresca en segundo
    /// plano (como mucho una vez por minuto) y solo vuelve a leer los archivos que cambiaron, asi que
    /// las otras mesas abiertas tambien se van sumando durante la sesion.
    /// </summary>
    public static class PlayerHistoryIndex
    {
        private sealed record FileEntry(string Directory, long Length, DateTime WriteUtc, IReadOnlyDictionary<string, PlayerStats> Players);

        private sealed record Merged(int Version, IReadOnlyDictionary<string, PlayerStats> Players);

        private static readonly object Sync = new();
        private static readonly Dictionary<string, FileEntry> Files = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, DateTime> ScannedAt = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> Scanning = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Merged> MergedByTable = new(StringComparer.OrdinalIgnoreCase);
        private static int _version;

        /// <summary>Cada cuanto se vuelve a mirar la carpeta.</summary>
        public static TimeSpan RefreshEvery { get; set; } = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Las estadisticas del jugador en las otras mesas de la carpeta de <paramref name="tablePath"/>
        /// (sin esa mesa), o null si no hay ninguna mano suya. La primera vez lanza la lectura en segundo
        /// plano y devuelve null hasta que termina.
        /// </summary>
        public static PlayerStats? Elsewhere(string? tablePath, string name)
        {
            if (string.IsNullOrWhiteSpace(tablePath) || string.IsNullOrWhiteSpace(name))
                return null;
            var directory = Path.GetDirectoryName(tablePath);
            if (string.IsNullOrWhiteSpace(directory))
                return null;

            RequestScan(directory);
            lock (Sync)
            {
                if (!MergedByTable.TryGetValue(tablePath, out var merged) || merged.Version != _version)
                {
                    merged = new Merged(_version, MergeWithout(directory, tablePath));
                    MergedByTable[tablePath] = merged;
                }

                return merged.Players.TryGetValue(PokerStarsHandHistory.NormalizeName(name), out var stats) ? stats : null;
            }
        }

        /// <summary>Lee ya la carpeta (sin esperar al minuto). Para pruebas y para la primera carga.</summary>
        public static void ScanNow(string directory)
        {
            lock (Sync)
            {
                if (!Scanning.Add(directory))
                    return;
            }

            Scan(directory);
        }

        /// <summary>Olvida todo (pruebas).</summary>
        public static void Clear()
        {
            lock (Sync)
            {
                Files.Clear();
                ScannedAt.Clear();
                MergedByTable.Clear();
                _version++;
            }
        }

        /// <summary>
        /// Color segun las manos que llevas con el jugador, los mismos que la lista de jugadores de
        /// Tiempo real. Con menos de 30 manos no hay color: las estadisticas todavia no dicen mucho.
        /// </summary>
        public static Color? SampleColor(int hands) => hands switch
        {
            >= 10000 => Color.FromRgb(245, 247, 250),
            >= 5000 => Color.FromRgb(109, 40, 217),
            >= 1000 => Color.FromRgb(183, 144, 255),
            >= 500 => Color.FromRgb(215, 198, 0),
            >= 200 => Color.FromRgb(33, 192, 122),
            >= 100 => Color.FromRgb(159, 232, 182),
            >= 30 => Color.FromRgb(96, 190, 240),
            _ => null
        };

        private static IReadOnlyDictionary<string, PlayerStats> MergeWithout(string directory, string tablePath)
        {
            var merged = new Dictionary<string, PlayerStats>(StringComparer.Ordinal);
            foreach (var (path, entry) in Files)
            {
                if (!string.Equals(entry.Directory, directory, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(path, tablePath, StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var (name, stats) in entry.Players)
                {
                    if (!merged.TryGetValue(name, out var sum))
                    {
                        sum = new PlayerStats(name);
                        merged[name] = sum;
                    }
                    sum.Add(stats);
                }
            }

            return merged;
        }

        private static void RequestScan(string directory)
        {
            lock (Sync)
            {
                if (Scanning.Contains(directory) ||
                    (ScannedAt.TryGetValue(directory, out var at) && DateTime.UtcNow - at < RefreshEvery))
                    return;
                Scanning.Add(directory);
            }

            Task.Run(() => Scan(directory));
        }

        private static void Scan(string directory)
        {
            try
            {
                var current = System.IO.Directory.Exists(directory)
                    ? System.IO.Directory.EnumerateFiles(directory, "*.txt").ToList()
                    : new List<string>();
                var changed = false;
                foreach (var path in current)
                {
                    var info = new FileInfo(path);
                    FileEntry? known;
                    lock (Sync)
                        Files.TryGetValue(path, out known);
                    if (known is not null && known.Length == info.Length && known.WriteUtc == info.LastWriteTimeUtc)
                        continue;

                    var players = Parse(path);
                    if (players is null)
                        continue;
                    lock (Sync)
                        Files[path] = new FileEntry(directory, info.Length, info.LastWriteTimeUtc, players);
                    changed = true;
                }

                lock (Sync)
                {
                    var present = new HashSet<string>(current, StringComparer.OrdinalIgnoreCase);
                    foreach (var gone in Files
                                 .Where(file => string.Equals(file.Value.Directory, directory, StringComparison.OrdinalIgnoreCase) &&
                                                !present.Contains(file.Key))
                                 .Select(file => file.Key)
                                 .ToList())
                    {
                        Files.Remove(gone);
                        changed = true;
                    }

                    if (changed)
                        _version++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Sin la carpeta el HUD sigue con la mesa en vivo.
            }
            finally
            {
                lock (Sync)
                {
                    Scanning.Remove(directory);
                    ScannedAt[directory] = DateTime.UtcNow;
                }
            }
        }

        /// <summary>Estadisticas de cada jugador de un historial (null si ahora no se puede leer).</summary>
        private static IReadOnlyDictionary<string, PlayerStats>? Parse(string path)
        {
            try
            {
                var aggregator = new StatsAggregator();
                var parser = new PokerStarsParser(aggregator);
                parser.FeedLines(ReadShared(path), () => { });
                var players = new Dictionary<string, PlayerStats>(StringComparer.Ordinal);
                foreach (var (name, stats) in aggregator.Players)
                {
                    if (stats.HandsReceived <= 0)
                        continue;
                    var key = PokerStarsHandHistory.NormalizeName(name);
                    if (players.TryGetValue(key, out var existing))
                        existing.Add(stats);
                    else
                        players[key] = stats;
                }

                return players;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>PokerStars sigue escribiendo en las mesas abiertas: se lee sin bloquearlas.</summary>
        private static IEnumerable<string> ReadShared(string path)
        {
            var lines = new List<string>();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            while (reader.ReadLine() is { } line)
                lines.Add(line);
            return lines;
        }
    }
}
