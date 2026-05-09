using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Hud.App.Services;

namespace Hud.App.Views
{
    public partial class DataVillainDictionaryWindow : Window
    {
        public DataVillainDictionaryWindow()
        {
            InitializeComponent();
            DataContext = DataVillainDictionaryViewModel.Build();
        }

        private sealed class DataVillainDictionaryViewModel
        {
            public IReadOnlyList<DictionaryTagRow> Tags { get; private init; } = Array.Empty<DictionaryTagRow>();
            public IReadOnlyList<ColorLegendRow> MetricColorRows { get; private init; } = Array.Empty<ColorLegendRow>();
            public IReadOnlyList<ColorLegendRow> RangeColorRows { get; private init; } = Array.Empty<ColorLegendRow>();
            public IReadOnlyList<MetricMeaningRow> MetricRows { get; private init; } = Array.Empty<MetricMeaningRow>();

            public static DataVillainDictionaryViewModel Build() =>
                new()
                {
                    Tags = BuildTags().ToList(),
                    MetricColorRows = BuildMetricColorRows().ToList(),
                    RangeColorRows = BuildRangeColorRows().ToList(),
                    MetricRows = BuildMetricRows().ToList()
                };

            private static IEnumerable<DictionaryTagRow> BuildTags()
            {
                yield return Neutral(T("Tag.NoSample"), T("Tag.NoSample.Desc"));
                yield return Neutral("Fish", T("Tag.Fish.Desc"));
                yield return Neutral("Maniac", T("Tag.Maniac.Desc"));
                yield return Neutral(T("Tag.LoosePassive"), T("Tag.LoosePassive.Desc"));
                yield return Neutral("LAG", T("Tag.Lag.Desc"));
                yield return Neutral("TAG", T("Tag.Tag.Desc"));
                yield return Neutral("Nit", T("Tag.Nit.Desc"));
                yield return Neutral("Tight", T("Tag.Tight.Desc"));
                yield return Neutral(T("Tag.Passive"), T("Tag.Passive.Desc"));
                yield return Neutral(T("Tag.Regular"), T("Tag.Regular.Desc"));

                yield return Neutral(T("Tag.FrequentRival"), T("Tag.FrequentRival.Desc"));
                yield return Positive(T("Tag.LosesVsHero"), T("Tag.LosesVsHero.Desc"));
                yield return Negative(T("Tag.WinsVsHero"), T("Tag.WinsVsHero.Desc"));
                yield return Negative(T("Tag.PlaysManyHands"), T("Tag.PlaysManyHands.Desc"));
                yield return Negative(T("Tag.Aggressor"), T("Tag.Aggressor.Desc"));
                yield return Negative(T("Tag.High3Bet"), T("Tag.High3Bet.Desc"));
                yield return Positive(T("Tag.FoldsCBet"), T("Tag.FoldsCBet.Desc"));
                yield return Negative(T("Tag.NoFoldCBet"), T("Tag.NoFoldCBet.Desc"));
                yield return Negative(T("Tag.ShowdownOften"), T("Tag.ShowdownOften.Desc"));
                yield return Positive("Calling station", T("Tag.CallingStation.Desc"));
                yield return Neutral(T("Tag.Rock"), T("Tag.Rock.Desc"));

                yield return Neutral(T("Tag.PremiumLover"), T("Tag.PremiumLover.Desc"));
                yield return Neutral(T("Tag.LowHands"), T("Tag.LowHands.Desc"));
                yield return Neutral("Suited connectors", T("Tag.SuitedConnectors.Desc"));
                yield return Neutral(T("Tag.Mixed"), T("Tag.Mixed.Desc"));
                yield return Negative(T("Tag.Trapper"), T("Tag.Trapper.Desc"));
                yield return Negative("All-in equity", T("Tag.AllInEquity.Desc"));
                yield return Negative("Color lover", T("Tag.ColorLover.Desc"));
                yield return Negative("Set lover", T("Tag.SetLover.Desc"));
                yield return Negative("Trips lover", T("Tag.TripsLover.Desc"));
                yield return Negative("Double par", T("Tag.DoublePair.Desc"));
                yield return Negative(T("Tag.HighPair"), T("Tag.HighPair.Desc"));
                yield return Negative(T("Tag.StraightLover"), T("Tag.StraightLover.Desc"));
            }

