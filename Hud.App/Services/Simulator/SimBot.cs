using Hud.App.Services.Trainer;
using S = Hud.App.Services.PreflopScenarios;

namespace Hud.App.Services.Simulator
{
    public enum SimProfile { Nit, Tag, Lag, Maniac, Station, Fish }

    /// <summary>
    /// Como juega cada perfil. Los numeros son "top X%" de las 169 manos (por equity contra una
    /// mano cualquiera, o contra manos fuertes para 3bet/4bet/all-in) y frecuencias despues del
    /// flop por fuerza de la mano (0 aire, 1 debil, 2 media, 3 fuerte). Estan ajustados con
    /// simulaciones para que cada perfil tenga las estadisticas de su tipo de jugador en la
    /// poblacion real (ver SimTraining).
    /// </summary>
    public sealed record SimStyle(
        double OpenFactor, double LimpPct,
        double ThreeBetPct, double CallRaisePct,
        double FourBetPct, double CallThreeBetPct,
        double FiveBetPct, double CallFourBetPct, double CallAllInPct,
        double IsoPct, double OverlimpPct,
        double[] BetFreq, double CbetBonus,
        double[][] VsBet, double[][] VsRaise,
        double BetSize, double OpenSize);

    /// <summary>Lo que ve un jugador cuando le toca: el estado publico de la mano desde su asiento.</summary>
    public sealed record SimSpot(
        SimProfile Profile, bool HeadsUp, SimStreet Street, string Position, IReadOnlyList<int> Board,
        double Pot, double ToCall, double Stack, double StreetBet, double CurrentBet,
        int PreflopRaises, int Limpers, int CallersAfterRaise, int OwnRaises, string VillainPosition,
        bool WasPreflopAggressor, int StreetAggressions, bool InPosition, int Opponents)
    {
        public bool FacingAllIn => ToCall >= Stack - 1e-9 && ToCall > 0;

        /// <summary>El estado de la mano desde el asiento de quien tiene que hablar.</summary>
        public static SimSpot For(SimHand hand, SimPlayer player)
        {
            var pre = hand.Actions.Where(a => a.Street == SimStreet.Preflop).ToList();
            var raises = pre.Count(a => a.Kind is SimActionKind.Raise or SimActionKind.Bet);
            var firstRaise = pre.FindIndex(a => a.Kind is SimActionKind.Raise or SimActionKind.Bet);
            var limpers = pre.Take(firstRaise < 0 ? pre.Count : firstRaise).Count(a => a.Kind == SimActionKind.Call);
            var lastRaise = pre.FindLastIndex(a => a.Kind is SimActionKind.Raise or SimActionKind.Bet);
            var callersAfter = lastRaise < 0 ? 0 : pre.Skip(lastRaise + 1).Count(a => a.Kind == SimActionKind.Call);
            var ownRaises = pre.Count(a => a.Seat == player.Seat && a.Kind is SimActionKind.Raise or SimActionKind.Bet);
            var lastAggressor = lastRaise >= 0 ? pre[lastRaise].Seat : -1;

            string villain;
            if (hand.Street == SimStreet.Preflop)
            {
                var source = raises == 0
                    ? pre.FirstOrDefault(a => a.Kind == SimActionKind.Call)?.Seat ?? -1
                    : lastAggressor;
                villain = hand.Players.FirstOrDefault(p => p.Seat == source)?.Position ?? "";
            }
            else
            {
                var bettor = hand.Actions.LastOrDefault(a => a.Street == hand.Street && a.Kind is SimActionKind.Bet or SimActionKind.Raise)?.Seat ?? -1;
                villain = hand.Players.FirstOrDefault(p => p.Seat == bettor)?.Position ?? "";
            }

            var options = hand.Options();
            var street = hand.Actions.Where(a => a.Street == hand.Street).ToList();
            return new SimSpot(
                player.Profile ?? SimProfile.Tag, hand.HeadsUp, hand.Street, player.Position, hand.Board.ToList(),
                hand.Pot, options.CallAmount, player.Stack, player.StreetBet, hand.CurrentBet,
                raises, limpers, callersAfter, ownRaises, villain,
                lastAggressor == player.Seat,
                street.Count(a => a.Kind is SimActionKind.Bet or SimActionKind.Raise),
                IsInPosition(hand, player),
                hand.Active.Count() - 1);
        }

        private static bool IsInPosition(SimHand hand, SimPlayer player)
        {
            // Despues del flop habla ultimo el mas cercano al boton (en heads-up, el boton).
            var n = hand.Players.Count;
            var order = Enumerable.Range(1, n).Select(step => hand.Players[(hand.ButtonIndex + step) % n]).Where(p => p.InHand).ToList();
            return order.Count > 0 && order[^1] == player;
        }
    }

