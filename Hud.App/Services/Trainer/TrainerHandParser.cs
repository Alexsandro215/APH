using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Hud.App.Services.Trainer
{
    public enum TrainerMoneyKind
    {
        Unknown,
        PlayMoney,
        RealMoney,
        Tournament
    }

    public enum TrainerActionKind
    {
        Post,
        Fold,
        Check,
        Call,
        Bet,
        Raise
    }

    public sealed record TrainerSeat(int Seat, string Name, double Stack, string Position, string ChartPosition, bool IsActive);

    /// <summary>
    /// Una accion de la mano con el estado justo ANTES de ejecutarse: es lo que el jugador veia
    /// al decidir (bote, cuanto tenia que pagar, su stack). Importes en fichas, no en bb.
    /// </summary>
    public sealed record TrainerAction(
        int Index,
        string Street,
        string Player,
        TrainerActionKind Kind,
        double Contribution,
        double RaiseTo,
        bool IsAllIn,
        double PotBefore,
        double ToCall,
        double CurrentBetBefore,
        double StackBefore,
        IReadOnlyList<int> Board,
        int PreflopRaisesBefore,
        double EffectiveToCall = 0,
        double EffectivePot = 0,
        bool IsIncompleteRaise = false)
    {
        /// <summary>Puede pagar menos que la apuesta (all-in por menos): lo que paga de verdad.</summary>
        public bool CallIsCapped => EffectiveToCall + 0.0001 < ToCall;
    }

    public sealed class TrainerHand
    {
        public string HandId { get; init; } = "";
        public TrainerMoneyKind Money { get; init; }
        public bool IsZoom { get; init; }

        /// <summary>Hold'em sin limite (las tablas y reglas del entrenador son de NLHE).</summary>
        public bool IsNoLimitHoldem { get; init; }

        public int MaxSeats { get; init; }
        public double BigBlind { get; init; }
        public string HeroName { get; init; } = "";
        public IReadOnlyList<int> HeroCards { get; init; } = Array.Empty<int>();
        public int ButtonSeat { get; init; }
        public IReadOnlyList<TrainerSeat> Seats { get; init; } = Array.Empty<TrainerSeat>();
        public IReadOnlyList<int> Board { get; init; } = Array.Empty<int>();
        public IReadOnlyList<TrainerAction> Actions { get; init; } = Array.Empty<TrainerAction>();
        public IReadOnlyDictionary<string, IReadOnlyList<int>> ShownCards { get; init; } =
            new Dictionary<string, IReadOnlyList<int>>();
        public IReadOnlyList<string> Lines { get; init; } = Array.Empty<string>();

        public IReadOnlyList<TrainerSeat> ActiveSeats => Seats.Where(seat => seat.IsActive).ToList();

        public TrainerSeat? SeatOf(string player) =>
            Seats.FirstOrDefault(seat => PokerStarsHandHistory.SamePlayer(seat.Name, player));

        public bool HasHero => HeroCards.Count == 2 && !string.IsNullOrWhiteSpace(HeroName);
    }

    public static class TrainerHandParser
    {
        // "PokerStars Hand #", "PokerStars Zoom Hand #", "PokerStars Home Game Hand #", "Mano #"...
        public static readonly Regex HandStartRx =
            new(HandReader.Core.Parsing.PokerStarsParser.HandStartPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex HandIdRx = new(@"(?<id>\d{6,})", RegexOptions.Compiled);
        private static readonly Regex MaxSeatsRx = new(@"(?<max>\d+)-max", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex BracketRx = new(@"\[(?<cards>[^\]]+)\]", RegexOptions.Compiled);
        private static readonly Regex SeatStackRx = new(@"\((?:[$\u20AC\u00A3]\s*)?(?<stack>[\d,.]+)\s+(?:in chips|en fichas)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex BlindsRx =
            new(@"\((?<sb>" + PokerAmountParser.BlindAmountPattern + @")\s*/\s*(?<bb>" + PokerAmountParser.BlindAmountPattern + @")(?:\s+[A-Z]{3})?\)", RegexOptions.Compiled);

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

        /// <summary>Una mano esta completa cuando ya tiene el bloque de resumen con sus asientos.</summary>
        public static bool IsComplete(IReadOnlyList<string> hand)
        {
            var summary = PokerStarsHandHistory.FindStreetIndex(hand, "SUMMARY");
            return summary >= 0 && hand.Skip(summary + 1).Any(line => PokerStarsHandHistory.SummarySeatNameRx.IsMatch(line));
        }

        public static TrainerMoneyKind DetectMoney(IReadOnlyList<string> hand)
        {
            var header = hand.Count > 0 ? hand[0] : "";
            var table = hand.Count > 1 ? hand[1] : "";
            var both = header + " " + table;

            if (header.Contains("Tournament", StringComparison.OrdinalIgnoreCase) ||
                header.Contains("Torneo", StringComparison.OrdinalIgnoreCase))
                return TrainerMoneyKind.Tournament;

            // Las mesas de dinero real llevan la moneda en las ciegas: ($0.01/$0.02 USD), (0,01/0,02 EUR).
            // Se mira la cabecera entera y no solo lo que case BlindsRx: un formato raro de importe
            // no debe dejar pasar una mano de dinero real como "desconocida".
            if (CurrencyRx.IsMatch(header))
                return TrainerMoneyKind.RealMoney;

            if (both.Contains("Play Money", StringComparison.OrdinalIgnoreCase) ||
                both.Contains("ficticio", StringComparison.OrdinalIgnoreCase))
                return TrainerMoneyKind.PlayMoney;

            return BlindsRx.IsMatch(header) ? TrainerMoneyKind.PlayMoney : TrainerMoneyKind.Unknown;
        }

        private static readonly Regex CurrencyRx =
            new(@"[$\u20AC\u00A3\u20B9]|\b(?:USD|EUR|GBP|CAD|INR)\b", RegexOptions.Compiled);

        public static TrainerHand? Parse(IReadOnlyList<string> hand, string? heroHint = null)
        {
            if (hand.Count < 3)
                return null;

            var header = hand[0];
            var idMatch = HandIdRx.Match(header);
            var bigBlind = 0.0;
            var blinds = BlindsRx.Match(header);
            if (blinds.Success)
                PokerAmountParser.TryParse(blinds.Groups["bb"].Value, out bigBlind);

            var maxSeatsMatch = hand.Count > 1 ? MaxSeatsRx.Match(hand[1]) : Match.Empty;

            // Asientos y boton: solo el bloque previo a las cartas propias (el resumen repite "Seat N:").
            var buttonSeat = 0;
            var seatList = new List<(int Seat, string Name, double Stack)>();
            foreach (var line in hand)
            {
                if (IsHoleCardsHeader(line))
                    break;

                if (buttonSeat == 0)
                {
                    var button = PokerStarsHandHistory.ButtonRx.Match(line);
                    if (button.Success)
                        int.TryParse(button.Groups["seat"].Value, out buttonSeat);
                }

                var seat = PokerStarsHandHistory.SeatRx.Match(line);
                if (seat.Success && int.TryParse(seat.Groups["seat"].Value, out var seatNumber) &&
                    seatList.All(item => item.Seat != seatNumber))
                {
                    var stackMatch = SeatStackRx.Match(line);
                    var stack = stackMatch.Success && PokerAmountParser.TryParse(stackMatch.Groups["stack"].Value, out var parsed) ? parsed : 0;
                    seatList.Add((seatNumber, PokerStarsHandHistory.NormalizeName(seat.Groups["name"].Value), stack));
                }
            }

            var heroName = heroHint ?? "";
            var heroCards = new List<int>();
            foreach (var line in hand)
            {
                var dealt = PokerStarsHandHistory.DealtRx.Match(line);
                if (!dealt.Success)
                    continue;

                var name = PokerStarsHandHistory.NormalizeName(dealt.Groups["hero"].Value);
                if (!string.IsNullOrWhiteSpace(heroHint) && !PokerStarsHandHistory.SamePlayer(name, heroHint))
                    continue;

                heroName = name;
                heroCards = TrainerCards.ParseMany(dealt.Groups["cards"].Value);
                break;
            }

            var seatNames = seatList.Select(seat => seat.Name).ToList();
            var activeNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in hand)
            {
                var actor = PokerStarsHandHistory.ActorRx.Match(line);
                if (!actor.Success)
                    continue;

                var name = PokerStarsHandHistory.NormalizeName(actor.Groups["actor"].Value);
                if (seatNames.Contains(name) && ClassifyAction(actor.Groups["action"].Value) is not null)
                    activeNames.Add(name);
            }

            var seats = BuildSeats(seatList, activeNames, buttonSeat);
            var (actions, board, shown) = ReplayActions(hand, seats, bigBlind > 0 ? bigBlind : 1);

            return new TrainerHand
            {
                HandId = idMatch.Success ? idMatch.Groups["id"].Value : "",
                Money = DetectMoney(hand),
                IsZoom = header.Contains("Zoom", StringComparison.OrdinalIgnoreCase),
                IsNoLimitHoldem = header.Contains("Hold'em", StringComparison.OrdinalIgnoreCase) &&
                                  (header.Contains("No Limit", StringComparison.OrdinalIgnoreCase) ||
                                   header.Contains("Sin l", StringComparison.OrdinalIgnoreCase)),
                MaxSeats = maxSeatsMatch.Success && int.TryParse(maxSeatsMatch.Groups["max"].Value, out var max) ? max : 0,
                BigBlind = bigBlind > 0 ? bigBlind : 1,
                HeroName = heroName,
                HeroCards = heroCards.Count == 2 ? heroCards : Array.Empty<int>(),
                ButtonSeat = buttonSeat,
                Seats = seats,
                Board = board,
                Actions = actions,
                ShownCards = shown,
                Lines = hand
            };
        }

        /// <summary>
        /// Posicion de tabla 6-max equivalente: con menos jugadores se cuenta hacia atras desde el
        /// boton, que es lo que importa para los rangos (el primero en hablar a 5 manos abre como HJ).
        /// </summary>
        public static string ChartPosition(int offsetFromButton, int playerCount)
        {
            if (playerCount <= 2)
                return offsetFromButton == 0 ? "SB" : "BB";

            switch (offsetFromButton)
            {
                case 0: return "BTN";
                case 1: return "SB";
                case 2: return "BB";
            }

            var toButton = playerCount - 1 - offsetFromButton;
            return toButton switch
            {
                0 => "CO",
                1 => "HJ",
                _ => "LJ"
            };
        }

        /// <summary>Orden de actuacion preflop en 6-max (para saber quien habla antes que quien).</summary>
        public static int PreflopOrder(string chartPosition) => chartPosition switch
        {
            "LJ" => 0,
            "HJ" => 1,
            "CO" => 2,
            "BTN" => 3,
            "SB" => 4,
            "BB" => 5,
            _ => 6
        };

        public static TrainerActionKind? ClassifyAction(string actionText) =>
            PokerStarsHandHistory.NormalizeAction(actionText) switch
            {
                "posts" => TrainerActionKind.Post,
                "folds" => TrainerActionKind.Fold,
                "checks" => TrainerActionKind.Check,
                "calls" => TrainerActionKind.Call,
                "bets" => TrainerActionKind.Bet,
                "raises" => TrainerActionKind.Raise,
                _ => null
            };

        private static List<TrainerSeat> BuildSeats(
            List<(int Seat, string Name, double Stack)> seatList,
            HashSet<string> activeNames,
            int buttonSeat)
        {
            var active = seatList.Where(seat => activeNames.Contains(seat.Name)).OrderBy(seat => seat.Seat).ToList();
            var positions = new Dictionary<int, (string Label, string Chart)>();

            if (active.Count > 0)
            {
                var buttonIndex = active.FindIndex(seat => seat.Seat == buttonSeat);
                if (buttonIndex < 0)
                {
                    // Boton muerto: hace de boton el ultimo asiento activo anterior al boton.
                    buttonIndex = active.FindLastIndex(seat => seat.Seat < buttonSeat);
                    if (buttonIndex < 0) buttonIndex = active.Count - 1;
                }

                for (var i = 0; i < active.Count; i++)
                {
                    var offset = (i - buttonIndex + active.Count) % active.Count;
                    positions[active[i].Seat] = (
                        PokerStarsHandHistory.PositionFromOffset(offset, active.Count),
                        ChartPosition(offset, active.Count));
                }
            }

            return seatList
                .OrderBy(seat => seat.Seat)
                .Select(seat => positions.TryGetValue(seat.Seat, out var pos)
                    ? new TrainerSeat(seat.Seat, seat.Name, seat.Stack, pos.Label, pos.Chart, true)
                    : new TrainerSeat(seat.Seat, seat.Name, seat.Stack, "-", "-", false))
                .ToList();
        }

        private static (List<TrainerAction> Actions, List<int> Board, Dictionary<string, IReadOnlyList<int>> Shown) ReplayActions(
            IReadOnlyList<string> hand,
            IReadOnlyList<TrainerSeat> seats,
            double bigBlind)
        {
            // Ultima subida completa de la calle: una subida all-in menor no reabre la accion.
            var lastFullRaise = bigBlind;
            var actions = new List<TrainerAction>();
            var board = new List<int>();
            var shown = new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal);
            var stacks = seats.ToDictionary(seat => seat.Name, seat => seat.Stack, StringComparer.Ordinal);
            var committed = new Dictionary<string, double>(StringComparer.Ordinal);
            var street = "PREFLOP";
            var pot = 0.0;
            var currentBet = 0.0;
            var preflopRaises = 0;

            foreach (var line in hand)
            {
                if (IsHoleCardsHeader(line))
                    continue;

                var newStreet = StreetFromHeader(line);
                if (newStreet is not null)
                {
                    if (newStreet is "SUMMARY")
                        break;

                    if (newStreet is "FLOP" or "TURN" or "RIVER")
                    {
                        street = newStreet;
                        committed.Clear();
                        currentBet = 0;
                        lastFullRaise = bigBlind;
                        foreach (Match block in BracketRx.Matches(line))
                        {
                            foreach (var card in TrainerCards.ParseMany(block.Groups["cards"].Value))
                            {
                                if (!board.Contains(card))
                                    board.Add(card);
                            }
                        }
                    }

                    continue;
                }

                var returned = PokerStarsHandHistory.ReturnedRx.Match(line);
                if (returned.Success)
                {
                    var amountText = returned.Groups["amount"].Success ? returned.Groups["amount"].Value : returned.Groups["amount2"].Value;
                    if (PokerAmountParser.TryParse(amountText, out var back))
                        pot = Math.Max(0, pot - back);
                    continue;
                }

                var shows = PokerStarsHandHistory.ShowCardsRx.Match(line);
                if (shows.Success)
                {
                    var cards = TrainerCards.ParseMany(shows.Groups["cards"].Value);
                    if (cards.Count == 2)
                        shown[PokerStarsHandHistory.NormalizeName(shows.Groups["name"].Value)] = cards;
                    continue;
                }

                var actor = PokerStarsHandHistory.ActorRx.Match(line);
                if (!actor.Success)
                    continue;

                var player = PokerStarsHandHistory.NormalizeName(actor.Groups["actor"].Value);
                if (!stacks.ContainsKey(player))
                    continue;

                var actionText = actor.Groups["action"].Value;
                var kind = ClassifyAction(actionText);
                if (kind is null)
                    continue;

                committed.TryGetValue(player, out var playerCommitted);
                var stackBefore = stacks[player];
                var toCall = Math.Max(0, currentBet - playerCommitted);
                var potBefore = pot;
                var betBefore = currentBet;
                var contribution = 0.0;
                var raiseTo = 0.0;
                var isAllIn = actionText.Contains("all-in", StringComparison.OrdinalIgnoreCase);

                // Precio real: con menos fichas que la apuesta solo se paga lo que queda, y del bote
                // solo se opta a lo que cada rival puede igualar (el resto se le devuelve).
                var cap = stackBefore > 0 ? playerCommitted + stackBefore : double.MaxValue;
                var effectiveToCall = Math.Max(0, Math.Min(currentBet, cap) - playerCommitted);
                var excess = committed.Where(item => item.Key != player).Sum(item => Math.Max(0, item.Value - cap));
                var effectivePot = Math.Max(0, pot - excess);
                var incompleteRaise = false;

                if (kind == TrainerActionKind.Raise)
                {
                    var raise = PokerStarsHandHistory.RaiseToRx.Match(line);
                    if (raise.Success && PokerAmountParser.TryParse(raise.Groups["amount"].Value, out raiseTo))
                    {
                        contribution = Math.Max(0, raiseTo - playerCommitted);
                        committed[player] = raiseTo;
                        var increment = raiseTo - currentBet;
                        if (isAllIn && increment + 0.0001 < lastFullRaise)
                            incompleteRaise = true;
                        else if (increment > 0)
                            lastFullRaise = increment;
                        currentBet = Math.Max(currentBet, raiseTo);
                    }
                }
                else
                {
                    var amount = PokerStarsHandHistory.ActionAmountRx.Match(line);
                    if (amount.Success && PokerAmountParser.TryParse(amount.Groups["amount"].Value, out var value))
                    {
                        contribution = value;
                        // La ante va al bote pero no cuenta como apuesta de la calle.
                        if (!actionText.Contains("ante", StringComparison.OrdinalIgnoreCase))
                        {
                            committed[player] = playerCommitted + value;
                            if (kind is TrainerActionKind.Post or TrainerActionKind.Bet)
                                currentBet = Math.Max(currentBet, committed[player]);
                            if (kind == TrainerActionKind.Bet)
                                lastFullRaise = Math.Max(bigBlind, value);
                        }
                    }
                }

                pot += contribution;
                stacks[player] = Math.Max(0, stackBefore - contribution);

                actions.Add(new TrainerAction(
                    actions.Count,
                    street,
                    player,
                    kind.Value,
                    contribution,
                    raiseTo,
                    isAllIn,
                    potBefore,
                    toCall,
                    betBefore,
                    stackBefore,
                    board.ToList(),
                    preflopRaises,
                    effectiveToCall,
                    effectivePot,
                    incompleteRaise));

                // Una subida all-in incompleta no cuenta como nueva subida para elegir el escenario.
                if (street == "PREFLOP" && kind == TrainerActionKind.Raise && !incompleteRaise)
                    preflopRaises++;
            }

            return (actions, board, shown);
        }

        private static string? StreetFromHeader(string line)
        {
            if (!line.StartsWith("***", StringComparison.Ordinal))
                return null;

            if (line.StartsWith("*** FLOP", StringComparison.OrdinalIgnoreCase)) return "FLOP";
            if (line.StartsWith("*** TURN", StringComparison.OrdinalIgnoreCase)) return "TURN";
            if (line.StartsWith("*** RIVER", StringComparison.OrdinalIgnoreCase)) return "RIVER";
            if (line.StartsWith("*** SUMMARY", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("*** RESUMEN", StringComparison.OrdinalIgnoreCase)) return "SUMMARY";
            return "OTHER";
        }

        private static bool IsHoleCardsHeader(string line) =>
            line.StartsWith("*** HOLE CARDS", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("*** CARTAS", StringComparison.OrdinalIgnoreCase);
    }
}
