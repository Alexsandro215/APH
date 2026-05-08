using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Hud.App.Services; // StakeProfile

namespace Hud.App.Views
{
    /// <summary>
    /// IMultiValueConverter para colorear mÃ©tricas del HUD segÃºn:
    ///  [0] valor (double)
    ///  [1] hands (int)
    ///  [2] stake (StakeProfile)
    ///  [3] key   (string) => "VPIP","PFR","THREEBET","AF","AFQ","CBF","FVCBF","WTSD","WSD","WWSF"
    ///
    /// Retorna un SolidColorBrush tomado de recursos XAML:
    ///  - "HudBlueDark", "HudBlue", "HudGreenSoft", "HudYellow",
    ///    "HudOrange", "HudRedSoft", "HudRed"
    ///
    /// Si Hands < MinHands, devuelve BgDark (neutral).
    /// </summary>
    public sealed class StatToBrushConverter : IMultiValueConverter
    {
        private const int MinHands = 30;

        // Claves que se consideran "invertidas" (un valor mÃ¡s alto NO es mejor)
        private static readonly string[] InvertedKeys = { "FVCBF", "WSD" };

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                if (values is null || values.Length < 4)
                    return FindBrush("BgDark");

                // 0) Valor
                if (!TryToDouble(values[0], out double statValue))
                    return FindBrush("BgDark");

                // 1) Hands
                if (!TryToInt(values[1], out int hands))
                    hands = 0;

                // 2) Stake
                var stake = values[2] is StakeProfile sp ? sp : StakeProfile.Low;

                // 3) Key
                var key = values[3]?.ToString()?.Trim().ToUpperInvariant();
                if (string.IsNullOrEmpty(key))
                    return FindBrush("BgDark");

                // Si no hay manos suficientes, color neutro
                if (hands < MinHands)
                    return FindBrush("BgDark");

                // Umbrales por (key, stake)
                var thresholds = GetThresholds(key, stake);
                if (thresholds is null || thresholds.Length == 0)
                    return FindBrush("BgDark");

                // Banda 0..7 contra 7 cortes
                int band = ComputeBand(statValue, thresholds);

                // Invertir si aplica
                if (IsInverted(key))
                    band = 7 - band;

                return BandToBrush(band);
            }
            catch
            {
                return FindBrush("BgDark");
            }
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();

        // ----------------- Helpers -----------------

        private static bool TryToDouble(object o, out double d)
        {
            if (o is double dd) { d = dd; return true; }
            if (o is float ff)  { d = ff; return true; }
            if (o is decimal mm){ d = (double)mm; return true; }
            if (o is int ii)    { d = ii; return true; }
            if (o is long ll)   { d = ll; return true; }
            return double.TryParse(o?.ToString() ?? "", NumberStyles.Float, CultureInfo.InvariantCulture, out d);
        }

        private static bool TryToInt(object o, out int i)
        {
            if (o is int ii) { i = ii; return true; }
            if (o is long ll && ll <= int.MaxValue) { i = (int)ll; return true; }
            return int.TryParse(o?.ToString() ?? "", NumberStyles.Integer, CultureInfo.InvariantCulture, out i);
        }