    /// <summary>Probabilidad de cada tipo de accion: retirarse, pasar/pagar o apostar/subir.</summary>
    public readonly record struct SimPolicy(double Fold, double Passive, double Aggressive)
    {
        public SimPolicy Normalized(bool canFold, bool canRaise)
        {
            var f = canFold ? Math.Max(0, Fold) : 0;
            var a = canRaise ? Math.Max(0, Aggressive) : 0;
            var p = Math.Max(0, Passive) + (canFold ? 0 : Math.Max(0, Fold)) + (canRaise ? 0 : Math.Max(0, Aggressive));
            var total = f + p + a;
            return total <= 0 ? new SimPolicy(0, 1, 0) : new SimPolicy(f / total, p / total, a / total);
        }
    }

    /// <summary>
    /// La maquina: decide con reglas por perfil. Antes del flop, el TAG usa las tablas de APH y los
    /// demas perfiles rangos "top X%"; despues del flop, frecuencias por fuerza de la mano. La
    /// misma funcion sirve para leer su rango: con que manos habria hecho lo que hizo.
    /// </summary>
    public static class SimBot
    {
        public static readonly IReadOnlyDictionary<SimProfile, SimStyle> Styles = new Dictionary<SimProfile, SimStyle>
        {
            [SimProfile.Nit] = new(0.88, 0, 2.6, 9, 1.0, 3.6, 0.9, 1.7, 1.7, 6, 0,
                new[] { 0.05, 0.12, 0.6, 0.85 }, 0.15,
                new[] { new[] { .96, .03, .01 }, new[] { .8, .2, 0 }, new[] { .25, .7, .05 }, new[] { 0, .45, .55 } },
                new[] { new[] { 1, 0, 0.0 }, new[] { .97, .03, 0 }, new[] { .6, .4, 0 }, new[] { 0, .35, .65 } },
                0.6, 2.5),
            [SimProfile.Tag] = new(1.0, 0, 7, 14, 3, 7, 1.5, 2.6, 3, 12, 0,
                new[] { 0.25, 0.35, 0.7, 0.85 }, 0.3,
                new[] { new[] { .85, .05, .10 }, new[] { .6, .38, .02 }, new[] { .12, .78, .10 }, new[] { 0, .45, .55 } },
                new[] { new[] { .95, 0, .05 }, new[] { .85, .15, 0 }, new[] { .5, .45, .05 }, new[] { 0, .5, .5 } },
                0.6, 2.5),
            [SimProfile.Lag] = new(1.7, 0, 11, 27, 5, 12, 2.5, 4, 5, 22, 0,
                new[] { 0.42, 0.5, 0.8, 0.9 }, 0.3,
                new[] { new[] { .68, .10, .22 }, new[] { .45, .43, .12 }, new[] { .08, .70, .22 }, new[] { 0, .35, .65 } },
                new[] { new[] { .85, 0, .15 }, new[] { .7, .25, .05 }, new[] { .35, .55, .10 }, new[] { 0, .4, .6 } },
                0.7, 2.5),
            [SimProfile.Maniac] = new(3.1, 0, 22, 52, 12, 25, 7, 10, 12, 60, 0,
                new[] { 0.7, 0.75, 0.85, 0.9 }, 0.2,
                new[] { new[] { .45, .20, .35 }, new[] { .25, .45, .30 }, new[] { .05, .55, .40 }, new[] { 0, .3, .7 } },
                new[] { new[] { .55, .15, .30 }, new[] { .4, .4, .2 }, new[] { .15, .6, .25 }, new[] { 0, .3, .7 } },
                0.85, 3),
            [SimProfile.Station] = new(0.35, 45, 1.5, 45, 0.9, 25, 0.5, 8, 6, 2, 50,
                new[] { 0.03, 0.08, 0.25, 0.45 }, 0.05,
                new[] { new[] { .5, .5, 0 }, new[] { .12, .88, 0 }, new[] { .03, .95, .02 }, new[] { 0, .8, .2 } },
                new[] { new[] { .75, .25, 0 }, new[] { .35, .65, 0 }, new[] { .1, .9, 0 }, new[] { 0, .85, .15 } },
                0.5, 2.5),
            [SimProfile.Fish] = new(0.5, 47, 2.5, 42, 1.2, 15, 0.9, 5, 5, 4, 45,
                new[] { 0.12, 0.22, 0.4, 0.5 }, 0.1,
                new[] { new[] { .7, .28, .02 }, new[] { .35, .63, .02 }, new[] { .1, .85, .05 }, new[] { 0, .6, .4 } },
                new[] { new[] { .9, .1, 0 }, new[] { .6, .4, 0 }, new[] { .3, .68, .02 }, new[] { 0, .7, .3 } },
                0.45, 3),
        };

