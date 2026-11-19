namespace Hud.App.Services.Simulator
{
    /// <summary>Estadisticas de un perfil medidas jugando miles de manos entre maquinas.</summary>
    public sealed class SimProfileStats
    {
        public int Hands, Vpip, Pfr, ThreeBetOpp, ThreeBet, PostAggr, PostCalls, SawFlop, Showdown, FacedCbet, FoldCbet, CbetOpp, Cbet;
        public double Net;

        public double VpipPct => Pct(Vpip, Hands);
        public double PfrPct => Pct(Pfr, Hands);
        public double ThreeBetPct => Pct(ThreeBet, ThreeBetOpp);
        public double Af => PostCalls == 0 ? PostAggr : (double)PostAggr / PostCalls;
        public double WtsdPct => Pct(Showdown, SawFlop);
        public double FoldToCbetPct => Pct(FoldCbet, FacedCbet);
        public double CbetPct => Pct(Cbet, CbetOpp);
        public double BbPer100 => Hands == 0 ? 0 : Net / Hands * 100;

        private static double Pct(int part, int total) => total == 0 ? 0 : 100.0 * part / total;

        public override string ToString() =>
            FormattableString.Invariant($"VPIP {VpipPct:0.0} PFR {PfrPct:0.0} 3B {ThreeBetPct:0.0} AF {Af:0.00} WTSD {WtsdPct:0.0} CB {CbetPct:0.0} FvCB {FoldToCbetPct:0.0} bb/100 {BbPer100:0.0} ({Hands} manos)");
    }

    /// <summary>
    /// "Entrenamiento" de las maquinas: juegan miles de manos entre ellas y se miden sus
    /// estadisticas (VPIP, PFR, 3bet, AF, WTSD...). Los perfiles se ajustan hasta que cada uno cae
    /// en la banda de su tipo de jugador segun la poblacion real; las pruebas lo comprueban.
    /// </summary>
    public static class SimTraining
    {
        public static Dictionary<SimProfile, SimProfileStats> Measure(int hands, int seed, bool headsUp = false)
        {
            var random = new Random(seed);
            var stats = Enum.GetValues<SimProfile>().ToDictionary(p => p, _ => new SimProfileStats());
            var profiles = Enum.GetValues<SimProfile>();
            var seats = headsUp ? 2 : 6;

            for (var h = 0; h < hands; h++)
            {
                var players = Enumerable.Range(0, seats)
                    .Select(i => { var p = profiles[random.Next(profiles.Length)]; return new SimPlayer(i, p.ToString(), p, 100); })
                    .ToList();
                var hand = new SimHand(players, random.Next(seats), random);
                var guard = 0;
                while (!hand.IsOver && guard++ < 500)
                    SimBot.Act(hand, random);

                Collect(hand, stats);
            }
            return stats;
        }

        private static void Collect(SimHand hand, Dictionary<SimProfile, SimProfileStats> stats)
        {
            var pre = hand.Actions.Where(a => a.Street == SimStreet.Preflop).ToList();
            var sawFlop = hand.Board.Count >= 3 && (hand.Actions.Any(a => a.Street >= SimStreet.Flop) || hand.Active.Count() >= 2);
            var lastRaiser = pre.LastOrDefault(a => a.Kind is SimActionKind.Raise or SimActionKind.Bet)?.Seat ?? -1;
            var flop = hand.Actions.Where(a => a.Street == SimStreet.Flop).ToList();
            var firstFlopBet = flop.FirstOrDefault(a => a.Kind is SimActionKind.Bet or SimActionKind.Raise);
            var cbet = firstFlopBet is not null && firstFlopBet.Seat == lastRaiser;
            var foldedPreflop = pre.Where(a => a.Kind == SimActionKind.Fold).Select(a => a.Seat).ToHashSet();
            var showdown = hand.Active.Count() >= 2;

            foreach (var player in hand.Players)
            {
                var s = stats[player.Profile!.Value];
                s.Hands++;
                s.Net += player.Net;
                var mine = pre.Where(a => a.Seat == player.Seat).ToList();
                if (mine.Any(a => a.Kind is SimActionKind.Call or SimActionKind.Raise or SimActionKind.Bet)) s.Vpip++;
                if (mine.Any(a => a.Kind is SimActionKind.Raise or SimActionKind.Bet)) s.Pfr++;

                // 3bet: en su primera accion habia exactamente una subida delante.
                var raisesBefore = 0;
                foreach (var action in pre)
                {
                    if (action.Seat == player.Seat)
                    {
                        if (raisesBefore == 1)
                        {
                            s.ThreeBetOpp++;
                            if (action.Kind == SimActionKind.Raise) s.ThreeBet++;
                        }
                        break;
                    }
                    if (action.Kind is SimActionKind.Raise or SimActionKind.Bet) raisesBefore++;
                }

                foreach (var action in hand.Actions.Where(a => a.Seat == player.Seat && a.Street >= SimStreet.Flop))
                {
                    if (action.Kind is SimActionKind.Bet or SimActionKind.Raise) s.PostAggr++;
                    else if (action.Kind == SimActionKind.Call) s.PostCalls++;
                }

                var playerSawFlop = sawFlop && !foldedPreflop.Contains(player.Seat);
                if (playerSawFlop)
                {
                    s.SawFlop++;
                    if (showdown && player.InHand) s.Showdown++;
                }

                if (playerSawFlop && player.Seat == lastRaiser)
                {
                    // Oportunidad de cbet: nadie aposto antes que el en el flop.
                    var firstBetBefore = flop.TakeWhile(a => a.Seat != player.Seat).Any(a => a.Kind is SimActionKind.Bet or SimActionKind.Raise);
                    if (!firstBetBefore && flop.Any(a => a.Seat == player.Seat))
                    {
                        s.CbetOpp++;
                        if (cbet) s.Cbet++;
                    }
                }

                if (cbet && playerSawFlop && player.Seat != lastRaiser)
                {
                    var after = flop.SkipWhile(a => a != firstFlopBet).Skip(1).FirstOrDefault(a => a.Seat == player.Seat);
                    if (after is not null)
                    {
                        s.FacedCbet++;
                        if (after.Kind == SimActionKind.Fold) s.FoldCbet++;
                    }
                }
            }
        }
    }
}
