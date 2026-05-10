using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Hud.App.Views;

namespace Hud.App.Services
{
    public static class HandHistoryTranslator
    {
        public static string TranslateActionLine(
            string line,
            string actor,
            string heroName,
            IReadOnlyDictionary<string, string> positions,
            double bigBlind)
        {
            if (line.StartsWith("***", StringComparison.Ordinal))
                return FormatStreetHeader(line);

            var dealt = PokerStarsHandHistory.DealtRx.Match(line);
            if (dealt.Success)
            {
                var hero = PokerStarsHandHistory.NormalizeName(dealt.Groups["hero"].Value);
                return $"{ActorLabel(hero, heroName, positions)} recibe {FormatCards(dealt.Groups["cards"].Value)}";
            }

            var show = PokerStarsHandHistory.ShowCardsRx.Match(line);
            if (show.Success)
                return $"{ActorLabel(PokerStarsHandHistory.NormalizeName(show.Groups["name"].Value), heroName, positions)} {Services.LocalizationManager.Text("Common.Shows")} {FormatCards(show.Groups["cards"].Value)}";

            var collected = PokerStarsHandHistory.CollectedRx.Match(line);
            if (collected.Success)
            {
                var amount = collected.Groups["amount"].Success ? collected.Groups["amount"].Value : collected.Groups["amount2"].Value;
                return $"{ActorLabel(PokerStarsHandHistory.NormalizeName(collected.Groups["name"].Value), heroName, positions)} cobra {FormatAmountAsBb(amount, bigBlind)}";
            }

            var returned = PokerStarsHandHistory.ReturnedRx.Match(line);
            if (returned.Success)
            {
                var name = returned.Groups["name"].Success ? returned.Groups["name"].Value : returned.Groups["name2"].Value;
                var amount = returned.Groups["amount"].Success ? returned.Groups["amount"].Value : returned.Groups["amount2"].Value;
                return $"{ActorLabel(PokerStarsHandHistory.NormalizeName(name), heroName, positions)} recupera apuesta no pagada {FormatAmountAsBb(amount, bigBlind)}";
            }

            if (!string.IsNullOrWhiteSpace(actor))
            {
                var actionPart = line.Contains(":") ? line.Split(new[] { ':' }, 2)[1].Trim() : line;
                return $"{ActorLabel(actor, heroName, positions)} {TranslatePlayerAction(actionPart, bigBlind)}";
            }

            return line.Trim();
        }

        private static string ActorLabel(string player, string heroName, IReadOnlyDictionary<string, string> positions)
        {
            var position = positions.TryGetValue(player, out var value) ? value : "?";
            var prefix = PokerStarsHandHistory.SamePlayer(player, heroName) ? $"{Services.LocalizationManager.Text("Common.Hero")} " : "";
            return $"{position}-{prefix}{player}:";
        }

        private static string FormatStreetHeader(string line)
        {
            var match = Regex.Match(line, @"^\*\*\*\s+(?<street>FLOP|TURN|RIVER)\s+\*\*\*\s+(?<rest>.+)$", RegexOptions.IgnoreCase);
            if (!match.Success) return line.Replace("*", "").Trim();

            var street = match.Groups["street"].Value.ToUpperInvariant();
            var rest = match.Groups["rest"].Value;
            
            // Extract only the last set of cards in brackets
            var cardMatches = Regex.Matches(rest, @"\[(?<cards>[^\]]+)\]");
            if (cardMatches.Count == 0) return street;

            var lastCards = cardMatches[^1].Groups["cards"].Value;
            return $"{street} [{FormatCards(lastCards)}]";
        }

        private static string FormatCards(string cards) =>
            string.Join(" ", cards.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(FormatCardForDisplay));

        private static string FormatCardForDisplay(string card)
        {
            if (card.Length < 2) return card;
            var rank = card[..^1];
            var suit = char.ToLowerInvariant(card[^1]) switch {
                'h' => "\u2665", 'd' => "\u2666", 'c' => "\u2663", 's' => "\u2660", _ => ""
            };
            return suit.Length == 0 ? card : $"{rank}{suit}";
        }

