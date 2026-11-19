using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

// =====================================================================================
//  APH.Calibration - "entrena" los cortes de APH con manos reales.
//
//  Lee manos en formato PHH (Poker Hand History, Universidad de Toronto, CC BY 4.0) y calcula
//  como juega la poblacion real: los percentiles de cada estadistica del HUD por nivel, las
//  frecuencias preflop (abrir, 3bet, pagar, 4bet) y los cortes para clasificar rivales.
//  El resultado (population_baselines.json) lo carga APH para colorear el HUD y clasificar.
//
//  Uso:
//    dotnet run --project APH.Calibration -c Release -- <carpeta_con_phh> <salida.json> [manos_minimas]
//  Ejemplo:
//    dotnet run --project APH.Calibration -c Release -- D:\APH_Datos\phh\data Hud.App\Data\population_baselines.json
// =====================================================================================

if (args.Length == 1 && args[0] == "--selftest")
    return SelfTest.Run();

if (args.Length >= 1 && args[0] == "--equidad")
    return EquityTable.Run(args.Skip(1).ToArray());

if (args.Length < 2)
{
    Console.WriteLine("Uso: APH.Calibration <carpeta_con_phh> <salida.json> [manos_minimas=200]");
    return 1;
}

var root = args[0];
var output = args[1];
var minHands = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 200;
var stopwatch = Stopwatch.StartNew();

var files = Directory.EnumerateFiles(root, "*.phh*", SearchOption.AllDirectories).ToList();
Console.WriteLine($"Archivos PHH: {files.Count}");

var players = new Dictionary<(string Bucket, string Name), PlayerTotals>();
var pools = new Dictionary<string, PoolTotals>(StringComparer.Ordinal);
long handsRead = 0, handsUsed = 0;
var levelsSeen = new SortedDictionary<string, long>(StringComparer.Ordinal);

foreach (var file in files)
{
    var source = DescribeSource(file);
    if (source is null)
        continue;

    foreach (var hand in PhhReader.ReadHands(file))
    {
        handsRead++;
        if (!HandAnalyzer.TryAnalyze(hand, source.Value, players, pools))
            continue;

        handsUsed++;
        levelsSeen[source.Value.Level] = levelsSeen.GetValueOrDefault(source.Value.Level) + 1;
        if (handsUsed % 500_000 == 0)
            Console.WriteLine($"  {handsUsed:N0} manos analizadas ({stopwatch.Elapsed:mm\\:ss})");
    }
}

Console.WriteLine($"Manos leidas: {handsRead:N0}  usadas (NLHE con nombres): {handsUsed:N0}");
Console.WriteLine($"Showdowns: comprobados contra ganancias {HandAnalyzer.CheckedShowdowns:N0} " +
                  $"(coinciden {HandAnalyzer.AgreedShowdowns:N0}; con alguien sin ensenar {HandAnalyzer.CheckedPartial:N0}, " +
                  $"coinciden {HandAnalyzer.AgreedPartial:N0}; all-in sin ensenar {HandAnalyzer.CheckedPartialAllIn:N0}, coinciden {HandAnalyzer.AgreedPartialAllIn:N0}), resueltos solo por cartas {HandAnalyzer.CardOnlyShowdowns:N0}, " +
                  $"sin resolver {HandAnalyzer.UnknownShowdowns:N0}");
foreach (var (level, count) in levelsSeen)
    Console.WriteLine($"  {level}: {count:N0}");

