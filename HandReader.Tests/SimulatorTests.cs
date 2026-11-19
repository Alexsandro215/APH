using Hud.App.Services.Simulator;
using Hud.App.Services.Trainer;
using Xunit;
using Xunit.Abstractions;

namespace HandReader.Tests;

/// <summary>
/// Simulador contra la maquina: reglas de la mano, "entrenamiento" de los perfiles (que cada uno
/// juegue como su tipo de jugador en miles de manos), lectura de rangos y revision de decisiones.
/// </summary>
public class SimulatorTests
{
    private readonly ITestOutputHelper _output;

    public SimulatorTests(ITestOutputHelper output) => _output = output;

    private static SimHand PlayBots(int seats, Random random)
    {
        var profiles = Enum.GetValues<SimProfile>();
        var players = Enumerable.Range(0, seats).Select(i => new SimPlayer(i, $"B{i}", profiles[random.Next(profiles.Length)], 100)).ToList();
        var hand = new SimHand(players, random.Next(seats), random);
        var guard = 0;
        while (!hand.IsOver && guard++ < 500)
            SimBot.Act(hand, random);
        Assert.True(hand.IsOver, "La mano no termino");
        return hand;
    }

    // ---------------- Reglas ----------------

    [Theory]
    [InlineData(2)]
    [InlineData(6)]
    public void Las_fichas_siempre_cuadran(int seats)
    {
        var random = new Random(11);
        for (var i = 0; i < 3000; i++)
        {
            var hand = PlayBots(seats, random);
            Assert.Equal(hand.Players.Sum(p => p.TotalBet), hand.Players.Sum(p => p.Won), precision: 6);
            Assert.All(hand.Players, p => Assert.True(p.Stack >= -1e-9));
            Assert.All(hand.Players, p => Assert.Equal(100, p.Stack + p.TotalBet, precision: 6));
            if (hand.Active.Count() > 1)
                Assert.Equal(5, hand.Board.Count); // si hubo showdown, la mesa esta completa
        }
    }

    [Fact]
    public void Las_subidas_respetan_la_subida_minima()
    {
        var random = new Random(5);
        for (var i = 0; i < 2000; i++)
        {
            var hand = PlayBots(6, random);
            foreach (var street in hand.Actions.GroupBy(a => a.Street))
            {
                var current = street.Key == SimStreet.Preflop ? 1.0 : 0.0;
                var lastSize = 1.0;
                foreach (var a in street.Where(a => a.Kind is SimActionKind.Bet or SimActionKind.Raise))
                {
                    var size = a.Amount - current;
                    Assert.True(size >= lastSize - 1e-9 || a.AllIn, $"Subida de {size} con minimo {lastSize}");
                    if (size >= lastSize) lastSize = size;
                    current = Math.Max(current, a.Amount);
                }
            }
        }
    }

    [Fact]
    public void Heads_up_el_boton_es_la_ciega_pequena_y_habla_primero()
    {
        var players = new List<SimPlayer> { new(0, "Yo", null, 100), new(1, "Nit", SimProfile.Nit, 100) };
        var hand = new SimHand(players, 0, new Random(1));
        Assert.Equal("SB", players[0].Position);
        Assert.Equal(0.5, players[0].TotalBet);
        Assert.Equal(1, players[1].TotalBet);
        Assert.Same(players[0], hand.ToAct);
    }

    [Fact]
    public void Botes_paralelos_el_corto_solo_gana_lo_que_puso()
    {
        // A tiene la mejor mano pero solo 10 bb; B y C pelean el resto.
        var a = new SimPlayer(0, "A", SimProfile.Tag, 10);
        var b = new SimPlayer(1, "B", SimProfile.Tag, 100);
        var c = new SimPlayer(2, "C", SimProfile.Tag, 100);
        int Card(string text) { TrainerCards.TryParse(text, out var card); return card; }
        var preset = new Dictionary<int, int[]>
        {
            [0] = new[] { Card("As"), Card("Ah") },
            [1] = new[] { Card("Ks"), Card("Kh") },
            [2] = new[] { Card("7c"), Card("2d") },
        };
        var board = new[] { Card("Ad"), Card("Kd"), Card("9c"), Card("4s"), Card("3h") };
        var hand = new SimHand(new[] { a, b, c }, 0, new Random(1), preset, board);
        // Boton = A (habla primero con 3 jugadores), luego SB = B, BB = C.
        hand.RaiseTo(10);    // A all-in
        hand.RaiseTo(40);    // B
        hand.CheckOrCall();  // C paga 40
        while (!hand.IsOver) hand.CheckOrCall();

        Assert.Equal(30, a.Won, precision: 6);          // 10 x 3
        Assert.Equal(60, b.Won, precision: 6);          // el bote paralelo de 30 x 2
        Assert.Equal(0, c.Won, precision: 6);
    }

