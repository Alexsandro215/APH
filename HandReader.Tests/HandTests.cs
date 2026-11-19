
using System.Text.RegularExpressions;
using HandReader.Core.Models;
using HandReader.Core.Parsing;
using HandReader.Core.Stats;
using Hud.App.Services;
using Xunit;

namespace HandReader.Tests;

/// <summary>
/// Pruebas de lectura de manos y de estadisticas con manos escritas a mano, cada una pensada
/// para un caso que antes fallaba o que es facil de romper.
/// Ejecutar con: dotnet test HandReader.Tests
/// </summary>
public class HandTests
{
    private static readonly Regex TotalPotRx =
        new(@"Total pot\s+(?<pot>[$€£]?[\d,.]+).*?\|\s*Rake\s+(?<rake>[$€£]?[\d,.]+)", RegexOptions.Compiled);

    private static string[] Load(string name) =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "TestData", name));

    private static IReadOnlyList<string> SingleHand(string name) =>
        PokerStarsHandHistory.SplitHands(Load(name)).Single();

    private static IReadOnlyDictionary<string, PlayerStats> StatsOf(string name)
    {
        var agg = new StatsAggregator();
        var parser = new PokerStarsParser(agg);
        // La ultima mano se cierra al llegar la siguiente cabecera: se agrega una ficticia.
        parser.FeedLines(Load(name).Append("PokerStars Hand #0: fin"), () => { });
        return agg.Players;
    }

    // ---------------- Cabeceras ----------------

    [Theory]
    [InlineData("PokerStars Hand #262268764104:  Hold'em No Limit (100/200)")]
    [InlineData("PokerStars Zoom Hand #132630000000:  Omaha Pot Limit ($1/$2 USD)")]
    [InlineData("PokerStars Home Game Hand #1234567890: {Club} Hold'em No Limit (10/20)")]
    [InlineData("PokerStars Game #109681313810:  Hold'em No Limit ($0.05/$0.10 USD)")]
    [InlineData("Mano #123456789 de PokerStars: Hold'em No Limit")]
    public void Reconoce_todas_las_cabeceras_de_PokerStars(string header)
    {
        Assert.Matches(new Regex(PokerStarsParser.HandStartPattern, RegexOptions.IgnoreCase), header);
    }

    [Theory]
    [InlineData("Table 'Halley' 6-max Seat #1 is the button")]
    [InlineData("Seat 1: Ana (€10 in chips)")]
    [InlineData("Dealt to Hero [Ah Kh]")]
    public void No_confunde_otras_lineas_con_una_cabecera(string line)
    {
        Assert.DoesNotMatch(new Regex(PokerStarsParser.HandStartPattern, RegexOptions.IgnoreCase), line);
    }

    [Fact]
    public void Las_manos_de_Zoom_se_cuentan()
    {
        var stats = StatsOf("zoom_euro_3bet_cbet.txt");
        Assert.Equal(1, stats["Ana"].HandsReceived);
        Assert.Equal(1, stats["Dario"].HandsReceived);
    }

    // ---------------- Ganancias ----------------

    [Theory]
    [InlineData("zoom_euro_3bet_cbet.txt")]
    [InlineData("old_header_ante_showdown_muck.txt")]
    [InlineData("dead_blinds_sitting_out.txt")]
    public void Las_fichas_cuadran_ganado_menos_perdido_es_menos_el_rake(string file)
    {
        var hand = SingleHand(file);
        var players = hand
            .TakeWhile(line => !line.StartsWith("*** HOLE CARDS", StringComparison.Ordinal))
            .Select(line => Regex.Match(line, @"^Seat \d+: (?<name>.+?) \("))
            .Where(match => match.Success)
            .Select(match => match.Groups["name"].Value)
            .ToList();

        var rakeText = TotalPotRx.Match(hand.Single(line => line.StartsWith("Total pot", StringComparison.Ordinal))).Groups["rake"].Value;
        Assert.True(PokerAmountParser.TryParse(rakeText, out var rake));

        var sum = players.Sum(player => PokerStarsHandHistory.EstimateNetForPlayer(hand, player));
        Assert.Equal(-rake, sum, precision: 6);
    }

    [Fact]
    public void Lee_importes_en_euros()
    {
        var hand = SingleHand("zoom_euro_3bet_cbet.txt");
        Assert.Equal(1.05, PokerStarsHandHistory.EstimateNetForPlayer(hand, "Ana"), precision: 6);
        Assert.Equal(-1.00, PokerStarsHandHistory.EstimateNetForPlayer(hand, "Dario"), precision: 6);
    }

    [Fact]
    public void El_ante_no_cuenta_como_apuesta_de_la_calle()
    {
        var hand = SingleHand("old_header_ante_showdown_muck.txt");
        Assert.Equal(1125, PokerStarsHandHistory.EstimateNetForPlayer(hand, "Villain"), precision: 6);
        Assert.Equal(-1125, PokerStarsHandHistory.EstimateNetForPlayer(hand, "Hero"), precision: 6);
    }

    [Fact]
    public void La_ciega_pequena_muerta_no_cuenta_como_apuesta_de_la_calle()
    {
        var hand = SingleHand("dead_blinds_sitting_out.txt");
        Assert.Equal(0.03, PokerStarsHandHistory.EstimateNetForPlayer(hand, "Newbie"), precision: 6);
    }

    // ---------------- Estadisticas ----------------

    [Fact]
    public void PFR_cuenta_a_todo_el_que_sube_preflop()
    {
        var stats = StatsOf("zoom_euro_3bet_cbet.txt");
        Assert.Equal(1, stats["Dario"].PfrCount); // open raise
        Assert.Equal(1, stats["Ana"].PfrCount);   // 3bet tambien es PFR
    }

    [Fact]
    public void ThreeBet_se_mide_sobre_oportunidades()
    {
        var stats = StatsOf("zoom_euro_3bet_cbet.txt");
        Assert.Equal(1, stats["Ana"].ThreeBetOpportunities);
        Assert.Equal(1, stats["Ana"].ThreeBetCount);
        Assert.Equal(100, stats["Ana"].ThreeBetPct, precision: 6);
        // Quien ya veia dos subidas no tenia opcion de 3bet.
        Assert.Equal(0, stats["Bruno"].ThreeBetOpportunities);
    }

    [Fact]
    public void CBet_es_del_ultimo_agresor_preflop()
    {
        var stats = StatsOf("zoom_euro_3bet_cbet.txt");
        Assert.Equal(1, stats["Ana"].CBetFlopOpportunities);
        Assert.Equal(1, stats["Ana"].CBetFlopMade);
        Assert.Equal(0, stats["Dario"].CBetFlopOpportunities);
        Assert.Equal(1, stats["Dario"].FacedCBetFlop);
        Assert.Equal(1, stats["Dario"].FoldToCBetFlop);
    }

    [Fact]
    public void WTSD_cuenta_tambien_a_quien_tira_las_cartas_en_el_showdown()
    {
        var stats = StatsOf("old_header_ante_showdown_muck.txt");
        Assert.Equal(1, stats["Hero"].WentToShowdown);
        Assert.Equal(1, stats["Villain"].WentToShowdown);
        Assert.Equal(1, stats["Villain"].WonAtShowdown);
        Assert.Equal(0, stats["Hero"].WonAtShowdown);
    }

    [Fact]
    public void Un_jugador_ausente_no_cuenta_como_mano_jugada()
    {
        var stats = StatsOf("dead_blinds_sitting_out.txt");
        Assert.True(!stats.TryGetValue("Away", out var away) || away.HandsReceived == 0);
        Assert.Equal(1, stats["Newbie"].HandsReceived);
    }

    [Fact]
    public void Las_estadisticas_son_coherentes_en_todas_las_manos_de_prueba()
    {
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "TestData"), "*.txt"))
        {
            var agg = new StatsAggregator();
            new PokerStarsParser(agg).FeedLines(File.ReadAllLines(file).Append("PokerStars Hand #0: fin"), () => { });
            foreach (var (name, s) in agg.Players)
            {
                var where = $"{Path.GetFileName(file)} / {name}";
                Assert.True(s.PfrCount <= s.VpipCount, $"PFR > VPIP en {where}");
                Assert.True(s.ThreeBetCount <= s.ThreeBetOpportunities, $"3bet > oportunidades en {where}");
                Assert.True(s.CBetFlopMade <= s.CBetFlopOpportunities, $"CBet > oportunidades en {where}");
                Assert.True(s.WonAtShowdown <= s.WentToShowdown, $"W$SD > WTSD en {where}");
                Assert.True(s.VpipCount <= s.HandsReceived, $"VPIP > manos en {where}");
            }
        }
    }

    // ---------------- Nivel de la mesa ----------------

    [Theory]
    [InlineData(200, "200", StakeProfile.Low)]      // dinero ficticio 100/200: antes salia como NL500+
    [InlineData(600, "600", StakeProfile.Low)]      // fichas de torneo
    [InlineData(0.02, "$0.02", StakeProfile.Low)]   // NL2
    [InlineData(0.25, "$0.25", StakeProfile.Mid)]   // NL25
    [InlineData(1, "€1", StakeProfile.Mid)]         // NL100 en euros
    [InlineData(2, "$2", StakeProfile.High)]        // NL200
    public void El_nivel_de_la_mesa_considera_el_dinero_ficticio(double bigBlind, string text, StakeProfile expected)
    {
        Assert.Equal(expected, StakeProfileRules.FromBigBlind(bigBlind, text));
    }

    // ---------------- Montos ----------------

    [Theory]
    [InlineData("$1,234.50", 1234.50)]
    [InlineData("€0.05", 0.05)]
    [InlineData("12215", 12215)]
    [InlineData("1.000,50", 1000.50)]
    public void Lee_montos_con_moneda_y_separadores(string raw, double expected)
    {
        Assert.True(PokerAmountParser.TryParse(raw, out var value));
        Assert.Equal(expected, value, precision: 6);
    }
}