var result = Baselines.Build(players, pools, minHands, handsUsed, levelsSeen);
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
File.WriteAllText(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Listo en {stopwatch.Elapsed:mm\\:ss}: {output}");
return 0;

// Sala y nivel a partir de la carpeta: handhq/PS-2009-07-01_2009-07-23_25NLH_OBFU/...
static (string Site, string Level, string Bucket)? DescribeSource(string path)
{
    var match = Regex.Match(path.Replace('\\', '/'), @"/handhq/(?<site>[A-Z]+)-[^/]*?_(?<nl>\d+)NLH");
    if (!match.Success)
        return null; // Solo poblacion humana online (HandHQ). Pluribus y WSOP no representan un pool.

    var site = match.Groups["site"].Value;
    var nl = int.Parse(match.Groups["nl"].Value, CultureInfo.InvariantCulture);
    var level = $"{site} NL{nl}";
    // Mismos cortes de nivel que APH (StakeProfile): ciega grande >= $2 es nivel alto.
    var bucket = nl >= 200 ? "High" : "Mid";
    return (site, level, bucket);
}

// ------------------------------------------------------------------------------------------
//  Lectura de PHH / PHHS (TOML sencillo: clave = valor, una mano por bloque)
// ------------------------------------------------------------------------------------------
sealed class PhhHand
{
    public string Variant = "";
    public double[] Blinds = Array.Empty<double>();
    public double[] Antes = Array.Empty<double>();
    public double[] Starting = Array.Empty<double>();
    public double[]? Finishing;
    public double[]? Winnings;
    public string[] Players = Array.Empty<string>();
    public string[] Actions = Array.Empty<string>();
}

static class PhhReader
{
    private static readonly Regex SectionRx = new(@"^\s*\[\s*\d+\s*\]\s*$", RegexOptions.Compiled);

    public static IEnumerable<PhhHand> ReadHands(string path)
    {
        var current = new PhhHand();
        var hasContent = false;
        string? pendingKey = null;
        var pendingValue = new System.Text.StringBuilder();

        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (pendingKey is not null)
            {
                pendingValue.Append(' ').Append(line);
                if (Balanced(pendingValue.ToString()))
                {
                    Assign(current, pendingKey, pendingValue.ToString());
                    pendingKey = null;
                    pendingValue.Clear();
                }
                continue;
            }

            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            if (SectionRx.IsMatch(line))
            {
                if (hasContent)
                    yield return current;
                current = new PhhHand();
                hasContent = false;
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (key == "variant" && hasContent && current.Variant.Length > 0)
            {
                yield return current;
                current = new PhhHand();
            }

            hasContent = true;
            if (value.StartsWith('[') && !Balanced(value))
            {
                pendingKey = key;
                pendingValue.Append(value);
                continue;
            }

            Assign(current, key, value);
        }

        if (hasContent)
            yield return current;
    }

    private static bool Balanced(string value) => value.Count(c => c == '[') <= value.Count(c => c == ']');

    private static void Assign(PhhHand hand, string key, string value)
    {
        switch (key)
        {
            case "variant": hand.Variant = Unquote(value); break;
            case "blinds_or_straddles": hand.Blinds = Numbers(value); break;
            case "antes": hand.Antes = Numbers(value); break;
            case "starting_stacks": hand.Starting = Numbers(value); break;
            case "finishing_stacks": hand.Finishing = Numbers(value); break;
            case "winnings": hand.Winnings = Numbers(value); break;
            case "players": hand.Players = Strings(value); break;
            case "actions": hand.Actions = Strings(value); break;
        }
    }

    private static string Unquote(string value) => value.Trim().Trim('\'', '"');

    private static double[] Numbers(string value) =>
        value.Trim('[', ']', ' ')
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                ? number
                : double.PositiveInfinity)
            .ToArray();

    private static string[] Strings(string value)
    {
        var list = new List<string>();
        var inside = false;
        var quote = '\0';
        var current = new System.Text.StringBuilder();
        foreach (var c in value)
        {
            if (!inside && (c == '\'' || c == '"'))
            {
                inside = true;
                quote = c;
                current.Clear();
            }
            else if (inside && c == quote)
            {
                inside = false;
                list.Add(current.ToString());
            }
            else if (inside)
            {
                current.Append(c);
            }
        }

        return list.ToArray();
    }
}

