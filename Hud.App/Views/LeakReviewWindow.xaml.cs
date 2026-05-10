using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using Hud.App.Services;
using Hud.App.Models;

namespace Hud.App.Views
{
    public class SeatXConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c) => (int)value switch { 0 => 425.0, 1 => 70.0, 2 => 70.0, 3 => 425.0, 4 => 780.0, 5 => 780.0, _ => 425.0 };
        public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotImplementedException();
    }

    public class SeatYConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c) => (int)value switch { 0 => 390.0, 1 => 310.0, 2 => 125.0, 3 => 25.0, 4 => 125.0, 5 => 310.0, _ => 390.0 };
        public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotImplementedException();
    }

    public partial class LeakReviewWindow : Window
    {
        private readonly LeakReviewViewModel _viewModel;
        private readonly DispatcherTimer _playTimer = new() { Interval = TimeSpan.FromMilliseconds(850) };

        public LeakReviewWindow(IEnumerable<LeakSpotRow> hands, string summary)
        {
            InitializeComponent();
            _viewModel = new LeakReviewViewModel(hands, summary);
            DataContext = _viewModel;
            _playTimer.Tick += (_, _) =>
            {
                if (!_viewModel.NextStep())
                    _playTimer.Stop();
            };
        }

        private void BtnMarkReviewed_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel.SelectedHand != null)
            {
                _viewModel.SelectedHand.IsReviewed = true;
                _viewModel.UpdateProgress();
            }
        }

        private void BtnPrevStep_Click(object sender, RoutedEventArgs e) => _viewModel.PrevStep();
        private void BtnNextStep_Click(object sender, RoutedEventArgs e) => _viewModel.NextStep();
        private void BtnPlay_Click(object sender, RoutedEventArgs e)
        {
            if (_playTimer.IsEnabled)
                _playTimer.Stop();
            else
                _playTimer.Start();
        }
    }

    public sealed class LeakReviewViewModel : INotifyPropertyChanged
    {
        private LeakSpotRow? _selectedHand;
        private IReadOnlyList<StreetActionViewModel> _preflopActions = Array.Empty<StreetActionViewModel>();
        private IReadOnlyList<StreetActionViewModel> _flopActions = Array.Empty<StreetActionViewModel>();
        private IReadOnlyList<StreetActionViewModel> _turnActions = Array.Empty<StreetActionViewModel>();
        private IReadOnlyList<StreetActionViewModel> _riverActions = Array.Empty<StreetActionViewModel>();
        private string _coachDiagnostic = "";
        private string _coachProTip = "";
        private string _heroHandWinrateLabel = "";
        private readonly AphReferenceEngine _aphReference = new();
        private List<HandStep> _handSteps = new();
        private int _currentStepIndex = -1;
        private HandStep? _currentStep;

        public LeakReviewViewModel(IEnumerable<LeakSpotRow> hands, string summary)
        {
            Hands = new ObservableCollection<LeakSpotRow>(hands);
            Summary = summary;
            TotalCount = Hands.Count;
            SelectedHand = Hands.FirstOrDefault();
            UpdateProgress();
        }

        public ObservableCollection<LeakSpotRow> Hands { get; }
        public string Summary { get; }
        public int TotalCount { get; }
        public int ReviewedCount => Hands.Count(h => h.IsReviewed);

        public LeakSpotRow? SelectedHand
        {
            get => _selectedHand;
            set
            {
                if (_selectedHand == value) return;
                _selectedHand = value;
                OnPropertyChanged(nameof(SelectedHand));
                try
                {
                    LoadHandActions();
                    UpdateAnalysis();
                    BuildReplayerSteps();
                }
                catch (Exception ex)
                {
                    _coachDiagnostic = "No se pudo cargar el replay completo de esta mano.";
                    _coachProTip = ex.Message;
                    _handSteps.Clear();
                    _currentStepIndex = -1;
                    CurrentStep = BuildFallbackStep(ex.Message);
                    OnPropertyChanged(nameof(CoachDiagnostic));
                    OnPropertyChanged(nameof(CoachProTip));
                }
            }
        }

        public HandStep? CurrentStep
        {
            get => _currentStep;
            set
            {
                _currentStep = value;
                OnPropertyChanged(nameof(CurrentStep));
                OnPropertyChanged(nameof(StepLabel));
                OnPropertyChanged(nameof(ResolvedHeroPosition));
            }
        }

        public string StepLabel => _handSteps.Count > 0 ? $"Paso {_currentStepIndex + 1} de {_handSteps.Count}" : "";
        public string ResolvedHeroPosition =>
            CurrentStep?.Players.FirstOrDefault(player => player.IsHero)?.Position is { Length: > 0 } pos && pos != "?"
                ? pos
                : SelectedHand?.Position ?? "?";

        public bool NextStep()
        {
            if (_currentStepIndex < _handSteps.Count - 1)
            {
                _currentStepIndex++;
                CurrentStep = _handSteps[_currentStepIndex];
                return true;
            }

            return false;
        }

        public bool PrevStep()
        {
            if (_currentStepIndex > 0)
            {
                _currentStepIndex--;
                CurrentStep = _handSteps[_currentStepIndex];
                return true;
            }

            return false;
        }

        private void BuildReplayerSteps()
        {
            _handSteps.Clear();
            _currentStepIndex = -1;
            CurrentStep = null;

            if (_selectedHand == null || !File.Exists(_selectedHand.Table.SourcePath)) return;

            var handsRaw = PokerStarsHandHistory.SplitHands(File.ReadLines(_selectedHand.Table.SourcePath)).ToList();
            if (_selectedHand.HandIndex <= 0 || _selectedHand.HandIndex > handsRaw.Count) return;

            var handLines = handsRaw[_selectedHand.HandIndex - 1];
            var positions = BuildBestPositionMap(handLines);
            var hero = _selectedHand.Table.HeroName;
            var bigBlind = _selectedHand.Table.BigBlind > 0 ? _selectedHand.Table.BigBlind : 1;
            var dealerButtonSeat = FindDealerButtonSeat(handLines);
            var heroCards = _selectedHand.Cards;
            if (string.IsNullOrWhiteSpace(heroCards) && PokerStarsHandHistory.TryGetDealtCards(handLines, hero, out var dealtCards))
                heroCards = dealtCards;
            _heroHandWinrateLabel = BuildHeroGlobalHandWinrateLabel(
                _selectedHand.Table.SourcePath,
                hero,
                heroCards,
                _selectedHand.Combo,
                bigBlind);
            var heroReachedShowdown = HeroReachedShowdown(handLines, hero);
            
            double currentPot = 0;
            double currentBet = 0;
            var board = new List<string>();
            var players = new List<PlayerReplayState>();
            var committedThisStreet = new Dictionary<string, double>(StringComparer.Ordinal);

            // Initialize players only from the seat block at the top of the hand.
            foreach(var line in handLines.TakeWhile(line => !line.StartsWith("*** HOLE CARDS", StringComparison.OrdinalIgnoreCase) &&
                                                            !line.StartsWith("*** CARTAS", StringComparison.OrdinalIgnoreCase))) {
                var seat = PokerStarsHandHistory.SeatRx.Match(line);
                if (seat.Success && int.TryParse(seat.Groups["seat"].Value, out var seatNumber)) {
                    string name = PokerStarsHandHistory.NormalizeName(seat.Groups["name"].Value);
                    var stack = ExtractSeatStack(line) / bigBlind;
                    players.Add(new PlayerReplayState { 
                        Name = name, 
                        Position = positions.TryGetValue(name, out var pos) ? pos : "?",
                        SeatNumber = seatNumber,
                        Stack = stack > 0 ? stack : 100,
                        IsHero = PokerStarsHandHistory.SamePlayer(name, hero),
                        IsDealerButton = seatNumber == dealerButtonSeat,
                        Cards = PokerStarsHandHistory.SamePlayer(name, hero) ? heroCards : "",
                        IsActive = true
                    });
                }
            }

            // Parse actions
            string currentStreet = "PREFLOP";
            var boardRx = new Regex(@"\[(?<cards>[^\]]+)\]");

            foreach (var line in handLines)
            {
                if (line.Contains("*** FLOP")) { 
                    currentStreet = "FLOP";
                    committedThisStreet.Clear();
                    currentBet = 0;
                    board.Clear();
                    var match = boardRx.Match(line);
                    if (match.Success) board.AddRange(SplitBoardCards(match.Groups["cards"].Value));
                }
                if (line.Contains("*** TURN")) {
                    currentStreet = "TURN";
                    committedThisStreet.Clear();
                    currentBet = 0;
                    UpdateBoardFromStreetLine(board, line, boardRx);
                }
                if (line.Contains("*** RIVER")) {
                    currentStreet = "RIVER";
                    committedThisStreet.Clear();
                    currentBet = 0;
                    UpdateBoardFromStreetLine(board, line, boardRx);
                }

                ApplyNonActorPotUpdate(line, ref currentPot);

                ApplyRevealedCards(line, players);

                var actorMatch = PokerStarsHandHistory.ActorRx.Match(line);
                if (actorMatch.Success)
                {
                    string actorName = PokerStarsHandHistory.NormalizeName(actorMatch.Groups["actor"].Value);
                    var p = players.FirstOrDefault(x => PokerStarsHandHistory.SamePlayer(x.Name, actorName));
                    if (p != null)
                    {
                        committedThisStreet.TryGetValue(hero, out var heroCommittedBefore);
                        var currentBetBefore = currentBet;
                        var currentPotBefore = currentPot;
                        var heroPlayerBefore = players.FirstOrDefault(x => x.IsHero || PokerStarsHandHistory.SamePlayer(x.Name, hero));
                        var heroStackBefore = heroPlayerBefore?.Stack ?? 0;
                        var contribution = ApplyActorPotUpdate(line, actorName, committedThisStreet, ref currentPot, ref currentBet);
                        if (contribution > 0)
                        {
                            p.Committed += contribution / bigBlind;
                            p.Stack = Math.Max(0, p.Stack - contribution / bigBlind);
                        }
                        committedThisStreet.TryGetValue(hero, out var heroCommittedAfter);
                        var isHeroAction = PokerStarsHandHistory.SamePlayer(actorName, hero);
                        var decisionBet = isHeroAction ? currentBetBefore : currentBet;
                        var decisionCommitted = isHeroAction ? heroCommittedBefore : heroCommittedAfter;
                        var isFacingBet = decisionBet > decisionCommitted + 0.0001;
                        var heroPlayer = players.FirstOrDefault(x => x.IsHero || PokerStarsHandHistory.SamePlayer(x.Name, hero));
                        var heroPosition = heroPlayer?.Position ?? _selectedHand.Position;
                        var toCall = Math.Max(0, decisionBet - decisionCommitted);
                        var decisionPot = isHeroAction ? currentPotBefore : currentPot;
                        var decisionStack = isHeroAction ? heroStackBefore : heroPlayer?.Stack ?? 0;

                        var step = new HandStep {
                            StepIndex = _handSteps.Count,
                            Street = currentStreet,
                            Actor = actorName,
                            ActionText = actorMatch.Groups["action"].Value,
                            PotSize = currentPot / bigBlind,
                            CurrentBet = currentBet / bigBlind,
                            BoardCards = new List<string>(board),
                            Players = players.Select((x, idx) => new PlayerReplayState {
                                Name = x.Name, 
                                Position = x.Position, 
                                SeatIndex = MapHeroCenteredSeat(x, players, hero),
                                SeatNumber = x.SeatNumber,
                                Stack = x.Stack, 
                                IsHero = x.IsHero, 
                                IsActive = x.IsActive,
                                IsDealerButton = x.IsDealerButton,
                                Cards = x.Cards,
                                IsCurrentActor = PokerStarsHandHistory.SamePlayer(x.Name, actorName),
                                CurrentAction = PokerStarsHandHistory.SamePlayer(x.Name, actorName)
                                    ? HandHistoryTranslator.TranslatePlayerAction(actorMatch.Groups["action"].Value, bigBlind)
                                    : ""
                            }).ToList()
                        };

                        // Reference/coach analysis. Only the hero has known hole cards here; avoid showing
                        // fake preflop references for villain actions with the hero's cards.
                        step.AphReference = BuildStepRecommendation(
                            p,
                            currentStreet,
                            actorMatch.Groups["action"].Value,
                            board,
                            heroPosition,
                            isHeroAction,
                            isFacingBet,
                            decisionPot / bigBlind,
                            toCall / bigBlind,
                            contribution / bigBlind,
                            decisionStack,
                            actorName);
                        ApplyShowdownReview(step.AphReference, actorMatch.Groups["action"].Value, heroReachedShowdown);
                        _handSteps.Add(step);

                        // Update state for next step
                        if (line.Contains("folds", StringComparison.OrdinalIgnoreCase) ||
                            line.Contains("retira", StringComparison.OrdinalIgnoreCase) ||
                            line.Contains("no va", StringComparison.OrdinalIgnoreCase))
                        {
                            p.IsActive = false;
                        }
                    }
                }
            }

            if (_handSteps.Count > 0)
            {
                _currentStepIndex = 0;
                CurrentStep = _handSteps[0];
            }
        }

        private void ApplyShowdownReview(AphReferenceRecommendation recommendation, string actionText, bool heroReachedShowdown)
        {
            if (_selectedHand == null || !heroReachedShowdown || !IsShowdownAction(actionText))
                return;

            recommendation.HeroHandWinrateLabel = _heroHandWinrateLabel;
            recommendation.ShowdownVerdict = BuildShowdownVerdict(_selectedHand);
        }

        private static bool HeroReachedShowdown(IReadOnlyList<string> handLines, string hero)
        {
            foreach (var line in handLines)
            {
                var shown = PokerStarsHandHistory.ShowCardsRx.Match(line);
                if (shown.Success && PokerStarsHandHistory.SamePlayer(shown.Groups["name"].Value, hero))
                    return true;

                var summaryShown = PokerStarsHandHistory.SummaryShownRx.Match(line);
                if (summaryShown.Success && PokerStarsHandHistory.SamePlayer(summaryShown.Groups["name"].Value, hero))
                    return true;
            }

            return false;
        }

        private static bool IsShowdownAction(string actionText)
        {
            var action = PokerStarsHandHistory.NormalizeAction(actionText);
            return action == "mucks" ||
                   actionText.Contains("shows", StringComparison.OrdinalIgnoreCase) ||
                   actionText.Contains("muestra", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildShowdownVerdict(LeakSpotRow hand)
        {
            var amount = hand.NetBbLabel;
            var madeHand = string.IsNullOrWhiteSpace(hand.MadeHand) ? "tu mano final" : hand.MadeHand;
            var villain = string.IsNullOrWhiteSpace(hand.VillainName) ? "el rival principal" : hand.VillainName;
            var villainHand = string.IsNullOrWhiteSpace(hand.VillainCombination) ? "una mano mejor o suficiente" : hand.VillainCombination;

            if (hand.NetBb > 0.05)
                return $"Showdown validado: la linea termino ganando {amount}. Fue rentable porque Hero llego con {madeHand} y supero a {villain}.";

            if (hand.NetBb < -0.05)
                return $"Showdown validado: la linea termino perdiendo {amount}. Fue costosa porque {villain} mostro {villainHand}; revisa si el ultimo continue/call tenia precio suficiente.";

            return $"Showdown neutral: la linea cerro practicamente even ({amount}). La decision depende del precio del bote y de repeticion futura del spot.";
        }

        private static string BuildHeroGlobalHandWinrateLabel(
            string sourcePath,
            string hero,
            string heroCards,
            string fallbackCombo,
            double fallbackBigBlind)
        {
            var targetCombo = NormalizeCardsToCombo(heroCards);
            if (string.IsNullOrWhiteSpace(targetCombo))
                targetCombo = fallbackCombo?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(targetCombo))
                return "";

            var total = 0;
            var wins = 0;
            var totalBb = 0.0;
            var files = EnumerateKnownHandHistoryFiles(sourcePath);

            foreach (var file in files)
            {
                double bigBlind = ExtractBigBlindFromPath(file, fallbackBigBlind);
                List<IReadOnlyList<string>> handsRaw;
                try
                {
                    handsRaw = PokerStarsHandHistory.SplitHands(File.ReadLines(file)).ToList();
                }
                catch
                {
                    continue;
                }

                foreach (var hand in handsRaw)
                {
                    if (!PokerStarsHandHistory.TryGetDealtCards(hand, hero, out var cards))
                        continue;

                    if (!string.Equals(NormalizeCardsToCombo(cards), targetCombo, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var net = PokerStarsHandHistory.EstimateNetForPlayer(hand, hero) / Math.Max(1, bigBlind);
                    total++;
                    totalBb += net;
                    if (net > 0.0001)
                        wins++;
                }
            }

            if (total == 0)
                return $"WR global de tu {targetCombo}: sin muestra suficiente.";

            var winrate = wins * 100.0 / total;
            var avg = totalBb / total;
            return $"WR global de tu {targetCombo}: {wins}/{total} ({winrate:0.#}%), media {avg:+0.#;-0.#;0} bb.";
        }

        private static IEnumerable<string> EnumerateKnownHandHistoryFiles(string sourcePath)
        {
            var directory = Path.GetDirectoryName(sourcePath);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                return File.Exists(sourcePath) ? new[] { sourcePath } : Array.Empty<string>();

            try
            {
                return Directory.EnumerateFiles(directory, "*.txt", SearchOption.AllDirectories).ToList();
            }
            catch
            {
                return File.Exists(sourcePath) ? new[] { sourcePath } : Array.Empty<string>();
            }
        }

        private static double ExtractBigBlindFromPath(string path, double fallbackBigBlind)
        {
            var fileName = Path.GetFileNameWithoutExtension(path);
            var match = Regex.Match(fileName, @"-\s*[\d,.]+\s*-\s*(?<bb>[\d,.]+)\s*-", RegexOptions.IgnoreCase);
            return match.Success && PokerAmountParser.TryParse(match.Groups["bb"].Value, out var bigBlind) && bigBlind > 0
                ? bigBlind
                : fallbackBigBlind;
        }

        private static string NormalizeCardsToCombo(string cards)
        {
            var parsed = SplitBoardCards(cards)
                .Select(ParseCardToken)
                .Where(card => card.Rank.Length > 0)
                .ToList();
            if (parsed.Count < 2)
                return "";

            var first = parsed[0];
            var second = parsed[1];
            var ordered = new[] { first, second }
                .OrderByDescending(card => RankValue(card.Rank))
                .ThenBy(card => card.Rank, StringComparer.Ordinal)
                .ToArray();
            var suited = ordered[0].Suit != '?' && ordered[0].Suit == ordered[1].Suit;
            if (ordered[0].Rank == ordered[1].Rank)
                return $"{ordered[0].Rank}{ordered[1].Rank}";

            return $"{ordered[0].Rank}{ordered[1].Rank}{(suited ? "s" : "o")}";
        }

        private static (string Rank, char Suit) ParseCardToken(string token)
        {
            var clean = token.Trim().Trim('[', ']', '(', ')', ',', '.', ';', ':', '|');
            if (clean.StartsWith("10", StringComparison.OrdinalIgnoreCase))
                return ("T", clean.Length > 2 ? char.ToLowerInvariant(clean[^1]) : '?');
            if (clean.Length < 2)
                return ("", '?');

            return (clean[0].ToString().ToUpperInvariant(), char.ToLowerInvariant(clean[^1]));
        }

        private static int RankValue(string rank) => rank switch
        {
            "A" => 14,
            "K" => 13,
            "Q" => 12,
            "J" => 11,
            "T" => 10,
            _ when int.TryParse(rank, out var value) => value,
            _ => 0
        };

        private static int FindDealerButtonSeat(IReadOnlyList<string> hand)
        {
            foreach (var line in hand)
            {
                if (line.StartsWith("*** HOLE CARDS", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("*** CARTAS", StringComparison.OrdinalIgnoreCase))
                    break;

                var button = PokerStarsHandHistory.ButtonRx.Match(line);
                if (button.Success && int.TryParse(button.Groups["seat"].Value, out var seat))
                    return seat;
            }

            return 0;
        }

        private static void ApplyRevealedCards(string line, List<PlayerReplayState> players)
        {
            var shown = PokerStarsHandHistory.ShowCardsRx.Match(line);
            if (!shown.Success)
                shown = PokerStarsHandHistory.SummaryShownRx.Match(line);

            if (!shown.Success)
                return;

            var name = PokerStarsHandHistory.NormalizeName(shown.Groups["name"].Value);
            var player = players.FirstOrDefault(p => PokerStarsHandHistory.SamePlayer(p.Name, name));
            if (player != null)
                player.Cards = shown.Groups["cards"].Value.Trim();
        }

        private AphReferenceRecommendation BuildStepRecommendation(
            PlayerReplayState player,
            string street,
            string actionText,
            List<string> board,
            string heroPosition,
            bool isHeroAction,
            bool isFacingBet,
            double potBeforeDecisionBb,
            double toCallBb,
            double actorContributionBb,
            double heroStackBb,
            string actorName)
        {
            try
            {
                if (_selectedHand is null)
                    return BuildFallbackRecommendation("Sin mano seleccionada.");

                var recommendation = _aphReference.AnalyzeAction(
                    street,
                    heroPosition,
                    $"{actionText} | {_selectedHand.PotType}",
                    _selectedHand.Cards,
                    board) ?? BuildFallbackRecommendation("Sin recomendacion disponible para este evento.");
                ApplyLegalActions(recommendation, isFacingBet);
                var villainContext = ResolveVillainContext(_selectedHand, actorName, isHeroAction);
                ApplyVillainProfileAdjustment(recommendation, villainContext, street, isHeroAction, isFacingBet);
                ApplyOddsContext(
                    recommendation,
                    villainContext,
                    isHeroAction,
                    isFacingBet,
                    potBeforeDecisionBb,
                    toCallBb,
                    actorContributionBb,
                    heroStackBb,
                    actionText);
                ApplyDecisionSummary(recommendation, actionText, isHeroAction);
                if (!isHeroAction)
                    recommendation.StrategyTip = $"Evento de rival: {player.Position} {actionText}. Situacion actual para Hero en {heroPosition}: {recommendation.StrategyTip}";
                return recommendation;
            }
            catch (Exception ex)
            {
                var fallback = BuildFallbackRecommendation($"Referencia no disponible para este paso: {ex.Message}");
                ApplyLegalActions(fallback, isFacingBet);
                var villainContext = ResolveVillainContext(_selectedHand, actorName, isHeroAction);
                ApplyVillainProfileAdjustment(fallback, villainContext, street, isHeroAction, isFacingBet);
                ApplyOddsContext(
                    fallback,
                    villainContext,
                    isHeroAction,
                    isFacingBet,
                    potBeforeDecisionBb,
                    toCallBb,
                    actorContributionBb,
                    heroStackBb,
                    actionText);
                ApplyDecisionSummary(fallback, actionText, isHeroAction);
                return fallback;
            }
        }

        private static AphReferenceRecommendation BuildFallbackRecommendation(string message) =>
            new()
            {
                Equity = 0,
                FoldPct = 34,
                CallPct = 33,
                RaisePct = 33,
                StrategyTip = message
            };

        private static IEnumerable<string> SplitBoardCards(string raw) =>
            raw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        private static void ApplyLegalActions(AphReferenceRecommendation recommendation, bool isFacingBet)
        {
            if (isFacingBet)
            {
                recommendation.ShowFold = true;
                recommendation.ShowCheck = false;
                recommendation.ShowCall = true;
                recommendation.ShowRaise = true;
                recommendation.CheckPct = 0;
                return;
            }

            recommendation.ShowFold = false;
            recommendation.ShowCheck = true;
            recommendation.ShowCall = false;
            recommendation.ShowRaise = true;
            recommendation.CheckPct = Math.Clamp(recommendation.FoldPct + recommendation.CallPct, 0, 100);
            recommendation.FoldPct = 0;
            recommendation.CallPct = 0;
        }

        private static VillainCoachContext ResolveVillainContext(LeakSpotRow? hand, string actorName, bool isHeroAction)
        {
            var candidate = isHeroAction
                ? hand?.VillainName
                : actorName;

            if (string.IsNullOrWhiteSpace(candidate) ||
                (hand != null && PokerStarsHandHistory.SamePlayer(candidate, hand.Table.HeroName)))
            {
                return VillainCoachContext.Unknown;
            }

            return VillainHistoryStore.TryGet(PokerStarsHandHistory.NormalizeName(candidate), out var row)
                ? VillainCoachContext.From(row)
                : new VillainCoachContext(candidate.Trim(), "Sin muestra", 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, false);
        }

        private static void ApplyVillainProfileAdjustment(
            AphReferenceRecommendation recommendation,
            VillainCoachContext villain,
            string street,
            bool isHeroAction,
            bool isFacingBet)
        {
            if (!villain.HasStats)
            {
                recommendation.VillainProfileLabel = string.IsNullOrWhiteSpace(villain.Name)
                    ? ""
                    : $"Villano: {villain.Name}; sin muestra historica suficiente.";
                return;
            }

            var confidence = villain.TotalHands switch
            {
                >= 200 => "Alta",
                >= 75 => "Media",
                >= 30 => "Baja",
                _ => "Muy baja"
            };
            recommendation.VillainProfileLabel =
                $"Villano: {villain.Name} | {villain.Profile} | {villain.TotalHands} manos | confianza {confidence}.";

            var foldEquity = EstimateFoldEquity(villain, street, isFacingBet);
            recommendation.FoldEquityLabel = $"Fold equity estimada vs este rival: {foldEquity:0.#}%.";

            if (isFacingBet)
                AdjustFacingBetFrequencies(recommendation, villain);
            else
                AdjustInitiativeFrequencies(recommendation, villain);

            NormalizeShownFrequencies(recommendation);
            recommendation.VillainExploitLabel = BuildExploitHint(recommendation, villain, street, isHeroAction, isFacingBet);
        }

        private static double EstimateFoldEquity(VillainCoachContext villain, string street, bool isFacingBet)
        {
            var fold = 38.0;
            if (villain.FoldVsCBetFlopPct > 0)
                fold = villain.FoldVsCBetFlopPct;

            if (villain.Profile.Contains("Nit", StringComparison.OrdinalIgnoreCase)) fold += 12;
            if (villain.Profile.Contains("Tight", StringComparison.OrdinalIgnoreCase)) fold += 8;
            if (villain.Profile.Contains("Fish", StringComparison.OrdinalIgnoreCase)) fold -= 10;
            if (villain.Profile.Contains("Maniac", StringComparison.OrdinalIgnoreCase)) fold -= 14;
            if (villain.Profile.Contains("Calling", StringComparison.OrdinalIgnoreCase) ||
                villain.Profile.Contains("Loose", StringComparison.OrdinalIgnoreCase)) fold -= 12;
            if (villain.WTSDPct >= 35) fold -= 10;
            if (villain.WSDPct >= 60 && !isFacingBet) fold -= 6;
            if (villain.WWSFPct <= 38) fold += 8;
            if (!string.Equals(street, "FLOP", StringComparison.OrdinalIgnoreCase) && villain.FoldVsCBetFlopPct > 0)
                fold -= 5;

            return Math.Clamp(fold, 5, 85);
        }

        private static void AdjustFacingBetFrequencies(AphReferenceRecommendation recommendation, VillainCoachContext villain)
        {
            if (villain.AF >= 3.5 || villain.AFqPct >= 65 || villain.Profile.Contains("Maniac", StringComparison.OrdinalIgnoreCase))
            {
                recommendation.FoldPct -= 10;
                recommendation.CallPct += 7;
                recommendation.RaisePct += 3;
            }

            if (villain.AF <= 1.2 && villain.WSDPct >= 55)
            {
                recommendation.FoldPct += 12;
                recommendation.CallPct -= 8;
                recommendation.RaisePct -= 4;
            }

            if (villain.WTSDPct >= 36)
            {
                recommendation.RaisePct = recommendation.Equity >= 55
                    ? recommendation.RaisePct + 6
                    : recommendation.RaisePct - 8;
                recommendation.CallPct += recommendation.Equity >= 35 ? 4 : -4;
            }
        }

        private static void AdjustInitiativeFrequencies(AphReferenceRecommendation recommendation, VillainCoachContext villain)
        {
            var foldEquity = EstimateFoldEquity(villain, "FLOP", false);
            if (foldEquity >= 60)
            {
                recommendation.RaisePct += 15;
                recommendation.CheckPct -= 15;
            }
            else if (foldEquity <= 28)
            {
                if (recommendation.Equity >= 55)
                    recommendation.RaisePct += 8;
                else
                    recommendation.RaisePct -= 18;
                recommendation.CheckPct += 10;
            }

            if (villain.Profile.Contains("Calling", StringComparison.OrdinalIgnoreCase) ||
                villain.WTSDPct >= 38)
            {
                recommendation.RaisePct += recommendation.Equity >= 55 ? 10 : -12;
                recommendation.CheckPct += recommendation.Equity < 45 ? 8 : 0;
            }
        }

        private static void NormalizeShownFrequencies(AphReferenceRecommendation recommendation)
        {
            recommendation.FoldPct = recommendation.ShowFold ? Math.Clamp(recommendation.FoldPct, 0, 100) : 0;
            recommendation.CheckPct = recommendation.ShowCheck ? Math.Clamp(recommendation.CheckPct, 0, 100) : 0;
            recommendation.CallPct = recommendation.ShowCall ? Math.Clamp(recommendation.CallPct, 0, 100) : 0;
            recommendation.RaisePct = recommendation.ShowRaise ? Math.Clamp(recommendation.RaisePct, 0, 100) : 0;

            var total = recommendation.FoldPct + recommendation.CheckPct + recommendation.CallPct + recommendation.RaisePct;
            if (total <= 0.0001)
                return;

            recommendation.FoldPct = recommendation.FoldPct / total * 100;
            recommendation.CheckPct = recommendation.CheckPct / total * 100;
            recommendation.CallPct = recommendation.CallPct / total * 100;
            recommendation.RaisePct = recommendation.RaisePct / total * 100;
        }

        private static string BuildExploitHint(
            AphReferenceRecommendation recommendation,
            VillainCoachContext villain,
            string street,
            bool isHeroAction,
            bool isFacingBet)
        {
            var prefix = isHeroAction ? "Ajuste explotativo" : "Lectura explotativa para Hero";
            if (villain.Profile.Contains("Maniac", StringComparison.OrdinalIgnoreCase) || villain.AF >= 4)
                return $"{prefix}: rival agresivo; baja el fold automatico y deja que farolee mas cuando tu equity/precio acompana.";
            if (villain.Profile.Contains("Fish", StringComparison.OrdinalIgnoreCase) ||
                villain.Profile.Contains("Loose", StringComparison.OrdinalIgnoreCase) ||
                villain.WTSDPct >= 36)
                return recommendation.Equity >= 55
                    ? $"{prefix}: rival pagador; prioriza value bet, sizing grande y menos faroles."
                    : $"{prefix}: rival pagador; los bluffs pierden valor, check/fold mejora sin equity suficiente.";
            if (villain.FoldVsCBetFlopPct >= 65)
                return $"{prefix}: foldea mucho a CBet; apostar tiene fold equity real, especialmente en boards secos.";
            if (villain.Profile.Contains("Nit", StringComparison.OrdinalIgnoreCase) || villain.VPIPPct < 16)
                return isFacingBet
                    ? $"{prefix}: rango fuerte cuando invierte; respeta apuestas grandes sin equity clara."
                    : $"{prefix}: puedes presionar mas botes pequenos, pero abandona si muestra resistencia fuerte.";
            if (villain.WSDPct >= 62)
                return $"{prefix}: cuando llega a showdown suele tener mano; cuidado con bluffcatchers marginales en {street}.";
            return $"{prefix}: perfil balanceado; manda la matematica de equity, precio y posicion.";
        }

        private static void ApplyOddsContext(
            AphReferenceRecommendation recommendation,
            VillainCoachContext villain,
            bool isHeroAction,
            bool isFacingBet,
            double potBeforeDecisionBb,
            double toCallBb,
            double actorContributionBb,
            double heroStackBb,
            string actionText)
        {
            potBeforeDecisionBb = Math.Max(0, potBeforeDecisionBb);
            toCallBb = Math.Max(0, toCallBb);
            actorContributionBb = Math.Max(0, actorContributionBb);

            if (isFacingBet && toCallBb > 0.0001)
            {
                var finalPotIfCall = potBeforeDecisionBb + toCallBb;
                var requiredEquity = finalPotIfCall > 0
                    ? toCallBb / finalPotIfCall * 100.0
                    : 0;
                var margin = recommendation.Equity - requiredEquity;
                var callEv = recommendation.Equity / 100.0 * finalPotIfCall - toCallBb;

                recommendation.PotOddsLabel =
                    $"Pot odds: pagar {FormatBb(toCallBb)} para bote final {FormatBb(finalPotIfCall)}; necesitas {requiredEquity:0.#}% equity.";
                recommendation.EquityMarginLabel =
                    $"Margen de equity: {FormatSignedPct(margin)} ({recommendation.Equity:0.#}% vs {requiredEquity:0.#}%).";
                recommendation.CallEvLabel =
                    $"EV call aprox: {FormatSignedBb(callEv)}.";
                recommendation.OddsInsight = margin switch
                {
                    >= 8 => "Precio favorable: el call/continue tiene colchon matematico.",
                    >= 0 => "Precio justo: call defendible, revisa posicion, rival y realizacion de equity.",
                    >= -8 => "Precio apretado: necesitas implied odds o fold equity para justificar continuar.",
                    _ => "Precio malo: fold gana peso salvo lectura fuerte del rival."
                };

                ApplyFacingBetMathAdjustment(recommendation, margin, callEv);
            }
            else if (!isFacingBet)
            {
                recommendation.PotOddsLabel = "Sin apuesta pendiente: no hay pot odds de call.";
                recommendation.OddsInsight = "Opciones reales: check o apostar/subir; fold/call no aplican en este punto.";
            }

            if (actorContributionBb > 0.0001 && potBeforeDecisionBb > 0.0001)
            {
                var sizingPct = actorContributionBb / potBeforeDecisionBb * 100.0;
                var action = ActionBucket(actionText);
                recommendation.SizingLabel = action switch
                {
                    "Raise" => $"Sizing: {FormatBb(actorContributionBb)} ({sizingPct:0.#}% del bote previo).",
                    "Call" => $"Pago realizado: {FormatBb(actorContributionBb)}.",
                    _ => $"Aporte al bote: {FormatBb(actorContributionBb)}."
                };

                if (action == "Raise" && villain.HasStats)
                {
                    var foldEquity = EstimateFoldEquity(villain, "FLOP", isFacingBet) / 100.0;
                    var finalPotIfCalled = potBeforeDecisionBb + actorContributionBb * 2;
                    var raiseEv = foldEquity * potBeforeDecisionBb +
                                  (1 - foldEquity) * (recommendation.Equity / 100.0 * finalPotIfCalled - actorContributionBb);
                    recommendation.RaiseEvLabel = $"EV raise/bet aprox: {FormatSignedBb(raiseEv)} con FE {foldEquity * 100:0.#}%.";
                }
            }

            if (heroStackBb > 0.0001 && potBeforeDecisionBb > 0.0001)
            {
                var spr = heroStackBb / potBeforeDecisionBb;
                recommendation.SprLabel = $"SPR aprox: {spr:0.#}. {SprHint(spr)}";
            }

            if (!isHeroAction && !string.IsNullOrWhiteSpace(recommendation.OddsInsight))
                recommendation.OddsInsight = $"Para Hero ahora: {recommendation.OddsInsight}";
        }

        private static void ApplyFacingBetMathAdjustment(AphReferenceRecommendation recommendation, double equityMargin, double callEv)
        {
            if (!recommendation.ShowFold || !recommendation.ShowCall)
                return;

            if (equityMargin <= -8 || callEv <= -2)
            {
                var severity = Math.Clamp(Math.Max(-equityMargin / 24.0, -callEv / 30.0), 0, 1);
                recommendation.FoldPct = Math.Max(recommendation.FoldPct, 62 + severity * 23);
                recommendation.CallPct = Math.Min(recommendation.CallPct, 28 - severity * 18);
                recommendation.RaisePct = Math.Min(recommendation.RaisePct, recommendation.Equity >= 42 ? 18 : 8);
                NormalizeShownFrequencies(recommendation);
                return;
            }

            if (equityMargin < 0 || callEv < 0)
            {
                recommendation.FoldPct = Math.Max(recommendation.FoldPct, 45);
                recommendation.CallPct = Math.Min(recommendation.CallPct, 38);
                recommendation.RaisePct = Math.Min(recommendation.RaisePct, 17);
                NormalizeShownFrequencies(recommendation);
            }
        }

        private static string SprHint(double spr) => spr switch
        {
            <= 2 => "Bote comprometido; top pair/draw fuerte sube de valor.",
            <= 6 => "SPR medio; sizing y posicion pesan bastante.",
            _ => "SPR alto; cuidado con manos dominadas e implied odds reversas."
        };

        private static string FormatBb(double value) => $"{value:0.##} bb";
        private static string FormatSignedBb(double value) => $"{value:+0.##;-0.##;0} bb";
        private static string FormatSignedPct(double value) => $"{value:+0.#;-0.#;0}%";

        private sealed record VillainCoachContext(
            string Name,
            string Profile,
            int TotalHands,
            double VPIPPct,
            double PFRPct,
            double ThreeBetPct,
            double AF,
            double AFqPct,
            double CBetFlopPct,
            double FoldVsCBetFlopPct,
            double WTSDPct,
            double WSDPct,
            double WWSFPct,
            bool HasStats)
        {
            public static VillainCoachContext Unknown { get; } =
                new("", "Sin muestra", 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, false);

            public static VillainCoachContext From(DataVillainsWindow.DataVillainRow row) =>
                new(
                    row.Name,
                    row.Profile,
                    row.TotalHands,
                    row.VPIPPct,
                    row.PFRPct,
                    row.ThreeBetPct,
                    row.AF,
                    row.AFqPct,
                    row.CBetFlopPct,
                    row.FoldVsCBetFlopPct,
                    row.WTSDPct,
                    row.WSDPct,
                    row.WWSFPct,
                    row.TotalHands >= 10);
        }

        private static void UpdateBoardFromStreetLine(List<string> board, string line, Regex boardRx)
        {
            var matches = boardRx.Matches(line).Cast<Match>().ToList();
            if (matches.Count == 0)
                return;

            var lastBlock = SplitBoardCards(matches[^1].Groups["cards"].Value).ToList();
            if (lastBlock.Count == 0)
                return;

            if (lastBlock.Count == 1)
            {
                if (matches.Count > 1 && board.Count == 0)
                    board.AddRange(SplitBoardCards(matches[^2].Groups["cards"].Value));

                board.Add(lastBlock[0]);
                return;
            }

            board.Clear();
            board.AddRange(lastBlock);
        }

        private static void ApplyDecisionSummary(AphReferenceRecommendation recommendation, string actionText, bool isHeroAction)
        {
            var chosen = isHeroAction ? ActionBucket(actionText) : "Evento rival";
            var recommended = BestFrequencyAction(recommendation);
            recommendation.ChosenAction = chosen;
            recommendation.RecommendedAction = recommended;

            if (!isHeroAction)
            {
                recommendation.ConcordanceLabel = "Situacion actual para Hero";
                return;
            }

            if (chosen == "N/A")
            {
                recommendation.ConcordanceLabel = "Sin decision evaluable";
                return;
            }

            recommendation.ConcordanceLabel = string.Equals(chosen, recommended, StringComparison.OrdinalIgnoreCase)
                ? $"Concordo: eligio {chosen}, la mayor frecuencia sugerida tambien es {recommended}."
                : $"No concordo: eligio {chosen}; la mayor frecuencia sugerida era {recommended}.";
        }

        private static string BestFrequencyAction(AphReferenceRecommendation recommendation)
        {
            var options = new List<(string Name, double Value)>();
            if (recommendation.ShowFold) options.Add(("Fold", recommendation.FoldPct));
            if (recommendation.ShowCheck) options.Add(("Check", recommendation.CheckPct));
            if (recommendation.ShowCall) options.Add(("Call", recommendation.CallPct));
            if (recommendation.ShowRaise) options.Add(("Raise", recommendation.RaisePct));
            return options.Count == 0
                ? "N/A"
                : options.OrderByDescending(item => item.Value).First().Name;
        }

        private static string ActionBucket(string actionText)
        {
            var action = PokerStarsHandHistory.NormalizeAction(actionText);
            return action switch
            {
                "folds" or "mucks" => "Fold",
                "checks" => "Check",
                "calls" => "Call",
                "bets" or "raises" or "all-in" => "Raise",
                _ => "N/A"
            };
        }

        private static HandStep BuildFallbackStep(string message) =>
            new()
            {
                StepIndex = 0,
                Street = "ERROR",
                Actor = "APH",
                ActionText = message,
                PotSize = 0,
                BoardCards = new List<string>(),
                Players = new List<PlayerReplayState>(),
                AphReference = BuildFallbackRecommendation(message)
            };

        private static int MapHeroCenteredSeat(PlayerReplayState player, IReadOnlyList<PlayerReplayState> players, string heroName)
        {
            if (player.IsHero || PokerStarsHandHistory.SamePlayer(player.Name, heroName))
                return 0;

            var hero = players.FirstOrDefault(p => p.IsHero || PokerStarsHandHistory.SamePlayer(p.Name, heroName));
            var heroPosition = NormalizeSeatPosition(hero?.Position ?? "");
            var heroSeatNumber = hero?.SeatNumber ?? 0;

            var villains = players
                .Where(p => !p.IsHero && !PokerStarsHandHistory.SamePlayer(p.Name, heroName))
                .OrderBy(p => PositionDistanceFromHero(heroPosition, p.Position))
                .ThenBy(p => SeatDistanceFromHero(heroSeatNumber, p.SeatNumber))
                .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var index = villains.FindIndex(p => PokerStarsHandHistory.SamePlayer(p.Name, player.Name));
            var count = Math.Min(villains.Count, 5);
            if (index < 0) return 3;
            var map = count switch
            {
                1 => new[] { 3 },
                2 => new[] { 1, 5 },
                3 => new[] { 1, 3, 5 },
                4 => new[] { 1, 2, 4, 5 },
                _ => new[] { 1, 2, 3, 4, 5 }
            };
            return map[Math.Min(index, map.Length - 1)];
        }

        private static int PositionDistanceFromHero(string heroPosition, string playerPosition)
        {
            var heroIndex = PositionRingIndex(heroPosition);
            var playerIndex = PositionRingIndex(playerPosition);
            if (heroIndex < 0 || playerIndex < 0)
                return 99;

            var distance = (playerIndex - heroIndex + 6) % 6;
            return distance == 0 ? 99 : distance;
        }

        private static int SeatDistanceFromHero(int heroSeatNumber, int playerSeatNumber)
        {
            if (heroSeatNumber <= 0 || playerSeatNumber <= 0)
                return playerSeatNumber <= 0 ? 99 : playerSeatNumber;

            var distance = (playerSeatNumber - heroSeatNumber + 6) % 6;
            return distance == 0 ? 99 : distance;
        }

        private static int PositionRingIndex(string position) => NormalizeSeatPosition(position) switch
        {
            "SB" => 0,
            "BB" => 1,
            "UTG" => 2,
            "HJ" => 3,
            "CO" => 4,
            "BTN" => 5,
            _ => -1
        };

        private static string NormalizeSeatPosition(string position)
        {
            var value = (position ?? "").Trim().ToUpperInvariant();
            return value switch
            {
                "BU" => "BTN",
                "BUTTON" => "BTN",
                "BTN/SB" => "SB",
                "SB/BTN" => "SB",
                "EP" => "UTG",
                "MP" => "HJ",
                "LJ" => "UTG",
                _ => value
            };
        }

        private static Dictionary<string, string> BuildBestPositionMap(IReadOnlyList<string> hand)
        {
            var standard = PokerStarsHandHistory.BuildPositionMap(hand);
            var active = BuildActivePositionMap(hand);
            foreach (var (name, position) in active)
                standard[name] = position;
            return standard;
        }

        private static Dictionary<string, string> BuildActivePositionMap(IReadOnlyList<string> hand)
        {
            var buttonSeat = 0;
            var seats = new SortedDictionary<int, string>();
            var activeNames = new HashSet<string>(StringComparer.Ordinal);
            var inSeatBlock = true;

            foreach (var line in hand)
            {
                if (line.StartsWith("*** HOLE CARDS", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("*** CARTAS", StringComparison.OrdinalIgnoreCase))
                    inSeatBlock = false;

                if (inSeatBlock && buttonSeat == 0)
                {
                    var button = PokerStarsHandHistory.ButtonRx.Match(line);
                    if (button.Success)
                        int.TryParse(button.Groups["seat"].Value, out buttonSeat);
                }

                if (inSeatBlock)
                {
                    var seat = PokerStarsHandHistory.SeatRx.Match(line);
                    if (seat.Success && int.TryParse(seat.Groups["seat"].Value, out var seatNumber) &&
                        !line.Contains("sitting out", StringComparison.OrdinalIgnoreCase))
                    {
                        seats[seatNumber] = PokerStarsHandHistory.NormalizeName(seat.Groups["name"].Value);
                    }
                }

                var actor = PokerStarsHandHistory.ActorRx.Match(line);
                if (actor.Success && IsReplayPlayerAction(actor.Groups["action"].Value))
                    activeNames.Add(PokerStarsHandHistory.NormalizeName(actor.Groups["actor"].Value));

                var shown = PokerStarsHandHistory.ShowCardsRx.Match(line);
                if (shown.Success)
                    activeNames.Add(PokerStarsHandHistory.NormalizeName(shown.Groups["name"].Value));
            }

            var activeSeats = seats
                .Where(seat => activeNames.Contains(seat.Value))
                .Select(seat => seat.Key)
                .OrderBy(seat => seat)
                .ToList();

            if (buttonSeat == 0 || activeSeats.Count == 0)
                return new Dictionary<string, string>(StringComparer.Ordinal);

            var buttonIndex = activeSeats.IndexOf(buttonSeat);
            if (buttonIndex < 0)
                buttonIndex = activeSeats.FindIndex(seat => seat > buttonSeat);
            if (buttonIndex < 0)
                buttonIndex = 0;

            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < activeSeats.Count; i++)
            {
                var offset = (i - buttonIndex + activeSeats.Count) % activeSeats.Count;
                result[seats[activeSeats[i]]] = PositionFromOffset(offset, activeSeats.Count);
            }

            return result;
        }

        private static bool IsReplayPlayerAction(string actionText)
        {
            var action = PokerStarsHandHistory.NormalizeAction(actionText);
            return action is "posts" or "folds" or "checks" or "calls" or "bets" or "raises" or "all-in" or "mucks" ||
                   actionText.Contains("shows", StringComparison.OrdinalIgnoreCase) ||
                   actionText.Contains("muestra", StringComparison.OrdinalIgnoreCase);
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

        private static double ExtractSeatStack(string line)
        {
            var match = Regex.Match(line, @"\((?<stack>[\d,.]+)\s+(?:in chips|en fichas)", RegexOptions.IgnoreCase);
            return match.Success && PokerAmountParser.TryParse(match.Groups["stack"].Value, out var stack) ? stack : 0;
        }

        private static double ApplyActorPotUpdate(
            string line,
            string actorName,
            Dictionary<string, double> committedThisStreet,
            ref double currentPot,
            ref double currentBet)
        {
            var raise = PokerStarsHandHistory.RaiseToRx.Match(line);
            if (raise.Success && PokerAmountParser.TryParse(raise.Groups["amount"].Value, out var raiseTo))
            {
                committedThisStreet.TryGetValue(actorName, out var previous);
                var contribution = Math.Max(0, raiseTo - previous);
                committedThisStreet[actorName] = Math.Max(previous, raiseTo);
                currentBet = Math.Max(currentBet, raiseTo);
                currentPot += contribution;
                return contribution;
            }

            var action = PokerStarsHandHistory.ActionAmountRx.Match(line);
            if (action.Success && PokerAmountParser.TryParse(action.Groups["amount"].Value, out var amount))
            {
                committedThisStreet.TryGetValue(actorName, out var previous);
                committedThisStreet[actorName] = previous + amount;
                var actor = PokerStarsHandHistory.ActorRx.Match(line);
                var normalizedAction = actor.Success
                    ? PokerStarsHandHistory.NormalizeAction(actor.Groups["action"].Value)
                    : "";
                if (normalizedAction is "posts" or "bets")
                    currentBet = Math.Max(currentBet, committedThisStreet[actorName]);
                currentPot += amount;
                return amount;
            }

            return 0;
        }

        private static void ApplyNonActorPotUpdate(string line, ref double currentPot)
        {
            var returned = PokerStarsHandHistory.ReturnedRx.Match(line);
            if (returned.Success)
            {
                var amountText = returned.Groups["amount"].Success ? returned.Groups["amount"].Value : returned.Groups["amount2"].Value;
                if (PokerAmountParser.TryParse(amountText, out var returnedAmount))
                    currentPot = Math.Max(0, currentPot - returnedAmount);
                return;
            }

            if (PokerStarsHandHistory.CollectedRx.IsMatch(line))
                currentPot = 0;
        }

        private static int PositionOrder(string pos) => pos switch
        {
            "SB" => 0,
            "BB" => 1,
            "UTG" => 2,
            "HJ" or "MP" => 3,
            "CO" => 4,
            "BTN" => 5,
            _ => 6
        };

        private static int MapPositionToSeat(string pos) => pos switch
        {
            "SB" => 0,
            "BB" => 1,
            "UTG" => 2,
            "HJ" or "MP" => 3,
            "CO" => 4,
            "BTN" => 5,
            _ => 0
        };

        public string CoachDiagnostic => _coachDiagnostic;
        public string CoachProTip => _coachProTip;

        public IReadOnlyList<StreetActionViewModel> PreflopActions => _preflopActions;
        public IReadOnlyList<StreetActionViewModel> FlopActions => _flopActions;
        public IReadOnlyList<StreetActionViewModel> TurnActions => _turnActions;
        public IReadOnlyList<StreetActionViewModel> RiverActions => _riverActions;

        public void UpdateProgress()
        {
            OnPropertyChanged(nameof(ReviewedCount));
        }

        private void UpdateAnalysis()
        {
            if (_selectedHand == null)
            {
                _coachDiagnostic = "";
                _coachProTip = "";
            }
            else
            {
                var analysis = LeakCoachService.GetAnalysis(_selectedHand);
                _coachDiagnostic = analysis.Diagnostic;
                _coachProTip = analysis.ProTip;
            }
            OnPropertyChanged(nameof(CoachDiagnostic));
            OnPropertyChanged(nameof(CoachProTip));
        }

        private void LoadHandActions()
        {
            if (_selectedHand == null || !File.Exists(_selectedHand.Table.SourcePath))
            {
                _preflopActions = _flopActions = _turnActions = _riverActions = Array.Empty<StreetActionViewModel>();
            }
            else
            {
                var handsRaw = PokerStarsHandHistory.SplitHands(File.ReadLines(_selectedHand.Table.SourcePath)).ToList();
                if (_selectedHand.HandIndex > 0 && _selectedHand.HandIndex <= handsRaw.Count)
                {
                    var handLines = handsRaw[_selectedHand.HandIndex - 1].ToList();
                    var positions = PokerStarsHandHistory.BuildPositionMap(handLines);
                    var hero = _selectedHand.Table.HeroName;
                    var bb = _selectedHand.Table.BigBlind;

                    _preflopActions = ExtractStreetActions(handLines, "PREFLOP", hero, positions, bb);
                    _flopActions = ExtractStreetActions(handLines, "FLOP", hero, positions, bb);
                    _turnActions = ExtractStreetActions(handLines, "TURN", hero, positions, bb);
                    _riverActions = ExtractStreetActions(handLines, "RIVER", hero, positions, bb, true);
                }
            }

            OnPropertyChanged(nameof(PreflopActions));
            OnPropertyChanged(nameof(FlopActions));
            OnPropertyChanged(nameof(TurnActions));
            OnPropertyChanged(nameof(RiverActions));
        }

        private static IReadOnlyList<StreetActionViewModel> ExtractStreetActions(
            IReadOnlyList<string> hand, string street, string heroName, 
            IReadOnlyDictionary<string, string> positions, double bigBlind, bool takeUntilEnd = false)
        {
            var start = street == "PREFLOP" ? 0 : PokerStarsHandHistory.FindStreetIndex(hand, street);
            if (start < 0) return Array.Empty<StreetActionViewModel>();

            var end = hand.Count;
            if (!takeUntilEnd)
            {
                var markers = street switch {
                    "PREFLOP" => new[] { "FLOP", "TURN", "RIVER", "SHOW", "SUMMARY" },
                    "FLOP" => new[] { "TURN", "RIVER", "SHOW", "SUMMARY" },
                    "TURN" => new[] { "RIVER", "SHOW", "SUMMARY" },
                    _ => new[] { "SHOW", "SUMMARY" }
                };

                foreach (var marker in markers) {
                    var idx = PokerStarsHandHistory.FindStreetIndex(hand, marker, start + 1);
                    if (idx >= 0) { end = idx; break; }
                }
            }

            var streetLines = hand.Skip(start).Take(end - start);
            return streetLines.Where(IsActionLine).Select(line => CreateAction(line, heroName, positions, bigBlind)).ToList();
        }

        private static bool IsActionLine(string line)
        {
            // Filter out seat info and table metadata
            if (line.StartsWith("Seat ", StringComparison.OrdinalIgnoreCase) && line.Contains(":")) return false;
            if (line.Contains("is the button", StringComparison.OrdinalIgnoreCase)) return false;
            if (line.StartsWith("Table '", StringComparison.OrdinalIgnoreCase)) return false;

            return IsPlayerActionLine(line) || 
                   line.Contains("collected") || 
                   line.Contains("cobra") || 
                   line.Contains("recupera") ||
                   line.Contains("muestra") ||
                   line.Contains("shows") ||
                   line.Contains("mucks") ||
                   line.Contains("uncalled");
        }

        private static bool IsPlayerActionLine(string line)
        {
            var match = PokerStarsHandHistory.ActorRx.Match(line);
            if (!match.Success) return false;

            var action = PokerStarsHandHistory.NormalizeAction(match.Groups["action"].Value);
            return action is "posts" or "folds" or "checks" or "calls" or "bets" or "raises" or "all-in" or "mucks" ||
                   match.Groups["action"].Value.Contains("shows", StringComparison.OrdinalIgnoreCase) ||
                   match.Groups["action"].Value.Contains("muestra", StringComparison.OrdinalIgnoreCase);
        }

        private static StreetActionViewModel CreateAction(string line, string heroName, IReadOnlyDictionary<string, string> positions, double bb)
        {
            var actor = ExtractActor(line);
            var isHero = PokerStarsHandHistory.SamePlayer(actor, heroName);
            var isSystem = string.IsNullOrEmpty(actor);
            var isBoard = line.StartsWith("***");
            
            var translated = HandHistoryTranslator.TranslateActionLine(line, actor, heroName, positions, bb);
            return new StreetActionViewModel(translated, isHero, isSystem, isBoard, false);
        }

        private static string ExtractActor(string line)
        {
            var actor = PokerStarsHandHistory.ActorRx.Match(line);
            if (actor.Success) return PokerStarsHandHistory.NormalizeName(actor.Groups["actor"].Value);
            return "";
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

