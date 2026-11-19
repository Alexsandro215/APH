using Hud.App.Services;
using Hud.App.Services.Trainer;
using Xunit;

namespace HandReader.Tests;

/// <summary>
/// Decisiones obvias que fallaban en vivo en las mesas de 250/500 (noche del 18-11): una placa mal
/// leida no puede convertir un open normal en una "subida enorme", y quien paga un all-in tiene mano.
/// </summary>
public class TrainerObviousSpotsTests
{
    private static readonly AphPreflopProvider Charts = PreflopLibrary.Load(Path.Combine(AppContext.BaseDirectory, "Data"));

    private static int[] Cards(string text) =>
        text.Split(' ').Select(c => TrainerCards.TryParse(c, out var card) ? card : -1).ToArray();

    // Heroe en la BB; limper en LJ, open del BTN a 3 bb y la SB paga.
    private static readonly Dictionary<int, string> Positions = new()
    {
        [0] = "BB", [1] = "LJ", [2] = "HJ", [3] = "CO", [4] = "BTN", [5] = "SB"
    };

    private static List<LivePlate> Start() => new()
    {
        new LivePlate(0, "Heroe", "Post", 26.5, true, false, false),
        new LivePlate(1, "Limper", null, 100, true, false, false),
        new LivePlate(2, "B", null, 100, true, false, false),
        new LivePlate(3, "C", null, 100, true, false, false),
        new LivePlate(4, "Boton", null, 100, true, false, false),
        new LivePlate(5, "Ciega", "Post", 99.5, true, false, false),
    };

    /// <summary>
    /// Zwetana V: AKo en la BB contra limp + open del BTN a 3 bb + call de la SB. El stack del BTN se
    /// leyo 70 en vez de 97: APH creyo que habia subido a 30 bb ("subida enorme"), le dio un rango de
    /// casi solo AA/KK y dijo "NO IR: con ~12%". El boton de igualar decia 2 bb.
    /// </summary>
    [Fact]
    public void Una_placa_mal_leida_no_convierte_un_open_en_subida_enorme()
    {
        var context = new LiveHandContext { HeroKey = "Kh As" };
        context.Merge("PREFLOP", Start(), Positions);
        var now = new List<LivePlate>
        {
            new(0, "Heroe", "Post", 26.5, true, false, false),
            new(1, "Limper", "Call", 99, true, false, false),
            new(2, "B", "Fold", 100, false, false, false),
            new(3, "C", "Fold", 100, false, false, false),
            new(4, "Boton", "Raise", 70, true, false, false),
            new(5, "Ciega", "Call", 97, true, false, false),
        };
        context.Merge("PREFLOP", now, Positions);

        var turn = new LiveTurnState(new[]
        {
            new LiveActionOption(LiveActionKind.Fold, null, "No ir"),
            new LiveActionOption(LiveActionKind.Call, 2, "Igualar 2 BB"),
            new LiveActionOption(LiveActionKind.Raise, 5, "Subir a 5 BB"),
        }, 8, now, 3);

        var decision = new TrainerLiveDecisionAdvisor(Charts).Decide(Cards("Kh As"), Array.Empty<int>(), "BB", turn,
            context, Positions, new Dictionary<int, string>(), _ => null);

        Assert.NotEqual("Fold", decision.Kind);
        Assert.Contains(decision.Details, line => line.Contains("imposible"));
        Assert.DoesNotContain(decision.Details, line => line.Contains("subida enorme"));
    }

    [Fact]
    public void Pagar_un_all_in_pesa_mucho_mas_mano_que_un_call_normal()
    {
        var call = TrainerRangeEstimator.Factors("call", "TURN", null);
        var callAllIn = TrainerRangeEstimator.Factors("callallin", "TURN", null);
        // [aire, debil, media, fuerte]: lo fuerte igual, lo flojo mucho menos.
        Assert.Equal(call[3], callAllIn[3]);
        Assert.True(callAllIn[0] < call[0] / 3);
        Assert.True(callAllIn[1] < call[1] / 2);
    }

    /// <summary>
    /// Zwetana V: QQ en el BTN, flop 9d 9h 7h a cuatro; la BB apuesta 9,45 y paga uno. La placa de la
    /// BB no se leyo ("-"): se le daba el rango de alguien que solo habia pagado preflop (sin apostar) y
    /// APH dijo "sube". Ahora, si puso fichas en la calle y abrio la apuesta, cuenta como que aposto.
    /// </summary>
    [Fact]
    public void Quien_puso_fichas_con_la_placa_sin_leer_cuenta_como_que_aposto()
    {
        var positions = new Dictionary<int, string> { [0] = "BTN", [1] = "SB", [2] = "BB", [3] = "CO", [4] = "LJ", [5] = "HJ" };
        var context = new LiveHandContext { HeroKey = "Qs Qh" };
        context.Merge("FLOP", new List<LivePlate>
        {
            new(0, "Heroe", null, 170.6, true, false, false),
            new(1, "SB", null, 176, true, false, false),
            new(2, "Donk", null, 91.05, true, false, false),
            new(3, "Pagador", null, 27.85, true, false, false),
        }, positions);
        var now = new List<LivePlate>
        {
            new(0, "Heroe", null, 170.6, true, false, false),
            new(1, "SB", null, 176, true, false, false),
            new(2, "Donk", null, 81.6, true, false, false),
            new(3, "Pagador", "Call", 18.4, true, false, false),
        };
        context.Merge("FLOP", now, positions);

        var turn = new LiveTurnState(new[]
        {
            new LiveActionOption(LiveActionKind.Fold, null, "No ir"),
            new LiveActionOption(LiveActionKind.Call, 9.45, "Igualar 9,45 BB"),
            new LiveActionOption(LiveActionKind.Raise, 18.9, "Subir a 18,9 BB"),
        }, 37.8, now, 3);

        var decision = new TrainerLiveDecisionAdvisor(Charts).Decide(Cards("Qs Qh"), Cards("9d 9h 7h"), "BTN", turn,
            context, positions, new Dictionary<int, string>(), _ => null);

        Assert.Contains(decision.Details, line => line.StartsWith("Rango de quien apuesta") && line.Contains("aposto en esta calle"));
    }

