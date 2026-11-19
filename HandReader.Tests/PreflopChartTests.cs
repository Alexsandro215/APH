using Hud.App.Services;
using Xunit;

namespace HandReader.Tests;

/// <summary>
/// Las tablas preflop (Hud.App/Data/APH_Reference_100bb_6max.csv) que se muestran en la pagina
/// "Tablas preflop" y que usa el Modo Entrenador: que esten todas y que no tengan errores obvios.
/// </summary>
public class PreflopChartTests
{
    private static readonly AphPreflopProvider Charts =
        new(Path.Combine(AppContext.BaseDirectory, "Data", "APH_Reference_100bb_6max.csv"));

    private static readonly string[] Positions = { "LJ", "HJ", "CO", "BTN", "SB", "BB" };

    // Pares (heroe, rival) posibles en 6-max: quien actua despues contra quien actuo antes, y al reves.
    private static IEnumerable<(string Later, string Earlier)> LaterVsEarlier() =>
        from later in Enumerable.Range(0, 6)
        from earlier in Enumerable.Range(0, 6)
        where earlier < later && !(Positions[earlier] == "BB")
        select (Positions[later], Positions[earlier]);

    [Fact]
    public void Hay_tabla_de_abrir_para_cada_posicion_menos_la_ciega_grande()
    {
        foreach (var hero in Positions.Take(5))
            Assert.True(Charts.HasChart("Open", hero, ""), $"Falta abrir desde {hero}");
    }

    [Fact]
    public void Hay_tabla_contra_subida_para_cada_par_de_posiciones()
    {
        foreach (var (later, earlier) in LaterVsEarlier())
            Assert.True(Charts.HasChart("vs Raise", later, earlier), $"Falta {later} contra subida de {earlier}");
    }

    [Fact]
    public void Hay_tabla_contra_3bet_y_contra_4bet_para_cada_par()
    {
        foreach (var (later, earlier) in LaterVsEarlier())
        {
            Assert.True(Charts.HasChart("vs 3bet", earlier, later), $"Falta {earlier} contra 3bet de {later}");
            Assert.True(Charts.HasChart("vs 4bet", later, earlier), $"Falta {later} contra 4bet de {earlier}");
        }
    }

    [Fact]
    public void Las_tablas_solo_tienen_subir_pagar_o_retirarse()
    {
        foreach (var (scenario, hero, villain) in Charts.AvailableCharts())
            for (var row = 0; row < 13; row++)
                for (var col = 0; col < 13; col++)
                    Assert.Contains(Charts.GetCode(scenario, hero, villain, AphPreflopProvider.HandAt(row, col)), new[] { "R", "C", "F" });
    }

    [Fact]
    public void AA_nunca_se_tira_y_72o_nunca_se_abre()
    {
        foreach (var (scenario, hero, villain) in Charts.AvailableCharts())
            Assert.NotEqual("F", Charts.GetCode(scenario, hero, villain, "AA"));

        foreach (var hero in Positions.Take(5))
            Assert.Equal("F", Charts.GetCode("Open", hero, "", "72o"));
    }

    [Fact]
    public void Se_abre_mas_cuanto_mas_tarde_se_habla()
    {
        double OpenPct(string hero)
        {
            var combos = 0;
            for (var row = 0; row < 13; row++)
                for (var col = 0; col < 13; col++)
                {
                    var hand = AphPreflopProvider.HandAt(row, col);
                    if (Charts.GetCode("Open", hero, "", hand) == "R")
                        combos += hand.Length == 2 ? 6 : hand.EndsWith('s') ? 4 : 12;
                }
            return 100.0 * combos / 1326;
        }

        Assert.True(OpenPct("LJ") < OpenPct("HJ"));
        Assert.True(OpenPct("HJ") < OpenPct("CO"));
        Assert.True(OpenPct("CO") < OpenPct("BTN"));
    }

    [Theory]
    [InlineData("AJo", "AJo")]
    [InlineData("aj o", "AJo")]
    [InlineData("JA", "AJs,AJo")]
    [InlineData("77", "77")]
    [InlineData("KTs", "KTs")]
    [InlineData("A10s", "ATs")]
    [InlineData("tks", "KTs")]
    [InlineData("77s", "")]
    [InlineData("AX", "")]
    [InlineData("", "")]
    public void Entiende_la_mano_que_se_escribe(string input, string expected)
    {
        var parsed = string.Join(",", AphPreflopProvider.ParseHandClasses(input));
        Assert.Equal(expected, parsed);
    }

    [Fact]
    public void La_tabla_13x13_tiene_las_169_manos_distintas()
    {
        var hands = new HashSet<string>();
        for (var row = 0; row < 13; row++)
            for (var col = 0; col < 13; col++)
                hands.Add(AphPreflopProvider.HandAt(row, col));

        Assert.Equal(169, hands.Count);
        Assert.Equal("AKs", AphPreflopProvider.HandAt(0, 1));
        Assert.Equal("AKo", AphPreflopProvider.HandAt(1, 0));
        Assert.Equal("22", AphPreflopProvider.HandAt(12, 12));
    }
}
