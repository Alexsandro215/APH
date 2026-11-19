using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Controls;
using System.Windows.Media;
using HandReader.Core.Models;
using Hud.App.Services;

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
            BackgroundLoad.Frozen(new SolidColorBrush(Color.FromRgb(r, g, b)));

        // Los mismos colores que la capsula del HUD sobre la mesa (PlayerHistoryIndex.SampleColor).
        private static Brush SampleBrush(int hands) =>
            PlayerHistoryIndex.SampleColor(hands) is { } color
                ? BackgroundLoad.Frozen(new SolidColorBrush(color))
                : BrushFrom(18, 20, 24);
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
                >= 30 => Brushes.Black,
                _ => BrushFrom(242, 244, 248)
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            Binding.DoNothing;

        private static Brush BrushFrom(byte r, byte g, byte b) =>
            BackgroundLoad.Frozen(new SolidColorBrush(Color.FromRgb(r, g, b)));
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
            new("-", LocalizationManager.Text("Common.NoData"), "", Array.Empty<PlayerTagSummary>());

        public static PlayerStatsSummary From(PlayerStats stats)
        {
            if (VillainHistoryStore.TryGet(stats.Name, out var row))
                return FromHistory(row, stats);

            var profile = ClassifyProfile(stats.HandsReceived, stats.VPIPPct, stats.PFRPct, stats.ThreeBetPct, stats.AF);
            var tags = BuildTags(stats, profile);
            var summary = string.Format(CultureInfo.InvariantCulture, LocalizationManager.Text("PlayerSummary.ProfileHands"), profile, stats.HandsReceived);
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
            tags.Insert(0, Neutral(string.Format(CultureInfo.InvariantCulture, LocalizationManager.Text("PlayerSummary.HistoryTag"), row.TotalHandsVsHero)));

            var summary =
                string.Format(CultureInfo.InvariantCulture, LocalizationManager.Text("PlayerSummary.History"), row.Profile, row.TotalHands, row.TotalHandsVsHero, live.HandsReceived);
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
                tags.Add(Neutral(LocalizationManager.Text("Tag.NoSample")));

            // Alto o bajo con la escala unica de APH (StatScale, la de los colores): "alto" es por encima del
            // percentil 85 de los jugadores normales y "bajo" por debajo del 15. Antes eran numeros fijos.
            bool High(string key, double value) => StatScale.Level(key, value, hands) >= StatLevel.High;
            bool Low(string key, double value) => StatScale.Level(key, value, hands) is StatLevel.Low or StatLevel.VeryLow;

            if (High("VPIP", vpip))
                tags.Add(Negative(LocalizationManager.Text("Tag.PlaysManyHands")));
            if (High("PFR", pfr))
                tags.Add(Neutral(LocalizationManager.Text("Tag.AggressivePreflop")));
            if (Low("PFR", pfr))
                tags.Add(Neutral(LocalizationManager.Text("Tag.LowPfr")));
            if (High("THREEBET", threeBet))
                tags.Add(Negative(LocalizationManager.Text("Tag.High3Bet")));
            if (High("AF", af) || High("AFQ", afq))
                tags.Add(Negative(LocalizationManager.Text("Tag.Aggressor")));
            if (High("FVCBF", foldVsCBet))
                tags.Add(Positive(LocalizationManager.Text("Tag.FoldsCBetShort")));
            if (Low("FVCBF", foldVsCBet))
                tags.Add(Negative(LocalizationManager.Text("Tag.NoFoldCBet")));
            if (High("WTSD", wtsd))
                tags.Add(Negative(LocalizationManager.Text("Tag.GoesShowdown")));
            if (High("WSD", wsd))
                tags.Add(Positive(LocalizationManager.Text("Tag.StrongShowdown")));
            if (Low("WSD", wsd))
                tags.Add(Negative(LocalizationManager.Text("Tag.WeakShowdown")));

            return tags;
        }

        private static string ClassifyProfile(int hands, double vpip, double pfr, double threeBet, double af) =>
            Hud.App.Services.PlayerProfileClassifier.Classify(hands, vpip, pfr, threeBet, af);

        private static PlayerTagSummary Positive(string text) =>
            new(text, BrushFrom(16, 76, 52), BrushFrom(33, 192, 122), Brushes.White);

        private static PlayerTagSummary Negative(string text) =>
            new(text, BrushFrom(98, 21, 32), BrushFrom(226, 78, 91), Brushes.White);

        private static PlayerTagSummary Neutral(string text) =>
            new(text, Hud.App.Services.ThemeBrushes.NeutralBackground, Hud.App.Services.ThemeBrushes.NeutralBorder, Hud.App.Services.ThemeBrushes.Text);

        private static Brush BrushFrom(byte r, byte g, byte b) =>
            BackgroundLoad.Frozen(new SolidColorBrush(Color.FromRgb(r, g, b)));

    }

    public sealed record PlayerTagSummary(
        string Text,
        Brush Background,
        Brush Border,
        Brush Foreground);
}

