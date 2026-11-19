namespace Hud.App.Services
{
    /// <summary>
    /// Etiqueta de estilo de un jugador (Fish, Maniac, LAG, TAG, Nit...). Antes estaba copiada en
    /// cuatro ventanas con numeros fijos; ahora es una sola y sus limites salen de como juega la
    /// poblacion real (Data/population_baselines.json, manos de NL25-NL100). Cada limite es un
    /// percentil: por ejemplo "Nit" = VPIP por debajo del 10% mas cerrado de los jugadores.
    /// Entre parentesis, el numero fijo de antes, que se usa si no esta el archivo de calibracion.
    /// Con la calibracion de 2009 los limites quedan muy cerca de los de antes, pero ahora se pueden
    /// recalibrar con otras manos sin tocar el codigo.
    /// </summary>
    public static class PlayerProfileClassifier
    {
        public const int MinHands = 30;

        private sealed record Limits(
            double FishVpip, double FishPfr, double FishAf,
            double ManiacVpip, double ManiacAf, double ManiacThreeBet,
            double LoosePassiveVpip, double LoosePassivePfr,
            double LagVpip, double LagPfr,
            double TagVpipMin, double TagVpipMax, double TagPfrMin, double TagPfrMax,
            double NitVpip, double NitPfr,
            double TightVpip, double TightPfr,
            double PassiveAf);

        private static readonly Lazy<Limits> Current = new(() => new Limits(
            FishVpip: P("VPIP", 70, 40), FishPfr: P("PFR", 50, 10), FishAf: P("AF", 15, 1.5),
            ManiacVpip: P("VPIP", 85, 45), ManiacAf: P("AF", 95, 5), ManiacThreeBet: P("THREEBET", 95, 15),
            LoosePassiveVpip: P("VPIP", 60, 35), LoosePassivePfr: P("PFR", 70, 15),
            LagVpip: P("VPIP", 50, 28), LagPfr: P("PFR", 85, 20),
            TagVpipMin: P("VPIP", 15, 18), TagVpipMax: P("VPIP", 50, 29), TagPfrMin: P("PFR", 70, 13), TagPfrMax: P("PFR", 95, 24),
            NitVpip: P("VPIP", 10, 14), NitPfr: P("PFR", 50, 10),
            TightVpip: P("VPIP", 30, 22), TightPfr: P("PFR", 70, 15),
            PassiveAf: P("AF", 10, 1.2)));

        private static double P(string key, double percentile, double fallback) =>
            PopulationBaselines.Percentile(key, percentile, fallback);

        public static string Classify(int hands, double vpip, double pfr, double threeBet, double af)
        {
            if (hands < MinHands) return LocalizationManager.Text("Tag.NoSample");

            var l = Current.Value;
            if (vpip >= l.FishVpip && pfr <= l.FishPfr && af < l.FishAf) return "Fish";
            if (vpip >= l.ManiacVpip || af >= l.ManiacAf || threeBet >= l.ManiacThreeBet) return "Maniac";
            if (vpip >= l.LoosePassiveVpip && pfr < l.LoosePassivePfr) return LocalizationManager.Text("Tag.LoosePassive");
            if (vpip >= l.LagVpip && pfr >= l.LagPfr) return "LAG";
            if (vpip >= l.TagVpipMin && vpip < l.TagVpipMax && pfr >= l.TagPfrMin && pfr < l.TagPfrMax) return "TAG";
            if (vpip < l.NitVpip && pfr < l.NitPfr) return "Nit";
            if (vpip < l.TightVpip && pfr < l.TightPfr) return "Tight";
            if (af < l.PassiveAf) return LocalizationManager.Text("Tag.Passive");
            return LocalizationManager.Text("Tag.Regular");
        }

        /// <summary>Los limites en uso, para revisarlos (log y pruebas).</summary>
        public static string Describe()
        {
            var l = Current.Value;
            return string.Join("; ",
                FormattableString.Invariant($"Fish VPIP>={l.FishVpip:0.#} PFR<={l.FishPfr:0.#} AF<{l.FishAf:0.##}"),
                FormattableString.Invariant($"Maniac VPIP>={l.ManiacVpip:0.#} o AF>={l.ManiacAf:0.##} o 3B>={l.ManiacThreeBet:0.#}"),
                FormattableString.Invariant($"LoosePassive VPIP>={l.LoosePassiveVpip:0.#} PFR<{l.LoosePassivePfr:0.#}"),
                FormattableString.Invariant($"LAG VPIP>={l.LagVpip:0.#} PFR>={l.LagPfr:0.#}"),
                FormattableString.Invariant($"TAG VPIP {l.TagVpipMin:0.#}-{l.TagVpipMax:0.#} PFR {l.TagPfrMin:0.#}-{l.TagPfrMax:0.#}"),
                FormattableString.Invariant($"Nit VPIP<{l.NitVpip:0.#} PFR<{l.NitPfr:0.#}"),
                FormattableString.Invariant($"Tight VPIP<{l.TightVpip:0.#} PFR<{l.TightPfr:0.#}"),
                FormattableString.Invariant($"Pasivo AF<{l.PassiveAf:0.##}"));
        }
    }
}
