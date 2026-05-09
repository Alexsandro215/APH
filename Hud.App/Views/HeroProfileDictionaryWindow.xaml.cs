using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Hud.App.Services;

namespace Hud.App.Views
{
    public partial class HeroProfileDictionaryWindow : Window
    {
        public HeroProfileDictionaryWindow()
        {
            InitializeComponent();
            DataContext = HeroProfileDictionaryViewModel.Build();
        }

        private sealed class HeroProfileDictionaryViewModel
        {
            public IReadOnlyList<DictionaryTagRow> Tags { get; private init; } = Array.Empty<DictionaryTagRow>();
            public IReadOnlyList<ColorLegendRow> ColorRows { get; private init; } = Array.Empty<ColorLegendRow>();
            public IReadOnlyList<MetricMeaningRow> MetricRows { get; private init; } = Array.Empty<MetricMeaningRow>();

            public static HeroProfileDictionaryViewModel Build() =>
                new()
                {
                    Tags = BuildTags().ToList(),
                    ColorRows = BuildColorRows().ToList(),
                    MetricRows = BuildMetricRows().ToList()
                };

            private static IEnumerable<DictionaryTagRow> BuildTags()
            {
                yield return Neutral("Fish", T("Tag.Fish.Desc"));
                yield return Neutral("Maniac", T("Tag.Maniac.Desc"));
                yield return Neutral(T("Tag.LoosePassive"), T("Tag.LoosePassive.Desc"));
                yield return Neutral("LAG", T("Tag.Lag.Desc"));
                yield return Neutral("TAG", T("Tag.Tag.Desc"));
                yield return Neutral("Nit", T("Tag.Nit.Desc"));
                yield return Neutral("Tight", T("Tag.Tight.Desc"));
                yield return Neutral(T("Tag.Passive"), T("Tag.Passive.Desc"));
                yield return Neutral(T("Tag.Regular"), T("Tag.Regular.Desc"));
                yield return Neutral(T("Tag.NoSample"), T("Tag.NoSample.Desc"));

                yield return Negative(T("Tag.PlaysManyHands"), T("Tag.PlaysManyHands.Desc"));
                yield return Negative(T("Tag.Aggressor"), T("Tag.Aggressor.Desc"));
                yield return Negative(T("Tag.High3Bet"), T("Tag.High3Bet.Desc"));
                yield return Positive(T("Tag.FoldsCBet"), T("Tag.FoldsCBet.Desc"));
                yield return Negative(T("Tag.NoFoldCBet"), T("Tag.NoFoldCBet.Desc"));
                yield return Negative(T("Tag.ShowdownOften"), T("Tag.ShowdownOften.Desc"));
                yield return Positive("Calling station", T("Tag.CallingStation.Desc"));
                yield return Neutral(T("Tag.Rock"), T("Tag.Rock.Desc"));

                yield return Positive("Loose", T("Tag.Loose.Desc"));
                yield return Neutral("Tight", T("Tag.TightHero.Desc"));
                yield return Neutral(T("Tag.MidRange"), T("Tag.MidRange.Desc"));
                yield return Neutral(T("Tag.AggressivePreflop"), T("Tag.AggressivePreflop.Desc"));
                yield return Neutral(T("Tag.LowPfr"), T("Tag.LowPfr.Desc"));
                yield return Neutral(T("Tag.StablePfr"), T("Tag.StablePfr.Desc"));
                yield return Negative("Overfold vs CBet", T("Tag.OverfoldCBet.Desc"));
                yield return Neutral(T("Tag.CBetDefenseOk"), T("Tag.CBetDefenseOk.Desc"));
                yield return Neutral(T("Tag.FrequentCBet"), T("Tag.FrequentCBet.Desc"));
                yield return Neutral(T("Tag.SelectiveCBet"), T("Tag.SelectiveCBet.Desc"));
                yield return Positive(T("Tag.StrongShowdown"), T("Tag.StrongShowdown.Desc"));
                yield return Negative(T("Tag.ReviewShowdown"), T("Tag.ReviewShowdown.Desc"));

                yield return Negative(T("Tag.LeakPosition"), T("Tag.LeakPosition.Desc"));
                yield return CardNeutral("AKs", "lover", T("Tag.ComboLover.Desc"));
                yield return Neutral(T("Tag.PremiumLover"), T("Tag.PremiumLoverHero.Desc"));
                yield return Neutral(T("Tag.LowHands"), T("Tag.LowHandsHero.Desc"));
                yield return Neutral("Suited connectors", T("Tag.SuitedConnectorsHero.Desc"));
                yield return Neutral(T("Tag.Mixed"), T("Tag.MixedHero.Desc"));
            }

