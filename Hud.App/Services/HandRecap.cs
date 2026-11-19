using System.Globalization;
using System.Text.RegularExpressions;
using Hud.App.Services.Trainer;

namespace Hud.App.Services
{
    /// <summary>Cartas que se vieron de un jugador (enseno en el showdown, o las del heroe).</summary>
    public sealed record RecapShown(string Name, IReadOnlyList<int> Cards, string Hand, bool IsHero);

    /// <summary>
    /// Repaso de una mano terminada para verlo unos segundos encima de la mesa: el preflop resumido
    /// ("A sube a 3 bb · B paga · 3 se retiran"), el board, las cartas que se vieron con su jugada y
    /// quien gano cuanto (en bb).
    /// </summary>
    public sealed record HandRecap(
        string HandId,
        string Preflop,
        IReadOnlyList<int> Board,
        IReadOnlyList<RecapShown> Shown,
        IReadOnlyList<(string Name, double Bb)> Winners,
        bool Showdown)
    {
        private static readonly Regex IdRx = new(@"#(?<id>\d+)", RegexOptions.Compiled);
        private static readonly Regex BlindsRx = new(@"\((?:[$€£])?(?<sb>[\d.,]+)/(?:[$€£])?(?<bb>[\d.,]+)", RegexOptions.Compiled);
        private static readonly Regex StreetRx = new(@"^\*\*\* (?<street>HOLE CARDS|CARTAS PROPIAS|FLOP|TURN|RIVER|SHOW ?DOWN|CONFRONTACI\S*N|SUMMARY|RESUMEN)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex BoardCardsRx = new(@"\[(?<cards>[^\]]+)\]", RegexOptions.Compiled);
        private static readonly Regex ActionRx = new(@"^(?<name>.+?): (?<verb>raises|bets|calls|checks|folds|sube|apuesta|iguala|paga|pasa|se retira|no va)\b(?<rest>.*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex RaiseToRx = new(@"(?:to|a)\s+(?:[$€£])?(?<to>[\d.,]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex AmountRx = new(@"(?:[$€£])?(?<amount>[\d.,]+)", RegexOptions.Compiled);
        private static readonly Regex ShowsRx = new(@"^(?<name>.+?): (?:shows|muestra) \[(?<cards>[^\]]+)\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex CollectedRx = new(@"^(?<name>.+?) (?:collected|se lleva|cobra) (?:[$€£])?(?<amount>[\d.,]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex DealtRx = new(@"^(?:Dealt to|Repartido a) (?<name>.+?) \[(?<cards>[^\]]+)\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>Repaso de las lineas de una mano (hasta el resumen), o null si no se entiende.</summary>
        public static HandRecap? Build(IReadOnlyList<string> lines, string? heroName)
        {
            if (lines.Count < 3)
                return null;

            var id = IdRx.Match(lines[0]).Groups["id"].Value;
            var blinds = BlindsRx.Match(lines[0]);
            var bb = blinds.Success && PokerAmountParser.TryParse(blinds.Groups["bb"].Value, out var big) && big > 0 ? big : 1;
            bool IsHero(string name) => heroName is not null && PokerStarsHandHistory.SamePlayer(name, heroName);
            string Who(string name) => IsHero(name) ? LocalizationManager.Text("Recap.You") : name.Trim();

            var street = "";
            var board = new List<int>();
            var preflop = new List<string>();
            var folds = 0;
            var shown = new List<RecapShown>();
            var won = new Dictionary<string, double>(StringComparer.Ordinal);
            List<int>? heroCards = null;
            var showdown = false;

            foreach (var raw in lines)
            {
                var line = raw.TrimEnd();
                var header = StreetRx.Match(line);
                if (header.Success)
                {
                    street = header.Groups["street"].Value.ToUpperInvariant();
                    if (street.StartsWith("SHOW", StringComparison.Ordinal) || street.StartsWith("CONFRONTACI", StringComparison.Ordinal))
                        showdown = true;
                    if (street is "FLOP" or "TURN" or "RIVER")
                    {
                        foreach (Match group in BoardCardsRx.Matches(line))
                            foreach (var token in group.Groups["cards"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                                if (TrainerCards.TryParse(token, out var card) && !board.Contains(card))
                                    board.Add(card);
                    }
                    continue;
                }

                var dealt = DealtRx.Match(line);
                if (dealt.Success && IsHero(dealt.Groups["name"].Value))
                {
                    heroCards = Cards(dealt.Groups["cards"].Value);
                    continue;
                }

                var shows = ShowsRx.Match(line);
                if (shows.Success)
                {
                    var name = shows.Groups["name"].Value;
                    if (shown.All(s => s.Name != Who(name)))
                        shown.Add(new RecapShown(Who(name), Cards(shows.Groups["cards"].Value), "", IsHero(name)));
                    continue;
                }

                var collected = CollectedRx.Match(line);
                if (collected.Success && PokerAmountParser.TryParse(collected.Groups["amount"].Value, out var amount))
                {
                    var name = Who(collected.Groups["name"].Value);
                    won[name] = won.GetValueOrDefault(name) + amount / bb;
                    continue;
                }

                if (street is "HOLE CARDS" or "CARTAS PROPIAS")
                {
                    var action = ActionRx.Match(line);
                    if (!action.Success)
                        continue;
                    var verb = action.Groups["verb"].Value.ToLowerInvariant();
                    var name = Who(action.Groups["name"].Value);
                    // Con el heroe, en segunda persona ("Tú pagas").
                    var you = IsHero(action.Groups["name"].Value) ? "You" : "";
                    var allIn = action.Groups["rest"].Value.Contains("all-in", StringComparison.OrdinalIgnoreCase);
                    switch (verb)
                    {
                        case "folds" or "se retira" or "no va":
                            folds++;
                            break;
                        case "checks" or "pasa":
                            preflop.Add(Format("Recap.Checks" + you, name));
                            break;
                        case "calls" or "iguala" or "paga":
                            preflop.Add(Format((allIn ? "Recap.CallsAllIn" : "Recap.Calls") + you, name));
                            break;
                        default:
                            var to = RaiseToRx.Match(action.Groups["rest"].Value);
                            var bet = AmountRx.Match(action.Groups["rest"].Value);
                            var value = to.Success && PokerAmountParser.TryParse(to.Groups["to"].Value, out var raiseTo) ? raiseTo
                                : bet.Success && PokerAmountParser.TryParse(bet.Groups["amount"].Value, out var betAmount) ? betAmount : 0;
                            preflop.Add(allIn
                                ? Format("Recap.RaisesAllIn" + you, name, value / bb)
                                : Format("Recap.Raises" + you, name, value / bb));
                            break;
                    }
                }
            }

            // Las cartas del heroe siempre (si no las enseno), y la jugada de cada uno con el board completo.
            if (heroCards is { Count: 2 } && shown.All(s => !s.IsHero))
                shown.Insert(0, new RecapShown(LocalizationManager.Text("Recap.You"), heroCards, "", true));
            if (board.Count >= 3)
                shown = shown.Select(s => s with { Hand = HandName(s.Cards, board) }).ToList();

            var preflopText = preflop.Count == 0 && folds == 0
                ? ""
                : string.Join(" · ", preflop) + (folds > 0 ? (preflop.Count > 0 ? " · " : "") + Format(folds == 1 ? "Recap.OneFolds" : "Recap.Folds", folds) : "");
            var winners = won.Where(w => w.Value > 0.001).Select(w => (w.Key, w.Value)).OrderByDescending(w => w.Value).ToList();
            return new HandRecap(id, preflopText, board, shown, winners, showdown);
        }

        private static List<int> Cards(string text) =>
            text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(token => TrainerCards.TryParse(token, out var card) ? card : -1)
                .Where(card => card >= 0)
                .ToList();

        private static string HandName(IReadOnlyList<int> cards, IReadOnlyList<int> board)
        {
            if (cards.Count != 2)
                return "";
            Span<int> all = stackalloc int[7];
            all[0] = cards[0];
            all[1] = cards[1];
            for (var i = 0; i < board.Count && i < 5; i++) all[2 + i] = board[i];
            var score = FastHandEvaluator.Evaluate(all[..(2 + Math.Min(5, board.Count))]);
            return HandInsightReader.CategoryName(FastHandEvaluator.Category(score));
        }

        private static string Format(string key, params object[] args) =>
            string.Format(CultureInfo.CurrentCulture, LocalizationManager.Text(key), args);
    }
}
