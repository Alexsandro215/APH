using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Hud.App.Services;

namespace Hud.App.Views
{
    /// <summary>
    /// Leyenda de los colores de las stats, la misma en todo APH: "Colores de las stats" y una
    /// pastilla por nivel (Muy bajo ... Muy alto) con su color, mas la gris de "pocas manos". Al
    /// pasar el raton explica que se compara con jugadores normales de 6-max.
    /// </summary>
    public sealed class StatLegendStrip : WrapPanel
    {
        public StatLegendStrip()
        {
            Orientation = Orientation.Horizontal;
            ToolTip = LocalizationManager.Text("StatLegend.Note");
            Children.Add(new TextBlock
            {
                Text = LocalizationManager.Text("StatLegend.Title"),
                FontWeight = FontWeights.SemiBold,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                Foreground = TryBrush("Brush.TextDim") ?? Brushes.Gray
            });

            foreach (var level in StatScale.Scale)
                Children.Add(Chip(StatScale.LabelOf(level), new SolidColorBrush(StatScale.ColorOf(level)!.Value), new SolidColorBrush(StatScale.InkOf(level)),
                    StatScale.RangeOf(level)));

            var noSample = Chip(StatScale.LabelOf(StatLevel.NoSample), TryBrush("BgDark") ?? new SolidColorBrush(Color.FromRgb(38, 44, 52)),
                TryBrush("Brush.Text") ?? Brushes.White, StatScale.RangeOf(StatLevel.NoSample), margin: 10);
            noSample.BorderBrush = TryBrush("Brush.Border") ?? Brushes.Gray;
            noSample.BorderThickness = new Thickness(1);
            Children.Add(noSample);
        }

        private static Border Chip(string text, Brush background, Brush ink, string tip, double margin = 3) => new()
        {
            Background = background,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 2, 7, 3),
            Margin = new Thickness(margin == 3 ? 0 : margin, 2, 3, 2),
            ToolTip = tip,
            Child = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = ink }
        };

        private static Brush? TryBrush(string key) => Application.Current?.TryFindResource(key) as Brush;
    }
}
