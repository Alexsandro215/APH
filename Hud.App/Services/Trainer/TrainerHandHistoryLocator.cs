using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Hud.App.Services.Trainer
{
    /// <summary>
    /// Busca el historial exacto de una mesa. PokerStars escribe un archivo por mesa y dia:
    /// "HH20261001 Antares V - 100-200 - Dinero ficticio ...txt". Se exige que el nombre de la mesa
    /// coincida entero ("Antares V" no es "Antares VI") y se prefiere la carpeta de la cuenta del
    /// heroe; si no existe, el archivo tiene que contener sus cartas ("Dealt to heroe").
    /// </summary>
    public static class TrainerHandHistoryLocator
    {
        private static readonly Regex FileNameRx =
            new(@"^HH(?<date>\d{8})\s+(?<table>.+?)\s+-\s", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex NonKeyRx = new(@"[^a-z0-9]+", RegexOptions.Compiled);

        public sealed record Located(string Path, string Date, DateTime LastWrite, DateTime Created);

        public static Located? Find(string tableName, string? heroName)
        {
            var key = Key(tableName);
            if (key.Length == 0)
                return null;

            var candidates = new List<(Located File, bool HeroFolder)>();
            foreach (var root in Roots())
            {
                foreach (var path in SafeEnumerate(root))
                {
                    var match = FileNameRx.Match(Path.GetFileName(path));
                    if (!match.Success || Key(match.Groups["table"].Value) != key)
                        continue;

                    try
                    {
                        var info = new FileInfo(path);
                        var folder = Path.GetFileName(Path.GetDirectoryName(path) ?? "");
                        var heroFolder = !string.IsNullOrWhiteSpace(heroName) && PokerStarsHandHistory.SamePlayer(folder, heroName);
                        candidates.Add((new Located(path, match.Groups["date"].Value, info.LastWriteTime, info.CreationTime), heroFolder));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                    }
                }
            }

            IEnumerable<(Located File, bool HeroFolder)> pool = candidates;
            if (!string.IsNullOrWhiteSpace(heroName))
            {
                var inHeroFolder = candidates.Where(candidate => candidate.HeroFolder).ToList();
                pool = inHeroFolder.Count > 0
                    ? inHeroFolder
                    : candidates.Where(candidate => FileHasHero(candidate.File.Path, heroName)).ToList();
            }

            return pool
                .OrderByDescending(candidate => candidate.File.Date, StringComparer.Ordinal)
                .ThenByDescending(candidate => candidate.File.LastWrite)
                .Select(candidate => candidate.File)
                .FirstOrDefault();
        }

        public static string Key(string raw) => NonKeyRx.Replace(raw.Trim().ToLowerInvariant(), "");

        /// <summary>Todos los historiales "HH*.txt" de las carpetas de PokerStars (ritmo de la sala).</summary>
        public static IReadOnlyList<string> AllHistoryFiles() =>
            Roots().SelectMany(SafeEnumerate).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>Nombre de la mesa en la linea "Table 'Antares V' 6-max ..." de una mano.</summary>
        public static string? TableOfHand(IReadOnlyList<string> hand)
        {
            foreach (var line in hand.Take(3))
            {
                var match = Regex.Match(line, @"^(?:Table|Mesa)\s+'(?<table>[^']+)'", RegexOptions.IgnoreCase);
                if (match.Success)
                    return match.Groups["table"].Value;
            }

            return null;
        }

        /// <summary>
        /// Carpetas de historiales: las configuradas en APH y las de los clientes de PokerStars. No
        /// se recorre Documentos entero: alli puede haber historiales de otras cuentas o salas.
        /// </summary>
        private static IEnumerable<string> Roots()
        {
            var roots = new List<string>();
            var settings = AppSettingsService.Load();
            if (settings.PokerRoomFolders.TryGetValue("PokerStars", out var configured))
                roots.Add(configured);
            if (!string.IsNullOrWhiteSpace(settings.PokerStarsHandHistoryFolder))
                roots.Add(settings.PokerStarsHandHistoryFolder);

            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            roots.Add(Path.Combine(documents, "manos poker"));

            try
            {
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                roots.AddRange(Directory.EnumerateDirectories(localAppData, "PokerStars*").Select(client => Path.Combine(client, "HandHistory")));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            return roots
                .Where(root => !string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static IEnumerable<string> SafeEnumerate(string root)
        {
            try
            {
                return Directory.EnumerateFiles(root, "HH*.txt", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 3 }).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Array.Empty<string>();
            }
        }

        /// <summary>El final del archivo contiene las cartas del heroe (es su cuenta).</summary>
        private static bool FileHasHero(string path, string heroName)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var length = Math.Min(stream.Length, 32 * 1024);
                stream.Seek(-length, SeekOrigin.End);
                var buffer = new byte[length];
                var read = stream.Read(buffer, 0, buffer.Length);
                var text = Encoding.UTF8.GetString(buffer, 0, read);
                return text.Contains($"Dealt to {heroName.Trim()} [", StringComparison.Ordinal) ||
                       text.Contains($"Repartido a {heroName.Trim()} [", StringComparison.Ordinal);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }
}
