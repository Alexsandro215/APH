using System.Globalization;
using Hud.App.Services.Trainer;

namespace Hud.App.Services.Simulator
{
    /// <summary>Las 1326 combinaciones de dos cartas, en orden fijo.</summary>
    public static class SimCombos
    {
        public static readonly (int A, int B)[] All = Build();
        public static readonly string[] Classes = All.Select(c => TrainerCards.ComboName(c.A, c.B)).ToArray();

        private static (int, int)[] Build()
        {
            var list = new List<(int, int)>(1326);
            for (var a = 0; a < 52; a++)
                for (var b = a + 1; b < 52; b++)
                    list.Add((a, b));
            return list.ToArray();
        }

        public static int IndexOf(int a, int b)
        {
            if (a > b) (a, b) = (b, a);
            // Posicion de (a, b) en el orden de Build.
            return a * 51 - a * (a - 1) / 2 + (b - a - 1);
        }
    }

    /// <summary>
    /// Lectura del rango de cada maquina: empieza con todas las manos y, en cada accion suya, cada
    /// combinacion se multiplica por la probabilidad de que la maquina hiciera eso con ella. Como la
    /// maquina juega con reglas conocidas, el resultado es su rango exacto: lo que el jugador
    /// deberia "intuir" (por ejemplo, que un nit que hace 5bet tiene AA o KK).
    /// </summary>
    public sealed class SimRangeTracker
    {
        private readonly Dictionary<int, double[]> _weights = new();

        public SimRangeTracker(SimHand hand)
        {
            foreach (var player in hand.Players.Where(p => !p.IsHero))
                _weights[player.Seat] = Enumerable.Repeat(1.0, SimCombos.All.Length).ToArray();
        }

        public double[] WeightsOf(int seat) => _weights[seat];

        /// <summary>Llamar ANTES de aplicar la accion de una maquina.</summary>
        public void Observe(SimHand hand, SimPlayer actor, SimActionKind kind)
        {
            if (actor.IsHero || !_weights.TryGetValue(actor.Seat, out var weights))
                return;

            var spot = SimSpot.For(hand, actor);
            var options = hand.Options();
            for (var i = 0; i < weights.Length; i++)
            {
                if (weights[i] <= 0) continue;
                var (a, b) = SimCombos.All[i];
                var policy = SimBot.Policy(spot, a, b, options);
                weights[i] *= kind switch
                {
                    SimActionKind.Fold => policy.Fold,
                    SimActionKind.Check or SimActionKind.Call => policy.Passive,
                    _ => policy.Aggressive
                };
            }
        }

        /// <summary>El rango como lo ve el heroe: sin las combinaciones que chocan con sus cartas o la mesa.</summary>
        public static List<WeightedCombo> Visible(double[] weights, IEnumerable<int> knownCards)
        {
            var dead = 0L;
            foreach (var card in knownCards) dead |= 1L << card;
            var list = new List<WeightedCombo>();
            for (var i = 0; i < weights.Length; i++)
            {
                if (weights[i] <= 1e-6) continue;
                var (a, b) = SimCombos.All[i];
                if ((dead & (1L << a)) != 0 || (dead & (1L << b)) != 0) continue;
                list.Add(new WeightedCombo(a, b, weights[i]));
            }
            return list;
        }

        /// <summary>Peso de cada clase de mano ("AKs", "QQ"...) en un rango, de mayor a menor.</summary>
        public static List<(string Class, double Share)> ClassShares(IReadOnlyList<WeightedCombo> range)
        {
            var total = range.Sum(c => c.Weight);
            if (total <= 0) return new();
            return range.GroupBy(c => TrainerCards.ComboName(c.A, c.B))
                .Select(g => (g.Key, g.Sum(c => c.Weight) / total))
                .OrderByDescending(x => x.Item2)
                .ToList();
        }
    }

    public enum SimVerdict { Good, Doubtful, Mistake }

    /// <summary>Una decision del heroe con todo lo necesario para revisarla al final de la mano.</summary>
    public sealed record SimDecision(
        int Index,
        SimStreet Street,
        int[] HeroCards,
        int[] Board,
        string HeroPosition,
        double Pot,
        double HeroStreetBet,
        double HeroStack,
        double CurrentBet,
        SimOptions Options,
        SimSpot HeroSpot,
        IReadOnlyList<SimOpponentState> Opponents,
        SimActionKind Taken,
        double TakenTo);

