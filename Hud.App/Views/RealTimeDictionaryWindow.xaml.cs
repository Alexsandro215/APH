using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Hud.App.Services;

namespace Hud.App.Views
{
    public partial class RealTimeDictionaryWindow : Window
    {
        public RealTimeDictionaryWindow()
        {
            InitializeComponent();
            DataContext = RealTimeDictionaryViewModel.Build();
        }

        private sealed class RealTimeDictionaryViewModel
        {
            public IReadOnlyList<HelpRow> ControlRows { get; private init; } = Array.Empty<HelpRow>();
            public IReadOnlyList<HelpRow> MetricRows { get; private init; } = Array.Empty<HelpRow>();
            public IReadOnlyList<ColorLegendRow> ColorRows { get; private init; } = Array.Empty<ColorLegendRow>();
            public IReadOnlyList<string> NoteRows { get; private init; } = Array.Empty<string>();

            public static RealTimeDictionaryViewModel Build() =>
                new()
                {
                    ControlRows = BuildControls().ToList(),
                    MetricRows = BuildMetrics().ToList(),
                    ColorRows = BuildColors().ToList(),
                    NoteRows = BuildNotes().ToList()
                };

            private static IEnumerable<HelpRow> BuildControls()
            {
                yield return new HelpRow("+", T("RT.Help.AddFile"));
                yield return new HelpRow("Play", T("RT.Help.Play"));
                yield return new HelpRow("Stop", T("RT.Help.Stop"));
                yield return new HelpRow(T("Common.Hero"), T("RT.Help.Hero"));
                yield return new HelpRow(T("Common.Lines"), T("RT.Help.Lines"));
                yield return new HelpRow(T("RT.Compact"), T("RT.Help.Compact"));
            }

            private static IEnumerable<HelpRow> BuildMetrics()
            {
                yield return new HelpRow(LocalizationManager.Text("Grid.Hands"), T("RT.Metric.Hands"));
                yield return new HelpRow("VPIP%", T("Metric.VPIP.Desc.Player"));
                yield return new HelpRow("PFR%", T("Metric.PFR.Desc.Player"));
                yield return new HelpRow("3Bet%", T("Metric.ThreeBet.Desc"));
                yield return new HelpRow("AF", T("Metric.AF.Desc.Short"));
                yield return new HelpRow("AFq%", T("Metric.AFq.Desc.Player"));
                yield return new HelpRow("CBet%", T("Metric.CBet.Desc.Player"));
                yield return new HelpRow("FvCBet%", T("Metric.FvCBet.Desc.Short"));
                yield return new HelpRow("WTSD%", T("Metric.WTSD.Desc.Player"));
                yield return new HelpRow("W$SD%", T("Metric.WSD.Desc.Short"));
                yield return new HelpRow("WWSF%", T("Metric.WWSF.Desc.Player"));
            }

            private static IEnumerable<ColorLegendRow> BuildColors()
            {
                yield return new ColorLegendRow(FindBrush("BgDark"), T("Color.Neutral"), T("Color.NeutralRt.Desc"));
                // La escala unica de APH (StatScale): azul = menos que lo normal, verde = normal,
                // naranja y rojo = mas que lo normal. El color ya no se invierte en ninguna stat.
                foreach (var level in StatScale.Scale)
                    yield return new ColorLegendRow(BackgroundLoad.Frozen(new SolidColorBrush(StatScale.ColorOf(level)!.Value)),
                        StatScale.LabelOf(level), StatScale.RangeOf(level) + ". " + LocalizationManager.Text("StatLegend.Short." + level));
            }

            private static IEnumerable<string> BuildNotes()
            {
                yield return T("RT.Note.Sample");
                yield return T("RT.Note.HeroRow");
                yield return T("RT.Note.File");
                yield return T("RT.Note.Refresh");
            }

            private static string T(string key) => LocalizationManager.Text(key);

            private static SolidColorBrush FindBrush(string key)
            {
                var obj = Application.Current.TryFindResource(key) as SolidColorBrush;
                return obj ?? new SolidColorBrush(Colors.Transparent);
            }
        }

        private sealed record HelpRow(string Name, string Description);
        private sealed record ColorLegendRow(Brush Brush, string Label, string Description);
    }
}

