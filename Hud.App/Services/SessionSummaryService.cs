using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Hud.App.Services
{
    /// <summary>Una mano del heroe para agrupar en sesiones.</summary>
    public sealed record SessionHand(DateTime At, string Table, string Cards, double NetBb, double LuckBb);

    /// <summary>
    /// Una sesion: juego seguido con pausas de menos de <see cref="SessionSummaryService.Gap"/>. Resultado,
    /// resultado sin la suerte de los all-in y la mejor y la peor mano.
    /// </summary>
    public sealed record SessionSummary(
        DateTime Start,
        DateTime End,
        int Hands,
        int Tables,
        double NetBb,
        double LuckBb,
        SessionHand? Best,
        SessionHand? Worst)
    {
        public double AdjustedBb => NetBb - LuckBb;
        public TimeSpan Duration => End - Start;
        public double BbPer100 => Hands == 0 ? 0 : NetBb * 100 / Hands;

        // Las horas del historial de PokerStars vienen en UTC: se muestran en la hora local.
        private static DateTime Local(DateTime value) =>
            value.Kind == DateTimeKind.Local ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc).ToLocalTime();

        public DateTime LocalStart => Local(Start);
        public string DateLabel => LocalStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        public string TimeLabel => $"{LocalStart:HH:mm} - {Local(End):HH:mm}";
        public string DurationLabel => Duration.TotalHours >= 1 ? $"{(int)Duration.TotalHours}h {Duration.Minutes:00}m" : $"{Math.Max(1, (int)Duration.TotalMinutes)}m";
        public string NetLabel => NetBb.ToString("+0.#;-0.#;0", CultureInfo.CurrentCulture) + " bb";
        public string AdjustedLabel => AdjustedBb.ToString("+0.#;-0.#;0", CultureInfo.CurrentCulture) + " bb";
        public string WinrateLabel => BbPer100.ToString("+0.#;-0.#;0", CultureInfo.CurrentCulture);
        public string BestLabel => Best is null ? "-" : $"{Best.Cards} {Best.NetBb.ToString("+0;-0;0", CultureInfo.CurrentCulture)} bb";
        public string WorstLabel => Worst is null ? "-" : $"{Worst.Cards} {Worst.NetBb.ToString("+0;-0;0", CultureInfo.CurrentCulture)} bb";
    }

    public static class SessionSummaryService
    {
        /// <summary>Una pausa mas larga que esto empieza otra sesion.</summary>
        public static readonly TimeSpan Gap = TimeSpan.FromMinutes(30);

        /// <summary>Agrupa las manos en sesiones (la mas reciente primero).</summary>
        public static IReadOnlyList<SessionSummary> Group(IEnumerable<SessionHand> hands)
        {
            var ordered = hands.OrderBy(hand => hand.At).ToList();
            var sessions = new List<SessionSummary>();
            var current = new List<SessionHand>();

            void Close()
            {
                if (current.Count == 0)
                    return;
                sessions.Add(new SessionSummary(
                    current[0].At,
                    current[^1].At,
                    current.Count,
                    current.Select(hand => hand.Table).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    current.Sum(hand => hand.NetBb),
                    current.Sum(hand => hand.LuckBb),
                    current.OrderByDescending(hand => hand.NetBb).First(),
                    current.OrderBy(hand => hand.NetBb).First()));
                current = new List<SessionHand>();
            }

            foreach (var hand in ordered)
            {
                if (current.Count > 0 && hand.At - current[^1].At > Gap)
                    Close();
                current.Add(hand);
            }
            Close();

            sessions.Reverse();
            return sessions;
        }

        /// <summary>Lee cada mano del heroe de las mesas (resultado en bb y suerte del all-in).</summary>
        public static IReadOnlyList<SessionHand> LoadHands(IEnumerable<MainWindow.TableSessionStats> tables)
        {
            var hands = new List<SessionHand>();
            foreach (var table in tables)
            {
                if (!File.Exists(table.SourcePath) || table.BigBlind <= 0 || string.IsNullOrWhiteSpace(table.HeroName))
                    continue;
                try
                {
                    foreach (var hand in PokerStarsHandHistory.SplitHands(File.ReadLines(table.SourcePath)))
                    {
                        if (!PokerStarsHandHistory.TryGetDealtCards(hand, table.HeroName, out var cards))
                            continue;
                        var luck = 0.0;
                        if (hand.Any(line => line.Contains("all-in", StringComparison.OrdinalIgnoreCase)) &&
                            hand.Any(line => line.StartsWith("*** SHOW", StringComparison.OrdinalIgnoreCase)) &&
                            Trainer.TrainerHandParser.Parse(hand, table.HeroName) is { } parsed)
                            luck = HandTypeProfit.AllInLuckBb(parsed);
                        hands.Add(new SessionHand(PokerStarsHandHistory.ExtractTimestamp(hand) ?? table.LastPlayedAt, table.TableName,
                            cards, PokerStarsHandHistory.EstimateNetForPlayer(hand, table.HeroName) / table.BigBlind, luck));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Una mesa que no se puede leer ahora solo falta en el resumen.
                }
            }

            return hands;
        }
    }
}
