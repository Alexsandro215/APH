using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Hud.App.Services
{
    public sealed record PokerStarsTableWindow(IntPtr Handle, string Title, string TableName, string? HeroName);

    public sealed record PokerStarsTableMatch(PokerStarsTableWindow Window, string HandHistoryPath);

    public static class PokerStarsWindowDetector
    {
        // El cliente cambia la coletilla segun version e idioma: "Iniciaste sesion como X",
        // "Sesion iniciada con el nombre de usuario X" o, en ingles, "Logged In as X".
        private static readonly Regex HeroFromTitleRx =
            new(@"(?:Iniciaste sesi\S*n como|Sesi\S*n iniciada con el nombre de usuario|Logged in as)\s+(?<hero>.+?)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex NonKeyCharsRx =
            new(@"[^a-z0-9]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static IReadOnlyList<PokerStarsTableWindow> DetectOpenTables()
        {
            var windows = new List<PokerStarsTableWindow>();
            EnumWindows((handle, _) =>
            {
                if (!IsWindowVisible(handle))
                    return true;

                var title = GetWindowTitle(handle);
                if (!LooksLikePokerStarsTable(title))
                    return true;
                // Solo ventanas del propio PokerStars: el titulo de VS Code con "PokerStarsParser.cs" abierto
                // pasaba por una mesa (y se le buscaban cartas).
                if (!IsPokerStarsProcess(handle))
                    return true;

                var tableName = ExtractTableName(title);
                if (string.IsNullOrWhiteSpace(tableName))
                    return true;

                windows.Add(new PokerStarsTableWindow(handle, title, tableName, ExtractHeroName(title)));
                return true;
            }, IntPtr.Zero);

            return windows
                .GroupBy(window => NormalizeKey(window.TableName), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(window => window.TableName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static IReadOnlyList<PokerStarsTableMatch> MatchHandHistories(
            IEnumerable<PokerStarsTableWindow> windows,
            string? rootFolder = null,
            int maxAgeDays = 3)
        {
            var roots = string.IsNullOrWhiteSpace(rootFolder)
                ? ResolveHandHistoryRoots()
                : Directory.Exists(rootFolder) ? new[] { rootFolder } : Array.Empty<string>();

            if (roots.Count == 0)
                return Array.Empty<PokerStarsTableMatch>();

            var cutoff = DateTime.Now.AddDays(-Math.Max(1, maxAgeDays));
            // IgnoreInaccessible: si se recorre Documentos entero, sus enlaces de sistema ("Mi
            // musica"...) lanzaban UnauthorizedAccessException.
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            var files = roots
                .SelectMany(root => Directory.EnumerateFiles(root, "*.txt", options))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(path => File.GetLastWriteTime(path) >= cutoff)
                .Select(path => new HandHistoryCandidate(path, NormalizeKey(Path.GetFileNameWithoutExtension(path)), File.GetLastWriteTime(path)))
                .OrderByDescending(file => file.LastWriteTime)
                .ToList();

            var matches = new List<PokerStarsTableMatch>();
            var usedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var window in windows)
            {
                var tableKey = NormalizeKey(window.TableName);
                var file = files.FirstOrDefault(candidate =>
                    !usedFiles.Contains(candidate.Path) &&
                    candidate.SearchKey.Contains(tableKey, StringComparison.OrdinalIgnoreCase));

                if (file is null)
                    continue;

                usedFiles.Add(file.Path);
                matches.Add(new PokerStarsTableMatch(window, file.Path));
            }

            return matches;
        }

        /// <summary>
        /// Donde buscar los historiales: la carpeta de PokerStars de Configuracion de APH; si no, la
        /// que usa el propio PokerStars ("SaveMyHandsPath" en su user.ini); si no, Documentos.
        /// Antes se buscaba siempre en Documentos\manos poker o, si no existia, en todo Documentos.
        /// </summary>
        public static IReadOnlyList<string> ResolveHandHistoryRoots()
        {
            var roots = new List<string>();
            void Add(string? folder)
            {
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                    return;
                var full = Path.GetFullPath(folder).TrimEnd('\\', '/');
                if (!roots.Contains(full, StringComparer.OrdinalIgnoreCase))
                    roots.Add(full);
            }

            try
            {
                var settings = AppSettingsService.Load();
                Add(settings.PokerRoomFolders.TryGetValue("PokerStars", out var configured) ? configured : null);
                Add(settings.PokerStarsHandHistoryFolder);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Tiempo real: no se pudo leer la carpeta de manos de Configuracion", ex);
            }

            // La de PokerStars tambien: si no coincide con la de APH, las mesas igual se encuentran.
            Add(PokerStarsSaveFolder());
            if (roots.Count == 0)
                Add(GetDefaultHandHistoryRoot());
            return roots;
        }

        /// <summary>La carpeta donde PokerStars guarda las manos, leida de su user.ini (PokerStars, PokerStars.MX...).</summary>
        public static string? PokerStarsSaveFolder()
        {
            try
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                foreach (var dir in Directory.EnumerateDirectories(local, "PokerStars*"))
                {
                    var ini = Path.Combine(dir, "user.ini");
                    if (!File.Exists(ini))
                        continue;
                    foreach (var line in File.ReadLines(ini))
                    {
                        if (!line.StartsWith("SaveMyHandsPath=", StringComparison.OrdinalIgnoreCase))
                            continue;
                        var path = line["SaveMyHandsPath=".Length..].Trim();
                        if (path.Length > 0 && Directory.Exists(path))
                            return path;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("Tiempo real: no se pudo leer la carpeta de manos de PokerStars", ex);
            }
            return null;
        }

        public static string? GetDefaultHandHistoryRoot()
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var pokerHands = Path.Combine(documents, "manos poker");
            return Directory.Exists(pokerHands) ? pokerHands : documents;
        }

        public static bool TryGetWindowRect(IntPtr handle, out WindowBounds bounds)
        {
            bounds = default;
            if (handle == IntPtr.Zero || !IsWindow(handle) || IsIconic(handle))
                return false;

            if (!GetWindowRect(handle, out var rect))
                return false;

            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0)
                return false;

            bounds = new WindowBounds(rect.Left, rect.Top, width, height);
            return true;
        }

        private static bool LooksLikePokerStarsTable(string title)
        {
            if (string.IsNullOrWhiteSpace(title) ||
                title.Contains("Lobby", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var looksLikePokerGame =
                title.Contains("Hold'em", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("Omaha", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("Stud", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("Draw", StringComparison.OrdinalIgnoreCase);

            return looksLikePokerGame &&
                (title.Contains("PokerStars", StringComparison.OrdinalIgnoreCase) ||
                 title.Contains("Dinero ficticio", StringComparison.OrdinalIgnoreCase) ||
                 title.Contains("Play Money", StringComparison.OrdinalIgnoreCase) ||
                 title.Contains("Iniciaste sesi", StringComparison.OrdinalIgnoreCase) ||
                 title.Contains("iniciada con el nombre de usuario", StringComparison.OrdinalIgnoreCase));
        }

        private static string ExtractTableName(string title)
        {
            var marker = title.IndexOf(" - ", StringComparison.Ordinal);
            return marker > 0 ? title[..marker].Trim() : "";
        }

        private static string? ExtractHeroName(string title)
        {
            var match = HeroFromTitleRx.Match(title);
            return match.Success ? NormalizePlayerName(match.Groups["hero"].Value) : null;
        }

        private static string NormalizePlayerName(string raw) =>
            raw.Trim().TrimEnd(':').Trim();

        private static string NormalizeKey(string raw) =>
            NonKeyCharsRx.Replace(raw.ToLowerInvariant(), "");

        private static string GetWindowTitle(IntPtr handle)
        {
            var length = GetWindowTextLength(handle);
            if (length <= 0)
                return "";

            var builder = new StringBuilder(length + 1);
            GetWindowText(handle, builder, builder.Capacity);
            return builder.ToString();
        }

        private sealed record HandHistoryCandidate(string Path, string SearchKey, DateTime LastWriteTime);

        public readonly record struct WindowBounds(int Left, int Top, int Width, int Height);

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        /// <summary>La ventana es de un proceso de PokerStars (si no se puede saber, se acepta por el titulo).</summary>
        private static bool IsPokerStarsProcess(IntPtr handle)
        {
            try
            {
                GetWindowThreadProcessId(handle, out var processId);
                using var process = System.Diagnostics.Process.GetProcessById((int)processId);
                return process.ProcessName.Contains("PokerStars", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return true;
            }
        }
    }
}