        private static readonly Lazy<Dictionary<string, double>> ChartOpenPct = new(() =>
        {
            var result = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var (scenario, hero, _) in PreflopLibrary.Charts.AvailableCharts().Where(c => c.Scenario is S.Open or S.HuOpen))
            {
                var combos = TrainerCards.AllClasses.Where(c => PreflopLibrary.Charts.GetCode(scenario, hero, "", c) == "R").Sum(TrainerCards.ComboCount);
                result[$"{scenario}:{hero}"] = 100.0 * combos / 1326;
            }
            return result;
        });

        /// <summary>Probabilidades de la maquina con esas dos cartas en ese momento de la mano.</summary>
        public static SimPolicy Policy(SimSpot spot, int a, int b, SimOptions options)
        {
            var policy = spot.Street == SimStreet.Preflop ? Preflop(spot, TrainerCards.ComboName(a, b)) : Postflop(spot, a, b);
            return policy.Normalized(options.FacingBet, options.CanRaise);
        }

        // ---------------------------------------------------------------- preflop

        private static SimPolicy Preflop(SimSpot spot, string hand)
        {
            var style = Styles[spot.Profile];
            var pct = PreflopRanking.TopPercentOf(hand);          // orden contra una mano cualquiera
            var strong = PreflopRanking.StrengthTopPercent(hand); // orden contra manos fuertes
            var hu = spot.HeadsUp;
            var widen = hu ? 2.2 : 1.0;

            // Contra un all-in (o pagar casi toda la pila) solo cuenta si la mano aguanta.
            if (spot.ToCall > 0 && spot.ToCall >= 0.45 * (spot.Stack + spot.StreetBet))
            {
                var callAllIn = Within(strong, style.CallAllInPct * (hu ? 1.8 : 1));
                return new SimPolicy(1 - callAllIn, callAllIn, 0);
            }

            // El TAG juega las tablas de APH cuando hay tabla para la situacion.
            if (spot.Profile == SimProfile.Tag && ChartCode(spot, hand) is { } code)
                return code switch
                {
                    "R" => new SimPolicy(0.01, 0.02, 0.97),
                    "C" => new SimPolicy(0.02, 0.96, 0.02),
                    _ => new SimPolicy(0.97, 0.03, 0)
                };

            if (spot.PreflopRaises == 0)
            {
                if (spot.Limpers == 0)
                {
                    var key = hu ? $"{S.HuOpen}:SB" : $"{S.Open}:{spot.Position}";
                    var baseOpen = ChartOpenPct.Value.TryGetValue(key, out var open) ? open : 20;
                    var raise = Within(pct, Math.Min(baseOpen * style.OpenFactor, hu ? 95 : 85));
                    var limp = style.LimpPct > 0 ? Math.Max(0, Within(pct, style.LimpPct * (hu ? 1.6 : 1)) - raise) : 0;
                    return new SimPolicy(1 - raise - limp, limp, raise);
                }

                // Contra limp: aislar subiendo o pagar detras (la ciega grande pasa gratis).
                var iso = Within(pct, style.IsoPct * widen);
                var over = spot.Position == "BB" ? 1 - iso : Math.Max(0, Within(pct, Math.Max(style.OverlimpPct, spot.Position == "SB" ? 30 : 0)) - iso);
                return new SimPolicy(Math.Max(0, 1 - iso - over), over, iso);
            }

            if (spot.OwnRaises == 0)
            {
                // Contra una subida (con o sin pagadores) o contra varias sin haber subido antes.
                var positionFactor = spot.Position switch { "BB" => 1.35, "SB" => 0.85, "BTN" => 1.1, _ => 1.0 };
                var openerFactor = spot.VillainPosition switch { "LJ" => 0.75, "HJ" => 0.85, "BTN" => 1.2, "SB" => 1.3, _ => 1.0 };
                if (spot.PreflopRaises == 1)
                {
                    var squeezeFactor = spot.CallersAfterRaise > 0 ? 0.8 : 1;
                    var threeBet = Within(strong, style.ThreeBetPct * widen * openerFactor * squeezeFactor);
                    var call = Math.Max(0, Within(pct, style.CallRaisePct * widen * positionFactor * openerFactor) - threeBet);
                    return new SimPolicy(1 - threeBet - call, call, threeBet);
                }

                var coldRaise = Within(strong, style.FiveBetPct);
                var coldCall = Math.Max(0, Within(strong, style.CallFourBetPct) - coldRaise);
                return new SimPolicy(1 - coldRaise - coldCall, coldCall, coldRaise);
            }

            // Ya subio y le volvieron a subir.
            var (raisePct, callPct) = spot.PreflopRaises switch
            {
                2 => (style.FourBetPct, style.CallThreeBetPct),
                3 => (style.FiveBetPct, style.CallFourBetPct),
                _ => (style.FiveBetPct * 0.6, style.CallAllInPct)
            };
            var factor = hu ? 1.8 : 1;
            var reraise = Within(strong, raisePct * factor);
            var continueCall = Math.Max(0, Within(strong, callPct * factor) - reraise);
            return new SimPolicy(1 - reraise - continueCall, continueCall, reraise);
        }