    /// <summary>Un rival en el momento de la decision del heroe: su rango y como responderia.</summary>
    public sealed record SimOpponentState(int Seat, SimProfile Profile, string Position, double[] Weights,
        double StreetBet, double Stack, int OwnPreflopRaises, int[] TrueCards);

    /// <summary>Una opcion revisada. EquityWhenCalled: lo que gana el heroe contra las manos con que la maquina sigue.</summary>
    public sealed record SimOptionValue(string Label, SimActionKind Kind, double To, double Ev, double? FoldPct, double? EquityWhenCalled = null);

    /// <summary>
    /// Headline: que era mejor, en una frase (vacio si la decision fue buena). Reason: el porque en
    /// palabras de principiante. Lines: el detalle tecnico (rango, equity, pot odds, valor de cada opcion).
    /// </summary>
    public sealed record SimDecisionReview(
        SimDecision Decision,
        SimVerdict Verdict,
        string TakenLabel,
        SimOptionValue Best,
        double TakenEv,
        double Equity,
        IReadOnlyList<SimOptionValue> Values,
        IReadOnlyList<string> Lines,
        IReadOnlyList<(int Seat, IReadOnlyList<WeightedCombo> Range)> Ranges,
        string Headline,
        string Reason);

    /// <summary>
    /// Revision de cada decision del heroe: su equity contra el rango real de las maquinas, las pot
    /// odds, cuanto se retiraria la maquina ante cada apuesta y el valor esperado aproximado de
    /// cada opcion (mirando solo esa calle). La mejor opcion es la que mas gana contra ESTA maquina:
    /// es lo que hay que aprender para explotar a cada perfil.
    /// </summary>
    public static class SimReviewer
    {
        public static SimDecision Capture(SimHand hand, SimRangeTracker tracker, int index)
        {
            var hero = hand.Hero;
            var options = hand.Options();
            var pre = hand.Actions.Where(a => a.Street == SimStreet.Preflop).ToList();
            var opponents = hand.Players.Where(p => !p.IsHero && p.InHand)
                .Select(p => new SimOpponentState(p.Seat, p.Profile!.Value, p.Position, (double[])tracker.WeightsOf(p.Seat).Clone(),
                    p.StreetBet, p.Stack, pre.Count(a => a.Seat == p.Seat && a.Kind is SimActionKind.Raise or SimActionKind.Bet), p.Cards.ToArray()))
                .ToList();
            return new SimDecision(index, hand.Street, hero.Cards.ToArray(), hand.Board.ToArray(), hero.Position, hand.Pot,
                hero.StreetBet, hero.Stack, hand.CurrentBet, options, SimSpot.For(hand, hero), opponents, SimActionKind.Check, 0);
        }