// ------------------------------------------------------------------------------------------
//  Analisis de una mano (mismas definiciones que el lector de APH)
// ------------------------------------------------------------------------------------------
/// <summary>Comprobaciones rapidas del evaluador de manos: APH.Calibration --selftest</summary>
static class SelfTest
{
    public static int Run()
    {
        var cases = new (string Better, string Worse, string What)[]
        {
            ("AhKh" + "QhJhTh2c3d", "AsAd" + "QhJhTh2c3d", "escalera real > color"),
            ("9h9d" + "9c9s2h3d4c", "AhAd" + "AcKsKh3d4c", "poker > full"),
            ("KhKd" + "Kc2s2h7d8c", "QhQd" + "Qc2s2hAdAc", "full de K > full de Q"),
            ("2h4h" + "6h8hTh2c3d", "AsKd" + "QcJhTs2c3d", "color > escalera"),
            ("6s7d" + "8c9hTs2c3d", "AsAd" + "Ac2h3s9c8d", "escalera > trio"),
            ("As2d" + "3c4h5s9cKd", "AsAd" + "Ac2h3s9c8d", "escalera al 5 > trio"),
            ("AhKd" + "As7c7d2h3s", "QhQd" + "As7c7d2h3s", "dobles A-7 > dobles Q-7"),
            ("AhQd" + "As7c7d2h3s", "AcJd" + "As7c7d2h3s", "mismo doble, gana el kicker"),
            ("AhKd" + "AsTc8d6h2s", "AcQd" + "AsTc8d6h2s", "pareja de A, kicker K > Q"),
            ("AcQd" + "QsJc9d6h2s", "AhKd" + "QsJc9d6h2s", "pareja de Q > carta alta"),
            ("AhKh" + "2h3h4h5h9c", "6c6d" + "2h3h4h5h9c", "escalera de color al 5"),
        };

        var failures = 0;
        foreach (var (better, worse, what) in cases)
        {
            var a = HandEvaluator.Best(better);
            var b = HandEvaluator.Best(worse);
            var ok = a > b;
            Console.WriteLine($"{(ok ? "OK " : "MAL")} {what}");
            if (!ok) failures++;
        }

        var split = HandEvaluator.Best("2c3d" + "AsKsQsJsTs") == HandEvaluator.Best("4h5h" + "AsKsQsJsTs");
        Console.WriteLine($"{(split ? "OK " : "MAL")} tablero que juega: empate");
        if (!split) failures++;

        Console.WriteLine(failures == 0 ? "Todo bien" : $"{failures} fallos");
        return failures == 0 ? 0 : 1;
    }
}

/// <summary>
/// Valor de la mejor mano de 5 cartas entre 5 a 7 cartas ("AhKd" + tablero). Mayor = mejor;
/// -1 si alguna carta no se entiende.
/// </summary>
static class HandEvaluator
{
    private const string Ranks = "23456789TJQKA";
    private const string Suits = "cdhs";

    public static long Best(string cards)
    {
        if (cards.Length % 2 != 0 || cards.Length < 10)
            return -1;

        Span<int> count = stackalloc int[15];
        Span<int> suitMask = stackalloc int[4];
        var rankMask = 0;
        for (var c = 0; c < cards.Length; c += 2)
        {
            var r = Ranks.IndexOf(char.ToUpperInvariant(cards[c])) + 2;
            var s = Suits.IndexOf(char.ToLowerInvariant(cards[c + 1]));
            if (r < 2 || s < 0)
                return -1;
            count[r]++;
            suitMask[s] |= 1 << r;
            rankMask |= 1 << r;
        }

        // Con 7 cartas, color y poker/full no pueden darse a la vez: el color se mira primero.
        for (var s = 0; s < 4; s++)
        {
            if (System.Numerics.BitOperations.PopCount((uint)suitMask[s]) < 5) continue;
            var straightFlush = StraightHigh(suitMask[s]);
            return straightFlush > 0 ? Score(8, straightFlush) : Score(5, TopRanks(suitMask[s], 5));
        }

        int quad = 0, trip = 0, trip2 = 0, pair = 0, pair2 = 0;
        for (var r = 14; r >= 2; r--)
        {
            switch (count[r])
            {
                case 4: quad = r; break;
                case 3: if (trip == 0) trip = r; else if (trip2 == 0) trip2 = r; break;
                case 2: if (pair == 0) pair = r; else if (pair2 == 0) pair2 = r; break;
            }
        }

        if (quad > 0)
            return Score(7, quad, TopRanks(rankMask & ~(1 << quad), 1));
        if (trip > 0 && (pair > 0 || trip2 > 0))
            return Score(6, trip, Math.Max(pair, trip2));
        var straight = StraightHigh(rankMask);
        if (straight > 0)
            return Score(4, straight);
        if (trip > 0)
            return Score(3, trip, TopRanks(rankMask & ~(1 << trip), 2));
        if (pair > 0 && pair2 > 0)
            return Score(2, pair, pair2, TopRanks(rankMask & ~(1 << pair) & ~(1 << pair2), 1));
        if (pair > 0)
            return Score(1, pair, TopRanks(rankMask & ~(1 << pair), 3));
        return Score(0, TopRanks(rankMask, 5));
    }

