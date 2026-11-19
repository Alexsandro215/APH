using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Hud.App.Services.Trainer
{
    public enum TrainerGrade
    {
        Good,
        Marginal,
        Mistake
    }

    /// <summary>Una decision del alumno ya calificada, con la explicacion para el coach.</summary>
    public sealed class TrainerDecision
    {
        public string HandId { get; init; } = "";
        public string Street { get; init; } = "";
        public string Position { get; init; } = "";
        public string HeroCards { get; init; } = "";
        public string Board { get; init; } = "";
        public string Situation { get; init; } = "";
        public string Chosen { get; init; } = "";
        public string Recommended { get; init; } = "";
        public TrainerGrade Grade { get; set; }
        public string Explanation { get; set; } = "";
        public IReadOnlyList<string> Facts { get; init; } = Array.Empty<string>();

        /// <summary>Accion elegida agrupada: Fold, Check, Call o Raise (las apuestas cuentan como Raise).</summary>
        public string ChosenKind { get; init; } = "";

        /// <summary>Tenia una apuesta delante (hay que pagar algo para seguir).</summary>
        public bool FacingBet { get; init; }

        /// <summary>Lo que se le recomendo en vivo en ese turno (si la decision en vivo estaba activa).</summary>
        public string? LiveRecommendation { get; set; }

        /// <summary>De donde salio la recomendacion en vivo: GTO, Chart (tabla APH) o Exploit (solver contra su rango).</summary>
        public string? LiveSource { get; set; }

        /// <summary>El motivo que se mostro en vivo con la recomendacion.</summary>
        public string? LiveReason { get; set; }

        /// <summary>Lo que se leyo en pantalla y el calculo del consejo en vivo (para revisar un consejo raro).</summary>
        public string? LiveDetails { get; set; }

        /// <summary>
        /// Comprobacion con el historial: null si las cartas, el board y la posicion leidos eran los
        /// correctos; si no, que se leyo mal ("posición leída HJ, era BB").
        /// </summary>
        public string? LiveReadCheck { get; set; }

        public bool? FollowedLive { get; set; }

        public string LiveLine => LiveRecommendation is null
            ? ""
            : $"En vivo se recomendo: {LiveRecommendation} ({(FollowedLive == true ? "la siguio" : "no la siguio")})";

        public string GradeLabel => Grade switch
        {
            TrainerGrade.Good => "BIEN",
            TrainerGrade.Marginal => "DUDOSA",
            _ => "ERROR"
        };

        public string StreetLabel => TrainerRangeEstimator.StreetName(Street);
        public string Headline => $"{StreetLabel} | {Position} | {Situation}";
        public string ChoiceLine => $"Eligio: {Chosen}   |   Referencia: {Recommended}";
        public string FactsText => string.Join(Environment.NewLine, Facts.Select(fact => "- " + fact));
    }

    public sealed class TrainerHandReport
    {
        public string HandId { get; init; } = "";

        /// <summary>Mesa de la mano (con varias mesas a la vez, para la lista de decisiones).</summary>
        public string TableName { get; set; } = "";

        public string HeroCards { get; init; } = "";
        public string Board { get; init; } = "";
        public string Position { get; init; } = "";
        public double NetBb { get; init; }
        public DateTime ProcessedAt { get; init; } = DateTime.Now;
        public IReadOnlyList<TrainerDecision> Decisions { get; init; } = Array.Empty<TrainerDecision>();
    }

    /// <summary>
    /// Califica las decisiones del heroe en una mano terminada. Preflop manda la tabla APH del
    /// escenario exacto (open, vs raise, vs 3bet, vs 4bet, vs 5bet); postflop, la equity contra
    /// el rango estimado del rival, el precio del bote y su perfil. Todo es una referencia de
    /// entrenamiento, no un solver: cada veredicto explica de donde sale.
    /// </summary>
    public sealed class TrainerCoach
    {
        private readonly AphPreflopProvider _charts;
        private readonly TrainerRangeEstimator _ranges;

        public TrainerCoach(AphPreflopProvider charts)
        {
            _charts = charts;
            _ranges = new TrainerRangeEstimator(charts);
        }

        public AphPreflopProvider Charts => _charts;
        public TrainerRangeEstimator Ranges => _ranges;

        public static AphPreflopProvider LoadDefaultCharts() =>
            new(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "APH_Reference_100bb_6max.csv"));

        public TrainerHandReport Grade(TrainerHand hand, Func<string, TrainerVillainProfile?> profiles)
        {
            var decisions = new List<TrainerDecision>();
            if (hand.HasHero)
            {
                foreach (var action in hand.Actions)
                {
                    if (action.Kind == TrainerActionKind.Post || !PokerStarsHandHistory.SamePlayer(action.Player, hand.HeroName))
                        continue;

                    decisions.Add(action.Street == "PREFLOP"
                        ? GradePreflop(hand, action, profiles)
                        : GradePostflop(hand, action, profiles));
                }
            }

            var netBb = hand.HasHero
                ? PokerStarsHandHistory.EstimateNetForPlayer(hand.Lines, hand.HeroName) / hand.BigBlind
                : 0;

            return new TrainerHandReport
            {
                HandId = hand.HandId,
                HeroCards = TrainerCards.ToText(hand.HeroCards),
                Board = TrainerCards.ToText(hand.Board),
                Position = hand.SeatOf(hand.HeroName)?.ChartPosition ?? "?",
                NetBb = netBb,
                Decisions = decisions
            };
        }

        // ------------------------------------------------------------------ PREFLOP

        private TrainerDecision GradePreflop(TrainerHand hand, TrainerAction action, Func<string, TrainerVillainProfile?> profiles)
        {
            var heroPos = hand.SeatOf(hand.HeroName)?.ChartPosition ?? "?";
            var combo = TrainerCards.ComboName(hand.HeroCards[0], hand.HeroCards[1]);
            var top = PreflopRanking.TopPercentOf(combo);
            var bb = hand.BigBlind;
            var before = hand.Actions
                .Where(item => item.Street == "PREFLOP" && item.Index < action.Index && item.Kind != TrainerActionKind.Post)
                .ToList();

            bool IsHero(TrainerAction item) => PokerStarsHandHistory.SamePlayer(item.Player, hand.HeroName);
            string PosOf(TrainerAction item) => hand.SeatOf(item.Player)?.ChartPosition ?? "?";

            // Una subida all-in incompleta no reabre la accion ni cambia el escenario de la tabla.
            var raises = before.Where(item => item.Kind == TrainerActionKind.Raise && !item.IsIncompleteRaise).ToList();
            // Entradas gratis de otros: limps y el check de quien puso ciega grande fuera de su sitio
            // (jugador nuevo). En un bote sin subidas, un check antes del heroe solo puede ser eso.
            var limpers = before.Count(item => !IsHero(item) && item.PreflopRaisesBefore == 0 &&
                                               item.Kind is TrainerActionKind.Call or TrainerActionKind.Check);
            var coldCallers = before.Count(item => item.Kind == TrainerActionKind.Call && item.PreflopRaisesBefore == 1);
            var chosen = Bucket(action);
            var facts = new List<string> { $"Mano: {combo} (top {top:0.#}% de las manos)" };

            var lastRaise = before.LastOrDefault(item => item.Kind == TrainerActionKind.Raise);
            if (lastRaise is not null && !IsHero(lastRaise) &&
                (IsPriceSpot(action, lastRaise) || TrainerDecisionRules.IsOversizedRaise(action.PreflopRaisesBefore, lastRaise.RaiseTo / bb)))
                return GradePreflopByPrice(hand, action, heroPos, combo, lastRaise, chosen, facts, profiles);

            if (action.PreflopRaisesBefore == 0 && limpers > 0)
                return GradeVsLimpers(hand, action, heroPos, combo, top, limpers, chosen, facts);

            if (action.PreflopRaisesBefore == 0 && action.ToCall <= 0)
                return GradeFreeOption(hand, action, heroPos, combo, top, chosen, facts);

            string scenario;
            string villainPos;
            string raiseName;
            string situation;
            TrainerAction? aggressor = null;
            var n = action.PreflopRaisesBefore;

            if (n == 0)
            {
                scenario = "Open";
                villainPos = "";
                raiseName = "Open raise";
                situation = $"Nadie entro antes; abres desde {heroPos}";
            }
            else if (n == 1 && !raises.Any(IsHero))
            {
                aggressor = raises[0];
                villainPos = PosOf(aggressor);
                // Con alguien que ya pago la subida el bote es multiway: tabla de squeeze, no la de
                // uno contra uno (que paga con manos que en multiway no llegan).
                var squeeze = coldCallers > 0 && _charts.HasChart("Squeeze", heroPos, villainPos);
                scenario = squeeze ? "Squeeze" : "vs Raise";
                raiseName = squeeze ? "squeeze" : "3bet";
                situation = $"Open de {villainPos} a {aggressor.RaiseTo / bb:0.#} bb{AllInMark(aggressor)}" +
                            (coldCallers > 0 ? $" + {coldCallers} call(s)" : "");
            }
            else if (n == 2 && raises.Count >= 2 && IsHero(raises[0]))
            {
                aggressor = raises[1];
                scenario = "vs 3bet";
                villainPos = PosOf(aggressor);
                raiseName = "4bet";
                situation = $"Abriste y {villainPos} hizo 3bet a {aggressor.RaiseTo / bb:0.#} bb{AllInMark(aggressor)}";
            }
            else if (n == 3 && raises.Count >= 3 && IsHero(raises[1]))
            {
                aggressor = raises[2];
                scenario = "vs 4bet";
                villainPos = PosOf(aggressor);
                raiseName = "5bet all-in";
                situation = $"Hiciste 3bet y {villainPos} hizo 4bet a {aggressor.RaiseTo / bb:0.#} bb{AllInMark(aggressor)}";
            }
            else if (n == 4 && raises.Count >= 4 && IsHero(raises[2]))
            {
                aggressor = raises[3];
                scenario = "vs 5bet";
                villainPos = PosOf(aggressor);
                raiseName = "all-in";
                situation = $"Hiciste 4bet y {villainPos} metio 5bet";
            }
            else
            {
                aggressor = raises.LastOrDefault();
                return n >= 3 && raises.Any(IsHero) && aggressor is not null
                    ? GradePreflopByPrice(hand, action, heroPos, combo, aggressor, chosen, facts, profiles)
                    : GradeColdMultiRaise(hand, action, heroPos, combo, top, n, aggressor, chosen, facts, profiles);
            }

            facts.Add($"Posicion: {heroPos}" + (string.IsNullOrEmpty(villainPos) ? "" : $" vs {villainPos}"));
            var code = _charts.GetCode(scenario, heroPos, villainPos, combo);
            if (code is null)
            {
                // El CSV no tiene ese cruce de posiciones: un 4bet+ despues de haber subido se decide
                // por precio; el resto, por fuerza de mano.
                return n >= 3 && raises.Any(IsHero) && aggressor is not null
                    ? GradePreflopByPrice(hand, action, heroPos, combo, aggressor, chosen, facts, profiles)
                    : GradeColdMultiRaise(hand, action, heroPos, combo, top, n, aggressor, chosen, facts, profiles);
            }

            var continueWidth = RangeWidth(scenario, heroPos, villainPos, "R", "C");
            var raiseWidth = RangeWidth(scenario, heroPos, villainPos, "R");
            var recommended = code switch { "R" => "Raise", "C" => "Call", _ => "Fold" };
            var recommendedText = code switch { "R" => raiseName, "C" => "Call", _ => "Fold" };
            facts.Add($"Tabla APH {scenario} {heroPos}{(villainPos.Length > 0 ? " vs " + villainPos : "")}: {recommendedText}");
            facts.Add(scenario == "Open"
                ? $"Rango de la tabla: abre ~{raiseWidth:0.#}% de manos"
                : $"Rango de la tabla: continua ~{continueWidth:0.#}% de manos ({raiseWidth:0.#}% subiendo)");

            if (aggressor is not null)
            {
                var profile = profiles(aggressor.Player);
                if (profile?.HasSample == true)
                    facts.Add($"Rival: {profile.Summary}");
            }

            var grade = GradeAgainstChart(recommended, chosen);
            if (grade == TrainerGrade.Mistake && recommended == "Fold" && chosen is "Call" or "Raise" && top <= continueWidth + 4)
                grade = TrainerGrade.Marginal;

            var explanation = ExplainPreflop(combo, heroPos, scenario, recommended, recommendedText, chosen, grade, top, continueWidth);
            explanation += SizingNote(hand, action, scenario, aggressor, coldCallers, facts);

            return new TrainerDecision
            {
                HandId = hand.HandId,
                Street = "PREFLOP",
                Position = heroPos,
                HeroCards = TrainerCards.ToText(hand.HeroCards),
                Situation = situation,
                Chosen = ChosenText(action, bb, chosen == "Raise" ? raiseName : null),
                ChosenKind = Bucket(action),
                FacingBet = action.ToCall > 0,
                Recommended = recommendedText,
                Grade = grade,
                Explanation = explanation,
                Facts = facts
            };
        }

        private static TrainerGrade GradeAgainstChart(string recommended, string chosen)
        {
            if (recommended == chosen)
                return TrainerGrade.Good;

            return (recommended, chosen) switch
            {
                ("Raise", "Call") => TrainerGrade.Marginal,
                ("Raise", "Check") => TrainerGrade.Marginal,
                ("Call", "Raise") => TrainerGrade.Marginal,
                ("Fold", "Check") => TrainerGrade.Good,
                _ => TrainerGrade.Mistake
            };
        }

        private static string ExplainPreflop(
            string combo, string heroPos, string scenario, string recommended, string recommendedText,
            string chosen, TrainerGrade grade, double top, double width)
        {
            var spot = scenario == "Open" ? $"abrir desde {heroPos}" : $"{scenario} en {heroPos}";
            if (grade == TrainerGrade.Good)
                return $"{combo}: la referencia para {spot} es {recommendedText} y eso hiciste. Decision alineada con el rango.";

            return (recommended, chosen) switch
            {
                ("Raise", "Fold") =>
                    $"{combo} (top {top:0.#}%) esta dentro del rango de {recommendedText} para {spot}. Foldear aqui deja dinero en la mesa: es una mano que gana jugando agresivo.",
                ("Call", "Fold") =>
                    $"{combo} entra en el rango de defensa para {spot} (la tabla continua con ~{width:0.#}% de manos). El precio y la posicion justifican pagar; foldear de mas es un leak frecuente.",
                ("Raise", "Call") or ("Raise", "Check") =>
                    $"La referencia juega {combo} con {recommendedText}. Pagar o pasar es mas pasivo: cedes la iniciativa y dejas de construir el bote con una mano de valor.",
                ("Call", "Raise") =>
                    $"La referencia prefiere call con {combo}. Subir puede usarse como mezcla o farol ocasional, pero como norma convierte una mano jugable en un farol caro.",
                ("Fold", _) when grade == TrainerGrade.Marginal =>
                    $"{combo} esta justo en el borde del rango para {spot} (top {top:0.#}% vs rango ~{width:0.#}%). No es un error grave, pero la referencia foldea.",
                ("Fold", _) =>
                    $"{combo} (top {top:0.#}%) queda fuera del rango para {spot}, que continua solo con ~{width:0.#}% de manos. Entrar con manos fuera de rango sube el VPIP y te deja dominado.",
                _ => $"Referencia: {recommendedText}. Elegiste {chosen}."
            };
        }

        private static string SizingNote(TrainerHand hand, TrainerAction action, string scenario, TrainerAction? aggressor, int callers, List<string> facts)
        {
            if (action.Kind != TrainerActionKind.Raise || action.RaiseTo <= 0)
                return "";

            var bb = hand.BigBlind;
            var sizeBb = action.RaiseTo / bb;
            facts.Add($"Tamano: subiste a {sizeBb:0.#} bb" + (action.IsAllIn ? " (all-in)" : ""));

            // Un all-in no se mide por multiplicador. En 4bet/5bet meter el resto es lo normal; de
            // entrada o como 3bet con mucha profundidad, solo te pagan las manos que te dominan.
            if (action.IsAllIn)
                return sizeBb > 25 && scenario is "Open" or "vs Raise" or "Squeeze"
                    ? $" Meter {sizeBb:0} bb all-in {(scenario == "Open" ? "de entrada" : "como 3bet")} es arriesgado: solo te pagan las manos que te dominan. Con esa profundidad la referencia es una subida normal."
                    : "";

            if (scenario == "Open")
            {
                if (sizeBb > 4.5)
                    return $" Ojo con el tamano: {sizeBb:0.#} bb es grande para un open (estandar 2-3 bb, 3 bb desde SB).";
                if (sizeBb < 2)
                    return " Tamano de open minimo: da muy buen precio a las ciegas.";
                return "";
            }

            // vs 4bet y vs 5bet se juegan all-in: no hay multiplicador que corregir.
            if (aggressor is null || aggressor.RaiseTo <= 0 || scenario is not ("vs Raise" or "Squeeze" or "vs 3bet"))
                return "";

            var ratio = action.RaiseTo / aggressor.RaiseTo;
            facts.Add($"Multiplicador sobre la subida anterior: x{ratio:0.#}");
            if (scenario == "vs 3bet")
            {
                if (ratio < 2.0)
                    return $" El 4bet es pequeno (x{ratio:0.#}): la referencia es x2,2-2,5 en posicion y x2,5-2,8 fuera de posicion.";
                if (ratio > 3.0)
                    return $" El 4bet es muy grande (x{ratio:0.#}): la referencia es x2,2-2,5 en posicion y x2,5-2,8 fuera de posicion.";
                return "";
            }

            if (ratio < 2.4)
                return $" La subida es pequena (x{ratio:0.#}): ofrece muy buen precio para pagar. Referencia: x3 en posicion, x3,5-4 fuera de posicion (+1x por cada call delante).";
            if (ratio > 5 + callers)
                return $" La subida es muy grande (x{ratio:0.#}): solo te pagan las manos que te tienen dominado.";
            return "";
        }

        /// <summary>
        /// Spots que decide el precio y no la tabla: all-in del rival (tambien una subida all-in
        /// minima), pagar deja al heroe all-in o el rival le cubre, o el precio es minimo (multiway).
        /// </summary>
        private static bool IsPriceSpot(TrainerAction action, TrainerAction lastRaise)
        {
            if (lastRaise.IsAllIn || action.CallIsCapped)
                return true;
            if (action.StackBefore > 0 && action.ToCall >= action.StackBefore)
                return true;

            var required = action.EffectiveToCall > 0
                ? action.EffectiveToCall / (action.EffectivePot + action.EffectiveToCall) * 100
                : 0;
            return required > 0 && required < TrainerDecisionRules.TinyPricePercent;
        }

        /// <summary>
        /// Ciega puesta y nadie ha entrado (jugador nuevo que pone la ciega grande fuera de su sitio):
        /// pasar es gratis. Subir solo con manos de apertura de esa posicion; foldear siempre es error.
        /// </summary>
        private TrainerDecision GradeFreeOption(TrainerHand hand, TrainerAction action, string heroPos, string combo, double top, string chosen, List<string> facts)
        {
            var code = heroPos is "BB" or "?" ? null : _charts.GetCode("Open", heroPos, "", combo);
            var raiseWorthy = code == "R" || code is null && top <= TrainerDecisionRules.IsoPercent(heroPos);
            facts.Add($"Posicion: {heroPos}, con la ciega ya puesta y sin nadie dentro");
            if (code is not null)
                facts.Add($"Tabla APH Open {heroPos}: {(code == "R" ? "abre" : "no abre")} {combo}");

            TrainerGrade grade;
            string recommended;
            string explanation;
            switch (chosen)
            {
                case "Check":
                    grade = raiseWorthy ? TrainerGrade.Marginal : TrainerGrade.Good;
                    recommended = raiseWorthy ? "Raise" : "Check";
                    explanation = raiseWorthy
                        ? $"Pasar gratis no es un error, pero con {combo} podias subir por valor: es mano de apertura desde {heroPos}."
                        : $"Pasar gratis es correcto: la referencia no abre {combo} desde {heroPos}.";
                    break;
                case "Raise":
                    grade = raiseWorthy ? TrainerGrade.Good : TrainerGrade.Marginal;
                    recommended = raiseWorthy ? "Raise" : "Check";
                    explanation = raiseWorthy
                        ? $"Subir con {combo} es correcto: es mano de apertura desde {heroPos}."
                        : $"Con {combo} lo normal es pasar gratis: subir convierte una mano mediocre en un farol caro.";
                    break;
                default:
                    grade = TrainerGrade.Mistake;
                    recommended = "Check";
                    explanation = "Foldeaste pudiendo pasar gratis: siempre se pasa cuando no hay que pagar nada.";
                    break;
            }

            return new TrainerDecision
            {
                HandId = hand.HandId,
                Street = "PREFLOP",
                Position = heroPos,
                HeroCards = TrainerCards.ToText(hand.HeroCards),
                Situation = "Ciega puesta y nadie entro: pasar es gratis",
                Chosen = ChosenText(action, hand.BigBlind, null),
                ChosenKind = Bucket(action),
                FacingBet = action.ToCall > 0,
                Recommended = recommended,
                Grade = grade,
                Explanation = explanation,
                Facts = facts
            };
        }

        /// <summary>
        /// Preflop decidido por el precio: equity contra el rango del que sube o empuja (y de quien
        /// ya lo pago) frente al precio real, es decir lo que el heroe puede pagar y el bote al que
        /// opta. Si queda juego postflop solo se cuenta parte de la equity. Sin subida legal posible
        /// nunca se recomienda subir.
        /// </summary>
        private TrainerDecision GradePreflopByPrice(
            TrainerHand hand, TrainerAction action, string heroPos, string combo,
            TrainerAction aggressor, string chosen, List<string> facts, Func<string, TrainerVillainProfile?> profiles)
        {
            var bb = hand.BigBlind;
            var depth = aggressor.RaiseTo / bb;
            var raisesBefore = action.PreflopRaisesBefore;
            var aggressorPos = hand.SeatOf(aggressor.Player)?.ChartPosition ?? "?";
            var profile = profiles(aggressor.Player);

            // Mismas reglas que la decision en vivo (TrainerDecisionRules).
            var rangeTop = TrainerDecisionRules.PreflopAggressorTop(aggressor.IsAllIn, depth, raisesBefore, profile);
            if (profile?.HasSample == true)
                facts.Add($"Rival: {profile.Summary}");

            var ranges = new List<IReadOnlyList<WeightedCombo>> { TrainerDecisionRules.PreflopRange(rangeTop) };
            // Siguen en el bote quienes ya pagaron esa subida (sin lo mas fuerte, que habria
            // resubido) y quienes metieron todo antes.
            var others = hand.Actions
                .Where(item => item.Street == "PREFLOP" && item.Index < action.Index && item.Player != aggressor.Player &&
                               !PokerStarsHandHistory.SamePlayer(item.Player, hand.HeroName) &&
                               (item.Index > aggressor.Index && item.Kind == TrainerActionKind.Call ||
                                item.Index < aggressor.Index && item.IsAllIn && item.Kind is TrainerActionKind.Call or TrainerActionKind.Raise))
                .GroupBy(item => item.Player, StringComparer.Ordinal)
                .Select(group => group.Last())
                .ToList();
            foreach (var other in others)
            {
                ranges.Add(other.Index < aggressor.Index
                    ? TrainerDecisionRules.PreflopRange(30)
                    : TrainerDecisionRules.PreflopCallerRange(aggressor.IsAllIn, rangeTop));
            }

            var equity = EquityCalculator.VsRanges(hand.HeroCards, Array.Empty<int>(), ranges, 4000);
            var effectiveCall = action.EffectiveToCall;
            var required = effectiveCall > 0 ? effectiveCall / (action.EffectivePot + effectiveCall) * 100 : 0;
            var heroAllInOnCall = action.StackBefore > 0 && effectiveCall >= action.StackBefore - 0.0001;
            var postflopPlay = !aggressor.IsAllIn && !heroAllInOnCall;
            // Con juego postflop la equity no se cobra entera.
            var realization = TrainerDecisionRules.Realization(combo, postflopPlay);
            var realized = equity * realization;
            var margin = realized - required;
            var canRaise = !aggressor.IsAllIn && action.StackBefore > action.ToCall;
            var valueRaise = canRaise && TrainerDecisionRules.IsValueRaise(combo, rangeTop);

            facts.Add($"Precio real: pagas {effectiveCall / bb:0.#} bb para ganar {(action.EffectivePot + effectiveCall) / bb:0.#} bb: necesitas {required:0.#}%" +
                      (action.CallIsCapped ? " (te cubre: el resto de su subida se le devuelve)" : ""));
            facts.Add($"Equity contra el rango de {(aggressor.IsAllIn ? "all-in" : "subida")} de {aggressorPos} (top {rangeTop:0}%)" +
                      (others.Count > 0 ? $" y {others.Count} jugador(es) mas en el bote" : "") + $": {equity:0.#}%");
            if (postflopPlay)
                facts.Add($"Queda juego postflop: se cuenta ~{realization * 100:0}% de esa equity ({realized:0.#}%)");
            if (!canRaise)
                facts.Add("No queda subida posible: solo pagar o foldear");

            var recommended = valueRaise ? "Raise" : margin >= 0 ? "Call" : "Fold";
            TrainerGrade grade;
            string explanation;
            switch (chosen)
            {
                case "Fold":
                    grade = margin >= 10 || valueRaise ? TrainerGrade.Mistake : margin >= 0 ? TrainerGrade.Marginal : TrainerGrade.Good;
                    explanation = grade == TrainerGrade.Good
                        ? $"Fold correcto: con {combo} (~{realized:0}%) no llegas al {required:0}% que pide el precio."
                        : grade == TrainerGrade.Marginal
                            ? $"Fold ajustado: el precio pedia {required:0}% y {combo} tiene ~{realized:0}%. Pagar era rentable por poco."
                            : $"Con {combo} tenias ~{realized:0}% contra ese rango y el precio solo pedia {required:0}%: foldear pierde dinero.";
                    break;
                case "Call":
                    grade = valueRaise ? TrainerGrade.Marginal
                        : margin >= 0 ? TrainerGrade.Good
                        : margin >= -5 ? TrainerGrade.Marginal
                        : TrainerGrade.Mistake;
                    explanation = valueRaise
                        ? $"Pagar con {combo} es rentable, pero es mano para subir por valor contra ese rango."
                        : margin >= 0
                            ? $"Call correcto: necesitabas {required:0}% y {combo} tiene ~{realized:0}%."
                            : $"Pagaste necesitando {required:0}% con ~{realized:0}% contra ese rango: el precio no alcanza.";
                    break;
                default:
                    grade = valueRaise || equity >= 50 && margin >= 0 ? TrainerGrade.Good
                        : margin >= 0 ? TrainerGrade.Marginal
                        : TrainerGrade.Mistake;
                    explanation = grade == TrainerGrade.Good
                        ? $"Subir con {combo} es correcto: tienes ~{equity:0}% contra ese rango."
                        : margin >= 0
                            ? $"Con {combo} pagar ya era rentable (~{realized:0}% vs {required:0}%); subir solo funciona si foldean manos mejores."
                            : $"Subir con {combo} (~{equity:0}%) contra un rango tan fuerte pierde dinero: el precio pedia {required:0}% solo para pagar.";
                    break;
            }

            var situation = $"{(aggressor.IsAllIn ? "All-in" : "Subida")} de {aggressorPos} a {aggressor.RaiseTo / bb:0.#} bb" +
                            (others.Count > 0 ? $" + {others.Count} en el bote" : "") +
                            (action.CallIsCapped ? " (te cubre)" : "");

            return new TrainerDecision
            {
                HandId = hand.HandId,
                Street = "PREFLOP",
                Position = heroPos,
                HeroCards = TrainerCards.ToText(hand.HeroCards),
                Situation = situation,
                Chosen = ChosenText(action, bb, null),
                ChosenKind = Bucket(action),
                FacingBet = action.ToCall > 0,
                Recommended = recommended,
                Grade = grade,
                Explanation = explanation,
                Facts = facts
            };
        }

        private TrainerDecision GradeVsLimpers(
            TrainerHand hand, TrainerAction action, string heroPos, string combo, double top, int limpers, string chosen, List<string> facts)
        {
            var isoPercent = TrainerDecisionRules.IsoPercent(heroPos);
            var inIso = top <= isoPercent;
            var playable = TrainerDecisionRules.PlayableMultiway(combo, top);
            TrainerGrade grade;
            string recommended;
            string explanation;

            facts.Add($"Posicion: {heroPos}");
            facts.Add($"Rango de iso-raise de referencia vs limpers: top {isoPercent}% de manos");

            switch (chosen)
            {
                case "Raise":
                    grade = inIso ? TrainerGrade.Good : top <= isoPercent + 8 ? TrainerGrade.Marginal : TrainerGrade.Mistake;
                    recommended = inIso ? "Iso-raise" : heroPos == "BB" ? "Check" : playable ? "Limp detras / fold" : "Fold";
                    explanation = inIso
                        ? $"{combo} es suficientemente fuerte para aislar a los limpers: subes con ventaja de rango y te quedas con la iniciativa."
                        : $"{combo} (top {top:0.#}%) es flojo para aislar a {limpers} limper(s): te pagan con manos que te dominan o te ves multiway sin posicion.";
                    break;
                case "Call":
                    // Una mano fuerte pero mala para multiway (A5o) cuesta poco entrando por 1 bb: dudosa, no error.
                    grade = inIso || !playable && top <= TrainerDecisionRules.PlayableMultiwayPercent
                        ? TrainerGrade.Marginal
                        : playable ? TrainerGrade.Good : TrainerGrade.Mistake;
                    recommended = inIso ? "Iso-raise" : playable ? "Limp detras" : "Fold";
                    explanation = inIso
                        ? $"Con {combo} conviene subir para aislar: limpear detras con una mano fuerte deja entrar a todos barato."
                        : playable
                            ? $"{combo} es jugable barato en un bote multiway ({MultiwayPlan(combo)}). Correcto entrar sin inflar el bote."
                            : TrainerDecisionRules.MultiwayFoldReason(combo);
                    break;
                case "Check":
                    grade = inIso ? TrainerGrade.Marginal : TrainerGrade.Good;
                    recommended = inIso ? "Raise" : "Check";
                    explanation = inIso
                        ? $"Con la ciega ya puesta y {combo} puedes subir por valor contra los limpers: tienes de las mejores manos de la mesa."
                        : "Pasar gratis con la ciega ya puesta y una mano media es lo correcto.";
                    break;
                default:
                    if (action.ToCall <= 0)
                    {
                        grade = TrainerGrade.Mistake;
                        recommended = "Check";
                        explanation = "Foldeaste pudiendo pasar gratis: siempre se pasa cuando no hay que pagar nada.";
                    }
                    else
                    {
                        grade = inIso ? TrainerGrade.Mistake : TrainerGrade.Good;
                        recommended = inIso ? "Iso-raise" : "Fold";
                        explanation = inIso
                            ? $"{combo} es una mano para aislar a los limpers; foldearla es demasiado tight."
                            : $"Fold correcto: {combo} no aprovecha el bote con limpers.";
                    }

                    break;
            }

            return new TrainerDecision
            {
                HandId = hand.HandId,
                Street = "PREFLOP",
                Position = heroPos,
                HeroCards = TrainerCards.ToText(hand.HeroCards),
                Situation = $"{limpers} limper(s) antes de ti",
                Chosen = ChosenText(action, hand.BigBlind, chosen == "Raise" ? "Iso-raise" : null),
                ChosenKind = Bucket(action),
                FacingBet = action.ToCall > 0,
                Recommended = recommended,
                Grade = grade,
                Explanation = explanation,
                Facts = facts
            };
        }

        private TrainerDecision GradeColdMultiRaise(
            TrainerHand hand, TrainerAction action, string heroPos, string combo, double top, int raisesBefore,
            TrainerAction? aggressor, string chosen, List<string> facts, Func<string, TrainerVillainProfile?> profiles)
        {
            // Sin tabla exacta: umbrales de fuerza segun cuantas subidas hay delante (los mismos que
            // usa la decision en vivo).
            var profile = aggressor is null ? null : profiles(aggressor.Player);
            if (profile?.HasSample == true)
                facts.Add($"Rival: {profile.Summary}");
            var (raiseTop, callTop) = TrainerDecisionRules.ColdThresholds(raisesBefore, profile);

            // Frente a varias subidas manda el orden de manos premium (AA, KK, QQ, AKs, JJ, AKo...),
            // no la equity contra una mano aleatoria, que sube de mas las parejas pequenas.
            if (raisesBefore >= 2)
            {
                top = PreflopRanking.StrengthTopPercent(combo);
                facts.Add($"Contra varias subidas, {combo} es top {top:0.#}% por fuerza contra rangos fuertes");
            }

            var recommended = top <= raiseTop ? "Raise" : top <= callTop ? "Call" : "Fold";
            var free = action.ToCall <= 0;
            if (free && recommended != "Raise")
                recommended = "Check";
            var grade = recommended == chosen ? TrainerGrade.Good
                : free && chosen == "Check" ? TrainerGrade.Marginal
                : recommended == "Fold" && top <= callTop + 3 ? TrainerGrade.Marginal
                : (recommended, chosen) is ("Raise", "Call") or ("Call", "Raise") ? TrainerGrade.Marginal
                : TrainerGrade.Mistake;

            facts.Add($"Sin tabla exacta para este spot: referencia por fuerza de mano (subir top {raiseTop:0.#}%, pagar top {callTop:0.#}%)");
            var situation = raisesBefore switch
            {
                0 => "Sin subidas delante",
                1 => "Open delante con varios jugadores",
                2 => "3bet delante sin haber invertido (cold)",
                _ => $"{raisesBefore} subidas delante"
            };
            var spot = raisesBefore == 0 ? "Sin subidas delante" : $"Frente a {raisesBefore} subida(s)";
            var explanation = free && chosen == "Fold"
                ? "Foldeaste pudiendo pasar gratis: siempre se pasa cuando no hay que pagar nada."
                : free && chosen == "Check"
                    ? grade == TrainerGrade.Good
                        ? $"Pasar gratis con {combo} es correcto: no es mano para subir aqui."
                        : $"Pasar gratis no es un error, pero con {combo} podias subir por valor."
                    : grade == TrainerGrade.Good
                        ? $"{combo} (top {top:0.#}%) encaja con {recommended}: {spot.ToLowerInvariant()}."
                        : raisesBefore == 0
                            ? $"{spot}: con {combo} (top {top:0.#}%) la referencia es {recommended}."
                            : $"{spot} los rangos se estrechan mucho: con {combo} (top {top:0.#}%) la referencia es {recommended}.";

            return new TrainerDecision
            {
                HandId = hand.HandId,
                Street = "PREFLOP",
                Position = heroPos,
                HeroCards = TrainerCards.ToText(hand.HeroCards),
                Situation = situation,
                Chosen = ChosenText(action, hand.BigBlind, null),
                ChosenKind = Bucket(action),
                FacingBet = action.ToCall > 0,
                Recommended = recommended,
                Grade = grade,
                Explanation = explanation,
                Facts = facts
            };
        }

        private double RangeWidth(string scenario, string heroPos, string villainPos, params string[] codes) =>
            _charts.GetClasses(scenario, heroPos, villainPos, codes).Sum(TrainerCards.ComboCount) * 100.0 / 1326;

        // ------------------------------------------------------------------ POSTFLOP

        private TrainerDecision GradePostflop(TrainerHand hand, TrainerAction action, Func<string, TrainerVillainProfile?> profiles)
        {
            var bb = hand.BigBlind;
            var heroPos = hand.SeatOf(hand.HeroName)?.ChartPosition ?? "?";
            var board = action.Board;
            var chosen = Bucket(action);
            var facts = new List<string>();

            var folded = hand.Actions
                .Where(item => item.Index < action.Index && item.Kind == TrainerActionKind.Fold)
                .Select(item => item.Player)
                .ToHashSet(StringComparer.Ordinal);
            var opponents = hand.ActiveSeats
                .Where(seat => !PokerStarsHandHistory.SamePlayer(seat.Name, hand.HeroName) && !folded.Contains(seat.Name))
                .Select(seat => seat.Name)
                .ToList();

            // Solo se puede subir si quedan fichas por encima de lo que hay que pagar y algun rival
            // no esta ya all-in.
            var allIn = hand.Actions
                .Where(item => item.Index < action.Index && item.IsAllIn)
                .Select(item => item.Player)
                .ToHashSet(StringComparer.Ordinal);
            var canRaise = action.StackBefore > action.ToCall && opponents.Any(name => !allIn.Contains(name));

            var lastAggressor = hand.Actions.LastOrDefault(item =>
                item.Street == action.Street && item.Index < action.Index &&
                item.Kind is TrainerActionKind.Bet or TrainerActionKind.Raise &&
                !PokerStarsHandHistory.SamePlayer(item.Player, hand.HeroName));
            var preflopAggressor = hand.Actions.LastOrDefault(item => item.Street == "PREFLOP" && item.Kind == TrainerActionKind.Raise);
            var heroIsPfa = preflopAggressor is not null && PokerStarsHandHistory.SamePlayer(preflopAggressor.Player, hand.HeroName);

            var primary = action.ToCall > 0 && lastAggressor is not null
                ? lastAggressor.Player
                : preflopAggressor is not null && opponents.Contains(preflopAggressor.Player)
                    ? preflopAggressor.Player
                    : opponents.FirstOrDefault() ?? "";

            var insight = HandInsightReader.Read(hand.HeroCards, board);
            facts.Add($"Mano: {insight.Describe()}");

            var equity = 0.0;
            var profile = string.IsNullOrEmpty(primary) ? null : profiles(primary);
            var solverOpponents = new List<SolverOpponent>();
            double CommittedBb(string player) => hand.Actions
                .Where(item => item.Street == action.Street && item.Index < action.Index && PokerStarsHandHistory.SamePlayer(item.Player, player))
                .Sum(item => item.Contribution) / bb;
            if (!string.IsNullOrEmpty(primary))
            {
                var range = _ranges.Estimate(hand, primary, profile, action.Index);
                facts.Add($"Rango estimado de {primary}: {range.Description}");
                if (profile?.HasSample == true)
                    facts.Add($"Rival: {profile.Summary}");

                solverOpponents.Add(new SolverOpponent(range.Combos, profile, CommittedBb(primary)));
                foreach (var name in opponents.Where(name => name != primary))
                {
                    var other = profiles(name);
                    solverOpponents.Add(new SolverOpponent(_ranges.Estimate(hand, name, other, action.Index).Combos, other, CommittedBb(name)));
                }
                equity = EquityCalculator.VsRanges(hand.HeroCards, board, solverOpponents.Select(item => item.Range).ToList(), 3000);
            }

            var multiway = opponents.Count > 1;
            facts.Add($"Equity estimada: {equity:0.#}%" + (multiway ? $" (contra los {opponents.Count} rivales a la vez)" : ""));

            // Precio real: con menos fichas que la apuesta solo se paga lo que queda, y del bote solo
            // cuenta lo que el heroe puede igualar (el resto se le devuelve al rival).
            var facingBet = action.ToCall > 0;
            var potBb = (facingBet ? action.EffectivePot : action.PotBefore) / bb;
            var toCallBb = action.EffectiveToCall / bb;
            facts.Add($"Bote: {potBb:0.#} bb" + (facingBet ? $" | a pagar: {toCallBb:0.#} bb" : "") +
                      (action.CallIsCapped ? " (te cubre: el resto de la apuesta se le devuelve)" : ""));

            var required = facingBet && action.EffectiveToCall > 0
                ? action.EffectiveToCall / (action.EffectivePot + action.EffectiveToCall) * 100
                : 0;
            if (facingBet)
                facts.Add($"Pot odds: necesitas {required:0.#}% de equity para pagar");
            if (facingBet && !canRaise)
                facts.Add("No queda subida posible: solo pagar o foldear");

            if (insight.Outs > 0 && action.Street != "RIVER")
                facts.Add(action.Street == "FLOP"
                    ? $"Outs: {insight.Outs} (~{Math.Min(insight.Outs * 4, 100)}% hasta el river, ~{insight.Outs * 2}% por carta)"
                    : $"Outs: {insight.Outs} (~{insight.Outs * 2}% en el river)");

            var (grade, recommended, explanation) = facingBet
                ? GradeFacingBet(action, chosen, equity, required, insight, profile, multiway, canRaise)
                : GradeWithInitiative(action, chosen, equity, insight, profile, heroIsPfa, Math.Max(1, opponents.Count), facts);

            // El mismo solver de entrenamiento que la decision en vivo: si tu jugada esta entre las
            // que mas ganan contra el rango estimado, no se marca como error (lo que aconsejo en vivo
            // nunca sale mal calificado).
            if (solverOpponents.Count > 0)
            {
                var heroCommitted = CommittedBb(hand.HeroName);
                var solverPot = potBb;
                var options = TrainerSolver.Postflop(hand.HeroCards, board, solverOpponents, solverPot, facingBet ? toCallBb : 0,
                    heroCommitted, action.StackBefore / bb, action.CurrentBetBefore / bb, canRaise);
                if (options.Count > 0)
                {
                    facts.Add("Solver de entrenamiento (lo que gana de media cada opción en esta calle): " +
                              string.Join(" · ", options.Take(3).Select(option => $"{SolverLabel(option)} {option.Ev:+0.0;-0.0;0} bb")));
                    var chosenAmount = action.Kind == TrainerActionKind.Raise ? action.RaiseTo / bb : heroCommitted + action.Contribution / bb;
                    var mine = chosen switch
                    {
                        "Fold" => options.FirstOrDefault(option => option.Kind == "Fold"),
                        "Check" => options.FirstOrDefault(option => option.Kind == "Check"),
                        "Call" => options.FirstOrDefault(option => option.Kind == "Call"),
                        _ => options.Where(option => option.IsAggressive).OrderBy(option => Math.Abs(option.Amount - chosenAmount)).FirstOrDefault()
                    };
                    var best = options[0];
                    if (mine is not null && grade != TrainerGrade.Good && best.Ev - mine.Ev <= Math.Max(0.3, 0.05 * solverPot))
                    {
                        grade = TrainerGrade.Good;
                        explanation += $" Según el solver de entrenamiento, contra este rango tu jugada está entre las que más ganan ({mine.Ev:+0.0;-0.0;0} bb frente a {best.Ev:+0.0;-0.0;0} bb de la mejor).";
                    }
                }
            }

            var situation = !facingBet
                ? $"Sin apuesta delante, bote {potBb:0.#} bb"
                : action.CallIsCapped
                    ? $"Te cubren: pagas {toCallBb:0.#} bb all-in por un bote de {potBb:0.#} bb"
                    : $"{(lastAggressor?.Kind == TrainerActionKind.Raise ? "Te suben" : "Te apuestan")} {toCallBb:0.#} bb en bote de {potBb:0.#} bb";

            return new TrainerDecision
            {
                HandId = hand.HandId,
                Street = action.Street,
                Position = heroPos,
                HeroCards = TrainerCards.ToText(hand.HeroCards),
                Board = TrainerCards.ToText(board),
                Situation = situation,
                Chosen = ChosenText(action, bb, null),
                ChosenKind = Bucket(action),
                FacingBet = action.ToCall > 0,
                Recommended = recommended,
                Grade = grade,
                Explanation = explanation,
                Facts = facts
            };
        }

        private static (TrainerGrade, string, string) GradeFacingBet(
            TrainerAction action, string chosen, double equity, double required, HandInsight insight,
            TrainerVillainProfile? profile, bool multiway, bool canRaise)
        {
            var margin = equity - required;
            var hasDraw = TrainerDecisionRules.HasStrongDraw(insight, action.Street);
            var strong = canRaise && (equity >= 70 && action.Street != "RIVER" || equity >= 80);
            var recommended = TrainerDecisionRules.FacingBet(equity, required, insight, action.Street, canRaise);

            switch (chosen)
            {
                case "Fold":
                    // En el river la lectura de rango es lo menos fiable: hace falta mas margen para
                    // llamar "error" a un fold.
                    if (margin >= (action.Street == "RIVER" ? 15 : 10))
                        return (TrainerGrade.Mistake, recommended,
                            $"Fold con equity de sobra: contra el rango estimado tenias ~{equity:0}% y solo necesitabas {required:0}%. Ese fold pierde dinero a largo plazo.");
                    if (margin >= 0)
                        return (TrainerGrade.Marginal, recommended,
                            $"Fold ajustado: el precio pedia {required:0}% y tenias ~{equity:0}%. Pagar era rentable por poco; contra este rival quizas el fold se justifica.");
                    if (hasDraw && margin >= -6)
                        return (TrainerGrade.Marginal, recommended,
                            $"Con {insight.Outs} outs y precio cercano ({equity:0}% vs {required:0}%) las implied odds podian justificar pagar.");
                    return (TrainerGrade.Good, recommended,
                        $"Fold correcto: con ~{equity:0}% de equity no llegas al {required:0}% que pide el precio.");

                case "Call":
                    if (margin >= 0)
                    {
                        if (strong && !multiway)
                            return (TrainerGrade.Marginal, recommended,
                                $"Pagar es rentable, pero con ~{equity:0}% de equity subir por valor (y proteger) gana mas.");
                        return (TrainerGrade.Good, recommended == "Raise" ? "Call (subir tambien vale)" : "Call",
                            $"Call correcto: necesitabas {required:0}% y tienes ~{equity:0}%.");
                    }

                    // La regla de referencia acepta pagar con proyecto fuerte cerca del precio.
                    if (hasDraw && margin >= -6)
                        return (TrainerGrade.Good, "Call por implied odds",
                            $"Call con proyecto ({insight.Outs} outs): el precio directo no alcanza ({equity:0}% vs {required:0}%), pero cobras mas cuando ligas.");
                    if (action.Street == "RIVER" && profile?.IsManiac == true && margin >= -8)
                        return (TrainerGrade.Marginal, recommended,
                            $"Call de bluffcatcher: por equity no llega ({equity:0}% vs {required:0}%), pero este rival farolea mucho.");
                    return (TrainerGrade.Mistake, recommended,
                        $"Call sin precio: pagaste necesitando {required:0}% con solo ~{equity:0}% contra el rango que apuesta asi.");

                default:
                    if (equity >= 60 && margin >= 0)
                        return (TrainerGrade.Good, recommended == "Raise" ? "Raise" : "Raise o call",
                            $"Raise por valor correcto: con ~{equity:0}% de equity quieres meter dinero en el bote.");
                    if (hasDraw && equity >= 30 && profile?.IsCallingStation != true)
                        return (TrainerGrade.Good, recommended == "Fold" ? "Fold / semi-farol" : recommended,
                            $"Semi-farol con {insight.Outs} outs: ganas cuando foldea y aun tienes ~{equity:0}% cuando paga.");
                    if (profile?.IsCallingStation == true)
                        return (TrainerGrade.Mistake, recommended,
                            $"Subir sin mano contra un pagador ({profile.Kind}) no tiene fold equity: con ~{equity:0}% la referencia es {recommended}.");
                    return (TrainerGrade.Marginal, recommended,
                        $"Subida de farol con ~{equity:0}% de equity: solo funciona si el rival foldea mucho. La referencia es {recommended}.");
            }
        }

        private static (TrainerGrade, string, string) GradeWithInitiative(
            TrainerAction action, string chosen, double equity, HandInsight insight, TrainerVillainProfile? profile,
            bool heroIsPfa, int opponents, List<string> facts)
        {
            // Con varios rivales la equity es contra todos a la vez: se compara con la parte justa
            // (25% contra tres), no con el 55% de un mano a mano.
            var multiway = opponents > 1;
            var fair = TrainerDecisionRules.FairShare(opponents);
            var hasDraw = TrainerDecisionRules.HasStrongDraw(insight, action.Street);
            var valueEquity = TrainerDecisionRules.ValueEquity(action.Street, opponents);
            var clearValue = equity >= TrainerDecisionRules.ClearValueEquity(action.Street, opponents);
            var recommended = TrainerDecisionRules.WithInitiative(equity, insight, action.Street, heroIsPfa, opponents);
            var vsAll = multiway ? $" contra {opponents} rivales a la vez (la parte justa es {fair:0}%)" : "";
            if (multiway)
                facts.Add($"Con {opponents} rivales: valor desde ~{valueEquity:0}% de equity contra todos a la vez");

            if (chosen == "Fold")
                return (TrainerGrade.Mistake, "Check", "Foldeaste pudiendo pasar gratis: sin apuesta delante nunca se foldea.");

            if (chosen == "Check")
            {
                if (clearValue)
                {
                    return profile?.IsManiac == true || profile?.Af >= 3
                        ? (TrainerGrade.Good, "Check (trampa) o bet", $"Check con mano fuerte (~{equity:0}%) contra un rival agresivo: buena trampa para que apueste el.")
                        : (TrainerGrade.Marginal, recommended, $"Con ~{equity:0}% de equity{vsAll} (segun el rango estimado) pasar regala valor: apuesta para que paguen las manos peores.");
                }

                if (equity >= valueEquity)
                    return (TrainerGrade.Good, "Check o bet fino",
                        $"Check aceptable con ~{equity:0}%{vsAll}: estas por delante del rango estimado, pero no tanto; apostar por valor fino tambien es razonable.");

                if (hasDraw)
                    return (TrainerGrade.Good, "Check o semi-farol",
                        "Check con proyecto: valido para ver carta gratis, aunque apostar como semi-farol tambien es buena opcion.");

                if (recommended == "Bet (c-bet)")
                    return (TrainerGrade.Good, "Check o c-bet",
                        $"Check aceptable con ~{equity:0}%; como agresor preflop, una c-bet pequena tambien era buena opcion.");

                return (TrainerGrade.Good, "Check",
                    $"Check correcto con ~{equity:0}%: no tienes suficiente valor para apostar ni un farol claro.");
            }

            var sizePct = action.PotBefore > 0 ? action.Contribution / action.PotBefore * 100 : 0;
            var breakEven = action.PotBefore > 0 ? action.Contribution / (action.PotBefore + action.Contribution) * 100 : 0;
            facts.Add($"Tamano de apuesta: {sizePct:0}% del bote");
            var sizeNote = sizePct switch
            {
                < 25 => " La apuesta es muy pequena: ni cobra valor ni hace foldear.",
                > 130 => " Overbet: tiene sentido con rango polarizado (muy fuerte o farol).",
                _ => ""
            };

            if (equity >= valueEquity)
                return (TrainerGrade.Good, recommended, $"Apuesta por valor correcta: con ~{equity:0}%{vsAll} te pagan manos peores.{sizeNote}");

            if (hasDraw)
                return (TrainerGrade.Good, recommended, $"Semi-farol correcto con {insight.Outs} outs: puede ganar ya o ligar despues.{sizeNote}");

            // Multiway con la parte justa o mas: no es un farol, es una apuesta fina (o de proteccion).
            if (multiway && equity >= fair)
                return (TrainerGrade.Marginal, "Check o bet fino",
                    $"Apuesta fina multiway: con ~{equity:0}%{vsAll} no vas por detras, pero tampoco claramente por delante. Con varios rivales te pagan sobre todo las manos que te ganan; pasar suele ser igual de bueno.{sizeNote}");

            facts.Add($"Para que el farol sea rentable el rival debe foldear mas del {breakEven:0}%");
            if (profile?.IsCallingStation == true)
                return (TrainerGrade.Mistake, "Check",
                    $"Farol contra un pagador ({profile.Kind}): necesita foldear mas del {breakEven:0}% y este perfil casi nunca suelta.{sizeNote}");

            if (heroIsPfa && action.Street == "FLOP" && !multiway)
            {
                var foldsALot = profile?.FoldsToCBets == true;
                return (foldsALot || equity >= 35 ? TrainerGrade.Good : TrainerGrade.Marginal, foldsALot ? "Bet (c-bet)" : recommended,
                    foldsALot
                        ? $"C-bet contra un rival que foldea mucho a c-bet ({profile!.FoldVsCBet:0}%): buena explotacion.{sizeNote}"
                        : $"C-bet como agresor preflop con ~{equity:0}%: aceptable en boards que favorecen tu rango; necesita {breakEven:0}% de folds.{sizeNote}");
            }

            if (multiway)
                return (TrainerGrade.Mistake, "Check",
                    $"Farol multiway con ~{equity:0}%{vsAll}: con varios rivales es muy dificil que foldeen todos.{sizeNote}");

            return (TrainerGrade.Marginal, recommended,
                $"Apuesta sin valor claro (~{equity:0}%): funciona solo si el rival foldea mas del {breakEven:0}%.{sizeNote}");
        }

        // ------------------------------------------------------------------ comun

        private static string AllInMark(TrainerAction action) => action.IsAllIn ? " (all-in)" : "";

        /// <summary>Que busca cada tipo de mano en un bote multiway barato.</summary>
        private static string MultiwayPlan(string combo)
        {
            if (combo.Length == 2)
                return "busca el set";

            var parts = new List<string>();
            if (combo.EndsWith("s", StringComparison.Ordinal))
                parts.Add("color");

            var gap = Math.Abs(TrainerCards.Ranks.IndexOf(combo[0]) - TrainerCards.Ranks.IndexOf(combo[1]));
            if (gap <= 3)
                parts.Add("escalera");

            return parts.Count > 0
                ? $"busca {string.Join(" o ", parts)}"
                : "busca pareja alta con buen kicker y suelta sin ella";
        }

        private static string SolverLabel(SolverOption option) => option.Kind switch
        {
            "Fold" => "fold",
            "Check" => "check",
            "Call" => "call",
            "Bet" => $"bet {option.Amount:0.#} bb",
            _ => $"raise a {option.Amount:0.#} bb"
        };

        private static string Bucket(TrainerAction action) => action.Kind switch
        {
            TrainerActionKind.Fold => "Fold",
            TrainerActionKind.Check => "Check",
            TrainerActionKind.Call => "Call",
            _ => "Raise"
        };

        private static string ChosenText(TrainerAction action, double bb, string? raiseName)
        {
            var allIn = action.IsAllIn ? " (all-in)" : "";
            return action.Kind switch
            {
                TrainerActionKind.Fold => "Fold",
                TrainerActionKind.Check => "Check",
                TrainerActionKind.Call => string.Format(CultureInfo.InvariantCulture, "Call {0:0.#} bb{1}", action.Contribution / bb, allIn),
                TrainerActionKind.Bet => string.Format(CultureInfo.InvariantCulture, "Bet {0:0.#} bb{1}", action.Contribution / bb, allIn),
                _ => string.Format(CultureInfo.InvariantCulture, "{0} a {1:0.#} bb{2}", raiseName ?? "Raise", action.RaiseTo / bb, allIn)
            };
        }
    }
}