    // ---------------- Entrenamiento de los perfiles ----------------

    [Fact]
    public void Cada_perfil_juega_como_su_tipo_de_jugador()
    {
        var stats = SimTraining.Measure(30000, seed: 7);
        foreach (var (profile, s) in stats)
            _output.WriteLine($"{profile,-8} {s}");

        var nit = stats[SimProfile.Nit];
        var tag = stats[SimProfile.Tag];
        var lag = stats[SimProfile.Lag];
        var maniac = stats[SimProfile.Maniac];
        var station = stats[SimProfile.Station];
        var fish = stats[SimProfile.Fish];

        Assert.InRange(nit.VpipPct, 8, 17);
        Assert.InRange(nit.PfrPct, 6, 14);
        Assert.InRange(tag.VpipPct, 17, 28);
        Assert.InRange(tag.PfrPct, 13, 24);
        Assert.InRange(lag.VpipPct, 26, 40);
        Assert.InRange(lag.PfrPct, 20, 34);
        Assert.True(maniac.VpipPct >= 50, $"Maniaco VPIP {maniac.VpipPct:0.0}");
        Assert.True(maniac.PfrPct >= 35, $"Maniaco PFR {maniac.PfrPct:0.0}");
        Assert.True(station.VpipPct >= 40, $"Station VPIP {station.VpipPct:0.0}");
        Assert.True(station.PfrPct <= 9, $"Station PFR {station.PfrPct:0.0}");
        Assert.InRange(fish.VpipPct, 35, 58);
        Assert.True(fish.PfrPct <= 14, $"Fish PFR {fish.PfrPct:0.0}");

        // Agresividad: maniaco > LAG > TAG > nit/fish > calling station.
        Assert.True(maniac.Af > lag.Af && lag.Af > tag.Af, "Orden de agresividad maniaco > LAG > TAG");
        Assert.True(station.Af < 1.0, $"Station AF {station.Af:0.00}");
        Assert.True(maniac.ThreeBetPct > lag.ThreeBetPct && lag.ThreeBetPct > tag.ThreeBetPct && tag.ThreeBetPct > nit.ThreeBetPct, "Orden de 3bet");

        // El calling station llega mucho al showdown; el nit se retira ante las c-bets mas que el station.
        Assert.True(station.WtsdPct > tag.WtsdPct && station.WtsdPct > nit.WtsdPct, "WTSD del calling station");
        Assert.True(nit.FoldToCbetPct > station.FoldToCbetPct, "Fold a c-bet del nit");
    }

    [Fact]
    public void En_heads_up_los_perfiles_juegan_mas_manos_pero_conservan_el_orden()
    {
        var stats = SimTraining.Measure(20000, seed: 3, headsUp: true);
        foreach (var (profile, s) in stats)
            _output.WriteLine($"HU {profile,-8} {s}");
        Assert.True(stats[SimProfile.Nit].VpipPct < stats[SimProfile.Tag].VpipPct);
        Assert.True(stats[SimProfile.Tag].VpipPct < stats[SimProfile.Lag].VpipPct);
        Assert.True(stats[SimProfile.Lag].VpipPct < stats[SimProfile.Maniac].VpipPct);
        Assert.True(stats[SimProfile.Station].PfrPct < stats[SimProfile.Tag].PfrPct);
    }

    // ---------------- Lectura de rangos ----------------

    [Fact]
    public void La_mano_real_de_la_maquina_siempre_esta_en_el_rango_leido()
    {
        var random = new Random(21);
        var checks = 0;
        var concentration = 0.0;
        for (var i = 0; i < 400; i++)
        {
            var session = new SimSession(SimDrills.All.First(d => d.Id == "6-playmoney"), random: random);
            var hand = session.NewHand();
            var guard = 0;
            while (!hand.IsOver && guard++ < 500)
            {
                if (session.HeroToAct)
                    session.HeroAct(random.NextDouble() < 0.5 ? SimActionKind.Call : SimActionKind.Fold);
                else
                    session.BotStep();
            }

            foreach (var bot in hand.Players.Where(p => !p.IsHero))
            {
                var weights = session.Tracker!.WeightsOf(bot.Seat);
                var index = SimCombos.IndexOf(bot.Cards[0], bot.Cards[1]);
                Assert.True(weights[index] > 0, $"{bot.Name}: su mano real no esta en el rango leido");
                var total = weights.Sum();
                concentration += weights[index] / total * 1326;
                checks++;
            }
        }
        // De media, la lectura le da a la mano real bastante mas peso que repartir a ciegas (1).
        _output.WriteLine($"Concentracion media en la mano real: {concentration / checks:0.00}x");
        Assert.True(concentration / checks > 1.5);
    }