    private static int StraightHigh(int mask)
    {
        if ((mask & (1 << 14)) != 0) mask |= 1 << 1; // el as tambien vale 1
        for (var high = 14; high >= 5; high--)
        {
            var need = 0x1F << (high - 4);
            if ((mask & need) == need) return high;
        }
        return 0;
    }

    private static int[] TopRanks(int mask, int take)
    {
        var result = new int[take];
        var k = 0;
        for (var r = 14; r >= 2 && k < take; r--)
            if ((mask & (1 << r)) != 0) result[k++] = r;
        return result;
    }

    private static long Score(int category, params object[] parts)
    {
        long value = category;
        var used = 0;
        foreach (var part in parts)
        {
            if (part is int single) { value = (value << 4) | (uint)single; used++; }
            else foreach (var r in (int[])part) { value = (value << 4) | (uint)r; used++; }
        }
        for (; used < 5; used++) value <<= 4;
        return value;
    }
}

sealed class PlayerTotals
{
    public int Hands, Vpip, Pfr, ThreeBetOpp, ThreeBet, PostBr, PostCalls, CbetOpp, Cbet, FacedCbet, FoldCbet,
        SawFlop, Wtsd, Wsd, Wwsf,
        // Manos donde se sabe quien gano (base de W$SD y WWSF).
        WsdBase, WwsfBase;
}

sealed class PoolTotals
{
    public readonly Dictionary<string, (long Opp, long Open, long Limp)> Rfi = new(StringComparer.Ordinal);
    public long VsOpenOpp, VsOpenFold, VsOpenCall, VsOpenRaise;
    public long Vs3BetOpp, Vs3BetFold, Vs3BetCall, Vs3BetRaise;
    public long CbetOpp, Cbet, FacedCbet, FoldCbet;
    public long Hands;
}

