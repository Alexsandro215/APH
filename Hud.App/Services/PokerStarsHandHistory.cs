using System.Globalization;
using System.Text.RegularExpressions;

namespace Hud.App.Services
{
    public static class PokerStarsHandHistory
    {
        public static readonly Regex HandStartRx =
            new(@"^(?:PokerStars Hand #|Mano #)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static readonly Regex HeaderTimestampRx =
            new(@"-\s*(?<stamp>\d{4}/\d{2}/\d{2}\s+\d{1,2}:\d{2}:\d{2})\s+(?<zone>[A-Z]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static readonly Regex DealtRx =
            new(@"^(?:Dealt to|Repartido a)\s+(?<hero>.+?)\s+\[(?<cards>[^\]]+)\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static readonly Regex SeatRx =
            new(@"^(?:Seat|Asiento(?:\s+n\.?\s*(?:\u00BA|\u00B0|o|ro|&ordm;))?)\s+(?<seat>\d+):\s+(?<name>[^(\r\n]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static readonly Regex ButtonRx =
            new(@"(?:Seat\s+#(?<seat>\d+)\s+is the button|El asiento\s+n\.?\s*(?:\u00BA|\u00B0|o|ro|&ordm;)?\s+(?<seat>\d+)\s+es el bot[o\u00F3]n)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static readonly Regex ActorRx =
            new(@"^(?<actor>.+?):+\s+(?<action>.+)$", RegexOptions.Compiled);

        public static readonly Regex ShowCardsRx =
            new(@"^(?<name>.+?):+\s+(?:shows|muestra)\s+\[(?<cards>[^\]]+)\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static readonly Regex SummaryShownRx =
            new(@"^(?:Seat|Asiento(?:\s+n\.?\s*(?:\u00BA|\u00B0|o|ro|&ordm;))?)\s+\d+:\s+(?<name>[^\s:]+).*\[(?<cards>[^\]]+)\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static readonly Regex CollectedRx =
            new(@"^(?<name>.+?):?\s+(?:(?:collected|recoge|cobra|cobr[o\u00F3]|recaud[o\u00F3]|se llev[o\u00F3]|se lleva el bote)\s+\$?(?<amount>[\d,.]+)|recaud[o\u00F3]\s+\(\$?(?<amount2>[\d,.]+)\))", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static readonly Regex ReturnedRx =
            new(@"^(?:(?:Uncalled bet|Apuesta no pagada|La apuesta no igualada)\s+\(\$?(?<amount>[\d,.]+)\)\s+(?:returned to|devuelta a|se ha devuelto a)\s+(?<name>.+)|(?<amount2>[\d,.]+)\s+devuelt[ao]\s+a\s+(?<name2>.+))$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static readonly Regex RaiseToRx =
            new(@"(?:raises|sube)\s+\$?[\d,.]+\s+(?:to|a|hasta)\s+\$?(?<amount>[\d,.]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static readonly Regex ActionAmountRx =
            new(@":+\s+(?:posts (?:small blind|big blind|the ante)|pone ciega peque(?:\u00F1|n)a(?: y grande)?|pone ciega chica(?: y grande)?|pone ciega grande|pone ante|calls|bets|iguala|paga|apuesta)\s+\$?(?<amount>[\d,.]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static IEnumerable<IReadOnlyList<string>> SplitHands(IEnumerable<string> lines)
        {
            var hand = new List<string>();
            foreach (var line in lines)
            {
                if (HandStartRx.IsMatch(line) && hand.Count > 0)
                {
                    yield return hand.ToList();
                    hand.Clear();
                }

                if (HandStartRx.IsMatch(line) || hand.Count > 0)
                    hand.Add(line);
            }

            if (hand.Count > 0)
                yield return hand;
        }

        public static string NormalizeName(string raw) =>
            raw.Trim().TrimEnd(':').Trim();

        public static bool SamePlayer(string left, string right) =>
            string.Equals(NormalizeName(left), NormalizeName(right), StringComparison.Ordinal);

        public static bool TryGetDealtCards(IReadOnlyList<string> hand, string heroName, out string cards)
        {
            foreach (var line in hand)
            {
                var match = DealtRx.Match(line);
                if (match.Success && SamePlayer(match.Groups["hero"].Value, heroName))
                {
                    cards = match.Groups["cards"].Value.Trim();
                    return true;
                }
            }

            cards = "";
            return false;
        }

        public static HashSet<string> ExtractPlayers(IReadOnlyList<string> hand)
        {
            var players = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in hand)
            {
                var seat = SeatRx.Match(line);
                if (seat.Success)
                    players.Add(NormalizeName(seat.Groups["name"].Value));
            }

            if (players.Count > 0)
                return players;

            foreach (var line in hand)
            {
                var actor = ActorRx.Match(line);
                if (actor.Success)
                    players.Add(NormalizeName(actor.Groups["actor"].Value));
            }

            return players;
        }

        public static bool HandHasPlayerActivity(IReadOnlyList<string> hand, string playerName)
        {
            foreach (var line in hand)
            {
                var actor = ActorRx.Match(line);
                if (actor.Success && SamePlayer(actor.Groups["actor"].Value, playerName))
                    return true;

                var collected = CollectedRx.Match(line);
                if (collected.Success && SamePlayer(collected.Groups["name"].Value, playerName))
                    return true;

                var returned = ReturnedRx.Match(line);
                if (returned.Success &&
                    (SamePlayer(returned.Groups["name"].Value, playerName) || SamePlayer(returned.Groups["name2"].Value, playerName)))
                    return true;
            }

            return false;
        }

        public static DateTime? ExtractTimestamp(IReadOnlyList<string> hand)
        {
            foreach (var line in hand)
            {
                var match = HeaderTimestampRx.Match(line);
                if (!match.Success)
                    continue;

                if (DateTime.TryParseExact(
                    match.Groups["stamp"].Value,
                    "yyyy/MM/dd H:mm:ss",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var timestamp))
                {
                    return timestamp;
                }
            }

            return null;
        }

        public static Dictionary<string, string> BuildPositionMap(IReadOnlyList<string> hand)
        {
            var buttonSeat = 0;
            var seats = new Dictionary<int, string>();

            foreach (var line in hand)
            {
                if (buttonSeat == 0)
                {
                    var button = ButtonRx.Match(line);
                    if (button.Success)
                        int.TryParse(button.Groups["seat"].Value, out buttonSeat);
                }

                var seat = SeatRx.Match(line);
                if (seat.Success && int.TryParse(seat.Groups["seat"].Value, out var seatNumber))
                    seats[seatNumber] = NormalizeName(seat.Groups["name"].Value);
            }

            if (buttonSeat == 0 || seats.Count == 0)
                return new Dictionary<string, string>(StringComparer.Ordinal);

            var orderedSeats = seats.Keys.OrderBy(seat => seat).ToList();
            var buttonIndex = orderedSeats.IndexOf(buttonSeat);
            if (buttonIndex < 0)
                return new Dictionary<string, string>(StringComparer.Ordinal);

            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < orderedSeats.Count; i++)
            {
                var offset = (i - buttonIndex + orderedSeats.Count) % orderedSeats.Count;
                result[seats[orderedSeats[i]]] = PositionFromOffset(offset, orderedSeats.Count);
            }

            return result;
        }

        public static string NormalizeAction(string action)
        {
            var value = action.Trim().ToLowerInvariant();
            if (value.StartsWith("posts", StringComparison.Ordinal) ||
                value.StartsWith("pone ciega", StringComparison.Ordinal) ||
                value.StartsWith("pone ante", StringComparison.Ordinal))
                return "posts";

            return value switch
            {
                var x when x.StartsWith("folds") || x.StartsWith("no va") || x.StartsWith("se retira") => "folds",
                var x when x.StartsWith("checks") || x.StartsWith("pasa") => "checks",
                var x when x.StartsWith("calls") || x.StartsWith("iguala") || x.StartsWith("paga") => "calls",
                var x when x.StartsWith("bets") || x.StartsWith("apuesta") => "bets",
                var x when x.StartsWith("raises") || x.StartsWith("sube") => "raises",
                var x when x.Contains("all-in") => "all-in",
                var x when x.StartsWith("mucks") || x.StartsWith("tira") => "mucks",
                _ => value
            };
        }

        public static double EstimateNetForPlayer(IReadOnlyList<string> hand, string playerName)
        {
            var net = 0.0;
            var committedThisStreet = 0.0;

            foreach (var line in hand)
            {
                if (line.StartsWith("*** FLOP", StringComparison.Ordinal) ||
                    line.StartsWith("*** TURN", StringComparison.Ordinal) ||
                    line.StartsWith("*** RIVER", StringComparison.Ordinal) ||
                    line.StartsWith("*** SHOW", StringComparison.Ordinal) ||
                    line.StartsWith("*** CONFRONT", StringComparison.Ordinal))
                {
                    committedThisStreet = 0;
                }

                var returned = ReturnedRx.Match(line);
                if (returned.Success)
                {
                    var name = returned.Groups["name"].Success ? returned.Groups["name"].Value : returned.Groups["name2"].Value;
                    var amountText = returned.Groups["amount"].Success ? returned.Groups["amount"].Value : returned.Groups["amount2"].Value;
                    if (SamePlayer(name, playerName) && PokerAmountParser.TryParse(amountText, out var returnedAmount))
                    {
                        net += returnedAmount;
                        continue;
                    }
                }

                var collected = CollectedRx.Match(line);
                if (collected.Success)
                {
                    var amountText = collected.Groups["amount"].Success ? collected.Groups["amount"].Value : collected.Groups["amount2"].Value;
                    if (SamePlayer(collected.Groups["name"].Value, playerName) && PokerAmountParser.TryParse(amountText, out var collectedAmount))
                    {
                        net += collectedAmount;
                        continue;
                    }
                }

                var actor = ActorRx.Match(line);
                if (!actor.Success || !SamePlayer(actor.Groups["actor"].Value, playerName))
                    continue;

                var raise = RaiseToRx.Match(line);
                if (raise.Success && PokerAmountParser.TryParse(raise.Groups["amount"].Value, out var raiseTo))
                {
                    var delta = Math.Max(0, raiseTo - committedThisStreet);
                    committedThisStreet += delta;
                    net -= delta;
                    continue;
                }

                var action = ActionAmountRx.Match(line);
                if (action.Success && PokerAmountParser.TryParse(action.Groups["amount"].Value, out var amount))
                {
                    committedThisStreet += amount;
                    net -= amount;
                }
            }

            return net;
        }

        public static int FindStreetIndex(IReadOnlyList<string> hand, string street, int startAt = 0)
        {
            for (var i = startAt; i < hand.Count; i++)
            {
                var line = hand[i];
                if ((street == "SUMMARY" || street == "SHOW") &&
                    (line.StartsWith("*** SUMMARY", StringComparison.OrdinalIgnoreCase) ||
                     line.StartsWith("*** RESUMEN", StringComparison.OrdinalIgnoreCase)))
                    return i;
                if (line.StartsWith($"*** {street} ", StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            return -1;
        }

        private static string PositionFromOffset(int offset, int playerCount) =>
            playerCount switch
            {
                <= 2 => offset == 0 ? "BTN/SB" : "BB",
                3 => offset switch { 0 => "BTN", 1 => "SB", 2 => "BB", _ => "?" },
                4 => offset switch { 0 => "BTN", 1 => "SB", 2 => "BB", 3 => "CO", _ => "?" },
                5 => offset switch { 0 => "BTN", 1 => "SB", 2 => "BB", 3 => "UTG", 4 => "CO", _ => "?" },
                _ => offset switch { 0 => "BTN", 1 => "SB", 2 => "BB", 3 => "UTG", 4 => "HJ", 5 => "CO", _ => "MP" }
            };
    }
}

