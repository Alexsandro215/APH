using System.Globalization;
using System.Text.RegularExpressions;

namespace Hud.App.Services
{
    public static class PokerStarsHandHistory
    {
        public static readonly Regex HandStartRx =
            new(HandReader.Core.Parsing.PokerStarsParser.HandStartPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);

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

        public static readonly Regex SummarySeatNameRx =
            new(@"^(?:Seat|Asiento(?:\s+n\.?\s*(?:\u00BA|\u00B0|o|ro|&ordm;))?)\s+(?<seat>\d+):\s+(?<rest>.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static readonly Regex SummaryShownRx =
            new(@"^(?:Seat|Asiento(?:\s+n\.?\s*(?:\u00BA|\u00B0|o|ro|&ordm;))?)\s+\d+:\s+(?<name>[^\s:]+).*\[(?<cards>[^\]]+)\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static readonly Regex CollectedRx =
            new(@"^(?<name>.+?):?\s+(?:(?:collected|recoge|cobra|cobr[o\u00F3]|recaud[o\u00F3]|se llev[o\u00F3]|se lleva el bote)\s+[$€£]?(?<amount>[\d,.]+)|recaud[o\u00F3]\s+\([$€£]?(?<amount2>[\d,.]+)\))", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static readonly Regex ReturnedRx =
            new(@"^(?:(?:Uncalled bet|Apuesta no pagada|La apuesta no igualada)\s+\([$€£]?(?<amount>[\d,.]+)\)\s+(?:returned to|devuelta a|se ha devuelto a)\s+(?<name>.+)|(?<amount2>[\d,.]+)\s+devuelt[ao]\s+a\s+(?<name2>.+))$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static readonly Regex RaiseToRx =
            new(@"(?:raises|sube)\s+[$€£]?[\d,.]+\s+(?:to|a|hasta)\s+[$€£]?(?<amount>[\d,.]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static readonly Regex ActionAmountRx =
            new(@":+\s+(?:posts (?:small blind|big blind|small & big blinds|small and big blinds|the ante)|pone ciega peque(?:\u00F1|n)a(?: y grande)?|pone ciega chica(?: y grande)?|pone ciega grande|pone ante|calls|bets|iguala|paga|apuesta)\s+[$€£]?(?<amount>[\d,.]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

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

        /// <summary>
        /// True si la linea del resumen habla de ese jugador exactamente.
        /// line.Contains(nombre) no vale: un villano llamado "Hero2" contaminaba las lecturas
        /// de "Hero". Con los asientos de la mano (BuildSeatRoster) el jugador sale del numero
        /// de asiento; sin ellos, la linea tiene que empezar por el nombre completo, que puede
        /// llevar espacios ("Big Fish").
        /// </summary>
        public static bool SummaryLineIsAbout(string line, string playerName, IReadOnlyDictionary<int, string>? seatRoster = null)
        {
            if (string.IsNullOrWhiteSpace(playerName)) return false;

            var match = SummarySeatNameRx.Match(line);
            if (!match.Success) return false;

            if (seatRoster is not null &&
                int.TryParse(match.Groups["seat"].Value, out var seatNumber) &&
                seatRoster.TryGetValue(seatNumber, out var seatedName))
            {
                return SamePlayer(seatedName, playerName);
            }

            // Sin asiento conocido: nombre completo seguido de fin de linea, espacio, "(" o ":".
            // Asi "Hero" no casa con "Hero2", aunque "Big" si casaria con "Big Fish".
            var name = NormalizeName(playerName);
            var rest = match.Groups["rest"].Value;
            if (!rest.StartsWith(name, StringComparison.Ordinal)) return false;
            if (rest.Length == name.Length) return true;

            var next = rest[name.Length];
            return char.IsWhiteSpace(next) || next == '(' || next == ':';
        }

        /// <summary>
        /// Numero de asiento -> nombre normalizado, leido del bloque de asientos previo a las
        /// cartas propias. Las lineas del resumen tambien empiezan por "Seat N:" y SeatRx se
        /// llevaria el resultado como parte del nombre ("Hero folded before Flop"), por eso se
        /// para en las cartas propias y se queda con la primera aparicion de cada asiento.
        /// </summary>
        public static Dictionary<int, string> BuildSeatRoster(IReadOnlyList<string> hand)
        {
            var roster = new Dictionary<int, string>();
            foreach (var line in hand)
            {
                if (IsHoleCardsHeader(line))
                    break;

                var seat = SeatRx.Match(line);
                if (seat.Success && int.TryParse(seat.Groups["seat"].Value, out var seatNumber))
                    roster.TryAdd(seatNumber, NormalizeName(seat.Groups["name"].Value));
            }

            return roster;
        }

        private static bool IsHoleCardsHeader(string line) =>
            line.StartsWith("*** HOLE CARDS", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("*** CARTAS PROPIAS", StringComparison.OrdinalIgnoreCase);

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
                if (IsHoleCardsHeader(line))
                    break;

                if (buttonSeat == 0)
                {
                    var button = ButtonRx.Match(line);
                    if (button.Success)
                        int.TryParse(button.Groups["seat"].Value, out buttonSeat);
                }

                // Los ausentes ("is sitting out") estan en la lista de asientos pero no juegan la mano:
                // contarlos corria las posiciones (la ciega pequena salia como ciega grande).
                var seat = SeatRx.Match(line);
                if (seat.Success && int.TryParse(seat.Groups["seat"].Value, out var seatNumber) && !IsSittingOut(line))
                    seats[seatNumber] = NormalizeName(seat.Groups["name"].Value);
            }

            if (buttonSeat == 0 || seats.Count == 0)
                return new Dictionary<string, string>(StringComparer.Ordinal);

            var orderedSeats = seats.Keys.OrderBy(seat => seat).ToList();
            var buttonIndex = orderedSeats.IndexOf(buttonSeat);
            if (buttonIndex < 0)
            {
                // Boton muerto (en un asiento vacio o ausente): la ciega pequena es el primer asiento
                // activo despues de el, y el boton efectivo el anterior.
                var next = orderedSeats.FindIndex(s => s > buttonSeat);
                buttonIndex = ((next < 0 ? 0 : next) - 1 + orderedSeats.Count) % orderedSeats.Count;
            }

            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < orderedSeats.Count; i++)
            {
                var offset = (i - buttonIndex + orderedSeats.Count) % orderedSeats.Count;
                result[seats[orderedSeats[i]]] = PositionFromOffset(offset, orderedSeats.Count);
            }

            return result;
        }

        /// <summary>
        /// Asiento de alguien que no juega la mano: "Seat 2: nombre (0 in chips) is sitting out".
        /// Solo se mira lo que va despues de las fichas, para no confundirse con el nombre.
        /// </summary>
        public static bool IsSittingOut(string seatLine)
        {
            var close = seatLine.LastIndexOf(')');
            var rest = close >= 0 ? seatLine[(close + 1)..] : "";
            return rest.Contains("sitting out", StringComparison.OrdinalIgnoreCase) ||
                   rest.Contains("out of hand", StringComparison.OrdinalIgnoreCase) ||
                   rest.Contains("ausente", StringComparison.OrdinalIgnoreCase);
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
                    net -= amount;

                    // El ante y la ciega pequena muerta son dinero muerto: salen del stack pero no
                    // cuentan como apuesta de la calle. Si se sumaran, un "raises X to Y" posterior
                    // descontaria de menos y el jugador apareceria ganando el ante de mas.
                    if (AnteLineRx.IsMatch(line))
                        continue;

                    committedThisStreet += DeadAndLiveBlindRx.IsMatch(line)
                        ? LiveBigBlind(hand, amount)
                        : amount;
                }
            }

            return net;
        }

        private static readonly Regex AnteLineRx =
            new(@":+\s+(?:posts the ante|pone ante)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex DeadAndLiveBlindRx =
            new(@":+\s+(?:posts small (?:&|and) big blinds|pone ciega (?:peque(?:ñ|n)a|chica) y grande)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex HeaderBlindsRx =
            new(@"\(\s*[$€]?(?<sb>\d[\d.,]*)\s*/\s*[$€]?(?<bb>\d[\d.,]*)", RegexOptions.Compiled);

        // "posts small & big blinds" junta la ciega pequena (muerta) y la grande (viva). Solo la
        // grande cuenta como apuesta de la calle. Se toma de la cabecera "(SB/BB)"; si no se puede
        // leer, se asume la estructura habitual SB = BB / 2.
        private static double LiveBigBlind(IReadOnlyList<string> hand, double postedTotal)
        {
            if (hand.Count > 0)
            {
                var blinds = HeaderBlindsRx.Match(hand[0]);
                if (blinds.Success &&
                    PokerAmountParser.TryParse(blinds.Groups["bb"].Value, out var bigBlind) &&
                    bigBlind > 0 && bigBlind <= postedTotal)
                    return bigBlind;
            }

            return postedTotal * 2.0 / 3.0;
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

        /// <summary>
        /// Posicion a partir del desplazamiento respecto al boton (0 = BTN, 1 = SB, 2 = BB...).
        /// El caso general se calcula contando hacia atras desde el boton (el asiento previo al
        /// boton siempre es el CO), no con una tabla fija de 6-max: en una mesa de 9 eso ponia
        /// el CO tres asientos antes de donde esta. Es publico para que las ventanas no
        /// mantengan su propia copia de la tabla.
        /// </summary>
        public static string PositionFromOffset(int offset, int playerCount)
        {
            if (playerCount <= 2) return offset == 0 ? "BTN/SB" : "BB";

            switch (offset)
            {
                case 0: return "BTN";
                case 1: return "SB";
                case 2: return "BB";
            }

            if (offset >= playerCount) return "?";

            // Distancia hasta el boton avanzando: 0 = ultimo en actuar antes del boton.
            var toButton = playerCount - 1 - offset;

            if (toButton == 0) return "CO";
            if (offset == 3) return "UTG";   // primero tras la ciega grande
            if (toButton == 1) return "HJ";

            return "MP";
        }

        /// <summary>
        /// Posicion a partir del orden de actuacion preflop (0 = el primero en hablar).
        /// Heads-up habla primero el BTN/SB (desplazamiento 0); con 3 o mas jugadores, el que
        /// esta 3 asientos despues del boton (el UTG; con 3 jugadores, el propio boton).
        /// </summary>
        public static string PositionFromPreflopIndex(int index, int playerCount)
        {
            if (playerCount <= 2)
                return PositionFromOffset(index, playerCount);

            if (index < 0 || index >= playerCount)
                return "?";

            return PositionFromOffset((index + 3) % playerCount, playerCount);
        }

        public static (string Result, string Combination, string VillainName, string VillainCards, string VillainCombination) ExtractHandSummaryInfo(IReadOnlyList<string> hand, string heroName)
        {
            string combination = "";
            string result = "Foldeo en Preflop";
            string villainName = "";
            string villainCards = "";
            string villainCombination = "";
            
            int lastStreetHeroActive = 0;
            bool heroFolded = false;
            bool showdownReached = false;
            
            int flopIdx = FindStreetIndex(hand, "FLOP");
            int turnIdx = FindStreetIndex(hand, "TURN");
            int riverIdx = FindStreetIndex(hand, "RIVER");
            int summaryIdx = FindStreetIndex(hand, "SUMMARY");
            
            for (int i = 0; i < hand.Count; i++)
            {
                var line = hand[i];
                var actorMatch = ActorRx.Match(line);
                if (actorMatch.Success && SamePlayer(actorMatch.Groups["actor"].Value, heroName))
                {
                    var action = NormalizeAction(actorMatch.Groups["action"].Value);
                    
                    int currentStreet = 0;
                    if (riverIdx >= 0 && i > riverIdx) currentStreet = 3;
                    else if (turnIdx >= 0 && i > turnIdx) currentStreet = 2;
                    else if (flopIdx >= 0 && i > flopIdx) currentStreet = 1;
                    
                    lastStreetHeroActive = currentStreet;
                    if (action == "folds") { heroFolded = true; break; }
                }
                
                if (line.Contains("*** SHOW DOWN") || line.Contains("*** CONFRONT")) showdownReached = true;
            }
            
            bool heroWon = false;
            if (heroFolded)
            {
                string street = lastStreetHeroActive switch { 1 => "Flop", 2 => "Turn", 3 => "River", _ => "Preflop" };
                result = $"Foldeo en {street}";
            }
            else
            {
                foreach (var line in hand)
                {
                    var collected = CollectedRx.Match(line);
                    if (collected.Success && SamePlayer(collected.Groups["name"].Value, heroName)) { heroWon = true; break; }
                }
                
                if (heroWon) result = showdownReached ? "Gano en Showdown" : "Gano sin ver el Showdown";
                else result = showdownReached ? "Perdio en Showdown" : "Perdio sin ver el Showdown";
            }

            // If Hero lost or folded, find the main winner
            if (!heroWon)
            {
                double maxWin = 0;
                foreach (var line in hand)
                {
                    var collected = CollectedRx.Match(line);
                    if (collected.Success && !SamePlayer(collected.Groups["name"].Value, heroName))
                    {
                        var amtText = collected.Groups["amount"].Success ? collected.Groups["amount"].Value : collected.Groups["amount2"].Value;
                        if (PokerAmountParser.TryParse(amtText, out var amt) && amt > maxWin)
                        {
                            maxWin = amt;
                            villainName = NormalizeName(collected.Groups["name"].Value);
                        }
                    }
                }
            }
            
            if (summaryIdx >= 0)
            {
                var seatRoster = BuildSeatRoster(hand);
                foreach (var line in hand.Skip(summaryIdx))
                {
                    if (SummaryLineIsAbout(line, heroName, seatRoster))
                    {
                        var comboMatch = Regex.Match(line, @"with\s+(?<combo>[^,\r\n(]+)", RegexOptions.IgnoreCase);
                        if (comboMatch.Success) combination = comboMatch.Groups["combo"].Value.Trim();
                        var shownMatch = Regex.Match(line, @"\((?<combo>[^)]+)\)", RegexOptions.IgnoreCase);
                        if (shownMatch.Success && string.IsNullOrEmpty(combination)) combination = shownMatch.Groups["combo"].Value.Trim();
                    }

                    if (!string.IsNullOrEmpty(villainName) && SummaryLineIsAbout(line, villainName, seatRoster))
                    {
                        var vCardsMatch = Regex.Match(line, @"\[(?<cards>[^\]]+)\]");
                        if (vCardsMatch.Success) villainCards = vCardsMatch.Groups["cards"].Value.Trim();
                        
                        var vComboMatch = Regex.Match(line, @"with\s+(?<combo>[^,\r\n(]+)", RegexOptions.IgnoreCase);
                        if (vComboMatch.Success) villainCombination = vComboMatch.Groups["combo"].Value.Trim();
                        var vShownMatch = Regex.Match(line, @"\((?<combo>[^)]+)\)", RegexOptions.IgnoreCase);
                        if (vShownMatch.Success && string.IsNullOrEmpty(villainCombination)) villainCombination = vShownMatch.Groups["combo"].Value.Trim();
                    }
                }
            }
            
            return (result, TranslateCombination(combination), villainName, villainCards, TranslateCombination(villainCombination));
        }

        public static (string Flop, string Turn, string River) ExtractBoardStreets(IReadOnlyList<string> hand)
        {
            string flop = "";
            string turn = "";
            string river = "";
            
            foreach (var line in hand)
            {
                if (line.StartsWith("*** FLOP ***", StringComparison.OrdinalIgnoreCase))
                {
                    var match = Regex.Match(line, @"\[(?<cards>[^\]]+)\]");
                    if (match.Success) flop = match.Groups["cards"].Value;
                }
                else if (line.StartsWith("*** TURN ***", StringComparison.OrdinalIgnoreCase))
                {
                    var matches = Regex.Matches(line, @"\[(?<cards>[^\]]+)\]");
                    if (matches.Count > 0) turn = matches[^1].Groups["cards"].Value;
                }
                else if (line.StartsWith("*** RIVER ***", StringComparison.OrdinalIgnoreCase))
                {
                    var matches = Regex.Matches(line, @"\[(?<cards>[^\]]+)\]");
                    if (matches.Count > 0) river = matches[^1].Groups["cards"].Value;
                }
            }
            return (flop, turn, river);
        }

        // El orden importa: "flush" antes que "royal flush" dejaba "royal Color", y la regla de
        // escalera real no llegaba a aplicarse nunca. Siempre de lo mas especifico a lo general.
        private static readonly (string English, string Spanish)[] CombinationNames =
        {
            ("royal flush", "Escalera Real"),
            ("straight flush", "Escalera de color"),
            ("four of a kind", "Poker"),
            ("full house", "Full"),
            ("three of a kind", "Trio"),
            ("two pair", "Doble pareja"),
            ("one pair", "Pareja"),
            ("high card", "Carta alta"),
            ("straight", "Escalera"),
            ("flush", "Color"),
        };

        private static string TranslateCombination(string combo)
        {
            if (string.IsNullOrEmpty(combo)) return "";

            foreach (var (english, spanish) in CombinationNames)
                combo = combo.Replace(english, spanish, StringComparison.OrdinalIgnoreCase);

            return combo;
        }

    }
}

