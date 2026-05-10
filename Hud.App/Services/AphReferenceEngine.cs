using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Collections.Concurrent;
using Hud.App.Models;

namespace Hud.App.Services
{
    public class AphReferenceEngine
    {
        private static readonly ConcurrentDictionary<string, double> EquityCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly AphPreflopProvider _preflopProvider;

        public AphReferenceEngine()
        {
            var csvPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "APH_Reference_100bb_6max.csv");
            _preflopProvider = new AphPreflopProvider(csvPath);
        }

        public AphReferenceRecommendation AnalyzeAction(string street, string position, string actionContext, string heroCards, List<string> board)
        {
            var rec = new AphReferenceRecommendation();
            if (string.Equals(street, "PREFLOP", StringComparison.OrdinalIgnoreCase))
                AnalyzePreflop(position, actionContext, heroCards, rec);
            else
                AnalyzePostflop(street, heroCards, board, rec);
            return rec;
        }

        private void AnalyzePreflop(string pos, string context, string cards, AphReferenceRecommendation rec)
        {
            if (string.IsNullOrWhiteSpace(cards))
            {
                ApplyFrequencies(rec, 34, 33, 33);
                rec.Equity = 0;
                rec.StrategyTip = "Cartas del Hero no detectadas.";
                return;
            }

            var action = ExtractActionFromContext(context);
            var potType = ExtractPotTypeFromContext(context);
            var reference = PreflopReference.Evaluate(cards, pos, action, potType);
            if (reference.HasReference)
            {
                ApplyFrequencies(rec, reference.FoldPct, reference.CallPct, reference.RaisePct);
                rec.Equity = EstimatePreflopEquity(cards);
                rec.StrategyTip = reference.Summary;
                return;
            }

            var heroPos = pos == "UTG" ? "LJ" : pos;
            var scenario = "Open";
            var villainPos = "";
            var recommendedAction = _preflopProvider.GetRecommendation(scenario, heroPos, villainPos, NormalizeCombo(cards));
            switch (recommendedAction)
            {
                case "Raise":
                    ApplyFrequencies(rec, 0, 0, 100);
                    break;
                case "Call":
                    ApplyFrequencies(rec, 0, 100, 0);
                    break;
                case "Fold":
                    ApplyFrequencies(rec, 100, 0, 0);
                    break;
                default:
                    ApplyFrequencies(rec, 34, 33, 33);
                    break;
            }

            rec.Equity = EstimatePreflopEquity(cards);
            rec.StrategyTip = $"APH Reference preflop: {recommendedAction} con {NormalizeCombo(cards)} en {pos}.";
        }

        private void AnalyzePostflop(string street, string heroCards, List<string> board, AphReferenceRecommendation rec)
        {
            rec.Equity = EstimatePostflopEquity(heroCards, board);
            if (rec.Equity >= 70)
            {
                ApplyFrequencies(rec, 5, 35, 60);
                rec.StrategyTip = $"APH Reference ({street}): Equity simulada {rec.Equity:0.0}%. Mano fuerte; raise/value bet gana peso.";
            }
            else if (rec.Equity >= 45)
            {
                ApplyFrequencies(rec, 15, 60, 25);
                rec.StrategyTip = $"APH Reference ({street}): Equity simulada {rec.Equity:0.0}%. Continuar es razonable; call/check-call suele dominar.";
            }
            else if (rec.Equity >= 28)
            {
                ApplyFrequencies(rec, 35, 50, 15);
                rec.StrategyTip = $"APH Reference ({street}): Equity simulada {rec.Equity:0.0}%. Spot marginal; depende de sizing, posicion y draw.";
            }
            else
            {
                ApplyFrequencies(rec, 65, 30, 5);
                rec.StrategyTip = $"APH Reference ({street}): Equity simulada {rec.Equity:0.0}%. Equity baja; fold gana peso salvo pot odds claras.";
            }
        }

        private static void ApplyFrequencies(AphReferenceRecommendation rec, double fold, double call, double raise)
        {
            rec.FoldPct = Math.Clamp(fold, 0, 100);
            rec.CallPct = Math.Clamp(call, 0, 100);
            rec.RaisePct = Math.Clamp(raise, 0, 100);
        }

