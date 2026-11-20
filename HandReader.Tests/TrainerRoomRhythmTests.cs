using Hud.App.Services;
using Hud.App.Services.Trainer;
using Xunit;

namespace HandReader.Tests;

/// <summary>
/// Ritmo de la sala: cuanto se retira de verdad la gente cuando le apuestan, aprendido de los
/// historiales, y el solver de entrenamiento usandolo en lugar de su tabla fija.
/// </summary>
public class TrainerRoomRhythmTests
{
    private static readonly AphPreflopProvider Charts = PreflopLibrary.Load(Path.Combine(AppContext.BaseDirectory, "Data"));

    private static int[] Cards(string text) => text.Split(' ').Select(card =>
    {
        Assert.True(TrainerCards.TryParse(card, out var parsed), card);
        return parsed;
    }).ToArray();

    private static TrainerHand Hand(string flopActions) => TrainerHandParser.Parse((
        "PokerStars Hand #262759537012:  Hold'em No Limit (250/500) - 2026/11/19 9:09:41 UTC [2026/11/19 4:09:41 ET]\n" +
        "Table 'Asta V' 6-max (Play Money) Seat #4 is the button\n" +
        "Seat 1: Ausente (39247 in chips) is sitting out\n" +
        "Seat 2: Heroe (50000 in chips) \n" +
        "Seat 3: Ana (40000 in chips) \n" +
        "Seat 4: Beto (50862 in chips) \n" +
        "Seat 5: Ciro (39000 in chips) \n" +
        "Ciro: posts small blind 250\n" +
        "Heroe: posts big blind 500\n" +
        "*** HOLE CARDS ***\n" +
        "Dealt to Heroe [Ah Kd]\n" +
        "Ana: calls 500\n" +
        "Beto: calls 500\n" +
        "Ciro: calls 250\n" +
        "Heroe: checks \n" +
        "*** FLOP *** [9c Kc Qh]\n" +
        flopActions +
        "*** SUMMARY ***\n" +
        "Total pot 2000 | Rake 100 \n" +
        "Board [9c Kc Qh]\n" +
        "Seat 2: Heroe (big blind) folded on the Flop\n" +
        "Seat 3: Ana folded on the Flop\n" +
        "Seat 4: Beto (button) collected (1900)\n" +
        "Seat 5: Ciro (small blind) folded on the Flop\n").Split('\n'))!;

    [Fact]
    public void Solo_cuenta_a_quien_esta_en_la_mano_y_si_se_retiran_todos()
    {
        // "Ausente" esta sentado pero fuera de la mano: contarlo como rival daba retiradas imposibles
        // (la primera medicion salia 9,5% en vez de 33%).
        var bets = TrainerRoomRhythm.FromHand(Hand(
            "Ciro: checks \nHeroe: checks \nAna: checks \nBeto: bets 1000\nCiro: folds \nHeroe: folds \nAna: folds \n" +
            "Uncalled bet (1000) returned to Beto\nBeto collected 1900 from pot\n")).ToList();

        var bet = Assert.Single(bets);
        Assert.Equal("FLOP", bet.Street);
        Assert.Equal(3, bet.Facing);
        Assert.True(bet.CheckedTo);
        Assert.False(bet.IsRaise);
        Assert.Equal(0.5, bet.Size, 3);   // 1000 en un bote de 2000
        Assert.True(bet.AllFolded);
    }

    [Fact]
    public void Si_alguien_paga_o_sube_no_se_retiraron_todos()
    {
        var called = TrainerRoomRhythm.FromHand(Hand(
            "Ciro: bets 1000\nHeroe: folds \nAna: calls 1000\nBeto: folds \n")).Single();
        Assert.False(called.AllFolded);
        Assert.False(called.CheckedTo);   // apuesta del primero en hablar: nadie habia pasado

        var raised = TrainerRoomRhythm.FromHand(Hand(
            "Ciro: bets 1000\nHeroe: raises 2000 to 3000\nAna: folds \nBeto: folds \nCiro: folds \n")).ToList();
        Assert.Equal(2, raised.Count);
        Assert.False(raised[0].AllFolded);
        Assert.True(raised[1].IsRaise);
        Assert.True(raised[1].AllFolded);
        Assert.Equal(0.5, raised[1].Size, 3);   // sube 2000 sobre el bote tras igualar (3000 + 1000)
    }