static class HandAnalyzer
{
    public static bool TryAnalyze(
        PhhHand hand,
        (string Site, string Level, string Bucket) source,
        Dictionary<(string Bucket, string Name), PlayerTotals> players,
        Dictionary<string, PoolTotals> pools)
    {
        var n = hand.Players.Length;
        if (hand.Variant != "NT" || n < 2 || hand.Blinds.Length != n || hand.Actions.Length == 0)
            return false;

        var bucket = source.Level.StartsWith("PS ", StringComparison.Ordinal) ? source.Bucket : $"{source.Site}-{source.Bucket}";
        var stats = new PlayerTotals[n];
        for (var i = 0; i < n; i++)
        {
            var key = (bucket, hand.Players[i]);
            if (!players.TryGetValue(key, out var totals))
                players[key] = totals = new PlayerTotals();
            stats[i] = totals;
            totals.Hands++;
        }

        if (!pools.TryGetValue(bucket, out var pool))
            pools[bucket] = pool = new PoolTotals();
        pool.Hands++;

        var folded = new bool[n];
        var vpip = new bool[n];
        var pfr = new bool[n];
        var sawFlop = new bool[n];
        var acted = new bool[n];
        var facedOpen = new bool[n];
        var faced3Bet = new bool[n];
        var committed = (double[])hand.Blinds.Clone();
        var contributed = (double[])hand.Blinds.Clone();
        if (hand.Antes.Length == n)
            for (var i = 0; i < n; i++) contributed[i] += hand.Antes[i];
        var shown = new string?[n];
        var board = new System.Text.StringBuilder(10);
        var betLevel = hand.Blinds.Max();
        var street = 0;
        var raises = 0;
        var firstRaiser = -1;
        var aggressor = -1;
        var potOpen = true;

        // CBet en el flop
        var flopBet = false;
        var aggressorActedOnFlop = false;
        var cbetMade = false;
        var cbetResponders = new bool[n];

        foreach (var action in hand.Actions)
        {
            var parts = action.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                continue;

            if (parts[0] == "d")
            {
                if (parts[1] != "db")
                    continue;
                if (parts.Length > 2) board.Append(parts[2]);
                street++;
                Array.Clear(committed);
                betLevel = 0;
                if (street == 1)
                {
                    for (var i = 0; i < n; i++)
                    {
                        if (folded[i]) continue;
                        sawFlop[i] = true;
                        stats[i].SawFlop++;
                    }
                }
                continue;
            }

            if (parts[0].Length < 2 || parts[0][0] != 'p' ||
                !int.TryParse(parts[0].AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seat))
                continue;
            var p = seat - 1;
            if (p < 0 || p >= n)
                continue;

            var verb = parts[1];
            if (verb == "sm")
            {
                // La ultima vez que ensena es la que vale; "????" = tiro las cartas sin ensenarlas.
                shown[p] = parts.Length > 2 ? parts[2] : null;
                continue;
            }
            if (verb is not ("f" or "cc" or "cbr"))
                continue;

            if (street == 0)
            {
                var position = Position(p, n);
                if (!acted[p] && potOpen && n >= 3 && p != 1)
                {
                    var (opp, open, limp) = pool.Rfi.GetValueOrDefault(position);
                    pool.Rfi[position] = (opp + 1, open + (verb == "cbr" ? 1 : 0), limp + (verb == "cc" ? 1 : 0));
                }

                if (raises == 1 && !facedOpen[p] && p != firstRaiser)
                {
                    facedOpen[p] = true;
                    stats[p].ThreeBetOpp++;
                    pool.VsOpenOpp++;
                    if (verb == "cbr") { stats[p].ThreeBet++; pool.VsOpenRaise++; }
                    else if (verb == "cc") pool.VsOpenCall++;
                    else pool.VsOpenFold++;
                }

                if (raises == 2 && p == firstRaiser && !faced3Bet[p])
                {
                    faced3Bet[p] = true;
                    pool.Vs3BetOpp++;
                    if (verb == "cbr") pool.Vs3BetRaise++;
                    else if (verb == "cc") pool.Vs3BetCall++;
                    else pool.Vs3BetFold++;
                }

                acted[p] = true;
                switch (verb)
                {
                    case "f":
                        folded[p] = true;
                        break;
                    case "cc":
                        if (betLevel - committed[p] > 1e-9)
                        {
                            vpip[p] = true;
                            potOpen = false;
                        }
                        contributed[p] += Math.Max(0, betLevel - committed[p]);
                        committed[p] = betLevel;
                        break;
                    case "cbr":
                        vpip[p] = true;
                        pfr[p] = true;
                        potOpen = false;
                        raises++;
                        if (raises == 1) firstRaiser = p;
                        aggressor = p;
                        if (parts.Length > 2 && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var to))
                        {
                            contributed[p] += Math.Max(0, to - committed[p]);
                            committed[p] = to;
                            betLevel = Math.Max(betLevel, to);
                        }
                        break;
                }

                continue;
            }

            // ---------- Postflop ----------
            var isCall = verb == "cc" && betLevel - committed[p] > 1e-9;
            if (verb == "cbr") stats[p].PostBr++;
            if (isCall) stats[p].PostCalls++;

            if (street == 1 && aggressor >= 0 && sawFlop[aggressor])
            {
                if (!flopBet && p == aggressor && !aggressorActedOnFlop)
                {
                    stats[p].CbetOpp++;
                    pool.CbetOpp++;
                    if (verb == "cbr")
                    {
                        stats[p].Cbet++;
                        pool.Cbet++;
                        cbetMade = true;
                    }
                }
                else if (cbetMade && p != aggressor && !cbetResponders[p])
                {
                    cbetResponders[p] = true;
                    stats[p].FacedCbet++;
                    pool.FacedCbet++;
                    if (verb == "f") { stats[p].FoldCbet++; pool.FoldCbet++; }
                }

                if (p == aggressor) aggressorActedOnFlop = true;
                if (verb == "cbr") flopBet = true;
            }

            switch (verb)
            {
                case "f":
                    folded[p] = true;
                    break;
                case "cc":
                    contributed[p] += Math.Max(0, betLevel - committed[p]);
                    committed[p] = betLevel;
                    break;
                case "cbr":
                    if (parts.Length > 2 && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
                    {
                        contributed[p] += Math.Max(0, amount - committed[p]);
                        committed[p] = amount;
                        betLevel = Math.Max(betLevel, amount);
                    }
                    break;
            }
        }

        // "Gano" = cobro algo del bote, igual que APH (linea "collected"). Si no se sabe quien gano
        // (showdown sin cartas a la vista y sin ganancias anotadas), la mano no cuenta para W$SD ni WWSF.
        var winners = Winners(hand, folded, shown, board.ToString(), contributed);

        var stillIn = Enumerable.Range(0, n).Count(i => !folded[i]);
        for (var i = 0; i < n; i++)
        {
            if (vpip[i]) stats[i].Vpip++;
            if (pfr[i]) stats[i].Pfr++;
            if (!sawFlop[i]) continue;

            var atShowdown = stillIn >= 2 && !folded[i];
            if (atShowdown) stats[i].Wtsd++;
            if (winners is null) continue;

            stats[i].WwsfBase++;
            if (winners[i]) stats[i].Wwsf++;
            if (atShowdown)
            {
                stats[i].WsdBase++;
                if (winners[i]) stats[i].Wsd++;
            }
        }

        return true;
    }