    [Fact]
    public void El_indice_de_combinaciones_es_consistente()
    {
        for (var i = 0; i < SimCombos.All.Length; i++)
        {
            var (a, b) = SimCombos.All[i];
            Assert.Equal(i, SimCombos.IndexOf(a, b));
            Assert.Equal(i, SimCombos.IndexOf(b, a));
        }
    }

    // ---------------- Revision de decisiones ----------------

    private static SimDecision AllInDecision(string heroHand, Func<string, bool> villainRange, SimProfile profile)
    {
        var hero = TrainerCards.ExpandClass(heroHand).First();
        var weights = SimCombos.Classes.Select(c => villainRange(c) ? 1.0 : 0.0).ToArray();
        var options = new SimOptions(false, 75, false, 100, 100);
        var spot = new SimSpot(SimProfile.Tag, true, SimStreet.Preflop, "BB", Array.Empty<int>(), 125, 75, 75, 25, 100,
            4, 0, 0, 1, "SB", false, 4, true, 1);
        var villain = new SimOpponentState(1, profile, "SB", weights, 100, 0, 2, new[] { 0, 1 });
        return new SimDecision(0, SimStreet.Preflop, new[] { hero.A, hero.B }, Array.Empty<int>(), "BB", 125, 25, 75, 100,
            options, spot, new[] { villain }, SimActionKind.Call, 0);
    }

    [Fact]
    public void Contra_el_all_in_del_nit_con_AA_KK_hay_que_soltar_QQ()
    {
        var review = SimReviewer.Review(AllInDecision("QQ", c => c is "AA" or "KK", SimProfile.Nit));
        Assert.Equal(SimActionKind.Fold, review.Best.Kind);
        Assert.Equal(SimVerdict.Mistake, review.Verdict);
        Assert.True(review.Equity < 0.25);
    }

    [Fact]
    public void Contra_el_all_in_del_maniaco_QQ_se_paga()
    {
        var wide = AphLikeTop(35);
        var review = SimReviewer.Review(AllInDecision("QQ", c => wide.Contains(c), SimProfile.Maniac));
        Assert.Equal(SimActionKind.Call, review.Best.Kind);
        Assert.Equal(SimVerdict.Good, review.Verdict);
    }

    private static HashSet<string> AphLikeTop(double percent) => PreflopRanking.TopPercent(percent).ToHashSet();

    private static int Card(string text) { TrainerCards.TryParse(text, out var card); return card; }

    [Fact]
    public void Con_full_house_contra_el_calling_station_pasar_es_error_y_se_apuesta_hasta_el_bote()
    {
        var board = new[] { Card("8s"), Card("Th"), Card("7c"), Card("Tc"), Card("4d") };
        var hero = new[] { Card("8c"), Card("Ts") };
        // El station paga con casi todo: rango amplio.
        var wide = AphLikeTop(60);
        var weights = SimCombos.Classes.Select(c => wide.Contains(c) ? 1.0 : 0.0).ToArray();
        var options = new SimOptions(true, 0, true, 1, 95);
        var spot = new SimSpot(SimProfile.Tag, true, SimStreet.River, "BB", board, 10, 0, 95, 0, 0,
            1, 0, 1, 0, "SB", false, 0, false, 1);
        var villain = new SimOpponentState(1, SimProfile.Station, "SB", weights, 0, 95, 0, new[] { Card("Kd"), Card("Qd") });
        var decision = new SimDecision(0, SimStreet.River, hero, board, "BB", 10, 0, 95, 0, options, spot, new[] { villain }, SimActionKind.Check, 0);

        var review = SimReviewer.Review(decision);
        Assert.NotEqual(SimVerdict.Good, review.Verdict);
        Assert.Equal(SimActionKind.Bet, review.Best.Kind);
        Assert.True(review.Best.To <= 10 + 1e-9, $"Recomienda apostar {review.Best.To} en un bote de 10");
        // El resumen dice que apostar era mejor y por que (la mano iba por delante).
        Assert.Contains(review.Best.Label, review.Headline);
        Assert.Contains(review.Best.Label, review.Reason);
    }