        public static string TranslatePlayerAction(string action, double bigBlind)
        {
            var normalized = action.Trim();
            
            var raise = Regex.Match(normalized, @"(?:raises|sube)\s+(?<from>\$?[\d,.]+)\s+(?:to|a|hasta)\s+(?<to>\$?[\d,.]+)", RegexOptions.IgnoreCase);
            if (raise.Success) return $"sube {FormatAmountAsBb(raise.Groups["from"].Value, bigBlind)} hasta {FormatAmountAsBb(raise.Groups["to"].Value, bigBlind)}";

            var smallBlind = Regex.Match(normalized, @"^posts small blind\s+\$?(?<amount>[\d,.]+)", RegexOptions.IgnoreCase);
            if (smallBlind.Success) return $"pone ciega chica {FormatAmountAsBb(smallBlind.Groups["amount"].Value, bigBlind)}";

            var bigBlindPost = Regex.Match(normalized, @"^posts big blind\s+\$?(?<amount>[\d,.]+)", RegexOptions.IgnoreCase);
            if (bigBlindPost.Success) return $"pone ciega grande {FormatAmountAsBb(bigBlindPost.Groups["amount"].Value, bigBlind)}";

            var smallAndBigBlind = Regex.Match(normalized, @"^posts (?:small & big blinds|small and big blinds)\s+\$?(?<amount>[\d,.]+)", RegexOptions.IgnoreCase);
            if (smallAndBigBlind.Success) return $"pone ciega chica y grande {FormatAmountAsBb(smallAndBigBlind.Groups["amount"].Value, bigBlind)}";

            var ante = Regex.Match(normalized, @"^posts the ante\s+\$?(?<amount>[\d,.]+)", RegexOptions.IgnoreCase);
            if (ante.Success) return $"pone ante {FormatAmountAsBb(ante.Groups["amount"].Value, bigBlind)}";

            var call = Regex.Match(normalized, @"^(?:calls|iguala|paga)\s+\$?(?<amount>[\d,.]+)(?<rest>.*)$", RegexOptions.IgnoreCase);
            if (call.Success)
                return $"paga {FormatAmountAsBb(call.Groups["amount"].Value, bigBlind)}{TranslateAllInText(call.Groups["rest"].Value)}";

            var bet = Regex.Match(normalized, @"^(?:bets|apuesta)\s+\$?(?<amount>[\d,.]+)(?<rest>.*)$", RegexOptions.IgnoreCase);
            if (bet.Success)
                return $"apuesta {FormatAmountAsBb(bet.Groups["amount"].Value, bigBlind)}{TranslateAllInText(bet.Groups["rest"].Value)}";

            return normalized.Replace("folds", "se retira", StringComparison.OrdinalIgnoreCase)
                             .Replace("checks", "pasa", StringComparison.OrdinalIgnoreCase)
                             .Replace("calls", "paga", StringComparison.OrdinalIgnoreCase)
                             .Replace("bets", "apuesta", StringComparison.OrdinalIgnoreCase)
                             .Replace("is all-in", "va all-in", StringComparison.OrdinalIgnoreCase)
                             .Replace("and is all-in", "y va all-in", StringComparison.OrdinalIgnoreCase);
        }

        private static string TranslateAllInText(string text) =>
            text.Replace("and is all-in", " y va all-in", StringComparison.OrdinalIgnoreCase)
                .Replace("is all-in", " va all-in", StringComparison.OrdinalIgnoreCase);

        private static string FormatAmountAsBb(string raw, double bigBlind)
        {
            if (bigBlind <= 0 || !PokerAmountParser.TryParse(raw, out var amount)) return raw.Trim();
            var bb = amount / bigBlind;
            return $"{bb:0.#} bb";
        }
    }
}