        public static SimDecisionReview Review(SimDecision d, int iterations = 1500)
        {
            var known = d.HeroCards.Concat(d.Board).ToList();
            var ranges = d.Opponents.Select(o => (o.Seat, Range: (IReadOnlyList<WeightedCombo>)SimRangeTracker.Visible(o.Weights, known))).ToList();
            var equity = EquityCalculator.VsRanges(d.HeroCards, d.Board, ranges.Select(r => r.Range).ToList(), iterations) / 100.0;

            var values = new List<SimOptionValue>();
            var call = d.Options.CallAmount;
            if (d.Options.FacingBet)
            {
                values.Add(new SimOptionValue(T("Sim.Opt.Fold"), SimActionKind.Fold, 0, 0, null));
                values.Add(new SimOptionValue(F("Sim.Opt.Call", call), SimActionKind.Call, 0, equity * (d.Pot + call) - call, null));
            }
            else
            {
                values.Add(new SimOptionValue(T("Sim.Opt.Check"), SimActionKind.Check, 0, equity * d.Pot, null));
            }

            if (d.Options.CanRaise)
            {
                foreach (var to in CandidateSizes(d).Append(d.Taken is SimActionKind.Bet or SimActionKind.Raise ? d.TakenTo : -1)
                             .Where(t => t > 0).Select(t => Math.Clamp(t, d.Options.MinRaiseTo, d.Options.MaxRaiseTo)).Distinct())
                {
                    var (ev, fold, whenCalled) = RaiseValue(d, ranges, to, iterations);
                    values.Add(new SimOptionValue(RaiseLabel(d, to), d.CurrentBet > 0 ? SimActionKind.Raise : SimActionKind.Bet, to, ev, fold, whenCalled));
                }
            }

            var takenValue = d.Taken switch
            {
                SimActionKind.Fold => values.First(v => v.Kind == SimActionKind.Fold),
                SimActionKind.Check or SimActionKind.Call => values.First(v => v.Kind is SimActionKind.Check or SimActionKind.Call),
                _ => values.Where(v => v.Kind is SimActionKind.Bet or SimActionKind.Raise).OrderBy(v => Math.Abs(v.To - d.TakenTo)).First()
            };
            var best = values.OrderByDescending(v => v.Ev).First();

            // Antes del flop sin all-in, el valor de una sola calle engana (falta todo lo que viene
            // despues): ahi manda la tabla de APH si la hay.
            var chartCode = d.Street == SimStreet.Preflop && !BigPreflopDecision(d) ? HeroChartCode(d) : null;
            var rawChartCode = chartCode;
            var takenCode = d.Taken switch { SimActionKind.Fold => "F", SimActionKind.Check or SimActionKind.Call => "C", _ => "R" };
            SimVerdict verdict;
            var lines = new List<string>();
            if (chartCode is not null)
            {
                if (d.Options.CanCheck && chartCode == "F") chartCode = "C"; // pasar gratis nunca es error
                verdict = takenCode == chartCode ? SimVerdict.Good : SimVerdict.Mistake;
                best = chartCode switch
                {
                    "F" => values.First(v => v.Kind == SimActionKind.Fold),
                    "C" => values.First(v => v.Kind is SimActionKind.Check or SimActionKind.Call),
                    _ => values.Where(v => v.Kind is SimActionKind.Bet or SimActionKind.Raise).OrderBy(v => v.To).FirstOrDefault() ?? best
                };
                lines.Add(F("Sim.Why.Chart", TrainerCards.ComboName(d.HeroCards[0], d.HeroCards[1]), ChartActionName(chartCode)));
            }
            else
            {
                var loss = best.Ev - takenValue.Ev;
                verdict = loss <= Math.Max(0.5, 0.05 * d.Pot) ? SimVerdict.Good
                    : loss <= Math.Max(1.5, 0.15 * d.Pot) ? SimVerdict.Doubtful
                    : SimVerdict.Mistake;
            }

            foreach (var (seat, range) in ranges)
            {
                var opponent = d.Opponents.First(o => o.Seat == seat);
                if (IsUntouched(opponent.Weights))
                {
                    // Todavia no actuo: su "rango" son todas las manos y listarlas no dice nada.
                    lines.Add(F("Sim.Why.RangeAny", SimProfiles.Name(opponent.Profile), opponent.Position));
                    continue;
                }
                var shares = SimRangeTracker.ClassShares(range);
                var top = string.Join(", ", shares.Take(6).Select(s => $"{s.Class} {s.Share * 100:0}%"));
                lines.Add(F("Sim.Why.Range", SimProfiles.Name(opponent.Profile), opponent.Position, top, shares.Count));
            }

            lines.Add(F("Sim.Why.Equity", equity * 100));
            if (d.Options.FacingBet)
                lines.Add(F("Sim.Why.PotOdds", call, d.Pot, 100 * call / (d.Pot + call)));

            var bestRaise = values.Where(v => v.FoldPct is not null).OrderByDescending(v => v.Ev).FirstOrDefault();
            if (bestRaise is not null)
                lines.Add(F("Sim.Why.FoldEquity", bestRaise.Label, bestRaise.FoldPct!.Value * 100));

            // Con la tabla de APH manda la tabla: los numeros de una sola calle la contradecirian.
            if (chartCode is null)
            {
                lines.Add(F("Sim.Why.Best", best.Label, best.Ev, takenValue.Label, takenValue.Ev));
                lines.Add(T("Sim.Why.Approx"));
            }

            var heroHand = TrainerCards.ComboName(d.HeroCards[0], d.HeroCards[1]);
            var (headline, reason) = chartCode is not null
                ? ChartSummary(heroHand, rawChartCode!, chartCode, takenCode, takenValue, best, d.Options.CanCheck)
                : ValueSummary(d, ranges, verdict, takenValue, best, equity);

            return new SimDecisionReview(d, verdict, takenValue.Label, best, takenValue.Ev, equity, values, lines,
                ranges.Select(r => (r.Seat, r.Range)).ToList(), headline, reason);
        }

