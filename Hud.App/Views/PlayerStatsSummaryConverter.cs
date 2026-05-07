using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Controls;
using System.Windows.Media;
using HandReader.Core.Models;

namespace Hud.App.Views
{
    public sealed class PlayerStatsSummaryConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not PlayerStats stats)
                return PlayerStatsSummary.Empty;

            return PlayerStatsSummary.From(stats);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            Binding.DoNothing;
    }

    public sealed class PlayerStatsToolTipConverter : IValueConverter
    {
        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not PlayerStats stats)
                return null;

            var summary = PlayerStatsSummary.From(stats);
            var panel = new StackPanel { MaxWidth = 380 };
            panel.Children.Add(new TextBlock
            {
                Text = summary.Title,
                FontWeight = System.Windows.FontWeights.Bold,
                Foreground = Brushes.Black,
                FontSize = 14
            });
            panel.Children.Add(new TextBlock
            {
                Text = summary.Summary,
                Foreground = Brushes.Black,
                Margin = new System.Windows.Thickness(0, 4, 0, 8)
            });

            var tags = new WrapPanel();
            foreach (var tag in summary.Tags)
            {
                tags.Children.Add(new Border
                {
                    Background = tag.Background,
                    BorderBrush = tag.Border,
                    BorderThickness = new System.Windows.Thickness(1),
                    CornerRadius = new System.Windows.CornerRadius(5),
                    Padding = new System.Windows.Thickness(6, 2, 6, 2),
                    Margin = new System.Windows.Thickness(0, 0, 5, 5),
                    Child = new TextBlock
                    {
                        Text = tag.Text,
                        Foreground = tag.Foreground,
                        FontWeight = System.Windows.FontWeights.SemiBold,
                        FontSize = 11
                    }
                });
            }

            panel.Children.Add(tags);
            panel.Children.Add(new TextBlock
            {
                Text = summary.StatsLine,
                Foreground = Brushes.Black,
                Margin = new System.Windows.Thickness(0, 4, 0, 0)
            });

            return panel;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            Binding.DoNothing;
    }

    public sealed class PlayerHistoryRowBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var name = value?.ToString() ?? "";
            return VillainHistoryStore.TryGet(name, out var row)
                ? SampleBrush(row.TotalHandsVsHero)
                : BrushFrom(18, 20, 24);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            Binding.DoNothing;

        private static Brush BrushFrom(byte r, byte g, byte b) =>
            new SolidColorBrush(Color.FromRgb(r, g, b));

        private static Brush SampleBrush(int hands) =>
            hands switch
            {
                >= 10000 => BrushFrom(245, 247, 250),
                >= 5000 => BrushFrom(109, 40, 217),
                >= 1000 => BrushFrom(183, 144, 255),
                >= 500 => BrushFrom(215, 198, 0),
                >= 200 => BrushFrom(33, 192, 122),
                >= 100 => BrushFrom(159, 232, 182),
                _ => BrushFrom(18, 20, 24)
            };
    }

    public sealed class PlayerHistoryTextBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var name = value?.ToString() ?? "";
            if (!VillainHistoryStore.TryGet(name, out var row))
                return BrushFrom(242, 244, 248);

            return row.TotalHandsVsHero switch
            {
                >= 10000 => Brushes.Black,
                >= 5000 => Brushes.White,
                >= 1000 => Brushes.Black,
                >= 500 => Brushes.Black,
                >= 200 => Brushes.Black,
                >= 100 => Brushes.Black,
                _ => BrushFrom(242, 244, 248)
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            Binding.DoNothing;

        private static Brush BrushFrom(byte r, byte g, byte b) =>
            new SolidColorBrush(Color.FromRgb(r, g, b));
    }

    public sealed class PlayerStatsSummary
    {
        private PlayerStatsSummary(
            string title,
            string summary,
            string statsLine,
            IReadOnlyList<PlayerTagSummary> tags)
        {
            Title = title;
            Summary = summary;
            StatsLine = statsLine;
            Tags = tags;
        }

        public string Title { get; }
        public string Summary { get; }
        public string StatsLine { get; }
        public IReadOnlyList<PlayerTagSummary> Tags { get; }

        public static PlayerStatsSummary Empty { get; } =
            new("-", "Sin datos.", "", Array.Empty<PlayerTagSummary>());

        public static PlayerStatsSummary From(PlayerStats stats)
        {
            if (VillainHistoryStore.TryGet(stats.Name, out var row))
                return FromHistory(row, stats);

            var profile = ClassifyProfile(stats.HandsReceived, stats.VPIPPct, stats.PFRPct, stats.ThreeBetPct, stats.AF);
            var tags = BuildTags(stats, profile);
            var summary = $"{profile} | {stats.HandsReceived} manos";
            var statsLine =
                $"VPIP {stats.VPIPPct:0.#}% | PFR {stats.PFRPct:0.#}% | 3Bet {stats.ThreeBetPct:0.#}% | AF {stats.AF:0.#} | WTSD {stats.WTSDPct:0.#}% | W$SD {stats.WSDPct:0.#}%";

            return new PlayerStatsSummary(stats.Name, summary, statsLine, tags);
        }

        private static PlayerStatsSummary FromHistory(DataVillainsWindow.DataVillainRow row, PlayerStats live)
        {
            var tags = BuildTags(
                row.Profile,
                row.TotalHands,
                row.VPIPPct,
                row.PFRPct,
                row.ThreeBetPct,
                row.AF,
                row.AFqPct,
                row.FoldVsCBetFlopPct,
                row.WTSDPct,
                row.WSDPct);
            tags.Insert(0, Neutral($"Historial {row.TotalHandsVsHero} vs hero"));

            var summary =
                $"{row.Profile} | Historico: {row.TotalHands} manos | Vs hero: {row.TotalHandsVsHero} | Mesa actual: {live.HandsReceived}";
            var statsLine =
                $"VPIP {row.VPIPPct:0.#}% | PFR {row.PFRPct:0.#}% | 3Bet {row.ThreeBetPct:0.#}% | AF {row.AF:0.#} | WTSD {row.WTSDPct:0.#}% | W$SD {row.WSDPct:0.#}%";

            return new PlayerStatsSummary(row.Name, summary, statsLine, tags);
        }

        private static List<PlayerTagSummary> BuildTags(PlayerStats stats, string profile) =>
            BuildTags(
                profile,
                stats.HandsReceived,
                stats.VPIPPct,
                stats.PFRPct,
                stats.ThreeBetPct,
                stats.AF,
                stats.AFqPct,
                stats.FoldVsCBetFlopPct,
                stats.WTSDPct,
                stats.WSDPct);

        private static List<PlayerTagSummary> BuildTags(
            string profile,
            int hands,
            double vpip,
            double pfr,
            double threeBet,
            double af,
            double afq,
            double foldVsCBet,
            double wtsd,
            double wsd)
        {
            var tags = new List<PlayerTagSummary> { Neutral(profile) };

            if (hands < 30)
                tags.Add(Neutral("Sin muestra"));
            if (vpip >= 35)
                tags.Add(Negative("Juega muchas manos"));
            if (pfr >= 20)
                tags.Add(Neutral("Agresivo preflop"));
            if (pfr > 0 && pfr < 10)
                tags.Add(Neutral("PFR bajo"));
            if (threeBet >= 10)
                tags.Add(Negative("3Bet alto"));
            if (af >= 4 || afq >= 65)
                tags.Add(Negative("Agresor"));
            if (foldVsCBet >= 65)
                tags.Add(Positive("Foldea CBet"));
            if (foldVsCBet > 0 && foldVsCBet <= 30)
                tags.Add(Negative("No foldea CBet"));
            if (wtsd >= 35)
                tags.Add(Negative("Va a showdown"));
            if (wsd >= 55)
                tags.Add(Positive("Showdown fuerte"));
            if (wsd > 0 && wsd < 45)
                tags.Add(Negative("Showdown debil"));

            return tags;
        }

        private static string ClassifyProfile(int hands, double vpip, double pfr, double threeBet, double af)
        {
            if (hands < 30) return "Sin muestra";
            if (vpip >= 40 && pfr <= 10 && af < 1.5) return "Fish";
            if (vpip >= 45 || af >= 5 || threeBet >= 15) return "Maniac";
            if (vpip >= 35 && pfr < 15) return "Loose pasivo";
            if (vpip >= 28 && pfr >= 20) return "LAG";
            if (vpip >= 18 && vpip < 29 && pfr >= 13 && pfr < 24) return "TAG";
            if (vpip < 14 && pfr < 10) return "Nit";
            if (vpip < 22 && pfr < 15) return "Tight";
            if (af < 1.2) return "Pasivo";
            return "Regular";
        }

        private static PlayerTagSummary Positive(string text) =>
            new(text, BrushFrom(16, 76, 52), BrushFrom(33, 192, 122), Brushes.White);

        private static PlayerTagSummary Negative(string text) =>
            new(text, BrushFrom(98, 21, 32), BrushFrom(226, 78, 91), Brushes.White);

        private static PlayerTagSummary Neutral(string text) =>
            new(text, BrushFrom(28, 40, 54), BrushFrom(86, 108, 132), Brushes.White);

        private static Brush BrushFrom(byte r, byte g, byte b) =>
            new SolidColorBrush(Color.FromRgb(r, g, b));

    }

    public sealed record PlayerTagSummary(
        string Text,
        Brush Background,
        Brush Border,
        Brush Foreground);
}