        private static string ExtractActionFromContext(string context)
        {
            if (context.Contains("fold", StringComparison.OrdinalIgnoreCase) ||
                context.Contains("retira", StringComparison.OrdinalIgnoreCase))
                return "Fold";
            if (context.Contains("call", StringComparison.OrdinalIgnoreCase) ||
                context.Contains("paga", StringComparison.OrdinalIgnoreCase) ||
                context.Contains("iguala", StringComparison.OrdinalIgnoreCase))
                return "Call";
            if (context.Contains("raise", StringComparison.OrdinalIgnoreCase) ||
                context.Contains("sube", StringComparison.OrdinalIgnoreCase) ||
                context.Contains("bet", StringComparison.OrdinalIgnoreCase) ||
                context.Contains("apuesta", StringComparison.OrdinalIgnoreCase))
                return "Raise";
            return "Unknown";
        }

        private static string ExtractPotTypeFromContext(string context)
        {
            if (context.Contains("3bet", StringComparison.OrdinalIgnoreCase) ||
                context.Contains("3-bet", StringComparison.OrdinalIgnoreCase))
                return "3Bet pot";
            if (context.Contains("4bet", StringComparison.OrdinalIgnoreCase) ||
                context.Contains("4-bet", StringComparison.OrdinalIgnoreCase))
                return "4Bet+ pot";
            if (context.Contains("limp", StringComparison.OrdinalIgnoreCase))
                return "Limped pot";
            if (context.Contains("blind", StringComparison.OrdinalIgnoreCase))
                return "Blind pot";
            return "Single raised";
        }

        private static double EstimatePreflopEquity(string cards)
        {
            var combo = NormalizeCombo(cards);
            if (combo is "AA" or "KK") return 82;
            if (combo is "QQ" or "JJ" or "AKs") return 72;
            if (combo is "TT" or "99" or "AQs" or "AKo") return 64;
            if (combo.StartsWith("A", StringComparison.Ordinal)) return combo.EndsWith("s", StringComparison.Ordinal) ? 58 : 52;
            if (combo.Length == 2 && combo[0] == combo[1]) return 54;
            if (combo.EndsWith("s", StringComparison.Ordinal)) return 47;
            return 42;
        }

        private static double EstimatePostflopEquity(string heroCards, IReadOnlyList<string> board)
        {
            if (board.Count == 0)
                return EstimatePreflopEquity(heroCards);

            var cacheKey = $"{NormalizeCardsKey(heroCards)}|{string.Join("-", board.Select(NormalizeCardsKey))}";
            if (EquityCache.TryGetValue(cacheKey, out var cached))
                return cached;

            var hero = ParseCards(heroCards).ToList();
            var boardCards = ParseCards(board).ToList();
            var simulated = EstimateMonteCarloEquity(hero, boardCards);
            if (simulated > 0)
                return EquityCache.GetOrAdd(cacheKey, simulated);

            var heuristic = EstimateHeuristicPostflopEquity(hero, boardCards);
            return EquityCache.GetOrAdd(cacheKey, heuristic);
        }

        private static double EstimateHeuristicPostflopEquity(IReadOnlyList<CardInfo> hero, IReadOnlyList<CardInfo> boardCards)
        {
            var all = hero.Concat(boardCards).ToList();
            if (hero.Count < 2 || boardCards.Count == 0)
                return 0;

            var rankGroups = all.GroupBy(card => card.Value).Select(group => group.Count()).OrderByDescending(count => count).ToList();
            var made = rankGroups.FirstOrDefault();
            var pairs = rankGroups.Count(count => count >= 2);
            var flush = all.GroupBy(card => card.Suit).Any(group => group.Key != '\0' && group.Count() >= 5);
            var flushDraw = boardCards.Count < 5 && all.GroupBy(card => card.Suit).Any(group => group.Key != '\0' && group.Count() >= 4);
            var ranks = all.Select(card => card.Value).ToList();
            var straight = HasStraight(ranks);
            var straightDraw = boardCards.Count < 5 && HasStraightDraw(ranks);

            var equity = 18.0;
            if (made >= 4) equity = 92;
            else if (made == 3 && pairs >= 2) equity = 88;
            else if (flush) equity = 82;
            else if (straight) equity = 78;
            else if (made == 3) equity = 68;
            else if (pairs >= 2) equity = 58;
            else if (made == 2) equity = 42;
            else if (flushDraw && straightDraw) equity = 48;
            else if (flushDraw) equity = 36;
            else if (straightDraw) equity = 33;
            else if (hero.Any(card => card.Value == 14)) equity = 25;

            if (boardCards.Count == 4) equity -= 4;
            if (boardCards.Count >= 5) equity -= 8;
            return Math.Clamp(equity, 3, 95);
        }

