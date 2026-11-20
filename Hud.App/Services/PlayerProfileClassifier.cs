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
            double ManiacVpip, double ManiacAf, double ManiacLooseAf,
            double LoosePassiveVpip, double LoosePassivePfr,
            double LagVpip, double LagPfr,
            double TagVpipMin, double TagVpipMax, double TagPfrMin, double TagPfrMax,
            double NitVpip, double NitPfr,
            double TightVpip, double TightPfr,
            double PassiveAf);

        private static readonly Lazy<Limits> Current = new(() => new Limits(
            FishVpip: P("VPIP", 70, 40), FishPfr: P("PFR", 50, 10), FishAf: P("AF", 15, 1.5),
            ManiacVpip: P("VPIP", 85, 45), ManiacAf: P("AF", 95, 5), ManiacLooseAf: P("AF", 50, 2.2),
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

            // Maniac es quien juega muchisimo Y aprieta (sube como un LAG o apuesta tanto como la media o
            // mas), o quien apuesta muchisimo siendo suelto. Antes bastaba con VPIP alto o 3Bet alto: en
            // ficticio salian "Maniac" pagadores de 86/12 con AF 0,5, y el heroe por resubir el 13% en 107
            // manos (20-11-2026). Quien juega muchisimo sin apretar es un pagador: "Loose pasivo".
            var l = Current.Value;
            if (vpip >= l.FishVpip && pfr <= l.FishPfr && af < l.FishAf) return "Fish";
            if (vpip >= l.ManiacVpip && (pfr >= l.LagPfr || af >= l.ManiacLooseAf) || af >= l.ManiacAf && vpip >= l.LagVpip) return "Maniac";
            if (vpip >= l.LoosePassiveVpip && (pfr < l.LoosePassivePfr || vpip >= l.ManiacVpip)) return LocalizationManager.Text("Tag.LoosePassive");
            if (vpip >= l.LagVpip && pfr >= l.LagPfr) return "LAG";
            if (vpip >= l.TagVpipMin && vpip < l.TagVpipMax && pfr >= l.TagPfrMin && pfr < l.TagPfrMax) return "TAG";
            if (vpip < l.NitVpip && pfr < l.NitPfr) return "Nit";
            if (vpip < l.TightVpip && pfr < l.TightPfr) return "Tight";
            if (af < l.PassiveAf) return LocalizationManager.Text("Tag.Passive");
            return LocalizationManager.Text("Tag.Regular");
        }

        /// <summary>Con menos manos que esto el tipo ni se sospecha: "Poca muestra".</summary>
        public const int TentativeHands = 15;

        /// <summary>
        /// Desde aqui el tipo deja de llevar "?". Medido el 20-11-2026 con 1.788 jugadores reales de NL25:
        /// con 30 manos el tipo exacto acierta el 36% (la familia el 68%); con 100, el 58% (81%); con
        /// 500, el 82% (91%). El mismo limite que usa el consejo para hacer faroles a un rival.
        /// </summary>
        public const int SureHands = 100;

        /// <summary>
        /// Tipo de jugador con su color, como lo muestra el HUD de la mesa (y el informe): con 100 manos o
        /// mas, el de APH; de 15 a 99, el mismo con "?" (todavia es una sospecha); con menos, "Poca muestra".
        /// </summary>
        public static (string Label, System.Windows.Media.Color Color) Tag(int hands, double vpip, double pfr, double threeBet, double af)
        {
            if (hands < TentativeHands)
                return (LocalizationManager.Text("Hud.FewHands"), System.Windows.Media.Color.FromRgb(70, 80, 94));

            var tentative = hands < SureHands;
            var label = Classify(Math.Max(hands, MinHands), vpip, pfr, threeBet, af);
            return (tentative ? label + "?" : label, ColorOf(label));
        }

        /// <summary>El color de cada tipo (el mismo en el HUD y en el informe).</summary>
        public static System.Windows.Media.Color ColorOf(string label) => label switch
        {
            "Fish" => System.Windows.Media.Color.FromRgb(46, 158, 91),
            "Maniac" => System.Windows.Media.Color.FromRgb(214, 69, 80),
            "LAG" => System.Windows.Media.Color.FromRgb(224, 123, 46),
            "TAG" => System.Windows.Media.Color.FromRgb(59, 130, 196),
            "Nit" => System.Windows.Media.Color.FromRgb(107, 122, 143),
            "Tight" => System.Windows.Media.Color.FromRgb(74, 144, 164),
            _ when label == LocalizationManager.Text("Tag.LoosePassive") => System.Windows.Media.Color.FromRgb(120, 168, 52),
            _ when label == LocalizationManager.Text("Tag.Passive") => System.Windows.Media.Color.FromRgb(150, 138, 82),
            _ when label == LocalizationManager.Text("Hud.FewHands") => System.Windows.Media.Color.FromRgb(70, 80, 94),
            _ => System.Windows.Media.Color.FromRgb(92, 102, 112)
        };

        /// <summary>
        /// Que es cada tipo, con los limites que de verdad se usan (para la leyenda del informe), en el
        /// orden en que se miran: el primero que encaja es el tipo.
        /// </summary>
        public static IReadOnlyList<(string Label, System.Windows.Media.Color Color, string Meaning)> Legend(IFormatProvider culture)
        {
            var l = Current.Value;
            string N(double value) => value.ToString("0.#", culture);
            (string, System.Windows.Media.Color, string) Item(string label, string meaning) => (label, ColorOf(label), meaning);
            return new[]
            {
                Item("Fish", $"VPIP {N(l.FishVpip)}% o más, PFR {N(l.FishPfr)}% o menos y AF menos de {N(l.FishAf)}: entra mucho y paga"),
                Item("Maniac", $"VPIP {N(l.ManiacVpip)}% o más con PFR {N(l.LagPfr)}% o más o AF {N(l.ManiacLooseAf)} o más; o AF {N(l.ManiacAf)} o más con VPIP {N(l.LagVpip)}% o más: juega muchísimo y aprieta"),
                Item(LocalizationManager.Text("Tag.LoosePassive"), $"VPIP {N(l.LoosePassiveVpip)}% o más y PFR menos de {N(l.LoosePassivePfr)}%, o VPIP {N(l.ManiacVpip)}% o más sin apretar: pagador"),
                Item("LAG", $"VPIP {N(l.LagVpip)}% o más y PFR {N(l.LagPfr)}% o más: suelto y agresivo"),
                Item("TAG", $"VPIP {N(l.TagVpipMin)}–{N(l.TagVpipMax)}% y PFR {N(l.TagPfrMin)}–{N(l.TagPfrMax)}%: selectivo y agresivo"),
                Item("Nit", $"VPIP menos de {N(l.NitVpip)}% y PFR menos de {N(l.NitPfr)}%: muy cerrado"),
                Item("Tight", $"VPIP menos de {N(l.TightVpip)}% y PFR menos de {N(l.TightPfr)}%: cerrado"),
                Item(LocalizationManager.Text("Tag.Passive"), $"AF menos de {N(l.PassiveAf)}: casi no apuesta"),
                Item(LocalizationManager.Text("Tag.Regular"), "sin un estilo marcado"),
                Item(LocalizationManager.Text("Hud.FewHands"), $"menos de {TentativeHands} manos; de {TentativeHands} a {SureHands - 1} el tipo lleva \"?\": aún es una sospecha (con 30 manos acierta 1 de cada 3)")
            };
        }

        /// <summary>Los limites en uso, para revisarlos (log y pruebas).</summary>
        public static string Describe()
        {
            var l = Current.Value;
            return string.Join("; ",
                FormattableString.Invariant($"Fish VPIP>={l.FishVpip:0.#} PFR<={l.FishPfr:0.#} AF<{l.FishAf:0.##}"),
                FormattableString.Invariant($"Maniac VPIP>={l.ManiacVpip:0.#} y (PFR>={l.LagPfr:0.#} o AF>={l.ManiacLooseAf:0.##}), o AF>={l.ManiacAf:0.##} con VPIP>={l.LagVpip:0.#}"),
                FormattableString.Invariant($"LoosePassive VPIP>={l.LoosePassiveVpip:0.#} y (PFR<{l.LoosePassivePfr:0.#} o VPIP>={l.ManiacVpip:0.#})"),
                FormattableString.Invariant($"LAG VPIP>={l.LagVpip:0.#} PFR>={l.LagPfr:0.#}"),
                FormattableString.Invariant($"TAG VPIP {l.TagVpipMin:0.#}-{l.TagVpipMax:0.#} PFR {l.TagPfrMin:0.#}-{l.TagPfrMax:0.#}"),
                FormattableString.Invariant($"Nit VPIP<{l.NitVpip:0.#} PFR<{l.NitPfr:0.#}"),
                FormattableString.Invariant($"Tight VPIP<{l.TightVpip:0.#} PFR<{l.TightPfr:0.#}"),
                FormattableString.Invariant($"Pasivo AF<{l.PassiveAf:0.##}"));
        }
    }
}