        // ------------------------------------------------------------ resumen para principiantes

        /// <summary>Resumen de una decision preflop que manda la tabla de APH.</summary>
        private static (string Headline, string Reason) ChartSummary(string hand, string rawChart, string chart, string taken,
            SimOptionValue takenValue, SimOptionValue best, bool canCheck)
        {
            if (taken == chart)
            {
                var why = rawChart == "F" && canCheck ? F("Sim.Reason.FreeCheck", hand)
                    : chart switch
                    {
                        "R" => F("Sim.Reason.ChartStrong", hand),
                        "C" => F("Sim.Reason.ChartCallOk", hand),
                        _ => F("Sim.Reason.ChartFoldOk", hand)
                    };
                return ("", why);
            }

            var reason = (chart, taken) switch
            {
                ("F", _) => F("Sim.Reason.ChartTooWeak", hand),
                ("C", "R") when canCheck => F("Sim.Reason.FreeCheck", hand),
                ("C", "R") => F("Sim.Reason.ChartCallNotRaise", hand),
                ("C", _) => F("Sim.Reason.ChartCallNotFold", hand),
                ("R", "F") => F("Sim.Reason.ChartPlayIt", hand),
                _ => F("Sim.Reason.ChartRaiseNotCall", hand)
            };
            return (F("Sim.Sum.BetterChart", hand, best.Label), reason);
        }

        /// <summary>Resumen de una decision medida por su valor: que era mejor y por que, sin jerga.</summary>
        private static (string Headline, string Reason) ValueSummary(SimDecision d,
            IReadOnlyList<(int Seat, IReadOnlyList<WeightedCombo> Range)> ranges, SimVerdict verdict,
            SimOptionValue taken, SimOptionValue best, double equity)
        {
            var eq = equity * 100;
            var call = d.Options.CallAmount;
            var need = d.Options.FacingBet ? 100 * call / (d.Pot + call) : 0;
            var takenGroup = Group(taken.Kind);

            if (verdict == SimVerdict.Good)
            {
                var why = takenGroup switch
                {
                    0 when Math.Round(eq) >= Math.Round(need) => F("Sim.Reason.Close", eq, need),
                    0 => F("Sim.Reason.FoldOk", eq, need),
                    1 when d.Options.FacingBet && Math.Round(eq) < Math.Round(need) => F("Sim.Reason.Close", eq, need),
                    1 when d.Options.FacingBet => F("Sim.Reason.CallOk", eq, need),
                    1 => F("Sim.Reason.CheckOk", eq),
                    _ => AggressionReason(taken, eq)
                };
                return ("", why);
            }

            var loss = best.Ev - taken.Ev;
            var headline = F(verdict == SimVerdict.Doubtful ? "Sim.Sum.Slightly" : "Sim.Sum.Better", best.Label)
                + (loss >= 0.1 ? " " + F("Sim.Sum.Cost", loss) : "");
            var reason = (takenGroup, Group(best.Kind)) switch
            {
                (0, 1) => F("Sim.Reason.ShouldCall", eq, need),
                (2, 2) => SizeReason(taken, best, eq),
                (_, 2) => AggressionReason(best, eq),
                (1, 0) => F("Sim.Reason.ShouldFold", eq, need) + Holding(d, ranges),
                (2, 0) => F("Sim.Reason.BetNoFoldsFold", eq, Pct(taken.FoldPct), taken.Label) + Holding(d, ranges),
                (2, 1) => F(d.Options.FacingBet ? "Sim.Reason.BetNoFoldsCall" : "Sim.Reason.BetNoFoldsCheck",
                    taken.EquityWhenCalled is double ec ? ec * 100 : eq, Pct(taken.FoldPct), taken.Label) + Holding(d, ranges),
                _ => F("Sim.Reason.ShouldCall", eq, need)
            };
            return (headline, reason);
        }

