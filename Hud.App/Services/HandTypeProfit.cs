using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Hud.App.Services.Trainer;

namespace Hud.App.Services
{
    /// <summary>Una mano jugada (VPIP) del heroe: su tipo, posicion, resultado y la suerte en el all-in.</summary>
    public sealed record HandTypeSample(string Combo, string Position, double NetBb, double LuckBb);

    /// <summary>
    /// Cuanto deja cada tipo de mano cuando el heroe decide jugarla, por posicion. "Sin suerte" quita lo
    /// que se gano o perdio por las cartas que salieron despues de un all-in (lo que tocaba segun la
    /// equity en ese momento contra las manos mostradas). Con poca muestra el resultado es sobre todo
    /// suerte: cada fila dice cuanta muestra tiene y su margen de error.
    /// </summary>
    public sealed record HandTypeRow(
        string Key,
        string Name,
        int Hands,
        double NetBb,
        double AdjustedBb,
        double MarginPerHand,
        IReadOnlyDictionary<string, (int Hands, double AdjustedBb)> ByGroup)
    {
        public double PerHand => Hands == 0 ? 0 : NetBb / Hands;
        public double AdjustedPerHand => Hands == 0 ? 0 : AdjustedBb / Hands;

        public string PerHandLabel => Hands == 0 ? "-" : AdjustedPerHand.ToString("+0.0;-0.0;0.0", CultureInfo.CurrentCulture);
        public string RawPerHandLabel => Hands == 0 ? "-" : PerHand.ToString("+0.0;-0.0;0.0", CultureInfo.CurrentCulture);
        public string MarginLabel => Hands < 2 ? "-" : "±" + MarginPerHand.ToString("0.0", CultureInfo.CurrentCulture);
        public string TotalLabel => AdjustedBb.ToString("+0;-0;0", CultureInfo.CurrentCulture) + " bb";

        /// <summary>Muestra: muy poca (&lt;30), poca (&lt;100), media (&lt;300) o buena.</summary>
        public string SampleKey => Hands < 30 ? "VeryLow" : Hands < 100 ? "Low" : Hands < 300 ? "Medium" : "Good";

        /// <summary>El resultado sin suerte se aleja de cero mas que su margen de error.</summary>
        public bool IsClear => Hands >= 30 && Math.Abs(AdjustedPerHand) > MarginPerHand;

        public string GroupLabel(string group) =>
            ByGroup.TryGetValue(group, out var value) && value.Hands > 0
                ? $"{(value.AdjustedBb / value.Hands).ToString("+0.0;-0.0;0.0", CultureInfo.CurrentCulture)} ({value.Hands})"
                : "-";
    }

    public static class HandTypeProfit
    {
        private const string Ranks = "23456789TJQKA";

        /// <summary>Grupos de posicion del reporte.</summary>
        public static readonly string[] Groups = { "Late", "Early", "Blinds" };

        /// <summary>Tipos de mano en el orden del reporte (clave de traduccion "HandType.{clave}").</summary>
        public static readonly string[] Keys =
        {
            "PairsHigh", "PairsMid", "PairsLow", "AkAq", "AxSuited", "AjAto", "AxOffWeak", "BroadwaySuited",
            "BroadwayOff", "SuitedConnectors", "SuitedWeak", "KqOffMid", "OffsuitWeak"
        };

        public static string GroupOf(string position) => position.ToUpperInvariant() switch
        {
            "BTN" or "CO" => "Late",
            "SB" or "BB" or "BTN/SB" => "Blinds",
            _ => "Early"
        };

        /// <summary>Tipo de mano de un combo como "AKs", "T9o" o "77".</summary>
        public static string Category(string combo)
        {
            if (combo.Length < 2)
                return "OffsuitWeak";
            var hi = Ranks.IndexOf(char.ToUpperInvariant(combo[0]));
            var lo = Ranks.IndexOf(char.ToUpperInvariant(combo[1]));
            if (hi < lo)
                (hi, lo) = (lo, hi);
            if (hi < 0 || lo < 0)
                return "OffsuitWeak";
            if (hi == lo)
                return hi >= Ranks.IndexOf('T') ? "PairsHigh" : hi >= Ranks.IndexOf('7') ? "PairsMid" : "PairsLow";

            var suited = combo.EndsWith("s", StringComparison.OrdinalIgnoreCase);
            var ace = hi == Ranks.IndexOf('A');
            if (ace && lo >= Ranks.IndexOf('Q'))
                return "AkAq";
            if (ace)
                return suited ? "AxSuited" : lo >= Ranks.IndexOf('T') ? "AjAto" : "AxOffWeak";
            var ten = Ranks.IndexOf('T');
            if (hi >= ten && lo >= ten)
                return suited ? "BroadwaySuited" : "BroadwayOff";
            if (suited)
                return hi - lo <= 2 && lo >= Ranks.IndexOf('4') ? "SuitedConnectors" : "SuitedWeak";
            if (hi >= Ranks.IndexOf('Q') && lo >= Ranks.IndexOf('7'))
                return "KqOffMid";
            return "OffsuitWeak";
        }

