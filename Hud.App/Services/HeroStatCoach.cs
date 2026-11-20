using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Media;

namespace Hud.App.Services
{
    /// <summary>Veredicto de una stat del heroe: el semaforo de la retroalimentacion.</summary>
    public enum StatVerdict
    {
        Good,
        Adjust,
        Bad
    }

    /// <summary>Las 10 stats del heroe que se evaluan.</summary>
    public sealed record HeroStatInput(
        int Hands,
        double Vpip,
        double Pfr,
        double ThreeBet,
        double Af,
        double Afq,
        double CBet,
        double FoldVsCBet,
        double Wtsd,
        double Wsd,
        double Wwsf)
    {
        public double ValueOf(string key) => key switch
        {
            "VPIP" => Vpip,
            "PFR" => Pfr,
            "THREEBET" => ThreeBet,
            "AF" => Af,
            "AFQ" => Afq,
            "CBF" => CBet,
            "FVCBF" => FoldVsCBet,
            "WTSD" => Wtsd,
            "WSD" => Wsd,
            _ => Wwsf
        };

        /// <summary>
        /// Cuantas veces hubo ocasion de cada stat ("FVCBF" = c-bets afrontadas, "CBF" = ocasiones de
        /// c-bet...), si se sabe. Con pocas, la stat no se juzga: en el informe de 6 mesas el FvCB salia
        /// "a mejorar" con 5 c-bets afrontadas.
        /// </summary>
        public IReadOnlyDictionary<string, int>? Opportunities { get; init; }

        /// <summary>
        /// Con mano fuerte (le gana menos del 5% de las manos posibles) y nada que pagar despues del
        /// flop: cuantas veces fue y cuantas apostaste. Es lo que de verdad dice si eres pasivo con tus
        /// manos buenas; el AF tambien baja por no farolear, que en ficticio es lo correcto.
        /// </summary>
        public (int Spots, int Bets)? StrongValue { get; init; }

        /// <summary>% de apuesta con mano fuerte, si hay muestra suficiente (<see cref="HeroStatCoach.MinStrongSpots"/>).</summary>
        public double? StrongValuePct =>
            StrongValue is { } value && value.Spots >= HeroStatCoach.MinStrongSpots ? value.Bets * 100.0 / value.Spots : null;
    }

    /// <summary>Una mano con error de la correccion: cartas ("A4s"), que hizo, que decia APH y lo que perdio sin la suerte de los all-in.</summary>
    public sealed record DecisionMistake(string Cards, string Chosen, string? Advice, double AdjustedBb);

    /// <summary>El semaforo de las decisiones de la sesion (errores de la correccion y consejos seguidos).</summary>
    public sealed record DecisionFeedback(
        int Decisions,
        int WithAdvice,
        int Followed,
        int Mistakes,
        IReadOnlyList<DecisionMistake> Costly,
        StatVerdict Verdict,
        string Advice)
    {
        public double FollowedPct => WithAdvice == 0 ? 100 : Followed * 100.0 / WithAdvice;
        public double CostlyBb => Costly.Sum(item => item.AdjustedBb);
        public string VerdictLabel => HeroStatCoach.LabelOf(Verdict);
        public string VerdictIcon => HeroStatCoach.IconOf(Verdict);

        public string Summary =>
            $"{Mistakes} {(Mistakes == 1 ? "error" : "errores")} en {Decisions.ToString("N0", CultureInfo.CurrentCulture)} decisiones · " +
            $"seguiste el consejo en el {FollowedPct.ToString("0", CultureInfo.CurrentCulture)}%" +
            (Costly.Count == 0 ? "" : $" · {Costly.Count} {(Costly.Count == 1 ? "error caro" : "errores caros")}");

        public string Sentence => $"Decisiones: {Advice}";
    }

    /// <summary>Etiqueta del heroe con el veredicto de su stat.</summary>
    public sealed record HeroTag(string Text, string Description, StatVerdict Verdict);

