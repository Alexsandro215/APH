using System;
using System.Globalization;
using System.Windows.Media;

namespace Hud.App.Services
{
    /// <summary>Donde cae una stat comparada con los jugadores normales de 6-max.</summary>
    public enum StatLevel
    {
        NoSample,
        VeryLow,
        Low,
        SlightlyLow,
        Normal,
        SlightlyHigh,
        High,
        VeryHigh
    }

    /// <summary>
    /// La escala de colores de las stats de todo APH (HUD de la mesa, ventanas e informe PDF).
    /// Un solo significado: azul = menos que lo normal, verde = normal, naranja y rojo = mas que lo
    /// normal, gris = todavia pocas manos. Los cortes son percentiles de la poblacion real de 6-max
    /// (Data/population_baselines.json): normal es entre el percentil 30 y el 70, "algo" hasta el
    /// 15/85, y "muy" por debajo del 5 o por encima del 95. Antes la escala tenia 7 colores sin orden
    /// claro, el verde "normal" caia entre los percentiles 15 y 30 (un jugador en la media salia
    /// amarillo o naranja) y en FvCB y W$SD iba al reves. Ahora el color nunca se invierte: si alto
    /// o bajo es bueno lo dice el texto de cada stat (<see cref="Meaning"/>).
    /// </summary>
    public static class StatScale
    {
        /// <summary>Con menos manos el color todavia no dice nada.</summary>
        public const int MinHands = 30;

        /// <summary>Las stats con color, con la clave que usa APH en todas partes.</summary>
        public static readonly string[] Keys = { "VPIP", "PFR", "THREEBET", "AF", "AFQ", "CBF", "FVCBF", "WTSD", "WSD", "WWSF" };

        /// <summary>Los niveles con color, de menos a mas (para las leyendas).</summary>
        public static readonly StatLevel[] Scale =
        {
            StatLevel.VeryLow, StatLevel.Low, StatLevel.SlightlyLow, StatLevel.Normal,
            StatLevel.SlightlyHigh, StatLevel.High, StatLevel.VeryHigh
        };

        public static StatLevel Level(string key, double value, int hands, StakeProfile stake = StakeProfile.Low)
        {
            if (hands < MinHands || double.IsNaN(value))
                return StatLevel.NoSample;
            var cuts = Cuts(key, stake);
            if (cuts.Length != 7)
                return StatLevel.NoSample;

            // Cortes: percentiles 5, 15, 30, 50, 70, 85 y 95.
            if (value < cuts[0]) return StatLevel.VeryLow;
            if (value < cuts[1]) return StatLevel.Low;
            if (value < cuts[2]) return StatLevel.SlightlyLow;
            if (value <= cuts[4]) return StatLevel.Normal;
            if (value <= cuts[5]) return StatLevel.SlightlyHigh;
            if (value <= cuts[6]) return StatLevel.High;
            return StatLevel.VeryHigh;
        }

        /// <summary>Color de fondo del nivel. Sin muestra devuelve null: cada vista usa su gris.</summary>
        public static Color? ColorOf(StatLevel level) => level switch
        {
            StatLevel.VeryLow => Color.FromRgb(31, 78, 140),
            StatLevel.Low => Color.FromRgb(52, 112, 190),
            StatLevel.SlightlyLow => Color.FromRgb(158, 196, 236),
            StatLevel.Normal => Color.FromRgb(93, 180, 113),
            StatLevel.SlightlyHigh => Color.FromRgb(238, 194, 76),
            StatLevel.High => Color.FromRgb(234, 130, 56),
            StatLevel.VeryHigh => Color.FromRgb(201, 52, 62),
            _ => null
        };

        /// <summary>Texto legible sobre el color (contraste de 4,5 o mas): blanco en los oscuros, casi negro en los claros.</summary>
        public static Color InkOf(StatLevel level) => level switch
        {
            StatLevel.VeryLow or StatLevel.Low or StatLevel.VeryHigh => Colors.White,
            StatLevel.NoSample => Color.FromRgb(154, 164, 178),
            _ => Color.FromRgb(11, 18, 32)
        };

        public static string LabelOf(StatLevel level) => LocalizationManager.Text("StatLevel." + level);

