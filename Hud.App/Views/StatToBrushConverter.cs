using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Hud.App.Services;

namespace Hud.App.Views
{
    /// <summary>
    /// Color de una stat con la escala unica de APH (<see cref="StatScale"/>): azul = menos que lo
    /// normal, verde = normal, naranja y rojo = mas que lo normal, gris = menos de 30 manos.
    /// Valores: [0] valor, [1] manos, [2] stake (StakeProfile), [3] clave ("VPIP", "PFR", "THREEBET",
    /// "AF", "AFQ", "CBF", "FVCBF", "WTSD", "WSD", "WWSF").
    /// ConverterParameter: sin parametro, el fondo; "ink", el color del texto que se lee sobre ese
    /// fondo; "tip", el texto del tooltip (que nivel es y que quiere decir).
    /// </summary>
    public sealed class StatToBrushConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            var mode = parameter?.ToString() ?? "";
            try
            {
                if (values is null || values.Length < 4 || !TryToDouble(values[0], out var value))
                    return Empty(mode);

                if (!TryToInt(values[1], out var hands))
                    hands = 0;
                var stake = values[2] is StakeProfile profile ? profile : StakeProfile.Low;
                var key = values[3]?.ToString()?.Trim().ToUpperInvariant();
                if (string.IsNullOrEmpty(key))
                    return Empty(mode);

                if (mode == "tip")
                {
                    var tip = StatScale.Describe(key, value, hands, stake);
                    var help = LocalizationManager.Text("StatTip." + key);
                    return help.StartsWith("StatTip.", StringComparison.Ordinal) ? tip : tip + Environment.NewLine + Environment.NewLine + help;
                }

                var level = StatScale.Level(key, value, hands, stake);
                if (mode == "label")
                    return StatScale.LabelOf(level);
                if (mode == "ink")
                    return level == StatLevel.NoSample ? NoSampleInk() : Frozen(StatScale.InkOf(level));
                return StatScale.ColorOf(level) is { } color ? Frozen(color) : NoSampleBackground();
            }
            catch
            {
                return Empty(mode);
            }
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();

        private static object Empty(string mode) => mode switch
        {
            "tip" or "label" => "",
            "ink" => NoSampleInk(),
            _ => NoSampleBackground()
        };

        private static Brush NoSampleBackground() =>
            Application.Current?.TryFindResource("BgDark") as Brush ?? Frozen(Color.FromRgb(38, 44, 52));

        private static Brush NoSampleInk() =>
            Application.Current?.TryFindResource("Brush.Text") as Brush ?? Frozen(Color.FromRgb(231, 238, 245));

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private static bool TryToDouble(object o, out double d)
        {
            switch (o)
            {
                case double dd: d = dd; return true;
                case float ff: d = ff; return true;
                case decimal mm: d = (double)mm; return true;
                case int ii: d = ii; return true;
                case long ll: d = ll; return true;
                default:
                    return double.TryParse(o?.ToString() ?? "", NumberStyles.Float, CultureInfo.InvariantCulture, out d);
            }
        }

        private static bool TryToInt(object o, out int i)
        {
            if (o is int ii) { i = ii; return true; }
            if (o is long ll && ll <= int.MaxValue) { i = (int)ll; return true; }
            return int.TryParse(o?.ToString() ?? "", NumberStyles.Integer, CultureInfo.InvariantCulture, out i);
        }
    }
}
