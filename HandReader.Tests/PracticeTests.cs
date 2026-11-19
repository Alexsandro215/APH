using Hud.App.Services;
using Xunit;
using S = Hud.App.Services.PreflopScenarios;

namespace HandReader.Tests;

/// <summary>
/// Rangos borrador (contra limp, squeeze, heads-up), lectura de las decisiones preflop reales y
/// la practica preflop por temas y niveles.
/// </summary>
public class PracticeTests
{
    private static readonly AphPreflopProvider Charts = PreflopLibrary.Load(Path.Combine(AppContext.BaseDirectory, "Data"));

    // ---------------- Notacion de rangos ----------------

    [Theory]
    [InlineData("QQ+", "AA,KK,QQ")]
    [InlineData("TT-88", "TT,99,88")]
    [InlineData("ATs+", "AKs,AQs,AJs,ATs")]
    [InlineData("K9o+", "KQo,KJo,KTo,K9o")]
    [InlineData("A5s-A3s", "A5s,A4s,A3s")]
    [InlineData("T9s-87s", "T9s,98s,87s")]
    [InlineData("AK", "AKs,AKo")]
    [InlineData("J8s, 76o", "J8s,76o")]
    public void Entiende_la_notacion_de_rangos(string text, string expected)
    {
        var hands = AphPreflopProvider.ParseRange(text, null, out var errors);
        Assert.Empty(errors);
        Assert.Equal(expected.Split(',').OrderBy(h => h), hands.OrderBy(h => h));
    }

    [Fact]
    public void Resto_son_las_manos_que_no_estan_en_subir()
    {
        var raise = AphPreflopProvider.ParseRange("QQ+", null, out _);
        var rest = AphPreflopProvider.ParseRange("resto", raise, out _);
        Assert.Equal(169 - 3, rest.Count);
        Assert.DoesNotContain("AA", rest);
    }

    [Theory]
    [InlineData("A1s")]
    [InlineData("AKx")]
    [InlineData("T9s-85s")] // conectores con distinto hueco
    public void Avisa_la_notacion_que_no_entiende(string text)
    {
        AphPreflopProvider.ParseRange(text, null, out var errors);
        Assert.Single(errors);
    }

    // ---------------- Rangos borrador ----------------

    [Fact]
    public void El_archivo_de_rangos_borrador_no_tiene_errores()
    {
        Assert.Empty(Charts.LoadErrors);
    }

    [Fact]
    public void Hay_tabla_contra_limp_y_squeeze_para_cada_situacion_posible()
    {
        var order = S.PositionOrder.ToList();
        foreach (var hero in order.Skip(1))
            foreach (var limper in order.TakeWhile(p => p != hero).Where(p => p != "BB"))
                Assert.True(Charts.HasChart(S.VsLimp, hero, limper), $"Falta {hero} contra limp de {limper}");

        // Squeeze: entre el que abre y el heroe tiene que haber alguien que pague.
        foreach (var hero in order)
            foreach (var opener in order.Where(p => order.IndexOf(p) < order.IndexOf(hero) - 1))
                Assert.True(Charts.HasChart(S.Squeeze, hero, opener), $"Falta squeeze de {hero} contra {opener}");

        Assert.True(Charts.HasChart(S.HuOpen, "SB", ""));
        Assert.True(Charts.HasChart(S.HuVsRaise, "BB", "SB"));
        Assert.True(Charts.HasChart(S.HuVs3Bet, "SB", "BB"));
        Assert.True(Charts.HasChart(S.HuVs4Bet, "BB", "SB"));
    }

    [Fact]
    public void Los_rangos_borrador_tienen_sentido()
    {
        foreach (var (scenario, hero, villain) in Charts.AvailableCharts().Where(c => S.IsDraft(c.Scenario)))
            Assert.Equal("R", Charts.GetCode(scenario, hero, villain, "AA"));

        // La ciega grande contra limp nunca se retira: pasar es gratis.
        foreach (var limper in new[] { "LJ", "HJ", "CO", "BTN", "SB" })
            for (var i = 0; i < 169; i++)
                Assert.NotEqual("F", Charts.GetCode(S.VsLimp, "BB", limper, AphPreflopProvider.HandAt(i / 13, i % 13)));

        // En heads-up el boton abre la gran mayoria de las manos.
        var opened = Enumerable.Range(0, 169).Select(i => AphPreflopProvider.HandAt(i / 13, i % 13))
            .Where(h => Charts.GetCode(S.HuOpen, "SB", "", h) == "R").Sum(S.Combos);
        Assert.InRange(100.0 * opened / 1326, 70, 90);

        // Y la ciega grande defiende (paga o 3bet) la mayoria contra esa subida.
        var defended = Enumerable.Range(0, 169).Select(i => AphPreflopProvider.HandAt(i / 13, i % 13))
            .Where(h => Charts.GetCode(S.HuVsRaise, "BB", "SB", h) != "F").Sum(S.Combos);
        Assert.InRange(100.0 * defended / 1326, 62, 80);
    }

    // ---------------- Decisiones reales ----------------

    private static List<PreflopSpot> Spots() =>
        PreflopSpotReader.ReadFiles(new[] { Path.Combine(AppContext.BaseDirectory, "TestData", "preflop_spots.txt") });