        /// <summary>Por que apostar o subir (con esa opcion) gana: por valor, porque la maquina se retira, o por las dos.</summary>
        private static string AggressionReason(SimOptionValue option, double eq)
        {
            var fold = Pct(option.FoldPct);
            if (eq >= 55)
                return F("Sim.Reason.BetValue", eq, option.Label);
            if (fold >= 35 || option.EquityWhenCalled is null)
                return F("Sim.Reason.BetFolds", fold, option.Label);
            return F("Sim.Reason.BetMix", option.EquityWhenCalled.Value * 100, fold, option.Label);
        }

        /// <summary>Por que otro tamano de apuesta era mejor.</summary>
        private static string SizeReason(SimOptionValue taken, SimOptionValue best, double eq)
        {
            var foldTaken = Pct(taken.FoldPct);
            var foldBest = Pct(best.FoldPct);
            if (best.To > taken.To)
            {
                if (best.EquityWhenCalled is double ec && (eq >= 50 || foldBest <= foldTaken + 5))
                    return F("Sim.Reason.BiggerBet", ec * 100, best.Label);
                return F("Sim.Reason.BiggerBetFolds", foldBest, foldTaken, best.Label);
            }
            return foldBest >= foldTaken - 5
                ? F("Sim.Reason.SmallerBetSameFold", foldBest, foldTaken, best.Label)
                : F("Sim.Reason.SmallerBetMoreCalls", 100 - foldBest, 100 - foldTaken, best.Label);
        }

        /// <summary>" Con lo que habia hecho, el Nit tenia sobre todo AA o KK." si el rango ya es estrecho.</summary>
        private static string Holding(SimDecision d, IReadOnlyList<(int Seat, IReadOnlyList<WeightedCombo> Range)> ranges)
        {
            foreach (var (seat, range) in ranges)
            {
                var opponent = d.Opponents.First(o => o.Seat == seat);
                if (IsUntouched(opponent.Weights))
                    continue;
                var top = SimRangeTracker.ClassShares(range).Take(3).ToList();
                if (top.Count == 0 || top.Sum(t => t.Share) < 0.5)
                    continue;
                var names = top.Select(t => t.Class).ToList();
                var list = names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + T("Sim.Sum.Or") + names[^1];
                return " " + F("Sim.Reason.Holding", SimProfiles.Name(opponent.Profile), list);
            }
            return "";
        }

        private static int Group(SimActionKind kind) => kind switch
        {
            SimActionKind.Fold => 0,
            SimActionKind.Check or SimActionKind.Call => 1,
            _ => 2
        };

        private static double Pct(double? share) => (share ?? 0) * 100;

        /// <summary>True si la maquina todavia no hizo nada que estreche su rango (todas las manos pesan igual).</summary>
        public static bool IsUntouched(double[] weights)
        {
            var first = weights[0];
            return weights.All(w => Math.Abs(w - first) < 1e-12);
        }

        private static bool BigPreflopDecision(SimDecision d) =>
            d.Options.CallAmount >= 0.25 * (d.HeroStack + d.HeroStreetBet) || d.Opponents.Any(o => o.Stack <= 1e-9);

        private static string? HeroChartCode(SimDecision d)
        {
            var hand = TrainerCards.ComboName(d.HeroCards[0], d.HeroCards[1]);
            return SimBot.ChartCodeFor(d.HeroSpot, hand);
        }

        private static string ChartActionName(string code) => code switch
        {
            "R" => T("Sim.Chart.Raise"),
            "C" => T("Sim.Chart.Call"),
            _ => T("Sim.Chart.Fold")
        };

        /// <summary>Tamanos a probar: tercio, mitad, dos tercios y bote (o los de preflop) y all-in.</summary>
        private static IEnumerable<double> CandidateSizes(SimDecision d)
        {
            var o = d.Options;
            if (d.Street == SimStreet.Preflop)
            {
                yield return SimBot.RaiseTo(d.HeroSpot with { Profile = SimProfile.Tag }, o);
                yield return o.MaxRaiseTo;
                yield break;
            }

            var potAfterCall = d.Pot + o.CallAmount;
            foreach (var fraction in new[] { 0.33, 0.5, 0.75, 1.0 })
                yield return d.CurrentBet + fraction * potAfterCall;
            // El all-in solo se propone si no es una sobreapuesta enorme (pila corta frente al bote):
            // recomendar 99 bb en un bote de 2.5 bb no ensena nada util.
            if (o.MaxRaiseTo - d.CurrentBet <= 1.5 * potAfterCall)
                yield return o.MaxRaiseTo;
        }