        /// <summary>"Mas alto que 85 de cada 100 jugadores", "Como la mayoria de los jugadores"...</summary>
        public static string RangeOf(StatLevel level) => LocalizationManager.Text("StatLevel.Range." + level);

        /// <summary>Que quiere decir que esa stat este alta, baja o normal (en palabras de poker).</summary>
        public static string Meaning(string key, StatLevel level)
        {
            var side = level switch
            {
                StatLevel.VeryLow or StatLevel.Low or StatLevel.SlightlyLow => "Low",
                StatLevel.SlightlyHigh or StatLevel.High or StatLevel.VeryHigh => "High",
                StatLevel.Normal => "Normal",
                _ => null
            };
            return side is null ? "" : LocalizationManager.Text($"StatMeaning.{Normalize(key)}.{side}");
        }

        /// <summary>
        /// Texto completo para el tooltip: "VPIP 46%: muy alto (mas alto que 95 de cada 100 jugadores).
        /// Entra en muchas manos (suelto)." Con pocas manos dice que todavia no es fiable.
        /// </summary>
        public static string Describe(string key, double value, int hands, StakeProfile stake = StakeProfile.Low)
        {
            var name = DisplayName(key);
            var shown = Format(key, value);
            var level = Level(key, value, hands, stake);
            if (level == StatLevel.NoSample)
                return string.Format(CultureInfo.CurrentCulture, LocalizationManager.Text("StatScale.NoSampleTip"), name, shown, hands, MinHands);

            return string.Format(CultureInfo.CurrentCulture, LocalizationManager.Text("StatScale.Tip"),
                name, shown, LabelOf(level).ToLower(CultureInfo.CurrentCulture), RangeOf(level).ToLower(CultureInfo.CurrentCulture), Meaning(key, level));
        }

        public static string DisplayName(string key) => Normalize(key) switch
        {
            "THREEBET" => "3Bet",
            "AFQ" => "AFq",
            "CBF" => "CBet",
            "FVCBF" => "FvCB",
            "WSD" => "W$SD",
            var other => other
        };

        public static string Format(string key, double value) =>
            Normalize(key) == "AF"
                ? value.ToString("0.#", CultureInfo.CurrentCulture)
                : value.ToString("0.#", CultureInfo.CurrentCulture) + "%";

        /// <summary>
        /// 7 cortes (percentiles 5, 15, 30, 50, 70, 85 y 95) de la stat. Los calibrados con manos reales
        /// y, si no estan, unos de respaldo para 6-max.
        /// </summary>
        public static double[] Cuts(string key, StakeProfile stake)
        {
            var normalized = Normalize(key);
            if (PopulationBaselines.TryGetCuts(normalized, stake, out var calibrated))
                return calibrated;
            return FallbackCuts(normalized, stake);
        }

        private static string Normalize(string key) => (key ?? "").Trim().ToUpperInvariant();

        private static double[] FallbackCuts(string key, StakeProfile stake)
        {
            var high = stake == StakeProfile.High;
            return key switch
            {
                "VPIP" => high ? new double[] { 7, 12, 18, 23, 29, 36, 46 } : new double[] { 10, 15, 22, 28, 35, 45, 55 },
                "PFR" => high ? new double[] { 4, 7, 11, 15, 20, 25, 31 } : new double[] { 6, 9, 13, 17, 22, 28, 35 },
                "THREEBET" => high ? new double[] { 1.5, 3, 4.5, 6, 8, 10.5, 14 } : new double[] { 2, 4, 6, 8, 10, 13, 16 },
                "AF" => new double[] { 0.7, 1.0, 1.5, 2.2, 3.0, 4.0, 6.0 },
                "AFQ" => new double[] { 20, 30, 40, 50, 60, 70, 80 },
                "CBF" => new double[] { 30, 40, 50, 60, 70, 75, 80 },
                "FVCBF" => new double[] { 25, 35, 45, 55, 65, 75, 85 },
                "WTSD" => new double[] { 18, 22, 25, 28, 32, 36, 40 },
                "WSD" => new double[] { 40, 45, 50, 55, 60, 65, 70 },
                "WWSF" => new double[] { 35, 40, 45, 50, 54, 58, 62 },
                _ => Array.Empty<double>()
            };
        }
    }
}