    [Fact]
    public void Pagar_contra_un_rango_de_solo_AA_y_KK_se_explica_en_palabras_sencillas()
    {
        var board = new[] { Card("2s"), Card("5h"), Card("9c"), Card("Jd"), Card("3c") };
        var hero = new[] { Card("7c"), Card("6c") };
        var weights = SimCombos.Classes.Select(c => c is "AA" or "KK" ? 1.0 : 0.0).ToArray();
        var options = new SimOptions(false, 10, false, 20, 90); // sin subir: solo pagar o retirarse
        var spot = new SimSpot(SimProfile.Tag, true, SimStreet.River, "BB", board, 20, 10, 90, 0, 10,
            1, 0, 1, 0, "SB", false, 1, false, 1);
        var villain = new SimOpponentState(1, SimProfile.Nit, "SB", weights, 10, 80, 1, new[] { Card("Ad"), Card("Ac") });
        var decision = new SimDecision(0, SimStreet.River, hero, board, "BB", 20, 0, 90, 10, options, spot, new[] { villain }, SimActionKind.Call, 0);

        var review = SimReviewer.Review(decision);
        Assert.Equal(SimVerdict.Mistake, review.Verdict);
        Assert.Equal(SimActionKind.Fold, review.Best.Kind);
        Assert.Contains(review.Best.Label, review.Headline);
        // Dice cuanto ganaba (0 de cada 100), cuanto hacia falta (33) y que tenia la maquina.
        Assert.Contains("33", review.Reason);
        Assert.Contains("AA", review.Reason);
        Assert.Contains("KK", review.Reason);
        Assert.DoesNotContain("equity", review.Reason);
    }

    [Fact]
    public void Si_la_maquina_no_actuo_no_se_lista_un_rango_sin_sentido()
    {
        var weights = Enumerable.Repeat(1.0, SimCombos.All.Length).ToArray();
        Assert.True(SimReviewer.IsUntouched(weights));
        var hero = TrainerCards.ExpandClass("QQ").First();
        var options = new SimOptions(false, 0.5, true, 2, 100);
        var spot = new SimSpot(SimProfile.Tag, true, SimStreet.Preflop, "SB", Array.Empty<int>(), 1.5, 0.5, 99.5, 0.5, 1,
            0, 0, 0, 0, "", false, 0, true, 1);
        var villain = new SimOpponentState(1, SimProfile.Nit, "BB", weights, 1, 99, 0, new[] { 0, 1 });
        var decision = new SimDecision(0, SimStreet.Preflop, new[] { hero.A, hero.B }, Array.Empty<int>(), "SB", 1.5, 0.5, 99.5, 1,
            options, spot, new[] { villain }, SimActionKind.Raise, 2.5);
        var review = SimReviewer.Review(decision);
        Assert.Contains(review.Lines, l => l.Contains("cualquier mano") || l.Contains("any hand"));
        Assert.Equal(SimVerdict.Good, review.Verdict); // QQ se sube desde el boton en heads-up
    }

    [Fact]
    public void Los_rubros_reparten_manos_y_se_pueden_jugar_enteros()
    {
        var random = new Random(9);
        foreach (var drill in SimDrills.All)
        {
            var session = new SimSession(drill, SimProfile.Lag, random);
            for (var i = 0; i < 12; i++)
            {
                var hand = session.NewHand();
                Assert.Equal(drill.HeadsUp ? 2 : 6, hand.Players.Count);
                var guard = 0;
                while (!hand.IsOver && guard++ < 500)
                {
                    if (session.HeroToAct)
                    {
                        var options = hand.Options();
                        if (options.CanRaise && random.NextDouble() < 0.3) session.HeroAct(SimActionKind.Raise, options.MinRaiseTo);
                        else session.HeroAct(SimActionKind.Call);
                    }
                    else session.BotStep();
                }
                Assert.True(hand.IsOver);
                foreach (var review in session.ReviewAll())
                {
                    Assert.NotEmpty(review.Lines);
                    // Siempre hay un porque en palabras sencillas, y "lo mejor era" solo si no fue buena.
                    Assert.False(string.IsNullOrWhiteSpace(review.Reason));
                    Assert.Equal(review.Verdict != SimVerdict.Good, review.Headline.Length > 0);
                    Assert.DoesNotContain("{", review.Reason);
                }
            }
        }
    }
}