        public static IReadOnlyList<HandTypeRow> Build(IEnumerable<HandTypeSample> samples, Func<string, string>? name = null)
        {
            name ??= key => LocalizationManager.Text("HandType." + key);
            var byKey = samples.GroupBy(sample => Category(sample.Combo)).ToDictionary(group => group.Key, group => group.ToList());
            var rows = new List<HandTypeRow>();
            foreach (var key in Keys)
            {
                if (!byKey.TryGetValue(key, out var list) || list.Count == 0)
                    continue;
                var adjusted = list.Select(sample => sample.NetBb - sample.LuckBb).ToList();
                var mean = adjusted.Average();
                var sd = list.Count > 1 ? Math.Sqrt(adjusted.Sum(value => (value - mean) * (value - mean)) / (list.Count - 1)) : 0;
                var groups = Groups.ToDictionary(
                    group => group,
                    group =>
                    {
                        var inGroup = list.Where(sample => GroupOf(sample.Position) == group).ToList();
                        return (inGroup.Count, inGroup.Sum(sample => sample.NetBb - sample.LuckBb));
                    });
                rows.Add(new HandTypeRow(key, name(key), list.Count, list.Sum(sample => sample.NetBb), adjusted.Sum(),
                    sd / Math.Sqrt(Math.Max(1, list.Count)), groups));
            }

            return rows;
        }

        // ------------------------------------------------------------------ suerte en el all-in

        private static readonly Regex UncalledRx = new(@"^Uncalled bet \((?<amount>[\d.,]+)\) returned to (?<name>.+)$", RegexOptions.Compiled);
        private static readonly Regex CollectedRx = new(@"^(?<name>.+?) collected (?<amount>[\d.,]+) from", RegexOptions.Compiled);
        private static readonly Regex RakeRx = new(@"Total pot (?<total>[\d.,]+).*\| Rake (?<rake>[\d.,]+)", RegexOptions.Compiled);

        /// <summary>
        /// Lo que el heroe gano o perdio por suerte en un all-in con cartas por salir (en bb): lo que
        /// cobro menos lo que le tocaba con su equity en ese momento contra las manos mostradas. 0 si
        /// no hubo all-in con showdown antes del river.
        /// </summary>
        public static double AllInLuckBb(TrainerHand hand) => AllInDetail(hand)?.LuckBb ?? 0;

        /// <summary>El all-in con cartas por salir: suerte en bb, la equity del heroe en ese momento, la calle y las manos rivales.</summary>
        public sealed record AllInResult(double LuckBb, double Equity, string Street, IReadOnlyList<IReadOnlyList<int>> Opponents);

        public static AllInResult? AllInDetail(TrainerHand hand)
        {
            if (!hand.HasHero || hand.BigBlind <= 0)
                return null;
            var hero = hand.HeroName;
            var folded = hand.Actions.Where(action => action.Kind == TrainerActionKind.Fold)
                .Select(action => action.Player).ToHashSet(StringComparer.Ordinal);
            if (folded.Any(name => PokerStarsHandHistory.SamePlayer(name, hero)))
                return null;
            var shown = hand.ShownCards
                .Where(pair => !PokerStarsHandHistory.SamePlayer(pair.Key, hero) && !folded.Contains(pair.Key) && pair.Value.Count == 2)
                .ToList();
            if (shown.Count == 0)
                return null;
            var opponents = shown.Select(pair => pair.Value).ToList();
            // El all-in tiene que ser del heroe o de alguno de los que llegan al showdown con el.
            var allIn = hand.Actions.Where(action => action.IsAllIn).Select(action => action.Player).ToList();
            if (!allIn.Any(name => PokerStarsHandHistory.SamePlayer(name, hero) || shown.Any(pair => PokerStarsHandHistory.SamePlayer(pair.Key, name))))
                return null;

            var last = hand.Actions.LastOrDefault(action => action.Kind is TrainerActionKind.Bet or TrainerActionKind.Call or TrainerActionKind.Raise);
            var known = (last?.Street ?? "PREFLOP") switch { "FLOP" => 3, "TURN" => 4, "RIVER" => 5, _ => 0 };
            if (known >= 5 || hand.Board.Count < 5)
                return null;

            var contribution = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var action in hand.Actions)
                contribution[action.Player] = contribution.GetValueOrDefault(action.Player) + action.Contribution;
            double collected = 0, total = 0, rake = 0;
            foreach (var line in hand.Lines)
            {
                if (UncalledRx.Match(line) is { Success: true } uncalled && PokerAmountParser.TryParse(uncalled.Groups["amount"].Value, out var back))
                {
                    var who = uncalled.Groups["name"].Value.Trim();
                    contribution[who] = contribution.GetValueOrDefault(who) - back;
                }
                else if (CollectedRx.Match(line) is { Success: true } got && PokerStarsHandHistory.SamePlayer(got.Groups["name"].Value, hero) &&
                         PokerAmountParser.TryParse(got.Groups["amount"].Value, out var amount))
                {
                    collected += amount;
                }
                else if (RakeRx.Match(line) is { Success: true } rakeLine)
                {
                    PokerAmountParser.TryParse(rakeLine.Groups["total"].Value, out total);
                    PokerAmountParser.TryParse(rakeLine.Groups["rake"].Value, out rake);
                }
            }

            var heroIn = contribution.FirstOrDefault(pair => PokerStarsHandHistory.SamePlayer(pair.Key, hero)).Value;
            if (heroIn <= 0)
                return null;
            var stake = contribution.Values.Sum(value => Math.Min(Math.Max(0, value), heroIn));
            var share = total > 0 ? 1 - rake / total : 1;
            var ranges = opponents.Select(cards => (IReadOnlyList<WeightedCombo>)new[] { new WeightedCombo(cards[0], cards[1], 1) }).ToList();
            var equity = EquityCalculator.VsRanges(hand.HeroCards, hand.Board.Take(known).ToList(), ranges, 4000) / 100.0;
            return new AllInResult((collected - equity * stake * share) / hand.BigBlind, equity, last?.Street ?? "PREFLOP", opponents);
        }
    }
}