    private static IEnumerable<(TrainerMoneyKind, TrainerRoomRhythm.BetResponse)> Bets(
        TrainerMoneyKind money, string street, int facing, bool checkedTo, double size, int count, double foldShare) =>
        Enumerable.Range(0, count).Select(i =>
            (money, new TrainerRoomRhythm.BetResponse(street, facing, checkedTo, size, false, i < count * foldShare)));

    [Fact]
    public void La_tabla_sale_de_lo_que_hace_la_gente_y_ficticio_y_real_no_se_mezclan()
    {
        // Pocas apuestas: no se usa (queda el modelo de siempre).
        var few = TrainerRoomRhythm.FromBets(Bets(TrainerMoneyKind.PlayMoney, "RIVER", 1, true, 0.5, 100, 0.5));
        Assert.Null(few.FoldAll(TrainerMoneyKind.PlayMoney, "RIVER", 1, true, 0.5, false));

        var room = TrainerRoomRhythm.FromBets(
            Bets(TrainerMoneyKind.PlayMoney, "RIVER", 1, true, 0.5, 300, 0.5)
                .Concat(Bets(TrainerMoneyKind.PlayMoney, "FLOP", 3, false, 0.5, 300, 0.1)));
        var river = room.FoldAll(TrainerMoneyKind.PlayMoney, "RIVER", 1, true, 0.5, false)!;
        var flop = room.FoldAll(TrainerMoneyKind.PlayMoney, "FLOP", 3, false, 0.5, false)!;
        Assert.InRange(river.FoldAll, 0.45, 0.5);
        Assert.InRange(flop.FoldAll, 0.1, 0.15);
        Assert.Equal(300, river.Sample);
        Assert.Equal(600, river.Total);

        // Las manos de ficticio nunca cuentan para el dinero real.
        Assert.Null(room.FoldAll(TrainerMoneyKind.RealMoney, "RIVER", 1, true, 0.5, false));
        Assert.Equal(0, room.Total(TrainerMoneyKind.RealMoney));
    }

    private static IReadOnlyList<WeightedCombo> Wide(IReadOnlyList<int> dead) =>
        HandRange.FromClasses("amplio", PreflopRanking.TopPercent(50)).Combos
            .Where(c => !dead.Contains(c.A) && !dead.Contains(c.B))
            .ToList();

    [Fact]
    public void El_solver_se_retira_lo_que_dice_la_sala_y_quien_paga_es_mas_fuerte()
    {
        // Pareja alta con kicker medio: lo que gana cuando pagan depende de quien se retira.
        var hero = Cards("Qh Jh");
        var board = Cards("Qs 8c 3d");
        var range = Wide(hero.Concat(board).ToList());
        var opponents = new[] { new SolverOpponent(range, null, 0) };

        var fixedTable = TrainerSolver.Postflop(hero, board, opponents, 6, 0, 0, 94, 0, canRaise: true, iterations: 4000);
        var room = TrainerSolver.Postflop(hero, board, opponents, 6, 0, 0, 94, 0, canRaise: true, iterations: 4000,
            room: (size, isRaise, facing) => 0.6);

        var before = fixedTable.Single(o => o.Kind == "Bet" && Math.Abs(o.Amount - 3) < 0.01);
        var after = room.Single(o => o.Kind == "Bet" && Math.Abs(o.Amount - 3) < 0.01);
        Assert.True(before.FoldPct < 0.5);
        Assert.Equal(0.6, after.FoldPct!.Value, 2);
        // Se retiran las manos flojas: lo que queda cuando pagan es mas fuerte.
        Assert.True(after.EquityWhenCalled < before.EquityWhenCalled);
        // Pasar no depende de cuanto se retiran.
        Assert.Equal(fixedTable.Single(o => o.Kind == "Check").Ev, room.Single(o => o.Kind == "Check").Ev, 6);
    }