    [Fact]
    public void Lee_cada_decision_preflop_con_su_situacion()
    {
        var spots = Spots().Select(s => $"{s.HandId[^1]} {s.Scenario} {s.Hero} {s.Villain} {s.Hand} {s.Action}").ToList();
        Assert.Equal(new[]
        {
            "1 OPEN CO  AJo R",
            "1 VS 3BET CO BTN AJo F",
            "2 VS LIMP BTN LJ KQs R",
            "3 SQUEEZE BB LJ QQ R",
            "4 HU OPEN SB  72o R",
            "4 HU VS 3BET SB BB 72o C",
            "5 OPEN HJ  94s F",
        }, spots);
    }

    [Theory]
    [InlineData("Ad8c", "A8o")]
    [InlineData("8cAd", "A8o")]
    [InlineData("KhQh", "KQs")]
    [InlineData("7s7d", "77")]
    public void Nombra_la_mano_por_sus_cartas(string cards, string expected)
    {
        Assert.Equal(expected, PreflopSpotReader.HandClass(cards));
    }

    // ---------------- Practica ----------------

    [Theory]
    [InlineData(PracticeLevel.Easy)]
    [InlineData(PracticeLevel.Medium)]
    [InlineData(PracticeLevel.Hard)]
    [InlineData(PracticeLevel.Expert)]
    public void Cada_tema_da_preguntas_con_la_respuesta_de_la_tabla(PracticeLevel level)
    {
        var engine = new PreflopPracticeEngine(Charts, Array.Empty<PreflopSpot>(), new Random(7));
        foreach (var topic in PreflopPracticeEngine.Topics.Where(t => t.Filter is not null))
        {
            for (var i = 0; i < 30; i++)
            {
                var q = engine.Next(topic.Id, level);
                Assert.NotNull(q);
                Assert.Equal(Charts.GetCode(q!.Scenario, q.Hero, q.Villain, q.Hand), q.Correct);
                Assert.Contains(q.Correct, q.Options);
                Assert.Equal(q.Hand, PreflopSpotReader.HandClass(q.Cards));
                Assert.NotEmpty(engine.Explain(q));
                Assert.False(string.IsNullOrWhiteSpace(PreflopPracticeEngine.Situation(q)));
            }
        }
    }

    [Fact]
    public void Abrir_desde_UTG_solo_pregunta_eso()
    {
        var engine = new PreflopPracticeEngine(Charts, Array.Empty<PreflopSpot>(), new Random(1));
        for (var i = 0; i < 50; i++)
        {
            var q = engine.Next("open-LJ", PracticeLevel.Medium)!;
            Assert.Equal((S.Open, "LJ", ""), (q.Scenario, q.Hero, q.Villain));
        }
    }

    [Fact]
    public void Los_niveles_separan_manos_claras_de_manos_del_borde()
    {
        var engine = new PreflopPracticeEngine(Charts, Array.Empty<PreflopSpot>(), new Random(3));
        // AA y 72o al abrir desde UTG estan lejos del borde; AJo/ATo cerca.
        Assert.True(engine.Distance(S.Open, "LJ", "", "AA") >= 3);
        Assert.True(engine.Distance(S.Open, "LJ", "", "72o") >= 3);

        var easy = Enumerable.Range(0, 60).Select(_ => engine.Next("open-CO", PracticeLevel.Easy)!).ToList();
        var hard = Enumerable.Range(0, 60).Select(_ => engine.Next("open-CO", PracticeLevel.Hard)!).ToList();
        Assert.True(easy.Average(q => q.Distance) > hard.Average(q => q.Distance));
        Assert.All(hard, q => Assert.Equal(1, q.Distance));
    }

    [Fact]
    public void Las_respuestas_no_son_casi_siempre_retirarse()
    {
        var engine = new PreflopPracticeEngine(Charts, Array.Empty<PreflopSpot>(), new Random(5));
        var answers = Enumerable.Range(0, 300).Select(_ => engine.Next("vs-4bet", PracticeLevel.Medium)!.Correct).ToList();
        Assert.True(answers.Count(a => a == "F") < answers.Count * 0.6);
    }

    [Fact]
    public void Mis_manos_y_mis_errores_usan_las_manos_reales_y_los_fallos()
    {
        var engine = new PreflopPracticeEngine(Charts, Spots(), new Random(2));
        var real = engine.Next(PreflopPracticeEngine.MyHandsTopic, PracticeLevel.Medium);
        Assert.NotNull(real?.RealSpot);
        Assert.Equal(real!.RealSpot!.Cards, real.Cards);

        var mistakes = new[] { new PracticeMistake(S.VsRaise, "BB", "BTN", "K7o") };
        var review = engine.Next(PreflopPracticeEngine.MistakesTopic, PracticeLevel.Easy, mistakes)!;
        Assert.Equal(("BB", "BTN", "K7o"), (review.Hero, review.Villain, review.Hand));
        Assert.Null(engine.Next(PreflopPracticeEngine.MistakesTopic, PracticeLevel.Easy, Array.Empty<PracticeMistake>()));
    }

    [Fact]
    public void El_progreso_quita_el_error_cuando_se_acierta()
    {
        var progress = new PracticeProgress();
        var q = new PracticeQuestion("open-LJ", PracticeLevel.Easy, S.Open, "LJ", "", "", "AJo", "AhJd", "F", new[] { "F", "R" }, 1, null);
        progress.Record(q, correct: false);
        Assert.Single(progress.Mistakes);
        progress.Record(q, correct: true);
        Assert.Empty(progress.Mistakes);
        Assert.Equal((2, 1), progress.Totals("open-LJ"));
    }
}