            private static IEnumerable<ColorLegendRow> BuildColorRows()
            {
                yield return new ColorLegendRow(FindBrush("BgDark"), T("Color.Neutral"), T("Color.Neutral.Desc"));
                yield return new ColorLegendRow(FindBrush("HudBlueDark"), T("Color.DarkBlue"), T("Color.DarkBlue.Desc"));
                yield return new ColorLegendRow(FindBrush("HudBlue"), T("Color.Blue"), T("Color.Blue.Desc"));
                yield return new ColorLegendRow(FindBrush("HudGreenSoft"), T("Color.Green"), T("Color.Green.Desc"));
                yield return new ColorLegendRow(FindBrush("HudYellow"), T("Color.Yellow"), T("Color.Yellow.Desc"));
                yield return new ColorLegendRow(FindBrush("HudOrange"), T("Color.Orange"), T("Color.Orange.Desc"));
                yield return new ColorLegendRow(FindBrush("HudRedSoft"), T("Color.SoftRed"), T("Color.SoftRed.Desc"));
                yield return new ColorLegendRow(FindBrush("HudRed"), T("Color.Red"), T("Color.Red.Desc"));
            }

            private static IEnumerable<MetricMeaningRow> BuildMetricRows()
            {
                yield return new MetricMeaningRow("VPIP%", T("Metric.VPIP.Desc.Hero"));
                yield return new MetricMeaningRow("PFR%", T("Metric.PFR.Desc.Hero"));
                yield return new MetricMeaningRow("3Bet%", T("Metric.ThreeBet.Desc"));
                yield return new MetricMeaningRow("AF", T("Metric.AF.Desc"));
                yield return new MetricMeaningRow("AFq%", T("Metric.AFq.Desc.Hero"));
                yield return new MetricMeaningRow("CBet%", T("Metric.CBet.Desc.Hero"));
                yield return new MetricMeaningRow("FvCBet%", T("Metric.FvCBet.Desc.Hero"));
                yield return new MetricMeaningRow("WTSD%", T("Metric.WTSD.Desc.Hero"));
                yield return new MetricMeaningRow("W$SD%", T("Metric.WSD.Desc"));
                yield return new MetricMeaningRow("WWSF%", T("Metric.WWSF.Desc.Hero"));
            }

            private static string T(string key) => LocalizationManager.Text(key);

            private static DictionaryTagRow Positive(string text, string description) =>
                new(text, description, BrushFrom(16, 76, 52), BrushFrom(33, 192, 122), Brushes.White, Array.Empty<CardChipViewModel>());

            private static DictionaryTagRow Negative(string text, string description) =>
                new(text, description, BrushFrom(98, 21, 32), BrushFrom(226, 78, 91), Brushes.White, Array.Empty<CardChipViewModel>());

            private static DictionaryTagRow Neutral(string text, string description) =>
                new(text, description, BrushFrom(28, 40, 54), BrushFrom(86, 108, 132), Brushes.White, Array.Empty<CardChipViewModel>());

            private static DictionaryTagRow CardNeutral(string combo, string text, string description) =>
                new(text, description, BrushFrom(28, 40, 54), BrushFrom(86, 108, 132), Brushes.White, CardChipViewModel.FromCards(ComboToCards(combo)));

            private static string ComboToCards(string combo)
            {
                if (string.IsNullOrWhiteSpace(combo) || combo.Length < 2)
                    return "";

                var first = combo[0];
                var second = combo[1];
                if (first == second)
                    return $"{first}h {second}d";

                return combo.EndsWith("s", StringComparison.Ordinal)
                    ? $"{first}h {second}h"
                    : $"{first}h {second}d";
            }

            private static SolidColorBrush FindBrush(string key)
            {
                var obj = Application.Current.TryFindResource(key) as SolidColorBrush;
                return obj ?? new SolidColorBrush(Colors.Transparent);
            }

            private static Brush BrushFrom(byte r, byte g, byte b) =>
                new SolidColorBrush(Color.FromRgb(r, g, b));
        }

        private sealed record DictionaryTagRow(
            string Text,
            string Description,
            Brush Background,
            Brush Border,
            Brush Foreground,
            IReadOnlyList<CardChipViewModel> CardChips);

        private sealed record ColorLegendRow(Brush Brush, string Label, string Description);
        private sealed record MetricMeaningRow(string Name, string Description);
    }
}