        private static double EstimateMonteCarloEquity(IReadOnlyList<CardInfo> hero, IReadOnlyList<CardInfo> boardCards)
        {
            if (hero.Count < 2 || boardCards.Count is < 3 or > 5)
                return 0;

            var known = hero.Concat(boardCards).ToHashSet();
            var deck = BuildDeck().Where(card => !known.Contains(card)).ToList();
            if (deck.Count < 2)
                return 0;

            var missingBoard = Math.Max(0, 5 - boardCards.Count);
            var iterations = boardCards.Count switch
            {
                3 => 900,
                4 => 1200,
                _ => Math.Min(1326, deck.Count * (deck.Count - 1) / 2)
            };
            var random = new Random(BuildSeed(hero, boardCards));
            var wins = 0.0;
            var samples = 0;

            if (boardCards.Count >= 5)
            {
                for (var i = 0; i < deck.Count; i++)
                for (var j = i + 1; j < deck.Count; j++)
                {
                    ScoreShowdown(hero, boardCards, new[] { deck[i], deck[j] }, ref wins, ref samples);
                }
            }
            else
            {
                for (var i = 0; i < iterations; i++)
                {
                    var sampleDeck = deck.ToList();
                    Shuffle(sampleDeck, random);
                    var villain = sampleDeck.Take(2).ToArray();
                    var runout = boardCards.Concat(sampleDeck.Skip(2).Take(missingBoard)).ToArray();
                    ScoreShowdown(hero, runout, villain, ref wins, ref samples);
                }
            }

            return samples == 0 ? 0 : Math.Clamp(wins * 100.0 / samples, 1, 99);
        }

        private static void ScoreShowdown(
            IReadOnlyList<CardInfo> hero,
            IReadOnlyList<CardInfo> finalBoard,
            IReadOnlyList<CardInfo> villain,
            ref double wins,
            ref int samples)
        {
            var heroScore = EvaluateSeven(hero.Concat(finalBoard));
            var villainScore = EvaluateSeven(villain.Concat(finalBoard));
            if (heroScore > villainScore) wins += 1;
            else if (heroScore == villainScore) wins += 0.5;
            samples++;
        }

        private static IReadOnlyList<CardInfo> BuildDeck()
        {
            var ranks = new[] { '2', '3', '4', '5', '6', '7', '8', '9', 'T', 'J', 'Q', 'K', 'A' };
            var suits = new[] { 'h', 'd', 'c', 's' };
            return ranks.SelectMany(rank => suits.Select(suit => new CardInfo(RankValue(rank.ToString()), rank, suit))).ToList();
        }

        private static void Shuffle(IList<CardInfo> cards, Random random)
        {
            for (var i = cards.Count - 1; i > 0; i--)
            {
                var j = random.Next(i + 1);
                (cards[i], cards[j]) = (cards[j], cards[i]);
            }
        }

        private static int BuildSeed(IEnumerable<CardInfo> hero, IEnumerable<CardInfo> board)
        {
            unchecked
            {
                var seed = 17;
                foreach (var card in hero.Concat(board))
                    seed = seed * 31 + card.GetHashCode();
                return seed;
            }
        }

        private static long EvaluateSeven(IEnumerable<CardInfo> cards)
        {
            var list = cards.ToList();
            var best = 0L;
            for (var a = 0; a < list.Count - 4; a++)
            for (var b = a + 1; b < list.Count - 3; b++)
            for (var c = b + 1; c < list.Count - 2; c++)
            for (var d = c + 1; d < list.Count - 1; d++)
            for (var e = d + 1; e < list.Count; e++)
            {
                var score = EvaluateFive(new[] { list[a], list[b], list[c], list[d], list[e] });
                if (score > best) best = score;
            }

            return best;
        }