    [Fact]
    public void Con_varios_rivales_se_retiran_todos_a_la_vez_lo_de_la_sala()
    {
        var hero = Cards("Ah Kh");
        var board = Cards("Ks 8c 3d");
        var dead = hero.Concat(board).ToList();
        var opponents = new[] { new SolverOpponent(Wide(dead), null, 0), new SolverOpponent(Wide(dead), null, 0) };

        var options = TrainerSolver.Postflop(hero, board, opponents, 10, 0, 0, 90, 0, canRaise: true,
            room: (size, isRaise, facing) => facing == 2 ? 0.3 : null);
        foreach (var bet in options.Where(o => o.Kind == "Bet"))
            Assert.Equal(0.3, bet.FoldPct!.Value, 2);
    }

    [Fact]
    public void Con_la_sala_las_parejas_siguen_pagando_y_la_escalera_apuesta()
    {
        // 7-3 con escalera en 4-J-J-6-5 y 3 rivales que pasaron (20-11, mano 262759545917): si para llegar
        // al % de la sala se retiraban tambien las parejas, "solo pagaban manos mejores" y se pasaba.
        var hero = Cards("7s 3c");
        var board = Cards("4h Js Jc 6d 5h");
        var dead = hero.Concat(board).ToList();
        var opponents = Enumerable.Range(0, 3).Select(_ => new SolverOpponent(Wide(dead), null, 0)).ToList();
        var options = TrainerSolver.Postflop(hero, board, opponents, 4, 0, 0, 90, 0, canRaise: true,
            room: (size, isRaise, facing) => 0.3);
        Assert.Equal("Bet", options[0].Kind);
        Assert.True(options[0].EquityWhenCalled > 0.6, $"si paga ganas {options[0].EquityWhenCalled:P0}");
    }

    [Fact]
    public void Sin_datos_de_la_sala_el_solver_no_cambia()
    {
        var hero = Cards("7h 7d");
        var board = Cards("7s Kc 2d");
        var opponents = new[] { new SolverOpponent(Wide(hero.Concat(board).ToList()), null, 0) };
        var plain = TrainerSolver.Postflop(hero, board, opponents, 6, 0, 0, 94, 0, canRaise: true);
        var noData = TrainerSolver.Postflop(hero, board, opponents, 6, 0, 0, 94, 0, canRaise: true, room: (_, _, _) => null);
        Assert.Equal(plain.Select(o => (o.Kind, o.Amount, o.FoldPct)), noData.Select(o => (o.Kind, o.Amount, o.FoldPct)));
    }

    [Fact]
    public void En_vivo_el_consejo_usa_la_sala_de_ficticio_y_lo_dice()
    {
        var turn = new LiveTurnState(new[]
        {
            new LiveActionOption(LiveActionKind.Check, null, "Pasar"),
            new LiveActionOption(LiveActionKind.Bet, 1, "Apostar 1 BB"),
        }, 6, new List<LivePlate>
        {
            new(0, "Heroe", null, 90, true, false, false),
            new(1, "A", "Fold", 100, false, false, false),
            new(2, "B", "Fold", 100, false, false, false),
            new(3, "Rival", "Check", 90, true, false, false),
            new(4, "C", "Fold", 100, false, false, false),
            new(5, "D", "Fold", 100, false, false, false),
        }, 2);

        LiveDecision Decide(TrainerRoomRhythm? rhythm)
        {
            var advisor = new TrainerLiveDecisionAdvisor(Charts) { Rhythm = rhythm };
            return advisor.Decide(Cards("7h 7d"), Cards("7s Kc 2d"), "BTN", turn, new LiveHandContext { HeroKey = "7h 7d" },
                new Dictionary<int, string> { [0] = "BTN", [3] = "BB" }, new Dictionary<int, string>(), _ => null);
        }

        var room = TrainerRoomRhythm.FromBets(Bets(TrainerMoneyKind.PlayMoney, "FLOP", 1, true, 0.5, 500, 0.6));
        Assert.Contains(Decide(room).Details, line => line.StartsWith("Ritmo de la sala", StringComparison.Ordinal));
        Assert.DoesNotContain(Decide(null).Details, line => line.StartsWith("Ritmo de la sala", StringComparison.Ordinal));

        // Una sala solo con manos de dinero real no cambia el consejo (que solo existe en ficticio).
        var real = TrainerRoomRhythm.FromBets(Bets(TrainerMoneyKind.RealMoney, "FLOP", 1, true, 0.5, 500, 0.6));
        Assert.DoesNotContain(Decide(real).Details, line => line.StartsWith("Ritmo de la sala", StringComparison.Ordinal));
    }
}