        /// <summary>
        /// Valor aproximado de apostar o subir hasta "to": la maquina se retira con la probabilidad
        /// que le da su perfil a cada mano de su rango; si sigue, se juega la equity contra lo que
        /// le queda (contando sus resubidas como si pagara).
        /// </summary>
        private static (double Ev, double FoldAll, double? EquityWhenCalled) RaiseValue(SimDecision d, IReadOnlyList<(int Seat, IReadOnlyList<WeightedCombo> Range)> ranges, double to, int iterations)
        {
            var add = to - d.HeroStreetBet;
            var foldAll = 1.0;
            var continuing = new List<IReadOnlyList<WeightedCombo>>();
            var expectedCalls = 0.0;
            foreach (var (seat, range) in ranges)
            {
                var o = d.Opponents.First(x => x.Seat == seat);
                var toCall = Math.Min(to - o.StreetBet, o.Stack);
                var spot = d.HeroSpot with
                {
                    Profile = o.Profile,
                    Position = o.Position,
                    Pot = d.Pot + add,
                    ToCall = toCall,
                    Stack = o.Stack,
                    StreetBet = o.StreetBet,
                    CurrentBet = to,
                    PreflopRaises = d.HeroSpot.PreflopRaises + (d.Street == SimStreet.Preflop ? 1 : 0),
                    OwnRaises = o.OwnPreflopRaises,
                    VillainPosition = d.HeroPosition,
                    CallersAfterRaise = 0,
                    WasPreflopAggressor = false,
                    StreetAggressions = d.HeroSpot.StreetAggressions + 1,
                    InPosition = !d.HeroSpot.InPosition,
                };
                var options = new SimOptions(false, toCall, o.Stack > toCall + 1e-9, Math.Min(o.Stack + o.StreetBet, to * 2), o.Stack + o.StreetBet);
                var total = 0.0;
                var folded = 0.0;
                var cont = new List<WeightedCombo>(range.Count);
                foreach (var combo in range)
                {
                    var policy = SimBot.Policy(spot, combo.A, combo.B, options);
                    total += combo.Weight;
                    folded += combo.Weight * policy.Fold;
                    if (policy.Fold < 1) cont.Add(combo with { Weight = combo.Weight * (1 - policy.Fold) });
                }
                var fold = total > 0 ? folded / total : 1;
                foldAll *= fold;
                expectedCalls += (1 - fold) * toCall;
                if (cont.Count > 0 && fold < 1) continuing.Add(cont);
            }

            if (continuing.Count == 0 || foldAll >= 0.9999)
                return (d.Pot, 1, null);

            var eqCont = EquityCalculator.VsRanges(d.HeroCards, d.Board, continuing, iterations) / 100.0;
            var called = expectedCalls / Math.Max(1e-9, 1 - foldAll);
            var ev = foldAll * d.Pot + (1 - foldAll) * (eqCont * (d.Pot + add + called) - add);
            return (ev, foldAll, eqCont);
        }

        private static string RaiseLabel(SimDecision d, double to)
        {
            if (to >= d.Options.MaxRaiseTo - 1e-9)
                return F("Sim.Opt.AllIn", to);
            if (d.Street != SimStreet.Preflop)
            {
                var fraction = (to - d.CurrentBet) / Math.Max(0.5, d.Pot + d.Options.CallAmount);
                var name = fraction switch { < 0.4 => "1/3", < 0.6 => "1/2", < 0.85 => "2/3", _ => T("Sim.Opt.PotWord") };
                return F(d.CurrentBet > 0 ? "Sim.Opt.RaiseFrac" : "Sim.Opt.BetFrac", name, to);
            }
            return F("Sim.Opt.RaiseTo", to);
        }

        private static string T(string key) => LocalizationManager.Text(key);
        private static string F(string key, params object[] args) => string.Format(CultureInfo.CurrentCulture, T(key), args);
    }

    public static class SimProfiles
    {
        public static string Name(SimProfile profile) => LocalizationManager.Text("Sim.Profile." + profile);
        public static string Description(SimProfile profile) => LocalizationManager.Text("Sim.Profile." + profile + ".Desc");
    }
}