        private static long EvaluateFive(IReadOnlyList<CardInfo> cards)
        {
            var values = cards.Select(card => card.Value).OrderByDescending(value => value).ToList();
            var groups = cards
                .GroupBy(card => card.Value)
                .Select(group => new { Value = group.Key, Count = group.Count() })
                .OrderByDescending(group => group.Count)
                .ThenByDescending(group => group.Value)
                .ToList();
            var flush = cards.GroupBy(card => card.Suit).Any(group => group.Key != '\0' && group.Count() == 5);
            var straightHigh = StraightHigh(values);

            if (flush && straightHigh > 0) return PackScore(8, straightHigh);
            if (groups[0].Count == 4) return PackScore(7, groups[0].Value, groups[1].Value);
            if (groups[0].Count == 3 && groups[1].Count == 2) return PackScore(6, groups[0].Value, groups[1].Value);
            if (flush) return PackScore(5, values.ToArray());
            if (straightHigh > 0) return PackScore(4, straightHigh);
            if (groups[0].Count == 3)
                return PackScore(3, new[] { groups[0].Value }
                    .Concat(groups.Where(group => group.Count == 1).Select(group => group.Value).OrderByDescending(value => value))
                    .ToArray());
            if (groups[0].Count == 2 && groups[1].Count == 2)
                return PackScore(2, groups[0].Value, groups[1].Value, groups.First(group => group.Count == 1).Value);
            if (groups[0].Count == 2)
                return PackScore(1, new[] { groups[0].Value }
                    .Concat(groups.Where(group => group.Count == 1).Select(group => group.Value).OrderByDescending(value => value))
                    .ToArray());

            return PackScore(0, values.ToArray());
        }

        private static int StraightHigh(IReadOnlyList<int> values)
        {
            var distinct = values.Distinct().OrderByDescending(value => value).ToList();
            if (distinct.Contains(14)) distinct.Add(1);
            for (var i = 0; i <= distinct.Count - 5; i++)
            {
                var window = distinct.Skip(i).Take(5).ToList();
                if (window.Zip(window.Skip(1), (left, right) => left - right).All(diff => diff == 1))
                    return window[0] == 14 && window[1] == 5 ? 5 : window[0];
            }

            return 0;
        }

        private static long PackScore(int category, params int[] ranks)
        {
            var score = (long)category;
            foreach (var rank in ranks.Take(5))
                score = score * 15 + rank;
            for (var i = ranks.Length; i < 5; i++)
                score *= 15;
            return score;
        }

        private static string NormalizeCombo(string cards)
        {
            var parsed = ParseCards(cards).Take(2).ToList();
            if (parsed.Count < 2) return cards.Trim();
            var first = parsed[0];
            var second = parsed[1];
            if (first.Value < second.Value)
                (first, second) = (second, first);
            if (first.Value == second.Value)
                return $"{first.Rank}{second.Rank}";
            var hasRealSuits = first.Suit != '\0' && second.Suit != '\0' && first.Suit != '?' && second.Suit != '?';
            return $"{first.Rank}{second.Rank}{(hasRealSuits && first.Suit == second.Suit ? "s" : "o")}";
        }

        private static string NormalizeCardsKey(string cards) =>
            string.Join("", ParseCards(cards)
                .OrderByDescending(card => card.Value)
                .ThenBy(card => card.Suit)
                .Select(card => $"{card.Rank}{card.Suit}"));

        private static IEnumerable<CardInfo> ParseCards(string cards) =>
            cards.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(ParseCard).Where(card => card.Value > 0);

        private static IEnumerable<CardInfo> ParseCards(IEnumerable<string> cards) =>
            cards.Select(ParseCard).Where(card => card.Value > 0);

        private static CardInfo ParseCard(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return default;
            var rank = char.ToUpperInvariant(raw[0]);
            var value = RankValue(rank.ToString());
            var suit = raw.Length > 1 && "hdcs\u2665\u2666\u2663\u2660".Contains(char.ToLowerInvariant(raw[^1]))
                ? char.ToLowerInvariant(raw[^1])
                : '\0';
            return new CardInfo(value, rank, suit);
        }

        private static bool HasStraight(IEnumerable<int> ranks)
        {
            var values = ranks.Distinct().OrderBy(value => value).ToList();
            if (values.Contains(14)) values.Insert(0, 1);
            var streak = 1;
            for (var i = 1; i < values.Count; i++)
            {
                streak = values[i] == values[i - 1] + 1 ? streak + 1 : 1;
                if (streak >= 5) return true;
            }
            return false;
        }

        private static bool HasStraightDraw(IEnumerable<int> ranks)
        {
            var values = ranks.Distinct().OrderBy(value => value).ToList();
            if (values.Contains(14)) values.Insert(0, 1);
            return values.Any(value => values.Count(candidate => candidate >= value && candidate <= value + 4) >= 4);
        }