    /// <summary>
    /// Quien cobro algo del bote (sin contar la apuesta no pagada que se le devuelve).
    /// HandHQ no anota bien las ganancias de PokerStars, iPoker ni Absolute (ceros, o nada en los
    /// showdowns), asi que se deduce de la mano:
    /// 1) si solo queda uno, gano el; 2) si hay ganancias o pilas finales validas, se usan;
    /// 3) se comparan las cartas ensenadas respetando los botes paralelos; quien no ensena perdio.
    /// En los all-in donde alguien no ensena no se puede saber: ahi el ganador a veces sale como "????".
    /// Devuelve null si no se puede saber.
    /// </summary>
    public static bool[]? Winners(PhhHand hand, bool[] folded, string?[] shown, string board, double[] contributed)
    {
        var n = folded.Length;
        var winners = new bool[n];
        var alive = Enumerable.Range(0, n).Where(i => !folded[i]).ToArray();
        if (alive.Length == 1)
        {
            winners[alive[0]] = true;
            return winners;
        }

        var capped = new double[n];
        for (var i = 0; i < n; i++)
            capped[i] = hand.Starting.Length == n ? Math.Min(contributed[i], hand.Starting[i]) : contributed[i];

        var (byCards, fullReveal) = ByCards(alive, shown, board, capped);
        var allIn = hand.Starting.Length == n && alive.Any(i => capped[i] >= hand.Starting[i] - 1e-9);

        if (hand.Winnings is { } winnings && winnings.Length == n && winnings.Sum() > 1e-9)
        {
            // Lo que puso de mas el que mas puso (nadie lo pudo pagar) vuelve a su pila: no es ganar.
            var top = Enumerable.Range(0, n).MaxBy(i => capped[i]);
            var secondMost = Enumerable.Range(0, n).Where(i => i != top).Select(i => capped[i]).DefaultIfEmpty(0).Max();
            var returned = Math.Max(0, capped[top] - secondMost);
            for (var i = 0; i < n; i++)
                winners[i] = winnings[i] - (i == top ? returned : 0) > 1e-6;
            CrossCheck(winners, byCards, fullReveal, allIn, alive, hand, winnings);
            return winners;
        }

        if (hand.Finishing is { } finishing && finishing.Length == n && hand.Starting.Length == n &&
            finishing.All(double.IsFinite) && hand.Starting.All(double.IsFinite) &&
            Enumerable.Range(0, n).Any(i => finishing[i] - hand.Starting[i] > 1e-9))
        {
            for (var i = 0; i < n; i++) winners[i] = finishing[i] - hand.Starting[i] > 1e-9;
            CrossCheck(winners, byCards, fullReveal, allIn, alive, hand, finishing);
            return winners;
        }

        // Comprobado con PartyPoker y Ongame: si todos ensenan coincide el 99%; si alguien tira sin
        // ensenar en un showdown normal, el 99.5%; en un all-in con cartas ocultas solo el 93-98%.
        if (byCards is not null && (fullReveal || !allIn))
        {
            CardOnlyShowdowns++;
            return byCards;
        }

        UnknownShowdowns++;
        return null;
    }

    // Comprobacion: donde la sala si anota las ganancias, las cartas deben dar el mismo ganador.
    public static long CheckedShowdowns, AgreedShowdowns, CheckedPartial, AgreedPartial, CheckedPartialAllIn, AgreedPartialAllIn, CardOnlyShowdowns, UnknownShowdowns;