        private static bool IsInverted(string key) =>
            InvertedKeys.Contains(key, StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// thresholds: array ASCENDENTE de 7 cortes (crea 8 bandas).
        /// Ej: [t1, t2, t3, t4, t5, t6, t7]
        /// band = nÃºmero de cortes que "statValue" supera.
        /// </summary>
        private static int ComputeBand(double statValue, double[] thresholds)
        {
            int band = 0;
            foreach (var t in thresholds)
                if (statValue >= t) band++;
            if (band < 0) band = 0;
            if (band > 7) band = 7;
            return band;
        }

        private static Brush BandToBrush(int band)
        {
            // 0..7 -> paleta
            return band switch
            {
                0 => FindBrush("HudBlueDark"),
                1 => FindBrush("HudBlue"),
                2 => FindBrush("HudGreenSoft"),
                3 => FindBrush("HudYellow"),
                4 => FindBrush("HudOrange"),
                5 => FindBrush("HudRedSoft"),
                6 => FindBrush("HudRed"),
                7 => FindBrush("HudRed"),
                _ => FindBrush("BgDark")
            };
        }

        private static SolidColorBrush FindBrush(string key)
        {
            var obj = Application.Current.TryFindResource(key) as SolidColorBrush;
            return obj ?? new SolidColorBrush(Colors.Transparent);
        }

        /// <summary>
        /// Devuelve 7 cortes (double[7]) por mÃ©trica y stake.
        /// NOTA: porcentajes expresados en 0-100 (no 0-1). AF es ratio.
        /// </summary>
        private static double[] GetThresholds(string key, StakeProfile stake)
        {
            switch (key)
            {
                // ---------- PREFLOP ----------
                case "VPIP":
                    return stake switch
                    {
                        StakeProfile.Low  => new double[] { 10, 15, 22, 28, 35, 45, 55 },
                        StakeProfile.Mid  => new double[] {  8, 13, 20, 25, 32, 40, 50 },
                        _ /*High*/        => new double[] {  7, 12, 18, 23, 29, 36, 46 },
                    };

                case "PFR":
                    return stake switch
                    {
                        StakeProfile.Low  => new double[] {  6,  9, 13, 17, 22, 28, 35 },
                        StakeProfile.Mid  => new double[] {  5,  8, 12, 16, 21, 26, 32 },
                        _ /*High*/        => new double[] {  4,  7, 11, 15, 20, 25, 31 },
                    };

                case "THREEBET":
                    return stake switch
                    {
                        StakeProfile.Low  => new double[] { 2,   4,   6,   8, 10, 13, 16 },
                        StakeProfile.Mid  => new double[] { 2,   3.5, 5,   7,  9, 12, 15 },
                        _ /*High*/        => new double[] { 1.5, 3,   4.5, 6,  8, 10.5,14 },
                    };

                // ---------- POSTFLOP / AGG ----------
                case "AF": // ratio
                    return stake switch
                    {
                        StakeProfile.Low  => new double[] { 0.7, 1.0, 1.5, 2.2, 3.0, 4.0, 6.0 },
                        StakeProfile.Mid  => new double[] { 0.8, 1.1, 1.6, 2.3, 3.1, 4.2, 6.2 },
                        _ /*High*/        => new double[] { 0.9, 1.2, 1.7, 2.4, 3.2, 4.3, 6.5 },
                    };

                case "AFQ": // %
                    return stake switch
                    {
                        StakeProfile.Low  => new double[] { 20, 30, 40, 50, 60, 70, 80 },
                        StakeProfile.Mid  => new double[] { 22, 32, 42, 52, 62, 72, 82 },
                        _ /*High*/        => new double[] { 24, 34, 44, 54, 64, 74, 84 },
                    };

                // ---------- CBET ----------
                case "CBF": // CBet flop %
                    return stake switch
                    {
                        StakeProfile.Low  => new double[] { 30, 40, 50, 60, 70, 75, 80 },
                        StakeProfile.Mid  => new double[] { 32, 42, 52, 62, 72, 77, 82 },
                        _ /*High*/        => new double[] { 34, 44, 54, 64, 74, 79, 84 },
                    };

                case "FVCBF": // Fold vs CBet flop % (invertida)
                    return stake switch
                    {
                        StakeProfile.Low  => new double[] { 25, 35, 45, 55, 65, 75, 85 },
                        StakeProfile.Mid  => new double[] { 23, 33, 43, 53, 63, 73, 83 },
                        _ /*High*/        => new double[] { 22, 32, 42, 52, 62, 72, 82 },
                    };

                // ---------- SHOWDOWN ----------
                case "WTSD":
                    return stake switch
                    {
                        StakeProfile.Low  => new double[] { 18, 22, 25, 28, 32, 36, 40 },
                        StakeProfile.Mid  => new double[] { 17, 21, 24, 27, 31, 35, 39 },
                        _ /*High*/        => new double[] { 16, 20, 23, 26, 30, 34, 38 },
                    };

                case "WSD": // invertida
                    return stake switch
                    {
                        StakeProfile.Low  => new double[] { 40, 45, 50, 55, 60, 65, 70 },
                        StakeProfile.Mid  => new double[] { 41, 46, 51, 56, 60, 64, 69 },
                        _ /*High*/        => new double[] { 42, 47, 52, 56, 60, 64, 68 },
                    };

                case "WWSF":
                    return stake switch
                    {
                        StakeProfile.Low  => new double[] { 35, 40, 45, 50, 54, 58, 62 },
                        StakeProfile.Mid  => new double[] { 36, 41, 46, 51, 55, 59, 63 },
                        _ /*High*/        => new double[] { 37, 42, 47, 52, 56, 60, 64 },
                    };

                default:
                    return Array.Empty<double>();
            }
        }
    }
}