    [Theory]
    [InlineData("Ah Qc", "Qd Kd Ad", "diamantes")]
    [InlineData("Ah Qc", "Qd Kd Ad 6c 4s", "diamantes")]
    [InlineData("As Ac", "5c 6d 7h 8s", "cuatro cartas seguidas")]
    [InlineData("Jd Td", "Qd Kd Ad", null)]   // tiene color (escalera real)
    [InlineData("Ah Qc", "Qd Kc 2h", null)]   // mesa seca
    [InlineData("9c 2d", "5c 6d 7h 8s", null)] // tiene la escalera
    [InlineData("2c 3d", "5c 6d 7h 8s 9c", "Juegas la mesa")]          // escalera puesta: juega la mesa
    [InlineData("Tc 2d", "5c 6d 7h 8s 9c", "ya hace escalera")]        // una mas alta, pero J-T le gana
    [InlineData("Qd Jd", "Qs Jh 7d 8d 9c", "cuatro cartas seguidas")]  // Pandora V: cualquier 10 hace escalera
    [InlineData("2d 3c", "Ad Kd 9d 5d Jc", "color es bajo")]           // color con el 2 y cuatro diamantes
    [InlineData("Kd 3c", "Ad Qd 9d 5d Jc", null)]                      // color con el rey: el mejor posible
    [InlineData("Kc Jd", "5d Kd Qh 3d Qd", null)]                      // Letaba IV: J con K y Q del palo en la mesa, solo el as gana
    [InlineData("Kc 8d", "5d Kd Qh 3d Qd", "color es bajo")]           // con el 8: el as, la J y el 10 ganan
    public void Mesa_de_color_o_de_escalera_sin_tenerla_es_peligrosa(string hero, string board, string? expected)
    {
        var danger = TrainerDecisionRules.BoardDanger(Cards(hero), Cards(board));
        if (expected is null)
            Assert.Null(danger);
        else
            Assert.Contains(expected, danger);
    }