            private static IEnumerable<ColorLegendRow> BuildMetricColorRows()
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

            private static IEnumerable<ColorLegendRow> BuildRangeColorRows()
            {
                yield return new ColorLegendRow(BrushFrom(80, 86, 96), "Fold", T("RangeColor.Fold.Desc"));
                yield return new ColorLegendRow(BrushFrom(75, 102, 122), "Check", T("RangeColor.Check.Desc"));
                yield return new ColorLegendRow(BrushFrom(0, 148, 198), "Call", T("RangeColor.Call.Desc"));
                yield return new ColorLegendRow(BrushFrom(64, 184, 4), "Bet", T("RangeColor.Bet.Desc"));
                yield return new ColorLegendRow(BrushFrom(184, 181, 4), "Raise", T("RangeColor.Raise.Desc"));
                yield return new ColorLegendRow(BrushFrom(226, 137, 0), "3Bet", T("RangeColor.ThreeBet.Desc"));
                yield return new ColorLegendRow(BrushFrom(255, 115, 115), "4Bet+", T("RangeColor.FourBet.Desc"));
                yield return new ColorLegendRow(BrushFrom(156, 0, 0), "All-in", T("RangeColor.AllIn.Desc"));
                yield return new ColorLegendRow(BrushFrom(33, 192, 122), T("Common.ProfitBb"), T("RangeColor.Profit.Desc"));
                yield return new ColorLegendRow(BrushFrom(226, 78, 91), T("Common.LossBb"), T("RangeColor.Loss.Desc"));
            }

            private static IEnumerable<MetricMeaningRow> BuildMetricRows()
            {
                yield return new MetricMeaningRow("VPIP%", T("Metric.VPIP.Desc.Player"));
                yield return new MetricMeaningRow("PFR%", T("Metric.PFR.Desc.Player"));
                yield return new MetricMeaningRow("3Bet%", T("Metric.ThreeBet.Desc"));
                yield return new MetricMeaningRow("AF", T("Metric.AF.Desc"));
                yield return new MetricMeaningRow("AFq%", T("Metric.AFq.Desc.Player"));
                yield return new MetricMeaningRow("CBet%", T("Metric.CBet.Desc.Player"));
                yield return new MetricMeaningRow("FvCBet%", T("Metric.FvCBet.Desc"));
                yield return new MetricMeaningRow("WTSD%", T("Metric.WTSD.Desc.Player"));
                yield return new MetricMeaningRow("W$SD%", T("Metric.WSD.Desc"));
                yield return new MetricMeaningRow("WWSF%", T("Metric.WWSF.Desc.Player"));
                yield return new MetricMeaningRow(T("Common.VillainTablesShort"), T("Metric.VillainTables.Desc"));
            }

            private static string T(string key) => LocalizationManager.Text(key);

            private static DictionaryTagRow Positive(string text, string description) =>
                new(text, description, BrushFrom(16, 76, 52), BrushFrom(33, 192, 122), Brushes.White);

            private static DictionaryTagRow Negative(string text, string description) =>
                new(text, description, BrushFrom(98, 21, 32), BrushFrom(226, 78, 91), Brushes.White);

            private static DictionaryTagRow Neutral(string text, string description) =>
                new(text, description, BrushFrom(28, 40, 54), BrushFrom(86, 108, 132), Brushes.White);

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
            Brush Foreground);

        private sealed record ColorLegendRow(Brush Brush, string Label, string Description);
        private sealed record MetricMeaningRow(string Name, string Description);
    }
}