        private static string? ChartCode(SimSpot spot, string hand)
        {
            string? scenario = (spot.PreflopRaises, spot.OwnRaises, spot.Limpers, spot.CallersAfterRaise) switch
            {
                (0, _, 0, _) => spot.HeadsUp ? S.HuOpen : S.Open,
                (0, _, _, _) => spot.HeadsUp ? null : S.VsLimp,
                (1, 0, 0, 0) => spot.HeadsUp ? S.HuVsRaise : S.VsRaise,
                (1, 0, _, _) => spot.HeadsUp ? null : S.Squeeze,
                (2, 1, _, _) => spot.HeadsUp ? S.HuVs3Bet : S.Vs3Bet,
                (3, 1, _, _) => spot.HeadsUp ? S.HuVs4Bet : S.Vs4Bet,
                (4, 2, _, _) => spot.HeadsUp ? null : S.Vs5Bet,
                _ => null
            };
            if (scenario is null)
                return null;
            var villain = scenario is S.Open or S.HuOpen ? "" : spot.VillainPosition;
            return PreflopLibrary.Charts.GetCode(scenario, spot.Position, villain, hand);
        }

        /// <summary>Probabilidad de que una mano del top "pct" entre en un rango del top "limit" (borde suave).</summary>
        private static double Within(double pct, double limit)
        {
            if (limit <= 0) return 0;
            var softness = Math.Max(0.5, limit * 0.08);
            return 1 / (1 + Math.Exp((pct - limit) / softness));
        }

        // ---------------------------------------------------------------- despues del flop