        private static int RankValue(string rank) => rank.ToUpperInvariant() switch
        {
            "A" => 14,
            "K" => 13,
            "Q" => 12,
            "J" => 11,
            "T" => 10,
            _ => int.TryParse(rank, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0
        };

        private readonly record struct CardInfo(int Value, char Rank, char Suit);

        private static class PreflopReference
        {
            private static readonly string[] RanksAsc = { "2", "3", "4", "5", "6", "7", "8", "9", "T", "J", "Q", "K", "A" };

            private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> Base = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["UTG"] = BuildRange("22+ AJs+ KQs AQo+ A5s-A4s KJs QJs JTs T9s"),
                ["MP"] = BuildRange("22+ ATs+ KTs+ QTs+ JTs T9s 98s AJo+ KQo A5s-A2s"),
                ["HJ"] = BuildRange("22+ ATs+ KTs+ QTs+ JTs T9s 98s AJo+ KQo A5s-A2s"),
                ["LJ"] = BuildRange("22+ AJs+ KQs AQo+ A5s-A4s KJs QJs JTs T9s"),
                ["CO"] = BuildRange("22+ A2s+ K8s+ Q9s+ J9s+ T8s+ 98s 87s 76s ATo+ KJo+ QJo"),
                ["BTN"] = BuildRange("22+ A2s+ K2s+ Q5s+ J7s+ T7s+ 97s+ 86s+ 75s+ 65s 54s A2o+ K8o+ Q9o+ J9o+ T9o"),
                ["SB"] = BuildRange("22+ A2s+ K2s+ Q6s+ J7s+ T7s+ 97s+ 86s+ 75s+ 65s A2o+ K9o+ Q9o+ J9o+ T9o"),
                ["BTN/SB"] = BuildRange("22+ A2s+ K2s+ Q2s+ J5s+ T6s+ 96s+ 85s+ 74s+ 64s+ 53s+ 43s A2o+ K2o+ Q7o+ J8o+ T8o+ 98o")
            };

            private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> Tight = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["UTG"] = BuildRange("55+ ATs+ KQs AQo+"),
                ["MP"] = BuildRange("44+ A9s+ KTs+ QTs+ JTs AJo+ KQo"),
                ["HJ"] = BuildRange("44+ A9s+ KTs+ QTs+ JTs AJo+ KQo"),
                ["LJ"] = BuildRange("55+ ATs+ KQs AQo+"),
                ["CO"] = BuildRange("33+ A2s+ K9s+ QTs+ JTs T9s 98s ATo+ KJo+ QJo"),
                ["BTN"] = BuildRange("22+ A2s+ K7s+ Q8s+ J8s+ T8s+ 98s 87s 76s A8o+ KTo+ QTo+ JTo"),
                ["SB"] = BuildRange("22+ A2s+ K8s+ Q9s+ J9s+ T9s 98s A9o+ KTo+ QTo+"),
                ["BTN/SB"] = BuildRange("22+ A2s+ K2s+ Q8s+ J8s+ T8s+ 98s A2o+ K8o+ QTo+ JTo")
            };