    private static void CrossCheck(bool[] recorded, bool[]? byCards, bool fullReveal, bool allIn, int[] alive, PhhHand hand, double[] amounts)
    {
        if (byCards is null) return;
        var agree = alive.All(i => recorded[i] == byCards[i]);
        if (fullReveal)
        {
            CheckedShowdowns++;
            if (agree) AgreedShowdowns++;
            else if (Environment.GetEnvironmentVariable("APH_DUMP") == "1" && CheckedShowdowns - AgreedShowdowns <= 6)
                Console.Error.WriteLine($"{string.Join(" | ", hand.Actions.Where(a => !a.StartsWith("d dh", StringComparison.Ordinal)))}\n" +
                                        $"  stacks={string.Join(",", hand.Starting)} amounts={string.Join(",", amounts)}\n");
        }
        else
        {
            if (allIn) { CheckedPartialAllIn++; if (agree) AgreedPartialAllIn++; return; }
            CheckedPartial++;
            if (agree) AgreedPartial++;
        }
    }

    /// <summary>
    /// Ganadores segun las cartas ensenadas. Cada jugador compite por los botes hasta lo que puso;
    /// gana algo si su mano es la mejor entre quienes pusieron al menos lo mismo que el.
    /// fullReveal = todos los del showdown ensenaron.
    /// </summary>
    private static (bool[]? Winners, bool FullReveal) ByCards(int[] alive, string?[] shown, string board, double[] capped)
    {
        var n = shown.Length;
        if (board.Length < 10)
            return (null, false);

        var score = new long[n];
        var anyShown = false;
        var full = true;
        foreach (var i in alive)
        {
            var cards = shown[i];
            score[i] = cards is { Length: 4 } && !cards.Contains('?') ? HandEvaluator.Best(cards + board) : -1;
            anyShown |= score[i] >= 0;
            full &= score[i] >= 0;
        }
        if (!anyShown)
            return (null, false);

        var winners = new bool[n];
        foreach (var i in alive)
        {
            if (score[i] < 0) continue;
            // Su bote mas alto llega hasta lo que puso, pero no mas alla de lo que puso otro:
            // lo de encima nadie lo pago y se le devuelve.
            var othersMax = Enumerable.Range(0, n).Where(j => j != i).Select(j => capped[j]).DefaultIfEmpty(0).Max();
            var level = Math.Min(capped[i], othersMax);
            winners[i] = alive.Where(j => score[j] >= 0 && capped[j] >= level - 1e-9).All(j => score[j] <= score[i]);
        }
        return (winners, full);
    }

    // Posicion por orden de la mano en PHH: p1 = ciega pequena, p2 = ciega grande, el ultimo = boton.
    public static string Position(int p, int n)
    {
        if (n == 2) return p == 0 ? "SB" : "BB";
        if (p == 0) return "SB";
        if (p == 1) return "BB";
        if (p == n - 1) return "BTN";
        if (p == n - 2) return "CO";
        if (p == n - 3) return "HJ";
        return "EP";
    }
}

// ------------------------------------------------------------------------------------------
//  Percentiles y salida
// ------------------------------------------------------------------------------------------
static class Baselines
{
    // 7 cortes -> 8 bandas de color del HUD (de "mucho menos que la poblacion" a "mucho mas").
    private static readonly double[] CutPercentiles = { 5, 15, 30, 50, 70, 85, 95 };