        private static SimPolicy Postflop(SimSpot spot, int a, int b)
        {
            var style = Styles[spot.Profile];
            var strength = HandInsightReader.StrengthClass(a, b, spot.Board);

            if (spot.ToCall <= 1e-9)
            {
                var bet = style.BetFreq[strength];
                if (spot.Street == SimStreet.Flop && spot.WasPreflopAggressor && strength <= 1)
                    bet = Math.Min(0.95, bet + style.CbetBonus);
                // En botes de varios jugadores se farolea menos.
                if (spot.Opponents >= 2 && strength <= 1)
                    bet *= 0.5;
                return new SimPolicy(0, 1 - bet, bet);
            }

            // Contra una apuesta, o contra una subida (ya hubo dos agresiones en la calle).
            var row = (spot.StreetAggressions >= 2 ? style.VsRaise : style.VsBet)[strength];
            double fold = row[0], call = row[1], raise = row[2];

            // El tamano importa: contra apuestas grandes se sigue menos con manos flojas.
            var potBefore = Math.Max(0.5, spot.Pot - spot.ToCall);
            var ratio = spot.ToCall / potBefore;
            if (strength <= 1)
            {
                if (ratio > 0.8) { call *= 0.65; raise *= 0.7; }
                else if (ratio < 0.4) { call *= 1.25; }
            }
            if (spot.FacingAllIn && strength <= 2)
            {
                call *= strength == 2 ? 0.6 : 0.3;
                raise = 0;
            }
            fold = Math.Max(0, 1 - call - raise);
            return new SimPolicy(fold, call, raise);
        }

        // ---------------------------------------------------------------- tamano de la subida

        /// <summary>Hasta cuanto sube la maquina (total en la calle).</summary>
        public static double RaiseTo(SimSpot spot, SimOptions options)
        {
            var style = Styles[spot.Profile];
            double to;
            if (spot.Street == SimStreet.Preflop)
            {
                to = spot.PreflopRaises switch
                {
                    0 => (spot.HeadsUp ? style.OpenSize : spot.Position == "SB" ? 3 : style.OpenSize) + spot.Limpers * (spot.Position is "SB" or "BB" ? 1.5 : 1),
                    1 => spot.CurrentBet * (spot.InPosition && spot.Position is not ("SB" or "BB") ? 3 : 4) + spot.CallersAfterRaise * spot.CurrentBet,
                    2 => spot.CurrentBet * 2.2,
                    _ => options.MaxRaiseTo
                };
            }
            else
            {
                var potBeforeCall = spot.Pot;
                to = spot.ToCall <= 1e-9
                    ? style.BetSize * potBeforeCall
                    : spot.CurrentBet * 3;
            }

            // Si la subida deja poco detras, all-in.
            if (to >= 0.45 * options.MaxRaiseTo)
                to = options.MaxRaiseTo;
            return Math.Clamp(to, options.MinRaiseTo, options.MaxRaiseTo);
        }

        public static string? ChartCodeFor(SimSpot spot, string hand) => ChartCode(spot, hand);

        /// <summary>Que va a hacer la maquina a la que le toca (sin hacerlo todavia).</summary>
        public static (SimActionKind Kind, double To) Decide(SimHand hand, Random random)
        {
            var player = hand.ToAct ?? throw new InvalidOperationException("No le toca a nadie");
            var spot = SimSpot.For(hand, player);
            var options = hand.Options();
            var policy = Policy(spot, player.Cards[0], player.Cards[1], options);
            var roll = random.NextDouble();
            if (roll < policy.Fold)
                return (SimActionKind.Fold, 0);
            if (roll < policy.Fold + policy.Passive)
                return (options.CanCheck ? SimActionKind.Check : SimActionKind.Call, 0);
            return (hand.CurrentBet > 0 ? SimActionKind.Raise : SimActionKind.Bet, RaiseTo(spot, options));
        }

        public static void Apply(SimHand hand, SimActionKind kind, double to)
        {
            switch (kind)
            {
                case SimActionKind.Fold: hand.Fold(); break;
                case SimActionKind.Check or SimActionKind.Call: hand.CheckOrCall(); break;
                default: hand.RaiseTo(to); break;
            }
        }

        /// <summary>La maquina actua en la mano (si le toca a ella).</summary>
        public static void Act(SimHand hand, Random random)
        {
            var (kind, to) = Decide(hand, random);
            Apply(hand, kind, to);
        }
    }
}