    /// <summary>
    /// Ruvuma V: AQ (doble pareja) en Q-K-A de diamantes contra dos rivales que pasan. El solver
    /// pedia apostar el bote entero (y en el turn otra vez); ahora como mucho medio bote, y frente a
    /// una apuesta nunca subir.
    /// </summary>
    [Fact]
    public void En_mesa_de_color_sin_color_no_apuesta_el_bote_entero()
    {
        var positions = new Dictionary<int, string> { [0] = "BTN", [1] = "SB", [2] = "BB", [3] = "CO", [4] = "LJ", [5] = "HJ" };
        var context = new LiveHandContext { HeroKey = "Ah Qc" };
        var plates = new List<LivePlate>
        {
            new(0, "Heroe", null, 105.8, true, false, false),
            new(1, "SB", "Check", 95, true, false, false),
            new(3, "CO", "Check", 220, true, false, false),
        };
        context.Merge("FLOP", plates, positions);
        var turn = new LiveTurnState(new[]
        {
            new LiveActionOption(LiveActionKind.Check, null, "Pasar"),
            new LiveActionOption(LiveActionKind.Bet, 1, "Apostar 1 BB"),
        }, 15.1, plates, 2);

        var decision = new TrainerLiveDecisionAdvisor(Charts).Decide(Cards("Ah Qc"), Cards("Qd Kd Ad"), "BTN", turn,
            context, positions, new Dictionary<int, string>(), _ => null);

        if (decision.Kind != "Check")
        {
            var amount = double.Parse(System.Text.RegularExpressions.Regex.Match(decision.Action, @"[\d,]+").Value.Replace(',', '.'),
                System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(amount <= 15.1 * 0.55 + 0.01, decision.Action);
        }
    }

    [Theory]
    // Mintaka IV: A-Q sin pareja en T-4-4 (el solver subia a 51 bb contra un pagador).
    [InlineData("Ac Qd", "Tc 4s 4h", "FLOP", false, false, 0.0)]
    // Pandora V: doble pareja en Q-J-7-8-9 (el GTO iba all-in; cualquier 10 hace escalera).
    [InlineData("Qd Jd", "Qs Jh 7d 8d 9c", "RIVER", false, false, 0.35)]
    // Setebos V: pareja de cincos en el river (el GTO subia a 44 bb como farol).
    [InlineData("7d 5d", "5c 3s 6h Tc Qs", "RIVER", false, false, 0.35)]
    // Top pair: apuesta por valor, pero sin subir ni all-in.
    [InlineData("Ah Kd", "Ac 7s 2d", "FLOP", false, false, 0.75)]
    // Proyecto de color sin pareja: como mucho medio bote, sin subir.
    [InlineData("Ah 5h", "Kh 8h 2c", "FLOP", false, false, 0.55)]
    // Trio en mesa seca: sin limite.
    [InlineData("9h 9c", "9s 4d 2c", "FLOP", true, true, double.PositiveInfinity)]
    public void En_dinero_ficticio_sin_mano_fuerte_no_se_sube_ni_all_in(string hero, string board, string street,
        bool canRaise, bool canAllIn, double maxBet)
    {
        var limit = TrainerDecisionRules.PlayMoneyAggression(Cards(hero), Cards(board), street, 90, 30);
        Assert.Equal(canRaise, limit.CanRaise);
        Assert.Equal(canAllIn, limit.CanAllIn);
        Assert.Equal(maxBet, limit.MaxBet, 3);
    }

    /// <summary>
    /// Aristaeus IV (22:09): la pila del heroe se leyo una vez "1162" (116,2 sin la coma) al empezar el
    /// flop; APH creyo que ya habia puesto 1.045 bb en la calle y recomendo "ALL-IN (1162 BB)" con 7 alto.
    /// </summary>
    [Theory]
    [InlineData(1162, 116.2)]  // la mal leida es la primera de la calle
    [InlineData(116.2, 1162)]  // la mal leida llega despues
    public void Una_pila_con_la_coma_perdida_no_cuenta_como_fichas_puestas(double first, double second)
    {
        var positions = new Dictionary<int, string> { [0] = "SB", [1] = "BB" };
        var context = new LiveHandContext { HeroKey = "7s 5s" };
        context.Merge("FLOP", new List<LivePlate> { new(0, "Heroe", null, first, true, false, false), new(1, "Rival", null, 110.2, true, false, false) }, positions);
        context.Merge("FLOP", new List<LivePlate> { new(0, "Heroe", "Check", second, true, false, false), new(1, "Rival", "Bet", 107.4, true, false, false) }, positions);
        context.Merge("FLOP", new List<LivePlate> { new(0, "Heroe", "Check", 116.2, true, false, false), new(1, "Rival", "Bet", 107.4, true, false, false) }, positions);

        Assert.True((context.Committed("FLOP", 0) ?? 0) < 0.01);
        Assert.Equal(2.8, context.Committed("FLOP", 1) ?? 0, 2);
    }

    /// <summary>
    /// Pandora V (22:29): 62s, trio de doses con una carta en 7h 2h 2c 8c Jd. El GTO fue all-in de 87,6
    /// bb en un bote de 29,3 y pago Q2 (mismo trio, mejor kicker): -90,6 bb. Con trio de una carta o
    /// doble pareja se apuesta y se sube, pero el all-in no puede ser mayor que el bote. Con un set, si.
    /// </summary>
    [Fact]
    public void Trio_con_una_carta_no_va_all_in_por_encima_del_bote()
    {
        var trips = TrainerDecisionRules.PlayMoneyAggression(Cards("6s 2s"), Cards("7h 2h 2c 8c Jd"), "RIVER", 87.6, 29.3);
        Assert.True(trips.CanRaise);
        Assert.False(trips.Allows(raise: false, allIn: true, potFraction: 87.6 / 29.3));
        Assert.True(trips.Allows(raise: false, allIn: false, potFraction: 0.75));
        Assert.True(trips.Allows(raise: false, allIn: true, potFraction: 0.9));

        var set = TrainerDecisionRules.PlayMoneyAggression(Cards("2s 2d"), Cards("7h 2h Kc 8c Jd"), "RIVER", 87.6, 29.3);
        Assert.True(set.Unlimited);
    }

    /// <summary>
    /// Aristaeus IV y Botha IV (22:29-22:37, mesas de 624x430): la pila del heroe se leyo 0,03 y 0,04 bb.
    /// En la linea habia otro numero delante (contador) y se tomaba como fichas; la mesa pidio
    /// "IGUALAR ALL-IN (0,03 BB)" con T4o. Si hay una cifra con BB, manda esa.
    /// </summary>
    [Theory]
    [InlineData("15 132,6 bb", 132.6)]
    [InlineData("20 189,3 bb", 189.3)]
    [InlineData("bote 3,5 bb", 3.5)]
    [InlineData("1.795", 3.59)]
    public void La_cifra_con_bb_manda_sobre_otro_numero_de_la_linea(string text, double expected)
    {
        Assert.Equal(expected, LiveTableReader.ParseAmount(text, 500)!.Value, 3);
    }

    /// <summary>
    /// La causa real del 0,03: el OCR parte la pila en dos ("172,8 BB" -> "17 2,8 bb"). Con solo la
    /// cifra con BB salia 2,8 (Numerowia V 22:58: JTo en la SB con 172 bb, "NO IR" por una pila de 8).
    /// </summary>
    [Theory]
    [InlineData("17 2,8 bb", 172.8)]
    [InlineData("13 2,6 bb", 132.6)]
    [InlineData("1 72,8 bb", 172.8)]
    [InlineData("172,8 bb", 172.8)]
    [InlineData("8 bb", 8)]
    [InlineData("jdow 88554 178,5 bb", 178.5)]
    [InlineData("13 2, 5 bb", 132.5)]   // Aristaeus IV 22:41: se leyo 5
    [InlineData("11 2, 1 bb", 112.1)]   // Oceax V 22:43: se leyo 1
    [InlineData("132 ,5 bb", 132.5)]
    [InlineData("172,8bb", 172.8)]
    [InlineData("101 2 bb", 101.2)]     // Roxane V 23:06: se leyo 2 ("IGUALAR ALL-IN (2 BB)")
    [InlineData("20z 1 bb", 202.1)]     // Fanatica V 23:07: "20Z1 BB"
    [InlineData("ganado 193 bb", 193)]
    [InlineData("9,07 bb", 9.07)]
    [InlineData("16 bb", 16)]
    public void Una_pila_partida_por_el_ocr_se_junta(string text, double expected)
    {
        Assert.Equal(expected, LiveTableReader.StackFromLine(text, 500)!.Value, 3);
    }

    [Theory]
    [InlineData(5.0, 132.5, 3.0, 1, true)]     // pila partida: imposible con un bote de 3
    [InlineData(2.0, 101.0, 28.1, 1, true)]    // Roxane V: "IGUALAR ALL-IN (2 BB)"
    [InlineData(90.0, 132.5, 40.0, 1, false)]  // metio 40 en este bote: cuadra
    [InlineData(5.0, 132.5, 3.0, 2, false)]    // falta una mano en el historial: no se sabe
    [InlineData(150.0, 100.0, 2.0, 1, false)]  // recompra: la pila solo puede crecer
    [InlineData(925.0, 93.5, 15.0, 1, true)]   // Letaba IV 23:22: "92,5" leido 925
    [InlineData(100.0, 15.0, 1.5, 1, false)]   // con 15 bb anadio fichas hasta 100
    public void Una_pila_propia_que_no_cuadra_con_el_historial_se_ignora(double read, double expected, double pot, int handsSince, bool implausible)
    {
        Assert.Equal(implausible, TrainerSession.ImplausibleHeroStack(read, expected, pot, handsSince));
    }

    /// <summary>
    /// Roxane V (00:04): ATs en el turn, el GTO mezclaba igualar 48% (-1,36 bb) y no ir 46% (0 bb) y
    /// se recomendaba igualar por ser lo mas frecuente. Ahora la que mas gana; con EV parecidos, la
    /// mas frecuente.
    /// </summary>
    [Fact]
    public void En_una_mezcla_gto_se_recomienda_la_que_mas_gana()
    {
        var call = new Hud.App.Services.Solver.GtoChoice(new Hud.App.Services.Solver.GtoAction(Hud.App.Services.Solver.GtoKind.Call, 27.3), 0.48, -1.36);
        var fold = new Hud.App.Services.Solver.GtoChoice(new Hud.App.Services.Solver.GtoAction(Hud.App.Services.Solver.GtoKind.Fold, 0), 0.46, 0);
        var shove = new Hud.App.Services.Solver.GtoChoice(new Hud.App.Services.Solver.GtoAction(Hud.App.Services.Solver.GtoKind.Raise, 73.7, true), 0.06, -2.76);
        Assert.Same(fold, TrainerLiveDecisionAdvisor.BetterGtoChoice(new[] { call, fold, shove }, 54.6));

        var allIn = new Hud.App.Services.Solver.GtoChoice(new Hud.App.Services.Solver.GtoAction(Hud.App.Services.Solver.GtoKind.Bet, 87.6, true), 0.74, 31.25);
        var bet = new Hud.App.Services.Solver.GtoChoice(new Hud.App.Services.Solver.GtoAction(Hud.App.Services.Solver.GtoKind.Bet, 21.98), 0.26, 31.25);
        Assert.Null(TrainerLiveDecisionAdvisor.BetterGtoChoice(new[] { allIn, bet }, 29.3));

        // Letaba IV (03:47): A5s, pareja de cincos, river que cierra el color, "no ir 58% · igualar 42%".
        // Igualar daba mas EV pero arriesga mas: se queda no ir (pago un color, -71 bb).
        var foldMost = new Hud.App.Services.Solver.GtoChoice(new Hud.App.Services.Solver.GtoAction(Hud.App.Services.Solver.GtoKind.Fold, 0), 0.58, 11.73);
        var callLess = new Hud.App.Services.Solver.GtoChoice(new Hud.App.Services.Solver.GtoAction(Hud.App.Services.Solver.GtoKind.Call, 48.7), 0.42, 21.57);
        Assert.Null(TrainerLiveDecisionAdvisor.BetterGtoChoice(new[] { foldMost, callLess }, 97.4));
    }

    /// <summary>
    /// Tanete V (00:09): KJs con proyecto de color en 3c 9s As contra un maniaco (VPIP 60, AF 9,3, 48
    /// manos). APH aposto medio bote de semi-farol, el maniaco fue all-in y pagar ya era obligado
    /// (AQ, -83 bb). Su propio plan dice "deja que apueste el y no le hagas faroles": sin pareja, pasar.
    /// </summary>
    [Fact]
    public void Contra_un_maniaco_sin_pareja_no_se_apuesta()
    {
        var positions = new Dictionary<int, string> { [0] = "SB", [5] = "BTN", [1] = "BB", [2] = "LJ", [3] = "HJ", [4] = "CO" };
        var context = new LiveHandContext { HeroKey = "Ks Js" };
        var plates = new List<LivePlate>
        {
            new(0, "Heroe", null, 63.1, true, false, false),
            new(5, "Maniaco", null, 381.8, true, false, false),
        };
        context.Merge("FLOP", plates, positions);
        var turn = new LiveTurnState(new[]
        {
            new LiveActionOption(LiveActionKind.Check, null, "Pasar"),
            new LiveActionOption(LiveActionKind.Bet, 1, "Apostar 1 BB"),
        }, 40.6, plates, 2);
        var maniac = new TrainerVillainProfile("Maniaco", 48, 60, 40, 0, 9.3, 0, 37, "sesion");

        var decision = new TrainerLiveDecisionAdvisor(Charts).Decide(Cards("Ks Js"), Cards("3c 9s As"), "SB", turn,
            context, positions, new Dictionary<int, string> { [5] = "Maniaco" }, name => name == "Maniaco" ? maniac : null);

        Assert.Equal("Check", decision.Kind);
        Assert.Contains(decision.Details, line => line.Contains("maníaco sin pareja"));
    }

    /// <summary>
    /// Lacrimosa V (00:14): 22 en HJ, primero en hablar; un jugador nuevo en el CO puso la ciega grande
    /// y el bote era 2,5 bb. APH conto "1 limper delante" y dijo igualar (limp de 22). Quien habla
    /// despues del heroe no puede haber entrado: el bote de mas es una ciega puesta.
    /// </summary>
    [Fact]
    public void Una_ciega_puesta_por_un_jugador_nuevo_no_es_un_limper()
    {
        var positions = new Dictionary<int, string> { [0] = "HJ", [1] = "CO", [3] = "BTN", [4] = "SB", [5] = "BB" };
        var context = new LiveHandContext { HeroKey = "2s 2d" };
        var plates = new List<LivePlate>
        {
            new(0, "Heroe", null, 397, true, false, false),
            new(1, "Nuevo", null, 99, true, false, false),
            new(3, "Boton", null, 152.4, true, false, false),
            new(4, "Chica", null, 165.3, true, false, false),
            new(5, "Grande", null, 90.2, true, false, false),
        };
        context.Merge("PREFLOP", plates, positions);
        var turn = new LiveTurnState(new[]
        {
            new LiveActionOption(LiveActionKind.Fold, null, "No ir"),
            new LiveActionOption(LiveActionKind.Call, 1, "Igualar 1 BB"),
            new LiveActionOption(LiveActionKind.Raise, 2, "Subir a 2 BB"),
        }, 2.5, plates, 3);

        var decision = new TrainerLiveDecisionAdvisor(Charts).Decide(Cards("2s 2d"), Array.Empty<int>(), "HJ", turn,
            context, positions, new Dictionary<int, string>(), _ => null);

        Assert.NotEqual("Call", decision.Kind);
        Assert.DoesNotContain(decision.Details, line => line.Contains("limper(s) delante"));
    }

    /// <summary>
    /// Lehigh V (00:45): AJo top pair en el river, bote 73,7 y 52,8 detras. El solver eligio apostar
    /// 55,5 (75%), que con esa pila es all-in; el limite solo lo contaba como all-in si llegaba a la
    /// pila entera. Pago AQ (-72,7 bb). Ahora lo que compromete dos tercios de la pila cuenta como
    /// all-in, y con top pair sin estar comprometido se apuesta menos.
    /// </summary>
    [Fact]
    public void Top_pair_no_apuesta_lo_que_ya_es_un_all_in()
    {
        var positions = new Dictionary<int, string> { [0] = "HJ", [4] = "SB", [5] = "BB", [1] = "CO", [2] = "BTN", [3] = "LJ" };
        var context = new LiveHandContext { HeroKey = "As Jc" };
        var plates = new List<LivePlate>
        {
            new(0, "Heroe", null, 52.8, true, false, false),
            new(4, "Rival", "Check", 36.7, true, false, false),
        };
        context.Merge("RIVER", plates, positions);
        var turn = new LiveTurnState(new[]
        {
            new LiveActionOption(LiveActionKind.Check, null, "Pasar"),
            new LiveActionOption(LiveActionKind.Bet, 1, "Apostar 1 BB"),
        }, 73.7, plates, 2);

        var decision = new TrainerLiveDecisionAdvisor(Charts).Decide(Cards("As Jc"), Cards("Kc Ah 3h Ts 4d"), "HJ", turn,
            context, positions, new Dictionary<int, string>(), _ => null);

        Assert.DoesNotContain("ALL-IN", decision.Action);
    }

    private static LiveDecision FlopVs(TrainerVillainProfile villain, bool bluffMode)
    {
        var positions = new Dictionary<int, string> { [0] = "BTN", [5] = "BB", [1] = "SB", [2] = "LJ", [3] = "HJ", [4] = "CO" };
        var context = new LiveHandContext { HeroKey = "Ks 7h", BluffFolders = bluffMode };
        var plates = new List<LivePlate>
        {
            new(0, "Heroe", null, 97, true, false, false),
            new(5, villain.Name, "Check", 97, true, false, false),
        };
        context.Merge("FLOP", plates, positions);
        var turn = new LiveTurnState(new[]
        {
            new LiveActionOption(LiveActionKind.Check, null, "Pasar"),
            new LiveActionOption(LiveActionKind.Bet, 1, "Apostar 1 BB"),
        }, 5.5, plates, 2);
        return new TrainerLiveDecisionAdvisor(Charts).Decide(Cards("Ks 7h"), Cards("9d 5c 2s"), "BTN", turn,
            context, positions, new Dictionary<int, string> { [5] = villain.Name }, name => name == villain.Name ? villain : null);
    }

    /// <summary>
    /// Farol por mesa (boton "Farol" en Tiempo real): contra quien se retira mucho a las c-bets, mano a
    /// mano y sin mano hecha, con el farol activado se puede apostar hasta medio bote; apagado se pasa y
    /// se avisa; contra un pagador no se farolea aunque este activado.
    /// </summary>
    [Fact]
    public void El_farol_por_mesa_solo_se_usa_contra_quien_se_retira()
    {
        var folder = new TrainerVillainProfile("Plegador", 60, 24, 15, 4, 2, 78, 22, "sesion");
        Assert.Equal("se retira mucho", folder.Leak);

        var on = FlopVs(folder, bluffMode: true);
        Assert.Equal("Bet", on.Kind);
        var amount = double.Parse(System.Text.RegularExpressions.Regex.Match(on.Action, @"[\d,]+").Value.Replace(',', '.'),
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(amount <= 5.5 * 0.5 + 0.5, on.Action);
        Assert.Contains("Farol activado", on.BluffNote);

        var off = FlopVs(folder, bluffMode: false);
        Assert.Equal("Check", off.Kind);
        Assert.Contains("activa «Farol»", off.BluffNote);

        var station = new TrainerVillainProfile("Pagador", 60, 62, 6, 1, 0.8, 20, 45, "sesion");
        var stationOn = FlopVs(station, bluffMode: true);
        Assert.Equal("Check", stationOn.Kind);
        Assert.Null(stationOn.BluffNote);
    }

    /// <summary>
    /// Thetis V (01:13): A10o en el BTN 3-betea al open del CO; la SB va all-in (34 bb) y el CO paga.
    /// La pila del CO se leyo ya con su open puesto, asi que "lo que puso" quedaba 2 bb corto y no
    /// contaba como que pago: equity solo contra el all-in (40%) y "IGUALAR 28,3". Con el CO dentro (y
    /// fichas detras para apostar) A10o no llega.
    /// </summary>
    [Fact]
    public void Quien_paga_un_all_in_cuenta_aunque_su_pila_se_leyera_tarde()
    {
        var positions = new Dictionary<int, string> { [0] = "BTN", [2] = "SB", [3] = "BB", [5] = "CO", [4] = "HJ", [1] = "LJ" };
        var context = new LiveHandContext { HeroKey = "Ac Th" };
        context.Merge("PREFLOP", new List<LivePlate>
        {
            new(0, "Heroe", null, 98.5, true, false, false),
            new(2, "Chica", null, 33.8, true, false, false),
            new(3, "Grande", null, 30, true, false, false),
            new(5, "Abridor", "Raise", 248.8, true, false, false),
        }, positions);
        context.Merge("PREFLOP", new List<LivePlate>
        {
            new(0, "Heroe", "Raise", 92.5, true, false, false),
            new(2, "Chica", null, 33.8, true, false, false),
            new(3, "Grande", null, 30, true, false, false),
            new(5, "Abridor", "Raise", 248.8, true, false, false),
        }, positions);
        var now = new List<LivePlate>
        {
            new(0, "Heroe", "Raise", 92.5, true, false, false),
            new(2, "Chica", "AllIn", null, true, false, false),
            new(3, "Grande", "Fold", 30, false, false, false),
            new(5, "Abridor", "Call", 216.5, true, false, false),
        };
        context.Merge("PREFLOP", now, positions);
        var turn = new LiveTurnState(new[]
        {
            new LiveActionOption(LiveActionKind.Fold, null, "No ir"),
            new LiveActionOption(LiveActionKind.Call, 28.3, "Igualar 28,3 BB"),
            new LiveActionOption(LiveActionKind.Raise, 62.6, "Subir a 62,6 BB"),
        }, 76.6, now, 3);

        var decision = new TrainerLiveDecisionAdvisor(Charts).Decide(Cards("Ac Th"), Array.Empty<int>(), "BTN", turn,
            context, positions, new Dictionary<int, string>(), _ => null);

        Assert.Contains(decision.Details, line => line.Contains("1 mas en el bote"));
        Assert.Equal("Fold", decision.Kind);
    }

    /// <summary>
    /// Gabriella V (01:38): QQ en 9h 9d Ad 8h 9s (full de nueves con damas; cualquier as gana), un fish
    /// pasivo apuesta 3 en el river con otro rival dentro. El solver subia a 9 aunque cuando pagan solo
    /// ganaba el 36%. En dinero ficticio subir es solo por valor: ganando menos de la mitad cuando
    /// pagan, se iguala.
    /// </summary>
    [Fact]
    public void No_se_sube_si_cuando_pagan_se_gana_menos_de_la_mitad()
    {
        var positions = new Dictionary<int, string> { [0] = "BTN", [1] = "SB", [3] = "LJ", [2] = "BB", [4] = "HJ", [5] = "CO" };
        var context = new LiveHandContext { HeroKey = "Qs Qc" };
        var plates = new List<LivePlate>
        {
            new(0, "Heroe", null, 88, true, false, false),
            new(1, "Otro", null, 200, true, false, false),
            new(3, "Fish", "Bet", 239.5, true, false, false),
        };
        context.Merge("RIVER", plates, positions);
        var turn = new LiveTurnState(new[]
        {
            new LiveActionOption(LiveActionKind.Fold, null, "No ir"),
            new LiveActionOption(LiveActionKind.Call, 3, "Igualar 3 BB"),
            new LiveActionOption(LiveActionKind.Raise, 6, "Subir a 6 BB"),
        }, 21.9, plates, 3);
        var fish = new TrainerVillainProfile("Fish", 19, 58, 0, 0, 0.3, 0, 36, "sesion");

        var decision = new TrainerLiveDecisionAdvisor(Charts).Decide(Cards("Qs Qc"), Cards("9h 9d Ad 8h 9s"), "BTN", turn,
            context, positions, new Dictionary<int, string> { [3] = "Fish" }, name => name == "Fish" ? fish : null);

        Assert.NotEqual("Raise", decision.Kind);

        // Las opciones que dio el solver en vivo: subir a 9 (+6,98, gana 36% si pagan) e igualar (+6,73).
        var options = new[]
        {
            new SolverOption("Raise", 9, 6.98, 0.01, 0.36),
            new SolverOption("Call", 3, 6.73, null, null),
            new SolverOption("Fold", 0, 0, null, null),
        };
        Assert.Equal("Call", TrainerLiveDecisionAdvisor.ValueRaisesOnly(options)[0].Kind);
        var valueRaise = new[] { new SolverOption("Raise", 9, 8, 0.05, 0.7), new SolverOption("Call", 3, 6, null, null) };
        Assert.Equal("Raise", TrainerLiveDecisionAdvisor.ValueRaisesOnly(valueRaise)[0].Kind);
    }

    /// <summary>
    /// Gabriella V (01:55): QJ doble pareja en 6h Qh Ks Kc Js; el bote del turn se vio en 71,6 y en el
    /// river se leyo 428,5 (eran ~134). El precio salio 8% y se pago el all-in de un full (QQ). Al
    /// empezar la calle, un bote mayor que el de antes mas las apuestas posibles es imposible: se toma
    /// lo minimo seguro y el precio vuelve a ser exigente.
    /// </summary>
    [Fact]
    public void Un_bote_imposible_al_empezar_la_calle_no_regala_el_precio()
    {
        var positions = new Dictionary<int, string> { [0] = "BB", [5] = "SB", [1] = "LJ", [2] = "HJ", [3] = "CO", [4] = "BTN" };
        var context = new LiveHandContext { HeroKey = "Qs Jh" };
        context.Merge("TURN", new List<LivePlate>
        {
            new(0, "Heroe", null, 94.1, true, false, false),
            new(4, "Otro", null, 348.8, true, false, false),
            new(5, "Rival", null, 60.9, true, false, false),
        }, positions);
        context.Merge("TURN", new List<LivePlate>
        {
            new(0, "Heroe", "Call", 70.1, true, false, false),
            new(4, "Otro", "Call", 324.8, true, false, false),
            new(5, "Rival", "Raise", 36.9, true, false, false),
        }, positions);
        context.PotSeen["TURN"] = 71.6;
        var plates = new List<LivePlate>
        {
            new(0, "Heroe", null, 70.1, true, false, false),
            new(1, "A", null, 110.8, false, false, false),
            new(2, "B", null, 99, false, false, false),
            new(3, "C", null, 213.9, false, false, false),
            new(4, "Otro", null, 324.8, true, false, false),
            new(5, "Rival", "Bet", null, true, false, false),
        };
        var turn = new LiveTurnState(new[]
        {
            new LiveActionOption(LiveActionKind.Fold, null, "No ir"),
            new LiveActionOption(LiveActionKind.Call, 36.9, "Igualar 36,9 BB"),
            new LiveActionOption(LiveActionKind.Raise, 70.1, "Subir a 70,1 BB"),
        }, 428.5, plates, 3);

        var decision = new TrainerLiveDecisionAdvisor(Charts).Decide(Cards("Qs Jh"), Cards("6h Qh Ks Kc Js"), "BB", turn,
            context, positions, new Dictionary<int, string>(), _ => null);

        Assert.Contains(decision.Details, line => line.Contains("Bote leído 428,5") && line.Contains("mínimo seguro"));
    }

    /// <summary>
    /// kastensyster (18-11, Gabriella V): VPIP 36, PFR 1,6, AF 2,6. Entra pagando con todo, pero cuando
    /// sube o va all-in despues del flop tiene mano (full, color, doble pareja). Ahora es "trampero": su
    /// rango de subida postflop pesa mucho mas las manos fuertes y el plan lo dice.
    /// </summary>
    [Fact]
    public void El_trampero_se_reconoce_y_sus_subidas_cuentan_como_mano()
    {
        var trapper = new TrainerVillainProfile("kastensyster", 61, 36.1, 1.6, 0, 2.6, 50, 30, "sesion");
        Assert.True(trapper.IsTrapper);
        Assert.Equal("trampero", trapper.Leak);
        Assert.Equal("Trampero", trapper.Kind);
        Assert.Contains("créele", trapper.LeakPlan);

        var normal = TrainerRangeEstimator.Factors("raise", "RIVER", null);
        var trap = TrainerRangeEstimator.Factors("raise", "RIVER", trapper);
        Assert.True(trap[0] < normal[0] && trap[1] < normal[1] && trap[2] < normal[2]);
        Assert.Equal(1.0, trap[3]);

        // Un fish que paga todo y casi nunca apuesta no es trampero.
        Assert.False(new TrainerVillainProfile("Fish", 60, 58, 2, 0, 0.7, 40, 30, "sesion").IsTrapper);
    }

    /// <summary>
    /// Tone IV (03:31): J9o en el BTN, todos fuera, bote 1,5. Una pila propia mal leida al empezar la
    /// mano (259,2 en vez de 192,6) daba 66,6 bb "puestos" y salia "subida a 67,6 bb: no ir". Lo puesto
    /// por el heroe no puede pasar de lo que hay en el bote sobre las ciegas.
    /// </summary>
    [Fact]
    public void Una_pila_propia_mal_leida_no_inventa_una_subida_preflop()
    {
        var positions = new Dictionary<int, string> { [0] = "BTN", [1] = "SB", [3] = "BB", [5] = "CO" };
        var context = new LiveHandContext { HeroKey = "Js 9c" };
        context.Merge("PREFLOP", new List<LivePlate>
        {
            new(0, "Heroe", null, 259.2, true, false, false),
            new(1, "Chica", "Post", 139.3, true, false, false),
            new(3, "Grande", "Post", 273.5, true, false, false),
            new(5, "Fuera", "Fold", 170.5, false, false, false),
        }, positions);
        var plates = new List<LivePlate>
        {
            new(0, "Heroe", null, 192.6, true, false, false),
            new(1, "Chica", "Post", 139.3, true, false, false),
            new(3, "Grande", "Post", 273.5, true, false, false),
            new(5, "Fuera", "Fold", 170.5, false, false, false),
        };
        context.Merge("PREFLOP", plates, positions);
        var turn = new LiveTurnState(new[]
        {
            new LiveActionOption(LiveActionKind.Fold, null, "No ir"),
            new LiveActionOption(LiveActionKind.Call, 1, "Igualar 1 BB"),
            new LiveActionOption(LiveActionKind.Raise, 2, "Subir a 2 BB"),
        }, 1.5, plates, 3);

        var decision = new TrainerLiveDecisionAdvisor(Charts).Decide(Cards("Js 9c"), Array.Empty<int>(), "BTN", turn,
            context, positions, new Dictionary<int, string>(), _ => null);

        Assert.DoesNotContain(decision.Details, line => line.Contains("Subida a"));
        Assert.Equal("Raise", decision.Kind);
    }

    /// <summary>
    /// 18/19-11: pagos grandes en turn o river con manos que pierden contra muchas. Se vetan; los sets
    /// en mesas secas y los precios baratos se siguen pagando.
    /// </summary>
    [Theory]
    [InlineData("Ah 5d", "7c Ad 3d 9c Qd", "RIVER", 51.3, 154, true)]   // Resi V: pareja de ases, tres diamantes
    [InlineData("Ad 5d", "5c Tc 2s 3s 9s", "RIVER", 48.7, 97.4, true)]  // Letaba IV: cincos, cierra el color
    [InlineData("Qd Jd", "Qs Jh 7d 8d 9c", "RIVER", 40.5, 92.5, true)]  // Pandora V: doble pareja, cualquier 10
    [InlineData("Qc Qs", "9h 9d Ad 8h 9s", "RIVER", 30, 60, true)]      // full de nueves con damas: cualquier as gana
    [InlineData("As Jh", "3h 7s 5h 2h", "TURN", 58, 120, true)]         // Tanete V: proyecto de color a la J
    [InlineData("Ah 5h", "3h 7s Kh 2c", "TURN", 40, 80, false)]         // proyecto al color del as: se puede pagar
    [InlineData("7s 7d", "5c 7h 2d 4h", "TURN", 37.6, 150, false)]      // set: pierde contra pocas
    [InlineData("Ah 5d", "7c Ad 3d 9c Qd", "RIVER", 10, 154, false)]    // precio barato (6%)
    public void Con_mano_vulnerable_no_se_paga_una_apuesta_grande(string hero, string board, string street, double toCall, double pot, bool blocked)
    {
        var reason = TrainerDecisionRules.NoCallReason(Cards(hero), Cards(board), street, toCall, pot, maniac: false);
        Assert.Equal(blocked, reason is not null);
        Assert.Null(TrainerDecisionRules.NoCallReason(Cards(hero), Cards(board), street, toCall, pot, maniac: true));
    }

    /// <summary>
    /// 19-11: si te suben en turn o river solo se resube con una mano casi imbatible. El color al K en
    /// K-Q-Q con cuatro diamantes (Letaba IV) o un set con escalera posible se pagan sin resubir; el set
    /// alto en mesa seca y el color al as sí se resuben; con la pila comprometida, all-in.
    /// </summary>
    [Theory]
    [InlineData("Kc Jd", "5d Kd Qh 3d Qd", "RIVER", 204.4, 161.35, true)]  // color al K, mesa doblada
    [InlineData("7s 7d", "5c 7h 2d 4h", "TURN", 200, 120, true)]           // set de 7 con 63, 68 o A3 posibles
    [InlineData("Ks Kd", "Kh 7c 2s 9d", "TURN", 150, 60, false)]           // set alto en mesa seca
    [InlineData("Ad Jd", "5d Kd Qh 3d 8c", "RIVER", 150, 80, false)]       // color al as
    [InlineData("7s 7d", "5c 7h 2d 4h", "TURN", 69.1, 235.6, false)]       // comprometido: pila de 30% del bote
    [InlineData("Kc Jd", "5d Kd Qh", "FLOP", 204.4, 40, false)]            // en el flop no aplica
    public void Si_te_suben_en_turn_o_river_solo_se_resube_con_casi_la_mejor(string hero, string board, string street,
        double stack, double pot, bool blocked)
    {
        var reason = TrainerDecisionRules.NoReRaiseReason(Cards(hero), Cards(board), street, stack, pot);
        Assert.Equal(blocked, reason is not null);
    }

    /// <summary>
    /// Dentro de las manos fuertes, lo arriba que esta cada una: si el rival sube el river en una mesa
    /// de cuatro diamantes, el color con as pesa mucho mas que un color con el 4 (antes pesaban igual).
    /// Pagando, no cambia nada.
    /// </summary>
    [Fact]
    public void Las_manos_fuertes_mejores_pesan_mas_cuando_el_rival_sube()
    {
        var board = Cards("5d Kd Qh 3d Qd");
        var nut = new WeightedCombo(Cards("Ad")[0], Cards("2c")[0], 1);
        var low = new WeightedCombo(Cards("4d")[0], Cards("2c")[0], 1);
        var raise = TrainerRangeEstimator.LineWeight("raise", "RIVER", null, board);
        Assert.True(raise(nut) > raise(low) * 2, $"{raise(nut)} vs {raise(low)}");

        var call = TrainerRangeEstimator.LineWeight("call", "RIVER", null, board);
        Assert.Equal(call(nut), call(low));
    }

    [Fact]
    public void Con_varios_rivales_no_hay_semi_farol_y_la_pareja_apuesta_menos()
    {
        var draw = TrainerDecisionRules.PlayMoneyAggression(Cards("Ah 5h"), Cards("Kh 8h 2c"), "FLOP", 90, 30, opponents: 3);
        Assert.Equal(0, draw.MaxBet);
        var drawHeadsUp = TrainerDecisionRules.PlayMoneyAggression(Cards("Ah 5h"), Cards("Kh 8h 2c"), "FLOP", 90, 30, opponents: 1);
        Assert.Equal(0.55, drawHeadsUp.MaxBet, 2);
        var topPair = TrainerDecisionRules.PlayMoneyAggression(Cards("Ah Kd"), Cards("Ac 7s 2d"), "FLOP", 90, 30, opponents: 2);
        Assert.Equal(0.6, topPair.MaxBet, 2);
    }

    [Fact]
    public void La_lectura_dudosa_sale_de_lo_que_se_corrigio()
    {
        var reads = TrainerLiveDecisionAdvisor.DoubtfulReads(new[]
        {
            "Bote leído 428,5 bb: imposible (como mucho 316,2 bb); se toma lo mínimo seguro, 108,5 bb.",
            "Importe a pagar deducido del \"subir a\" (si el slider estaba en el minimo).",
            "Importe a pagar deducido (bote solo con las ciegas).",
        });
        Assert.Equal(new[] { "el bote", "lo que hay que pagar" }, reads);
        Assert.Empty(TrainerLiveDecisionAdvisor.DoubtfulReads(new[] { "Importe a pagar deducido (bote solo con las ciegas)." }));
    }

    [Fact]
    public void Con_top_pair_y_casi_sin_pila_si_puede_terminar_de_meterla()
    {
        var limit = TrainerDecisionRules.PlayMoneyAggression(Cards("Ah Kd"), Cards("Ac 7s 2d 9h"), "TURN", stackBehind: 12, pot: 40);
        Assert.True(limit.CanAllIn);
        Assert.False(limit.CanRaise);
    }

    /// <summary>
    /// Pandora V (22:01): QJ de diamantes, river Qs Jh 7d 8d 9c, el rival pasa con 40,6 bb detras y un
    /// bote de 52. El GTO dijo "siempre all-in" y cualquier 10 tenia escalera. Ahora nunca all-in ni
    /// subida, y si apuesta es chica.
    /// </summary>
    [Fact]
    public void Con_escalera_posible_en_la_mesa_no_va_all_in_en_el_river()
    {
        var positions = new Dictionary<int, string> { [0] = "BB", [5] = "SB", [1] = "LJ", [2] = "HJ", [3] = "CO", [4] = "BTN" };
        var context = new LiveHandContext { HeroKey = "Qd Jd" };
        var plates = new List<LivePlate>
        {
            new(0, "Heroe", null, 70, true, false, false),
            new(5, "Rival", "Check", 40.6, true, false, false),
        };
        context.Merge("RIVER", plates, positions);
        var turn = new LiveTurnState(new[]
        {
            new LiveActionOption(LiveActionKind.Check, null, "Pasar"),
            new LiveActionOption(LiveActionKind.Bet, 1, "Apostar 1 BB"),
        }, 52, plates, 2);

        var decision = new TrainerLiveDecisionAdvisor(Charts).Decide(Cards("Qd Jd"), Cards("Qs Jh 7d 8d 9c"), "BB", turn,
            context, positions, new Dictionary<int, string>(), _ => null);

        Assert.DoesNotContain("ALL-IN", decision.Action);
        Assert.NotEqual("Raise", decision.Kind);
        if (decision.Kind == "Bet")
        {
            var amount = double.Parse(System.Text.RegularExpressions.Regex.Match(decision.Action, @"[\d,]+").Value.Replace(',', '.'),
                System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(amount <= 52 * 0.35 + 0.5, decision.Action);
        }
    }

    /// <summary>
    /// Mintaka IV (22:00): A-Q sin pareja en Tc 4s 4h, el rival apuesta 17 en un bote de 34. Subir
    /// con carta alta en dinero ficticio es un farol que pagan: pagar o no ir, nunca subir.
    /// </summary>
    [Fact]
    public void Con_carta_alta_frente_a_una_apuesta_nunca_sube()
    {
        var positions = new Dictionary<int, string> { [0] = "BB", [4] = "BTN", [5] = "SB", [1] = "LJ", [2] = "HJ", [3] = "CO" };
        var context = new LiveHandContext { HeroKey = "Ac Qd" };
        var plates = new List<LivePlate>
        {
            new(0, "Heroe", null, 96.5, true, false, false),
            new(4, "Rival", "Bet", 61.5, true, false, false),
        };
        context.Merge("FLOP", plates, positions);
        var turn = new LiveTurnState(new[]
        {
            new LiveActionOption(LiveActionKind.Fold, null, "No ir"),
            new LiveActionOption(LiveActionKind.Call, 17, "Igualar 17 BB"),
            new LiveActionOption(LiveActionKind.Raise, 34, "Subir a 34 BB"),
        }, 34, plates, 3);

        var decision = new TrainerLiveDecisionAdvisor(Charts).Decide(Cards("Ac Qd"), Cards("Tc 4s 4h"), "BB", turn,
            context, positions, new Dictionary<int, string>(), _ => null);

        Assert.NotEqual("Raise", decision.Kind);
        Assert.DoesNotContain("ALL-IN", decision.Action);
    }
}
