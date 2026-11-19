using System.IO;
using System.Text.Json;

namespace Hud.App.Services
{
    /// <summary>
    /// Cortes calibrados con manos reales (Data/population_baselines.json, generado por
    /// APH.Calibration). Antes los colores del HUD usaban cortes puestos a ojo; ahora salen de los
    /// percentiles de como juega la poblacion real en cada nivel. Si el archivo no esta, APH usa
    /// los cortes de siempre.
    /// </summary>
    public static class PopulationBaselines
    {
        private static readonly Lazy<JsonDocument?> Document = new(Load);

        public static string FilePath =>
            Path.Combine(AppContext.BaseDirectory, "Data", "population_baselines.json");

        public static bool IsAvailable => Document.Value is not null;

        /// <summary>
        /// 7 cortes (percentiles 5, 15, 30, 50, 70, 85 y 95) para una estadistica del HUD.
        /// Los niveles bajos y el dinero ficticio usan los de NL25-NL100, el nivel real mas parecido
        /// que hay en los datos.
        /// </summary>
        public static bool TryGetCuts(string key, StakeProfile stake, out double[] cuts)
        {
            cuts = Array.Empty<double>();
            if (!TryGetBucket(stake, out var bucket) ||
                !bucket.TryGetProperty("cuts", out var allCuts) ||
                !allCuts.TryGetProperty(key, out var values) ||
                values.ValueKind != JsonValueKind.Array)
                return false;

            cuts = values.EnumerateArray().Select(value => value.GetDouble()).ToArray();
            return cuts.Length == 7;
        }

        /// <summary>Un percentil (p10, p20, p50, p80, p90) de una estadistica en la poblacion.</summary>
        public static double? Quantile(string key, string percentile, StakeProfile stake = StakeProfile.Mid)
        {
            if (TryGetBucket(stake, out var bucket) &&
                bucket.TryGetProperty("quantiles", out var all) &&
                all.TryGetProperty(key, out var metric) &&
                metric.TryGetProperty(percentile, out var value))
                return value.GetDouble();

            return null;
        }

        /// <summary>Frecuencia preflop de la poblacion, por ejemplo ("vsOpen", "threeBetPct").</summary>
        public static double? Preflop(string group, string field, StakeProfile stake = StakeProfile.Mid)
        {
            if (TryGetBucket(stake, out var bucket) &&
                bucket.TryGetProperty("preflop", out var preflop) &&
                preflop.TryGetProperty(group, out var section) &&
                section.TryGetProperty(field, out var value) &&
                value.ValueKind == JsonValueKind.Number)
                return value.GetDouble();

            return null;
        }

        /// <summary>Un corte concreto: indice 0..6 = percentiles 5, 15, 30, 50, 70, 85, 95.</summary>
        public static double Cut(string key, int index, double fallback, StakeProfile stake = StakeProfile.Mid) =>
            TryGetCuts(key, stake, out var cuts) && index >= 0 && index < cuts.Length ? cuts[index] : fallback;

        private static readonly double[] CutPercentiles = { 5, 15, 30, 50, 70, 85, 95 };

        /// <summary>
        /// Valor de la poblacion en cualquier percentil entre 5 y 95 (interpolando entre los cortes).
        /// Por ejemplo Percentile("VPIP", 60) = el VPIP que supera el 60% de los jugadores.
        /// </summary>
        public static double Percentile(string key, double percentile, double fallback, StakeProfile stake = StakeProfile.Mid)
        {
            if (!TryGetCuts(key, stake, out var cuts))
                return fallback;

            var q = Math.Clamp(percentile, CutPercentiles[0], CutPercentiles[^1]);
            for (var i = 1; i < CutPercentiles.Length; i++)
            {
                if (q > CutPercentiles[i]) continue;
                var t = (q - CutPercentiles[i - 1]) / (CutPercentiles[i] - CutPercentiles[i - 1]);
                return cuts[i - 1] + t * (cuts[i] - cuts[i - 1]);
            }
            return cuts[^1];
        }

        private static bool TryGetBucket(StakeProfile stake, out JsonElement bucket)
        {
            bucket = default;
            var document = Document.Value;
            if (document is null || !document.RootElement.TryGetProperty("buckets", out var buckets))
                return false;

            var name = stake == StakeProfile.High ? "High" : "Mid";
            return buckets.TryGetProperty(name, out bucket);
        }

        private static JsonDocument? Load()
        {
            try
            {
                if (!File.Exists(FilePath))
                    return null;

                return JsonDocument.Parse(File.ReadAllText(FilePath));
            }
            catch (Exception ex)
            {
                AppLog.Warn("No se pudieron leer los cortes de poblacion", ex);
                return null;
            }
        }
    }
}