    public static object Build(
        Dictionary<(string Bucket, string Name), PlayerTotals> players,
        Dictionary<string, PoolTotals> pools,
        int minHands,
        long handsUsed,
        SortedDictionary<string, long> levels)
    {
        var buckets = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var bucket in players.Keys.Select(k => k.Bucket).Distinct().OrderBy(b => b, StringComparer.Ordinal))
        {
            var sample = players.Where(kv => kv.Key.Bucket == bucket && kv.Value.Hands >= minHands).Select(kv => kv.Value).ToList();
            if (sample.Count < 20)
                continue;

            var metrics = new Dictionary<string, Func<PlayerTotals, double?>>
            {
                ["VPIP"] = t => Pct(t.Vpip, t.Hands),
                ["PFR"] = t => Pct(t.Pfr, t.Hands),
                ["THREEBET"] = t => t.ThreeBetOpp >= 20 ? Pct(t.ThreeBet, t.ThreeBetOpp) : null,
                ["AF"] = t => t.PostCalls == 0 ? (t.PostBr > 0 ? t.PostBr : null) : (double)t.PostBr / t.PostCalls,
                ["AFQ"] = t => t.PostBr + t.PostCalls > 0 ? Pct(t.PostBr, t.PostBr + t.PostCalls) : null,
                ["CBF"] = t => t.CbetOpp >= 10 ? Pct(t.Cbet, t.CbetOpp) : null,
                ["FVCBF"] = t => t.FacedCbet >= 10 ? Pct(t.FoldCbet, t.FacedCbet) : null,
                ["WTSD"] = t => t.SawFlop >= 20 ? Pct(t.Wtsd, t.SawFlop) : null,
                ["WSD"] = t => t.WsdBase >= 10 ? Pct(t.Wsd, t.WsdBase) : null,
                ["WWSF"] = t => t.WwsfBase >= 20 ? Pct(t.Wwsf, t.WwsfBase) : null,
                ["PFR_VPIP_RATIO"] = t => t.Vpip >= 10 ? (double)t.Pfr / t.Vpip : null,
            };

            var cuts = new Dictionary<string, double[]>(StringComparer.Ordinal);
            var quantiles = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
            foreach (var (name, metric) in metrics)
            {
                var values = sample.Select(metric).Where(v => v.HasValue && double.IsFinite(v.Value)).Select(v => v!.Value).OrderBy(v => v).ToArray();
                if (values.Length < 20)
                    continue;
                cuts[name] = CutPercentiles.Select(q => Round(Percentile(values, q))).ToArray();
                quantiles[name] = new[] { 10, 20, 50, 80, 90 }.ToDictionary(q => $"p{q}", q => Round(Percentile(values, q)));
            }

            var pool = pools[bucket];
            buckets[bucket] = new
            {
                playersWithSample = sample.Count,
                hands = pool.Hands,
                cuts,
                quantiles,
                preflop = new
                {
                    openRaisePctByPosition = pool.Rfi.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key, kv => Round(Pct(kv.Value.Open, kv.Value.Opp))),
                    limpPctByPosition = pool.Rfi.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key, kv => Round(Pct(kv.Value.Limp, kv.Value.Opp))),
                    vsOpen = new { foldPct = Round(Pct(pool.VsOpenFold, pool.VsOpenOpp)), callPct = Round(Pct(pool.VsOpenCall, pool.VsOpenOpp)), threeBetPct = Round(Pct(pool.VsOpenRaise, pool.VsOpenOpp)) },
                    vsThreeBet = new { foldPct = Round(Pct(pool.Vs3BetFold, pool.Vs3BetOpp)), callPct = Round(Pct(pool.Vs3BetCall, pool.Vs3BetOpp)), fourBetPct = Round(Pct(pool.Vs3BetRaise, pool.Vs3BetOpp)) },
                },
                postflop = new
                {
                    cbetFlopPct = Round(Pct(pool.Cbet, pool.CbetOpp)),
                    foldVsCbetFlopPct = Round(Pct(pool.FoldCbet, pool.FacedCbet)),
                },
            };
        }

        return new
        {
            source = "A Dataset of Poker Hand Histories (Kim, U. of Toronto), Zenodo doi:10.5281/zenodo.10796885, CC BY 4.0 - HandHQ, julio 2009",
            generatedBy = "APH.Calibration",
            generatedAt = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            minHandsPerPlayer = minHands,
            handsUsed,
            handsByLevel = levels,
            cutPercentiles = CutPercentiles,
            notes = "Mid = NL25-NL100, High = NL200+. Los buckets sin prefijo son PokerStars; los demas llevan la sala (FTP, PTY, IPN, ONG, ABS). No hay manos de microlimites ni de dinero ficticio: APH usa Mid para esos niveles.",
            buckets,
        };
    }

    private static double Pct(long part, long total) => total == 0 ? 0 : 100.0 * part / total;

    private static double Percentile(double[] sorted, double q)
    {
        var position = (sorted.Length - 1) * q / 100.0;
        var lower = (int)Math.Floor(position);
        var upper = Math.Min(lower + 1, sorted.Length - 1);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }

    private static double Round(double value) => Math.Round(value, 2);
}