    /// <summary>Una stat del heroe con su nivel (la escala de colores), su veredicto y que hacer.</summary>
    public sealed record StatFeedback(string Key, double Value, StatLevel Level, StatVerdict Verdict, string Advice)
    {
        public string Name => StatScale.DisplayName(Key);
        public string ValueLabel => StatScale.Format(Key, Value);
        public string LevelLabel => StatScale.LabelOf(Level);
        public string VerdictLabel => HeroStatCoach.LabelOf(Verdict);
        public string VerdictIcon => HeroStatCoach.IconOf(Verdict);

        /// <summary>"VPIP 19,4% (algo bajo): ..." para notas y avisos.</summary>
        public string Sentence =>
            $"{Name} {ValueLabel} ({LevelLabel.ToLower(CultureInfo.CurrentCulture)}): {Advice}";
    }

    /// <summary>
    /// Retroalimentacion de las stats del heroe con semaforo: verde = bien, ambar = por ajustar,
    /// rojo = a mejorar. El nivel (alto, bajo, normal) sale de la escala unica de APH
    /// (<see cref="StatScale"/>); el veredicto dice si eso le conviene al heroe. En dinero ficticio
    /// los rivales pagan casi todo, asi que se premia jugar cerrado y por valor y nunca se aconseja
    /// robar o farolear mas (el usuario lo pidio asi): una CBet baja o un WWSF algo bajo estan bien,
    /// y lo que se corrige es la pasividad con mano, los faroles y los pagos flojos al final.
    /// Las notas del informe, los avisos y la pestana del perfil usan esta misma retroalimentacion.
    /// </summary>
    public static class HeroStatCoach
    {
        public static IReadOnlyList<StatFeedback> Evaluate(HeroStatInput stats, bool playMoney, StakeProfile stake = StakeProfile.Low)
        {
            if (stats.Hands < StatScale.MinHands)
                return Array.Empty<StatFeedback>();

            StatLevel L(string key) => StatScale.Level(key, stats.ValueOf(key), stats.Hands, stake);
            var result = new List<StatFeedback>();
            void Add(string key, StatVerdict verdict, string advice) =>
                result.Add(new StatFeedback(key, stats.ValueOf(key), L(key), verdict, advice));

            // VPIP: cuantas manos juegas.
            switch (L("VPIP"))
            {
                case StatLevel.VeryHigh: Add("VPIP", StatVerdict.Bad, "Juegas demasiadas manos: quédate con parejas, ases y figuras del mismo palo, y entra subiendo."); break;
                case StatLevel.High: Add("VPIP", StatVerdict.Bad, "Juegas muchas manos: quita las flojas, sobre todo desde las primeras posiciones y las ciegas."); break;
                case StatLevel.SlightlyHigh: Add("VPIP", StatVerdict.Adjust, "Algo suelto: revisa las manos marginales que juegas desde UTG y MP."); break;
                case StatLevel.Normal: Add("VPIP", StatVerdict.Good, "Entras en una cantidad normal de manos."); break;
                case StatLevel.SlightlyLow:
                    Add("VPIP", StatVerdict.Good, playMoney
                        ? "Juegas más cerrado que la mayoría: en ficticio te conviene, entras con manos que cobran."
                        : "Algo cerrado: bien, sin pasarte.");
                    break;
                case StatLevel.Low:
                    Add("VPIP", StatVerdict.Adjust, "Muy cerrado: puedes jugar más manos con valor en posición (parejas medianas, ases del mismo palo, figuras).");
                    break;
                default:
                    Add("VPIP", playMoney ? StatVerdict.Adjust : StatVerdict.Bad,
                        "Demasiado cerrado: dejas pasar manos que cobran; abre parejas, ases del mismo palo y figuras en posición.");
                    break;
            }

            // PFR: subir antes del flop, y la diferencia con el VPIP (manos que entras pagando).
            var gap = stats.Vpip - stats.Pfr;
            var (pfrVerdict, pfrAdvice) = L("PFR") switch
            {
                StatLevel.VeryHigh => (StatVerdict.Bad, "Subes demasiado antes del flop: sube con manos que quieran ser pagadas."),
                StatLevel.High => (StatVerdict.Adjust, playMoney
                    ? "Subes mucho: en ficticio sube por valor, no para robar ciegas (casi siempre te pagan)."
                    : "Subes mucho: revisa las subidas desde las primeras posiciones."),
                StatLevel.Low => (StatVerdict.Adjust, "Subes poco: cuando entres en una mano buena, entra subiendo."),
                StatLevel.VeryLow => (StatVerdict.Bad, "Casi no subes: entras pagando y le das la iniciativa al rival."),
                _ => (StatVerdict.Good, "Subes antes del flop con una frecuencia sana.")
            };
            if (gap >= 12 && pfrVerdict != StatVerdict.Bad)
            {
                pfrVerdict = gap >= 18 ? StatVerdict.Bad : StatVerdict.Adjust;
                pfrAdvice = $"Entre tu VPIP y tu PFR hay {gap.ToString("0", CultureInfo.CurrentCulture)} puntos: entras pagando demasiado; sube o retírate.";
            }
            Add("PFR", pfrVerdict, pfrAdvice);

            // 3Bet: resubir. En ficticio se resube por valor (te pagan con peores).
            switch (L("THREEBET"))
            {
                case StatLevel.VeryHigh:
                    Add("THREEBET", playMoney ? StatVerdict.Adjust : StatVerdict.Bad, playMoney
                        ? "Resubes muchísimo: en ficticio resube por valor (QQ+, AK, AQ); los faroles no funcionan."
                        : "Resubes demasiado: te pagan o te resuben con manos mejores.");
                    break;
                case StatLevel.High:
                    Add("THREEBET", playMoney ? StatVerdict.Good : StatVerdict.Adjust, playMoney
                        ? "Resubes bastante: bien si es por valor contra rivales que abren y pagan mucho."
                        : "Resubes bastante: vigila que tu resubida no lleve demasiados faroles.");
                    break;
                case StatLevel.SlightlyHigh: Add("THREEBET", StatVerdict.Good, "Resubes algo más que la mayoría: bien contra rivales sueltos."); break;
                case StatLevel.Normal: Add("THREEBET", StatVerdict.Good, "Resubes con una frecuencia normal."); break;
                case StatLevel.SlightlyLow:
                    Add("THREEBET", playMoney ? StatVerdict.Adjust : StatVerdict.Good, playMoney
                        ? "Resubes poco: en ficticio resube más por valor; te pagan con manos peores."
                        : "Resubes poco, pero dentro de lo razonable.");
                    break;
                default:
                    Add("THREEBET", StatVerdict.Adjust, "Casi no resubes: con QQ+, AK y AQ resube siempre, te pagan con peores.");
                    break;
            }

            // Con mano fuerte, ¿apuestas? (de los historiales: sirve en ficticio y en real).
            var valuePct = stats.StrongValuePct;
            var valueText = stats.StrongValue is { } strong && valuePct is { } pct
                ? $"con mano fuerte apostaste el {pct.ToString("0", CultureInfo.CurrentCulture)}% ({strong.Bets} de {strong.Spots})"
                : null;
            var valueOk = valuePct >= GoodStrongValuePct;

            // AF y AFq: agresion despues del flop. En ficticio un AF bajo sale sobre todo de no farolear
            // con las manos flojas, que es lo correcto ahi: 20-11-2026, siguiendo el consejo en el 97% el AF
            // era 1 (muy bajo) y aun asi con mano fuerte se apostaba 9 de cada 10 veces. Lo que se juzga es
            // si apuestas tus manos fuertes. En real la pasividad si cuesta: faltan apuestas finas de valor
            // y semi-faroles, aunque las manos fuertes las apuestes.
            foreach (var key in new[] { "AF", "AFQ" })
            {
                var level = L(key);
                if (level is StatLevel.VeryLow or StatLevel.Low or StatLevel.SlightlyLow)
                {
                    if (playMoney && valueText is not null)
                    {
                        if (valueOk)
                            Add(key, StatVerdict.Good, $"Bajo porque no haces faroles, que en ficticio es lo correcto: {valueText}.");
                        else
                            Add(key, level == StatLevel.VeryLow ? StatVerdict.Bad : StatVerdict.Adjust,
                                $"Pasivo con tus manos buenas: {valueText}. Cóbralas, en ficticio te pagan.");
                        continue;
                    }
                    if (playMoney)
                    {
                        Add(key, StatVerdict.Adjust, "Bajo: en ficticio es normal si no haces faroles; asegúrate de apostar tus manos fuertes.");
                        continue;
                    }
                    if (valueText is not null && valueOk)
                    {
                        Add(key, level == StatLevel.SlightlyLow ? StatVerdict.Adjust : StatVerdict.Bad,
                            $"Tus manos fuertes sí las apuestas ({valueText}), pero en dinero real lo que falta son apuestas finas por valor y semi-faroles con proyecto.");
                        continue;
                    }
                }
                switch (level)
                {
                    case StatLevel.VeryLow: Add(key, StatVerdict.Bad, "Muy pasivo después del flop: apuesta tus manos buenas, te pagan" + (valueText is null ? "." : $" ({valueText}).")); break;
                    case StatLevel.Low: Add(key, StatVerdict.Adjust, "Pasivo después del flop: apuesta tus manos hechas en vez de pasar y pagar" + (valueText is null ? "." : $" ({valueText}).")); break;
                    case StatLevel.SlightlyLow: Add(key, StatVerdict.Adjust, "Algo pasivo: cuando tengas mano, apuesta tú en lugar de pagar" + (valueText is null ? "." : $" ({valueText}).")); break;
                    case StatLevel.Normal: Add(key, StatVerdict.Good, "Agresión normal después del flop."); break;
                    case StatLevel.SlightlyHigh:
                        Add(key, StatVerdict.Good, playMoney
                            ? "Apuestas tus manos: bien, en ficticio la apuesta por valor es lo que más gana."
                            : "Agresivo después del flop: bien si es con mano o con proyecto.");
                        break;
                    case StatLevel.High:
                        Add(key, StatVerdict.Adjust, playMoney
                            ? "Muy agresivo: revisa que no sean faroles; en ficticio casi nadie se retira."
                            : "Muy agresivo: revisa los barrels y las subidas sin equity.");
                        break;
                    default: Add(key, StatVerdict.Bad, "Agresión excesiva: demasiados faroles o subidas sin mano; frena en los boards malos."); break;
                }
            }

            // CBet: en ficticio apostar el flop solo con mano esta bien (no se aconseja apostar mas sin nada).
            var cbet = L("CBF");
            if (playMoney)
            {
                switch (cbet)
                {
                    case StatLevel.VeryHigh: Add("CBF", StatVerdict.Bad, "Apuestas casi todos los flops: en ficticio te pagan; apuesta solo con pareja o proyecto fuerte."); break;
                    case StatLevel.High: Add("CBF", StatVerdict.Adjust, "Apuestas muchos flops: quita las apuestas sin mano, aquí no se retiran."); break;
                    case StatLevel.SlightlyHigh: Add("CBF", StatVerdict.Adjust, "Apuestas bastantes flops: revisa los que apuestas sin pareja ni proyecto."); break;
                    case StatLevel.VeryLow:
                        Add("CBF", valueOk ? StatVerdict.Good : StatVerdict.Adjust, valueOk
                            ? $"Apuestas pocos flops porque solo apuestas con mano: en ficticio está bien; {valueText}."
                            : "Apuestas muy pocos flops: cuando conectes pareja o un proyecto fuerte, apuesta; te pagan.");
                        break;
                    case StatLevel.Normal: Add("CBF", StatVerdict.Good, "Sigues apostando el flop con una frecuencia normal."); break;
                    default: Add("CBF", StatVerdict.Good, "Apuestas el flop solo con mano: en ficticio está bien, los faroles no funcionan."); break;
                }
            }
            else
            {
                switch (cbet)
                {
                    case StatLevel.VeryHigh: Add("CBF", StatVerdict.Bad, "Apuestas casi todos los flops: te leen y te suben."); break;
                    case StatLevel.High: Add("CBF", StatVerdict.Adjust, "Apuestas muchos flops: frena en los boards que no te favorecen."); break;
                    case StatLevel.Low: Add("CBF", StatVerdict.Adjust, "Apuestas poco el flop: dejas pasar botes que podrías ganar."); break;
                    case StatLevel.VeryLow: Add("CBF", StatVerdict.Bad, "Casi no apuestas el flop con la iniciativa: regalas botes."); break;
                    default: Add("CBF", StatVerdict.Good, "Apuestas el flop con una frecuencia sana."); break;
                }
            }

            // FvCB: cuanto te retiras ante la apuesta del flop.
            switch (L("FVCBF"))
            {
                case StatLevel.VeryHigh: Add("FVCBF", StatVerdict.Bad, "Te retiras casi siempre ante la apuesta del flop: defiende con pareja y proyectos."); break;
                case StatLevel.High: Add("FVCBF", StatVerdict.Adjust, "Te retiras mucho ante la apuesta del flop: no sueltes las parejas ni los proyectos buenos."); break;
                case StatLevel.SlightlyHigh: Add("FVCBF", StatVerdict.Good, "Te retiras algo más que la mayoría ante la apuesta del flop: bien si es sin pareja ni proyecto."); break;
                case StatLevel.Normal: Add("FVCBF", StatVerdict.Good, "Te defiendes de forma normal ante la apuesta del flop."); break;
                case StatLevel.SlightlyLow: Add("FVCBF", StatVerdict.Good, "Pagas bastante la apuesta del flop: bien si es con pareja o proyecto."); break;
                case StatLevel.Low: Add("FVCBF", StatVerdict.Adjust, "Casi no te retiras ante la apuesta del flop: sin pareja ni proyecto, retírate."); break;
                default: Add("FVCBF", StatVerdict.Bad, "Nunca te retiras ante la apuesta del flop: pagas con manos que no ganan."); break;
            }

            // WTSD con W$SD: llegar mucho al showdown solo es malo si se pierde.
            var wtsd = L("WTSD");
            var wsd = L("WSD");
            var losingShowdowns = wsd is StatLevel.SlightlyLow or StatLevel.Low or StatLevel.VeryLow;
            if (wtsd is StatLevel.SlightlyHigh or StatLevel.High or StatLevel.VeryHigh && losingShowdowns)
                Add("WTSD", StatVerdict.Bad, "Llegas mucho al showdown y pierdes: paga menos en el turn y el river con manos medias.");
            else
            {
                switch (wtsd)
                {
                    case StatLevel.VeryHigh: Add("WTSD", StatVerdict.Adjust, "Llegas muchísimo al showdown: filtra los pagos del river con manos medias."); break;
                    case StatLevel.High:
                    case StatLevel.SlightlyHigh:
                        Add("WTSD", playMoney ? StatVerdict.Good : StatVerdict.Adjust, playMoney
                            ? "Llegas mucho al showdown y ganas lo normal: en ficticio es lógico, te pagan hasta el final."
                            : "Llegas mucho al showdown: revisa los pagos del river.");
                        break;
                    case StatLevel.Low:
                    case StatLevel.VeryLow:
                        Add("WTSD", StatVerdict.Adjust, "Abandonas mucho antes del showdown: no sueltes tan fácil una pareja buena.");
                        break;
                    default: Add("WTSD", StatVerdict.Good, "Llegas al showdown con una frecuencia normal."); break;
                }
            }

            // W$SD: con que llegas al showdown.
            switch (wsd)
            {
                case StatLevel.VeryLow: Add("WSD", StatVerdict.Bad, "Llegas al showdown con manos flojas: deja de pagar el river con manos medias."); break;
                case StatLevel.Low: Add("WSD", StatVerdict.Bad, "Pierdes muchos showdowns: paga menos en el river con manos medias."); break;
                case StatLevel.SlightlyLow: Add("WSD", StatVerdict.Adjust, "Pierdes algo más de showdowns de lo normal: revisa tus pagos del river."); break;
                case StatLevel.Normal: Add("WSD", StatVerdict.Good, "Ganas una parte normal de los showdowns."); break;
                case StatLevel.VeryHigh:
                    Add("WSD", playMoney ? StatVerdict.Good : StatVerdict.Adjust, playMoney
                        ? "Solo llegas al showdown con manos muy fuertes: bien en ficticio."
                        : "Solo llegas con manos muy fuertes: puedes pagar algo más.");
                    break;
                default: Add("WSD", StatVerdict.Good, "Llegas al showdown con buenas manos."); break;
            }

            // WWSF: en ficticio casi nadie se retira, asi que algo bajo es normal.
            switch (L("WWSF"))
            {
                case StatLevel.VeryLow when playMoney && valueOk:
                    Add("WWSF", StatVerdict.Good,
                        $"Ganas pocos botes sin llegar al final: en ficticio es normal, casi nadie se retira, y tus manos fuertes sí las apuestas: {valueText}.");
                    break;
                case StatLevel.VeryLow:
                    Add("WWSF", playMoney ? StatVerdict.Adjust : StatVerdict.Bad, playMoney
                        ? "Ganas muy pocos botes después del flop: apuesta tus manos hechas para cobrarlas."
                        : "Ganas muy pocos botes después del flop: presiona más cuando tengas la iniciativa.");
                    break;
                case StatLevel.Low:
                case StatLevel.SlightlyLow:
                    Add("WWSF", playMoney ? StatVerdict.Good : StatVerdict.Adjust, playMoney
                        ? "Ganas menos botes sin showdown que la mayoría: en ficticio es normal, aquí casi nadie se retira."
                        : "Ganas pocos botes después del flop: presiona más cuando tengas la iniciativa.");
                    break;
                default: Add("WWSF", StatVerdict.Good, "Ganas una buena parte de los botes después del flop."); break;
            }

            // Sin bastantes ocasiones, la stat no se juzga (ni color de veredicto ni enfoque).
            if (stats.Opportunities is { } opportunities)
                result.RemoveAll(item => opportunities.TryGetValue(item.Key, out var count) && count < MinOpportunities(item.Key));

            // Primero lo que hay que mejorar, luego lo que hay que ajustar y al final lo que va bien.
            return result
                .OrderByDescending(item => item.Verdict)
                .ThenBy(item => Array.IndexOf(StatScale.Keys, item.Key))
                .ToList();
        }

