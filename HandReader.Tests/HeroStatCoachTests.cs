using Hud.App.Services;
using Xunit;

namespace HandReader.Tests;

/// <summary>
/// Retroalimentacion con semaforo de las stats del heroe: el nivel viene de la escala unica y el
/// veredicto respeta el criterio de dinero ficticio (cerrado y por valor, sin aconsejar faroles).
/// </summary>
public class HeroStatCoachTests
{
    private static double Median(string key) => StatScale.Cuts(key, StakeProfile.Low)[3];

    // Un regular sano: todo en la zona normal y sin mucha diferencia entre VPIP y PFR (la mediana de
    // la poblacion entra pagando demasiado: es normal, pero no es lo que se aconseja).
    private static HeroStatInput Normal(int hands = 500) => new(hands,
        StatScale.Cuts("VPIP", StakeProfile.Low)[2] + 1, StatScale.Cuts("PFR", StakeProfile.Low)[4] - 0.5, Median("THREEBET"), Median("AF"), Median("AFQ"),
        Median("CBF"), Median("FVCBF"), Median("WTSD"), Median("WSD"), Median("WWSF"));

    private static StatFeedback Of(IReadOnlyList<StatFeedback> feedback, string key) => feedback.Single(item => item.Key == key);

    [Fact]
    public void Un_jugador_en_la_media_esta_bien_en_todo()
    {
        var feedback = HeroStatCoach.Evaluate(Normal(), playMoney: true);
        Assert.Equal(10, feedback.Count);
        Assert.All(feedback, item => Assert.Equal(StatVerdict.Good, item.Verdict));
    }

    [Fact]
    public void Con_menos_de_30_manos_no_hay_veredicto() =>
        Assert.Empty(HeroStatCoach.Evaluate(Normal(hands: 20), playMoney: true));

    [Fact]
    public void Jugar_demasiadas_manos_es_a_mejorar()
    {
        var feedback = HeroStatCoach.Evaluate(Normal() with { Vpip = 70, Pfr = 60 }, playMoney: true);
        Assert.Equal(StatVerdict.Bad, Of(feedback, "VPIP").Verdict);
        Assert.Equal(StatVerdict.Bad, feedback[0].Verdict); // lo peor va primero
    }

    [Fact]
    public void En_ficticio_jugar_algo_cerrado_y_apostar_poco_el_flop_esta_bien()
    {
        var cuts = StatScale.Cuts("VPIP", StakeProfile.Low);
        var cbet = StatScale.Cuts("CBF", StakeProfile.Low);
        var input = Normal() with { Vpip = (cuts[1] + cuts[2]) / 2, Pfr = (cuts[1] + cuts[2]) / 2 - 6, CBet = (cbet[0] + cbet[1]) / 2 };
        var play = HeroStatCoach.Evaluate(input, playMoney: true);
        Assert.Equal(StatVerdict.Good, Of(play, "VPIP").Verdict);
        Assert.Equal(StatVerdict.Good, Of(play, "CBF").Verdict);

        // Con dinero real, apostar poco el flop si se corrige.
        var real = HeroStatCoach.Evaluate(input, playMoney: false);
        Assert.Equal(StatVerdict.Adjust, Of(real, "CBF").Verdict);
    }