            private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> Wide = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["UTG"] = BuildRange("22+ ATs+ KJs+ QJs JTs T9s AJo+ KQo A5s-A2s"),
                ["MP"] = BuildRange("22+ A2s+ K9s+ QTs+ J9s+ T9s 98s ATo+ KJo+ QJo"),
                ["HJ"] = BuildRange("22+ A2s+ K9s+ QTs+ J9s+ T9s 98s ATo+ KJo+ QJo"),
                ["LJ"] = BuildRange("22+ ATs+ KJs+ QJs JTs T9s AJo+ KQo A5s-A2s"),
                ["CO"] = BuildRange("22+ A2s+ K6s+ Q8s+ J8s+ T8s+ 97s+ 86s+ 76s 65s A8o+ KTo+ QTo+ JTo"),
                ["BTN"] = BuildRange("22+ A2s+ K2s+ Q2s+ J5s+ T6s+ 96s+ 85s+ 75s+ 64s+ 54s A2o+ K6o+ Q8o+ J8o+ T8o+ 98o"),
                ["SB"] = BuildRange("22+ A2s+ K2s+ Q5s+ J7s+ T7s+ 97s+ 86s+ 75s+ 65s A2o+ K8o+ Q9o+ J9o+ T9o"),
                ["BTN/SB"] = BuildRange("22+ A2s+ K2s+ Q2s+ J2s+ T5s+ 95s+ 84s+ 74s+ 63s+ 53s+ 43s A2o+ K2o+ Q5o+ J7o+ T7o+ 98o")
            };

            public static ReferenceResult Evaluate(string combo, string position, string action, string potType)
            {
                combo = NormalizeCombo(combo);
                position = NormalizePosition(position);
                if (!IsOpenSpot(action, potType))
                    return ReferenceResult.Unknown("Referencia APH solo disponible para RFI/open preflop 6-max 100bb.");

                var votes = new[] { Has(Tight, position, combo), Has(Base, position, combo), Has(Wide, position, combo) };
                var raiseVotes = votes.Count(v => v);
                var raise = raiseVotes / 3.0 * 100;
                var fold = 100 - raise;
                var confidence = raiseVotes is 0 or 3 ? "Alta" : raiseVotes == 2 ? "Media" : "Baja";
                var verdict = Verdict(combo, position, action, raiseVotes);
                return new ReferenceResult(true, $"APH Reference 6-max 100bb: {verdict} ({raiseVotes}/3 fuentes libres, confianza {confidence}).", fold, 0, raise);
            }

            private static bool IsOpenSpot(string action, string potType) =>
                action is "Fold" or "Call" or "Raise" or "Unknown" &&
                potType is "Single raised" or "Limped pot" or "Blind pot";

            private static string Verdict(string combo, string position, string action, int raiseVotes)
            {
                if (raiseVotes >= 2)
                    return action == "Fold"
                        ? $"{combo} en {position}: la mayoria lo juega; fold tight/dudoso"
                        : $"{combo} en {position}: jugada alineada o defendible";
                if (raiseVotes == 1)
                    return $"{combo} en {position}: spot marginal, depende de mesa/rivales";
                return action == "Fold"
                    ? $"{combo} en {position}: fold alineado con rango base"
                    : $"{combo} en {position}: fuera de rango base, revisar leak preflop";
            }

            private static bool Has(IReadOnlyDictionary<string, IReadOnlySet<string>> ranges, string position, string combo) =>
                ranges.TryGetValue(position, out var range) && range.Contains(combo);

            private static string NormalizePosition(string pos) => pos switch
            {
                "BU" => "BTN",
                "EP" => "UTG",
                "LJ" => "UTG",
                "SB/BTN" => "BTN/SB",
                _ => pos
            };

            private static IReadOnlySet<string> BuildRange(string expression)
            {
                var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var token in expression.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    AddToken(result, token);
                return result;
            }

            private static void AddToken(HashSet<string> result, string token)
            {
                if (token.EndsWith("+", StringComparison.Ordinal))
                {
                    AddPlus(result, token[..^1]);
                    return;
                }
                if (token.Contains('-', StringComparison.Ordinal))
                {
                    var parts = token.Split('-', 2);
                    AddRange(result, parts[0], parts[1]);
                    return;
                }
                result.Add(NormalizeCombo(token));
            }

            private static void AddPlus(HashSet<string> result, string start)
            {
                start = NormalizeCombo(start);
                if (start.Length == 2 && start[0] == start[1])
                {
                    foreach (var rank in RanksAsc.Where(rank => RankValue(rank) >= RankValue(start[0].ToString())))
                        result.Add($"{rank}{rank}");
                    return;
                }

                if (start.Length < 3) return;
                var high = start[0].ToString();
                var low = start[1].ToString();
                var suitedness = start[2].ToString();
                foreach (var rank in RanksAsc.Where(rank => RankValue(rank) >= RankValue(low) && RankValue(rank) < RankValue(high)))
                    result.Add($"{high}{rank}{suitedness}");
            }

            private static void AddRange(HashSet<string> result, string from, string to)
            {
                from = NormalizeCombo(from);
                to = NormalizeCombo(to);
                if (from.Length < 3 || to.Length < 3 || from[0] != to[0] || from[2] != to[2])
                    return;

                var high = from[0].ToString();
                var suitedness = from[2].ToString();
                var min = Math.Min(RankValue(from[1].ToString()), RankValue(to[1].ToString()));
                var max = Math.Max(RankValue(from[1].ToString()), RankValue(to[1].ToString()));
                foreach (var rank in RanksAsc.Where(rank => RankValue(rank) >= min && RankValue(rank) <= max && RankValue(rank) < RankValue(high)))
                    result.Add($"{high}{rank}{suitedness}");
            }
        }

        private sealed record ReferenceResult(bool HasReference, string Summary, double FoldPct, double CallPct, double RaisePct)
        {
            public static ReferenceResult Unknown(string reason) => new(false, reason, 34, 33, 33);
        }
    }
}