        /// <summary>Veces con mano fuerte y nada que pagar para juzgar si apuestas tus manos buenas.</summary>
        public const int MinStrongSpots = 12;

        /// <summary>Con mano fuerte, apostar al menos esto es cobrar tus manos (el consejo de APH apuesta ~90%).</summary>
        public const double GoodStrongValuePct = 75;

        /// <summary>Ocasiones minimas para juzgar una stat.</summary>
        public static int MinOpportunities(string key) => key switch
        {
            "CBF" or "FVCBF" or "WSD" => 15,
            "AF" or "AFQ" => 20,
            "THREEBET" or "WTSD" or "WWSF" => 30,
            _ => 0
        };

        public static string LabelOf(StatVerdict verdict) => verdict switch
        {
            StatVerdict.Good => "Bien",
            StatVerdict.Adjust => "Por ajustar",
            _ => "A mejorar"
        };

        public static string IconOf(StatVerdict verdict) => verdict switch
        {
            StatVerdict.Good => "✓",
            StatVerdict.Adjust => "!",
            _ => "✗"
        };

        /// <summary>Verde bien, ambar por ajustar, rojo a mejorar (siempre con su icono y su palabra).</summary>
        public static Color ColorOf(StatVerdict verdict) => verdict switch
        {
            StatVerdict.Good => Color.FromRgb(22, 135, 84),
            StatVerdict.Adjust => Color.FromRgb(196, 124, 16),
            _ => Color.FromRgb(196, 50, 60)
        };