    [Fact]
    public void Nunca_aconseja_robar_o_farolear_mas_en_ficticio()
    {
        var cuts = new Func<string, double[]>(key => StatScale.Cuts(key, StakeProfile.Low));
        var tight = Normal() with { CBet = cuts("CBF")[1] - 1, Wwsf = cuts("WWSF")[2] - 1 };
        var feedback = HeroStatCoach.Evaluate(tight, playMoney: true);
        Assert.DoesNotContain(feedback, item => item.Advice.Contains("roba", StringComparison.OrdinalIgnoreCase) ||
                                                item.Advice.Contains("farolea", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Llegar_mucho_al_showdown_solo_es_malo_si_pierdes()
    {
        var wtsd = StatScale.Cuts("WTSD", StakeProfile.Low);
        var wsd = StatScale.Cuts("WSD", StakeProfile.Low);
        var winning = HeroStatCoach.Evaluate(Normal() with { Wtsd = wtsd[5] + 1 }, playMoney: true);
        Assert.Equal(StatVerdict.Good, Of(winning, "WTSD").Verdict);

        var losing = HeroStatCoach.Evaluate(Normal() with { Wtsd = wtsd[5] + 1, Wsd = wsd[1] - 1 }, playMoney: true);
        Assert.Equal(StatVerdict.Bad, Of(losing, "WTSD").Verdict);
        Assert.Equal(StatVerdict.Bad, Of(losing, "WSD").Verdict);
    }

    [Fact]
    public void Mucha_diferencia_entre_VPIP_y_PFR_se_corrige()
    {
        var feedback = HeroStatCoach.Evaluate(Normal() with { Vpip = 30, Pfr = 14 }, playMoney: true);
        Assert.Equal(StatVerdict.Adjust, Of(feedback, "PFR").Verdict);
        Assert.Contains("pagando", Of(feedback, "PFR").Advice);
    }

    [Fact]
    public void El_enfoque_es_lo_peor_o_seguir_asi()
    {
        Assert.Contains("sigue", HeroStatCoach.Focus(HeroStatCoach.Evaluate(Normal(), true)));
        Assert.StartsWith("VPIP", HeroStatCoach.Focus(HeroStatCoach.Evaluate(Normal() with { Vpip = 70, Pfr = 60 }, true)));
    }

    private static DecisionMistake Mistake(string cards, double adjusted, string chosen = "Call 10 bb", string? advice = "NO IR") =>
        new(cards, chosen, advice, adjusted);

    [Fact]
    public void Con_pocas_decisiones_no_hay_veredicto() =>
        Assert.Null(HeroStatCoach.EvaluateDecisions(20, 15, 14, 0, Array.Empty<DecisionMistake>()));

    [Fact]
    public void Pocos_errores_y_ninguno_caro_es_bien()
    {
        var verdict = HeroStatCoach.EvaluateDecisions(1000, 600, 580, 12, new[] { Mistake("KTo", -12), Mistake("Q9s", -6) })!;
        Assert.Equal(StatVerdict.Good, verdict.Verdict);
        Assert.Empty(verdict.Costly);
    }

    [Fact]
    public void Un_buen_all_in_que_pierde_por_mala_suerte_no_es_error_caro()
    {
        // A4s contra la basura de un rival: -155 bb en la mano, pero +55 bb sin la suerte.
        var verdict = HeroStatCoach.EvaluateDecisions(1000, 600, 580, 12, new[] { Mistake("A4s", 55, "Raise (all-in)") })!;
        Assert.Equal(StatVerdict.Good, verdict.Verdict);
    }

    [Fact]
    public void Errores_caros_se_ajustan_y_muchos_son_a_mejorar()
    {
        var two = HeroStatCoach.EvaluateDecisions(1000, 600, 580, 12, new[] { Mistake("TQo", -84), Mistake("KTo", -75) })!;
        Assert.Equal(StatVerdict.Adjust, two.Verdict);
        Assert.Equal(2, two.Costly.Count);
        Assert.Contains("TQo", two.Advice);

        var four = HeroStatCoach.EvaluateDecisions(1000, 600, 580, 12,
            new[] { Mistake("TQo", -84), Mistake("KTo", -75), Mistake("A9o", -60), Mistake("QJs", -55) })!;
        Assert.Equal(StatVerdict.Bad, four.Verdict);
    }

    [Fact]
    public void Un_all_in_que_APH_no_recomendaba_se_menciona()
    {
        var verdict = HeroStatCoach.EvaluateDecisions(1000, 600, 580, 12, new[] { Mistake("A4s", -80, "Raise a 791 bb (all-in)", "NO IR") })!;
        Assert.Contains("all-in que APH no recomienda", verdict.Advice);
    }

    [Fact]
    public void Seguir_poco_el_consejo_es_a_mejorar() =>
        Assert.Equal(StatVerdict.Bad, HeroStatCoach.EvaluateDecisions(500, 200, 140, 5, Array.Empty<DecisionMistake>())!.Verdict);

    [Fact]
    public void El_enfoque_pone_primero_las_decisiones_si_estan_peor()
    {
        var decisions = HeroStatCoach.EvaluateDecisions(1000, 600, 580, 12, new[] { Mistake("TQo", -84) })!;
        Assert.StartsWith("Decisiones", HeroStatCoach.Focus(HeroStatCoach.Evaluate(Normal(), true), decisions));
    }
}
