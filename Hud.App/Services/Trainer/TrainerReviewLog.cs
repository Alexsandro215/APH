using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Hud.App.Services.Trainer
{
    /// <summary>
    /// Guarda cada mano corregida en vivo con lo que se recomendo en cada turno (accion, motivo y si
    /// era GTO, tabla o solver), lo que hizo el jugador y la nota, una linea JSON por mano en
    /// %LOCALAPPDATA%\APH\trainer\revisiones\AAAAMMDD.jsonl. Antes solo vivia en memoria y se perdia
    /// al cerrar APH, asi que no se podia revisar la sesion despues.
    /// </summary>
    public static class TrainerReviewLog
    {
        private static readonly object Gate = new();
        private static string? _folderOverride;

        public static string Folder => _folderOverride ?? Path.Combine(TrainerCalibration.FolderPath, "revisiones");

        /// <summary>Para las pruebas: otra carpeta (null vuelve a la normal).</summary>
        public static void UseFolder(string? folder) => _folderOverride = folder;

        public static string FileFor(DateTime day) => Path.Combine(Folder, $"{day:yyyyMMdd}.jsonl");

        public sealed record Entry(
            string HandId, string Table, DateTime At, string HeroCards, string Board, string Position, double NetBb,
            IReadOnlyList<DecisionEntry> Decisions);

        public sealed record DecisionEntry(
            string Street, string Situation, string Chosen, string Recommended, string Grade, string Explanation,
            string? LiveRecommendation, string? LiveSource, string? LiveReason, bool? FollowedLive, string? LiveDetails = null,
            string? LiveReadCheck = null);

        public static Entry ToEntry(TrainerHandReport report) => new(
            report.HandId, report.TableName, report.ProcessedAt, report.HeroCards, report.Board, report.Position, report.NetBb,
            report.Decisions.Select(d => new DecisionEntry(
                d.Street, d.Situation, d.Chosen, d.Recommended, d.Grade.ToString(), d.Explanation,
                d.LiveRecommendation, d.LiveSource, d.LiveReason, d.FollowedLive, d.LiveDetails, d.LiveReadCheck)).ToList());

        public static void Append(TrainerHandReport report)
        {
            try
            {
                var line = JsonSerializer.Serialize(ToEntry(report));
                lock (Gate)
                {
                    Directory.CreateDirectory(Folder);
                    File.AppendAllText(FileFor(report.ProcessedAt), line + Environment.NewLine);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                AppLog.Warn("Revision: no se pudo guardar la mano corregida", ex);
            }
        }

        public static IReadOnlyList<Entry> Read(DateTime day)
        {
            var path = FileFor(day);
            if (!File.Exists(path))
                return Array.Empty<Entry>();
            var result = new List<Entry>();
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                try
                {
                    if (JsonSerializer.Deserialize<Entry>(line) is { } entry)
                        result.Add(entry);
                }
                catch (JsonException)
                {
                    // Linea cortada (se cerro APH a mitad de escritura): se salta.
                }
            }
            return result;
        }
    }
}