        /// <summary>Una mano con error es cara si, sin la suerte de los all-in, perdio esto o mas.</summary>
        public const double CostlyMistakeBb = 50;

        /// <summary>
        /// Semaforo de las decisiones: verde si casi no hubo errores, ambar si hubo errores caros o pocos
        /// consejos seguidos, rojo si fueron muchos. Lo que cuenta es lo que se perdio SIN la suerte de los
        /// all-in: un buen all-in que perdio por mala suerte no es un error caro (por ejemplo A4s contra la
        /// basura de alguien que empuja todo). Con menos de 30 decisiones registradas no hay veredicto.
        /// </summary>
        public static DecisionFeedback? EvaluateDecisions(int decisions, int withAdvice, int followed, int mistakes,
            IReadOnlyList<DecisionMistake> mistakeHands)
        {
            if (decisions < 30)
                return null;

            var rate = mistakes * 100.0 / decisions;
            var costly = mistakeHands.Where(item => item.AdjustedBb <= -CostlyMistakeBb).OrderBy(item => item.AdjustedBb).ToList();
            var followedPct = withAdvice == 0 ? 100 : followed * 100.0 / withAdvice;
            var lowFollow = withAdvice >= 30;
            var verdict = rate >= 6 || costly.Count >= 4 || lowFollow && followedPct < 75
                ? StatVerdict.Bad
                : rate >= 3 || costly.Count >= 1 || lowFollow && followedPct < 90
                    ? StatVerdict.Adjust
                    : StatVerdict.Good;

            string advice;
            if (verdict == StatVerdict.Good)
            {
                advice = $"Seguiste el consejo en el {followedPct.ToString("0", CultureInfo.CurrentCulture)}% y no hubo errores caros: decisiones sólidas.";
            }
            else
            {
                var parts = new List<string>();
                if (costly.Count > 0)
                    parts.Add($"{costly.Count} {(costly.Count == 1 ? "error caro" : "errores caros")} ({string.Join(", ", costly.Take(4).Select(item => item.Cards))}): " +
                              $"{costly.Sum(item => item.AdjustedBb).ToString("+0;-0;0", CultureInfo.CurrentCulture)} bb sin la suerte de los all-in.");
                else
                    parts.Add($"{mistakes} errores ({rate.ToString("0.#", CultureInfo.CurrentCulture)}% de tus decisiones).");
                if (costly.Any(item => item.Chosen.Contains("all-in", StringComparison.OrdinalIgnoreCase) &&
                                       !(item.Advice ?? "").Contains("ALL-IN", StringComparison.OrdinalIgnoreCase)))
                    parts.Add("Evita los all-in que APH no recomienda.");
                if (lowFollow && followedPct < 90)
                    parts.Add($"Seguiste el consejo solo en el {followedPct.ToString("0", CultureInfo.CurrentCulture)}%.");
                parts.Add(verdict == StatVerdict.Bad
                    ? "Repasa esas manos y, si notas cansancio o enojo, para la sesión."
                    : costly.Count == 1 ? "Repásala en «Cómo jugaste»." : "Repásalas en «Cómo jugaste».");
                advice = string.Join(" ", parts);
            }

            return new DecisionFeedback(decisions, withAdvice, followed, mistakes, costly, verdict, advice);
        }

        /// <summary>
        /// Etiquetas del heroe (panel y perfil) a partir de la retroalimentacion: "VPIP algo bajo",
        /// "WTSD alto"... con el color del veredicto (verde bien, ambar por ajustar, rojo a mejorar).
        /// Las stats normales y bien no llevan etiqueta (no hay nada que decir). Antes cada etiqueta tenia
        /// sus propios numeros fijos y podia contradecir a los colores de las stats.
        /// </summary>
        public static IReadOnlyList<HeroTag> Tags(IReadOnlyList<StatFeedback> feedback) =>
            feedback
                .Where(item => item.Verdict != StatVerdict.Good || item.Level != StatLevel.Normal)
                .Select(item => new HeroTag(
                    $"{item.Name} {item.LevelLabel.ToLower(CultureInfo.CurrentCulture)}",
                    $"{item.VerdictIcon} {item.VerdictLabel}: {item.Advice} ({StatScale.RangeOf(item.Level).ToLower(CultureInfo.CurrentCulture)}.)",
                    item.Verdict))
                .ToList();

        /// <summary>
        /// El consejo principal: lo peor que hay que mejorar (las decisiones van primero si estan igual o
        /// peor que la peor stat), o un "sigue asi".
        /// </summary>
        public static string Focus(IReadOnlyList<StatFeedback> feedback, DecisionFeedback? decisions = null)
        {
            var worst = feedback.FirstOrDefault(item => item.Verdict != StatVerdict.Good);
            if (decisions is { Verdict: not StatVerdict.Good } && (worst is null || decisions.Verdict >= worst.Verdict))
                return decisions.Sentence;
            if (worst is not null)
                return $"{worst.Name}: {worst.Advice}";
            return decisions is null
                ? "Tus stats están bien para esta muestra: sigue jugando cerrado y por valor, y acumula manos."
                : "Tus stats y tus decisiones están bien: sigue jugando cerrado y por valor, y acumula manos.";
        }
    }
}
