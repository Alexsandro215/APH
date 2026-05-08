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
        private static readonly Regex HeroFromTitleRx =
            new(@"Iniciaste sesi\S*n como\s+(?<hero>.+?)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
            var root = string.IsNullOrWhiteSpace(rootFolder)
                ? GetDefaultHandHistoryRoot()
                : rootFolder;

            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                return Array.Empty<PokerStarsTableMatch>();

            var cutoff = DateTime.Now.AddDays(-Math.Max(1, maxAgeDays));
            var files = Directory.EnumerateFiles(root, "*.txt", SearchOption.AllDirectories)
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
                 title.Contains("Iniciaste sesi", StringComparison.OrdinalIgnoreCase));
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
    }
}
