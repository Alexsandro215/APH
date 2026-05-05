using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Hud.App.Views
{
    public partial class MisTablasWindow : Window
    {
        private static readonly string[] Ranks = { "A", "K", "Q", "J", "T", "9", "8", "7", "6", "5", "4", "3", "2" };
        private static readonly Regex DealtRx = new(@"^Dealt to\s+(?<hero>.+?)\s+\[(?<cards>.+?)\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ActorRx = new(@"^(?<actor>[^:]+):\s+(?<action>.+)$", RegexOptions.Compiled);
        private static readonly Regex CollectedRx = new(@"^(?<name>[^:]+?)\s+collected\s+\$?(?<amount>[\d,.]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ReturnedRx = new(@"^Uncalled bet \(\$?(?<amount>[\d,.]+)\) returned to (?<name>.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex RaiseToRx = new(@"raises\s+\$?[\d,.]+\s+to\s+\$?(?<amount>[\d,.]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ActionAmountRx = new(@":\s+(?:posts (?:small blind|big blind|the ante)|calls|bets)\s+\$?(?<amount>[\d,.]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex BoardCardsRx = new(@"\[(?<cards>[^\]]+)\]", RegexOptions.Compiled);

        private readonly IReadOnlyList<MainWindow.TableSessionStats> _tables;
        private readonly List<ExactHandItem> _allExamples = new();
        private readonly Dictionary<string, HandCellStats> _cells = new(StringComparer.OrdinalIgnoreCase);
        private HandCellStats? _selectedCell;
        private HeroAction? _exactActionFilter;
        private string _exactResultFilter = "ALL";
        private bool _colorByProfit;
        private string _street = "PREFLOP";

        private static readonly HeroAction[] ActionDisplayOrder =
        {
            HeroAction.Fold,
            HeroAction.Check,
            HeroAction.Call,
            HeroAction.Bet,
            HeroAction.Raise,
            HeroAction.ThreeBet,
            HeroAction.FourBetPlus,
            HeroAction.AllIn
        };

        public MisTablasWindow(IEnumerable<MainWindow.TableSessionStats> tables, string summary)
        {
            InitializeComponent();
            Height = SystemParameters.WorkArea.Height;
            Width = Math.Min(1420, SystemParameters.WorkArea.Width);
            MaxHeight = SystemParameters.WorkArea.Height;
            MaxWidth = SystemParameters.WorkArea.Width;
            Top = SystemParameters.WorkArea.Top;
            _tables = tables.ToList();
            LoadStats();
            PopulateFilterOptions();
            RebuildFilteredCells();
            RenderGrid();
        }

        private void Street_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not string street)
                return;

            _street = street;
            RenderGrid();
        }

        private void Filter_Changed(object sender, RoutedEventArgs e)
        {
            if (_allExamples.Count == 0)
                return;

            RebuildFilteredCells();
            RenderGrid();
        }

        private void ProfitColorToggle_Changed(object sender, RoutedEventArgs e)
        {
            _colorByProfit = ProfitColorToggle.IsChecked == true;
            RenderGrid();
        }

        private void LoadStats()
        {
            foreach (var table in _tables)
            {
                if (!File.Exists(table.SourcePath))
                    continue;

                var tableHandNumber = 0;
                var cumulativeBb = 0.0;
                foreach (var hand in SplitHands(File.ReadLines(table.SourcePath)))
                {
                    if (!TryGetHeroHandInfo(hand, table.HeroName, out var handCode, out var exactCards))
                        continue;

                    tableHandNumber++;
                    var netAmount = EstimateHeroNetForHand(hand, table.HeroName);
                    var netBb = table.BigBlind > 0 ? netAmount / table.BigBlind : 0;
                    cumulativeBb += netBb;
                    var spot = ExtractBoardSpot(hand);
                    var positions = BuildPositionMap(hand);
                    var position = positions.TryGetValue(table.HeroName, out var heroPosition)
                        ? heroPosition
                        : "?";
                    var handDateTime = ExtractHandDateTime(hand) ?? table.LastPlayedAt;

                    foreach (var street in new[] { "PREFLOP", "FLOP", "TURN", "RIVER" })
                    {
                        var action = DetectHeroAction(hand, table.HeroName, street);
                        if (action is null)
                            continue;

                        _allExamples.Add(
                            new ExactHandItem(
                                street,
                                handCode,
                                table.TableName,
                                table.SourcePath,
                                tableHandNumber,
                                position,
                                table.GameFormat,
                                table.IsCash,
                                table.LastPlayedAt.Date,
                                handDateTime,
                                exactCards,
                                spot,
                                action.Value,
                                netBb,
                                cumulativeBb));
                    }
                }
            }
        }

        private void PopulateFilterOptions()
        {
            PositionFilter.ItemsSource = new[] { "Todas" }
                .Concat(_allExamples.Select(example => example.Position)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(PositionSort)
                    .ThenBy(value => value, StringComparer.OrdinalIgnoreCase))
                .ToList();
            PositionFilter.SelectedIndex = 0;

            FormatFilter.ItemsSource = new[] { "Todos" }
                .Concat(_allExamples.Select(example => example.GameFormat)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
                .ToList();
            FormatFilter.SelectedIndex = 0;

            MoneyTypeFilter.ItemsSource = new[] { "Todos", "Cash", "Fichas" };
            MoneyTypeFilter.SelectedIndex = 0;
        }

        private void RebuildFilteredCells()
        {
            _cells.Clear();
            foreach (var example in FilterExamples(_allExamples))
            {
                var key = CellKey(example.Street, example.HandCode);
                if (!_cells.TryGetValue(key, out var cell))
                {
                    cell = new HandCellStats(example.Street, example.HandCode);
                    _cells[key] = cell;
                }

                cell.Add(example.Action, example.NetBb, example);
            }
        }

        private IEnumerable<ExactHandItem> FilterExamples(IEnumerable<ExactHandItem> examples)
        {
            var position = SelectedFilter(PositionFilter);
            var format = SelectedFilter(FormatFilter);
            var moneyType = SelectedFilter(MoneyTypeFilter);
            var from = FromDateFilter.SelectedDate?.Date;
            var to = ToDateFilter.SelectedDate?.Date;

            return examples.Where(example =>
                (position is null || string.Equals(example.Position, position, StringComparison.OrdinalIgnoreCase)) &&
                (format is null || string.Equals(example.GameFormat, format, StringComparison.OrdinalIgnoreCase)) &&
                (moneyType is null ||
                    (moneyType == "Cash" && example.IsCash) ||
                    (moneyType == "Fichas" && !example.IsCash)) &&
                (from is null || example.PlayedAt >= from.Value) &&
                (to is null || example.PlayedAt <= to.Value));
        }

        private static string? SelectedFilter(ComboBox comboBox)
        {
            var value = comboBox.SelectedItem as string;
            return string.IsNullOrWhiteSpace(value) || value is "Todas" or "Todos"
                ? null
                : value;
        }

        private static int PositionSort(string position) =>
            position switch
            {
                "BTN" => 0,
                "BTN/SB" => 1,
                "SB" => 2,
                "BB" => 3,
                "UTG" => 4,
                "MP" => 5,
                "HJ" => 6,
                "CO" => 7,
                "?" => 99,
                _ => 50
            };

        private void RenderGrid()
        {
            SelectedStreetText.Text = StreetLabel(_street);
            ActionSummaryList.ItemsSource = null;
            SelectedHandText.Text = "Selecciona una celda";
            ExactHandsPanel.Visibility = Visibility.Collapsed;
            ExactHandsGrid.ItemsSource = null;
            _selectedCell = null;
            _exactActionFilter = null;
            _exactResultFilter = "ALL";
            RefreshExactFilterChips();

            HandsGrid.Children.Clear();
            for (var row = 0; row < Ranks.Length; row++)
            {
                for (var col = 0; col < Ranks.Length; col++)
                {
                    var handCode = HandCode(row, col);
                    _cells.TryGetValue(CellKey(_street, handCode), out var stats);
                    HandsGrid.Children.Add(CreateCell(handCode, stats));
                }
            }
        }

        private Button CreateCell(string handCode, HandCellStats? stats)
        {
            var root = new Grid
            {
                ClipToBounds = true,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            var segments = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            root.Children.Add(segments);

            if (stats is null || stats.Total == 0)
            {
                segments.Background = new SolidColorBrush(Color.FromRgb(38, 43, 50));
            }
            else
            {
                foreach (var action in ActionDisplayOrder.Where(stats.Actions.ContainsKey))
                {
                    segments.ColumnDefinitions.Add(new ColumnDefinition
                    {
                        Width = new GridLength(stats.Actions[action].Count, GridUnitType.Star)
                    });
                }

                var index = 0;
                foreach (var action in ActionDisplayOrder.Where(stats.Actions.ContainsKey))
                {
                    var border = new Border
                    {
                        Background = _colorByProfit
                            ? BrushForBb(stats.Actions[action].AverageBb)
                            : BrushForAction(action),
                        BorderBrush = Brushes.Black,
                        BorderThickness = index == 0 ? new Thickness(0) : new Thickness(1, 0, 0, 0)
                    };
                    Grid.SetColumn(border, index++);
                    segments.Children.Add(border);
                }
            }

            var overlay = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(4, 7, 10)),
                BorderThickness = new Thickness(1)
            };
            root.Children.Add(overlay);

            var label = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            label.Children.Add(new TextBlock
            {
                Text = handCode,
                Foreground = stats is null || stats.Total == 0
                    ? new SolidColorBrush(Color.FromRgb(135, 145, 156))
                    : Brushes.Black,
                FontWeight = FontWeights.Bold,
                FontSize = 15,
                HorizontalAlignment = HorizontalAlignment.Center
            });

            if (stats is not null && stats.Total > 0)
            {
                label.Children.Add(new TextBlock
                {
                    Text = $"{stats.Total}",
                    Foreground = Brushes.Black,
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center
                });
            }

            root.Children.Add(label);

            var button = new Button
            {
                Content = root,
                Style = (Style)FindResource("MatrixCellButton"),
                Padding = new Thickness(0),
                Margin = new Thickness(0),
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Tag = stats ?? new HandCellStats(_street, handCode),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                ToolTip = stats is null || stats.Total == 0
                    ? $"{handCode} | sin muestra"
                    : $"{handCode} | {stats.Total} acciones | {stats.AverageBb:0.#} bb media"
            };

            button.Click += Cell_Click;
            return button;
        }

        private void Cell_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not HandCellStats stats)
                return;

            SelectedHandText.Text = stats.HandCode;
            SelectedStreetText.Text = StreetLabel(stats.Street);
            ActionSummaryList.ItemsSource = stats.SummaryItems(_colorByProfit);
            _selectedCell = stats;
            _exactActionFilter = null;
            _exactResultFilter = "ALL";
            RefreshExactFilterChips();

            if (stats.Examples.Count == 0)
            {
                ExactHandsPanel.Visibility = Visibility.Collapsed;
                ExactHandsGrid.ItemsSource = null;
                return;
            }

            ExactHandsTitle.Text = $"{stats.HandCode} | {StreetLabel(stats.Street)} | Manos exactas";
            ApplyExactHandsFilter();
            ExactHandsPanel.Visibility = Visibility.Visible;
        }

        private void ExactActionFilter_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not string tag)
                return;

            _exactActionFilter = tag == "ALL" ? null : ActionFromLabel(tag);
            RefreshExactFilterChips();
            ApplyExactHandsFilter();
        }

        private void ExactResultFilter_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not string tag)
                return;

            _exactResultFilter = tag;
            RefreshExactFilterChips();
            ApplyExactHandsFilter();
        }

        private void ApplyExactHandsFilter()
        {
            if (_selectedCell is null)
            {
                ExactHandsGrid.ItemsSource = null;
                return;
            }

            ExactHandsGrid.ItemsSource = _selectedCell.Examples
                .Where(example => _exactActionFilter is null || example.Action == _exactActionFilter.Value)
                .Where(example => _exactResultFilter switch
                {
                    "WIN" => example.NetBb > 0,
                    "LOSS" => example.NetBb < 0,
                    _ => true
                })
                .OrderBy(example => example.TableName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(example => example.HandNumber)
                .ToList();
        }

        private void RefreshExactFilterChips()
        {
            var selectedBrush = new SolidColorBrush(Color.FromRgb(143, 211, 244));
            var selectedForeground = Brushes.Black;
            var defaultBrush = new SolidColorBrush(Color.FromRgb(17, 26, 36));
            var defaultForeground = (Brush)FindResource("Brush.Text");

            void SetChip(Button button, bool selected)
            {
                button.Background = selected ? selectedBrush : defaultBrush;
                button.Foreground = selected ? selectedForeground : defaultForeground;
                button.BorderBrush = selected ? selectedBrush : new SolidColorBrush(Color.FromRgb(42, 58, 76));
            }

            SetChip(ActionAllChip, _exactActionFilter is null);
            SetChip(ActionFoldChip, _exactActionFilter == HeroAction.Fold);
            SetChip(ActionCheckChip, _exactActionFilter == HeroAction.Check);
            SetChip(ActionCallChip, _exactActionFilter == HeroAction.Call);
            SetChip(ActionBetChip, _exactActionFilter == HeroAction.Bet);
            SetChip(ActionRaiseChip, _exactActionFilter == HeroAction.Raise);
            SetChip(ActionThreeBetChip, _exactActionFilter == HeroAction.ThreeBet);
            SetChip(ActionFourBetChip, _exactActionFilter == HeroAction.FourBetPlus);
            SetChip(ActionAllInChip, _exactActionFilter == HeroAction.AllIn);

            SetChip(ResultAllChip, _exactResultFilter == "ALL");
            SetChip(ResultWinChip, _exactResultFilter == "WIN");
            SetChip(ResultLossChip, _exactResultFilter == "LOSS");
        }

        private void ExactHandsGrid_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (ExactHandsGrid.SelectedItem is not ExactHandItem item)
                return;

            var table = _tables.FirstOrDefault(t =>
                string.Equals(t.SourcePath, item.SourcePath, StringComparison.OrdinalIgnoreCase));
            if (table is null)
                return;

            var window = new TableDetailWindow(table, item.HandNumber)
            {
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = true
            };

            window.Show();
        }

        private static IEnumerable<IReadOnlyList<string>> SplitHands(IEnumerable<string> lines)
        {
            var current = new List<string>();
            foreach (var line in lines)
            {
                if (line.StartsWith("PokerStars Hand #", StringComparison.Ordinal) && current.Count > 0)
                {
                    yield return current.ToList();
                    current.Clear();
                }

                current.Add(line);
            }

            if (current.Count > 0)
                yield return current;
        }

        private static bool TryGetHeroHandInfo(
            IReadOnlyList<string> hand,
            string heroName,
            out string handCode,
            out string exactCards)
        {
            foreach (var line in hand)
            {
                var match = DealtRx.Match(line);
                if (!match.Success)
                    continue;

                if (!string.Equals(match.Groups["hero"].Value.Trim(), heroName, StringComparison.Ordinal))
                    continue;

                var cards = match.Groups["cards"].Value.Trim();
                handCode = NormalizeHand(cards);
                exactCards = FormatCards(cards);
                return true;
            }

            handCode = "";
            exactCards = "";
            return false;
        }

        private static string FormatCards(string cards) =>
            string.Join(" ", cards.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(FormatCardForDisplay));

        private static string ExtractBoardSpot(IReadOnlyList<string> hand)
        {
            var board = new List<string>();
            foreach (var line in hand)
            {
                if (!line.StartsWith("*** FLOP ***", StringComparison.Ordinal) &&
                    !line.StartsWith("*** TURN ***", StringComparison.Ordinal) &&
                    !line.StartsWith("*** RIVER ***", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (Match match in BoardCardsRx.Matches(line))
                {
                    foreach (var rawCard in match.Groups["cards"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var card = FormatCardForDisplay(rawCard);
                        if (!board.Contains(card, StringComparer.Ordinal))
                            board.Add(card);
                    }
                }
            }

            return board.Count == 0 ? "-" : string.Join(" - ", board);
        }

        private static DateTime? ExtractHandDateTime(IReadOnlyList<string> hand)
        {
            var header = hand.FirstOrDefault(line => line.StartsWith("PokerStars Hand #", StringComparison.Ordinal));
            if (string.IsNullOrWhiteSpace(header))
                return null;

            var match = Regex.Match(
                header,
                @"-\s+(?<date>\d{4}/\d{2}/\d{2})\s+(?<time>\d{2}:\d{2}:\d{2})",
                RegexOptions.IgnoreCase);

            if (!match.Success)
                return null;

            return DateTime.TryParseExact(
                $"{match.Groups["date"].Value} {match.Groups["time"].Value}",
                "yyyy/MM/dd HH:mm:ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed)
                ? parsed
                : null;
        }

        private static Dictionary<string, string> BuildPositionMap(IReadOnlyList<string> hand)
        {
            var buttonSeat = 0;
            var seats = new Dictionary<int, string>();

            foreach (var line in hand)
            {
                if (buttonSeat == 0)
                {
                    var buttonMatch = Regex.Match(line, @"Seat #(?<seat>\d+) is the button", RegexOptions.IgnoreCase);
                    if (buttonMatch.Success)
                        int.TryParse(buttonMatch.Groups["seat"].Value, out buttonSeat);
                }

                var seatMatch = Regex.Match(line, @"^Seat\s+(?<seat>\d+):\s+(?<name>\S+)", RegexOptions.IgnoreCase);
                if (seatMatch.Success && int.TryParse(seatMatch.Groups["seat"].Value, out var seat))
                    seats[seat] = seatMatch.Groups["name"].Value.Trim();
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

        private static string PositionFromOffset(int offset, int playerCount) =>
            playerCount switch
            {
                <= 2 => offset == 0 ? "BTN/SB" : "BB",
                3 => offset switch { 0 => "BTN", 1 => "SB", 2 => "BB", _ => "?" },
                4 => offset switch { 0 => "BTN", 1 => "SB", 2 => "BB", 3 => "CO", _ => "?" },
                5 => offset switch { 0 => "BTN", 1 => "SB", 2 => "BB", 3 => "UTG", 4 => "CO", _ => "?" },
                _ => offset switch { 0 => "BTN", 1 => "SB", 2 => "BB", 3 => "UTG", 4 => "HJ", 5 => "CO", _ => "MP" }
            };

        private static string FormatCardForDisplay(string card)
        {
            if (card.Length < 2)
                return card;

            var rank = card[..^1].ToUpperInvariant();
            var suit = char.ToLowerInvariant(card[^1]) switch
            {
                'h' => "\u2665",
                'd' => "\u2666",
                'c' => "\u2663",
                's' => "\u2660",
                _ => ""
            };

            return suit.Length == 0 ? card : $"{rank}{suit}";
        }

        private static HeroAction? DetectHeroAction(IReadOnlyList<string> hand, string heroName, string street)
        {
            var lines = GetStreetLines(hand, street).ToList();
            var raisesBeforeHero = 0;

            foreach (var line in lines)
            {
                var match = ActorRx.Match(line);
                if (!match.Success)
                    continue;

                var actor = match.Groups["actor"].Value.Trim();
                var actionText = match.Groups["action"].Value.Trim();
                var isRaise = actionText.StartsWith("raises ", StringComparison.OrdinalIgnoreCase);

                if (string.Equals(actor, heroName, StringComparison.Ordinal))
                {
                    if (actionText.Contains("all-in", StringComparison.OrdinalIgnoreCase))
                        return HeroAction.AllIn;
                    if (actionText.StartsWith("folds", StringComparison.OrdinalIgnoreCase))
                        return HeroAction.Fold;
                    if (actionText.StartsWith("checks", StringComparison.OrdinalIgnoreCase))
                        return HeroAction.Check;
                    if (actionText.StartsWith("calls", StringComparison.OrdinalIgnoreCase))
                        return HeroAction.Call;
                    if (actionText.StartsWith("bets", StringComparison.OrdinalIgnoreCase))
                        return HeroAction.Bet;
                    if (isRaise)
                    {
                        if (street == "PREFLOP")
                            return raisesBeforeHero switch
                            {
                                0 => HeroAction.Raise,
                                1 => HeroAction.ThreeBet,
                                _ => HeroAction.FourBetPlus
                            };

                        return HeroAction.Raise;
                    }
                }

                if (isRaise)
                    raisesBeforeHero++;
            }

            return null;
        }

        private static IEnumerable<string> GetStreetLines(IReadOnlyList<string> hand, string street)
        {
            var start = street == "PREFLOP" ? 0 : FindStreetIndex(hand, street);
            if (start < 0)
                return Enumerable.Empty<string>();

            var end = hand.Count;
            foreach (var next in street switch
            {
                "PREFLOP" => new[] { "FLOP", "TURN", "RIVER", "SHOW" },
                "FLOP" => new[] { "TURN", "RIVER", "SHOW" },
                "TURN" => new[] { "RIVER", "SHOW" },
                _ => new[] { "SHOW" }
            })
            {
                var idx = FindStreetIndex(hand, next, start + 1);
                if (idx >= 0)
                {
                    end = idx;
                    break;
                }
            }

            return hand.Skip(start).Take(end - start);
        }

        private static int FindStreetIndex(IReadOnlyList<string> hand, string street, int startAt = 0)
        {
            for (var i = startAt; i < hand.Count; i++)
            {
                var line = hand[i];
                if (street == "SHOW" && line.StartsWith("*** SUMMARY ***", StringComparison.Ordinal))
                    return i;
                if (line.StartsWith($"*** {street} ***", StringComparison.Ordinal))
                    return i;
            }

            return -1;
        }

        private static double EstimateHeroNetForHand(IReadOnlyList<string> hand, string heroName)
        {
            var committed = 0.0;
            var won = 0.0;
            var committedThisStreet = 0.0;

            foreach (var line in hand)
            {
                if (line.StartsWith("*** FLOP ***", StringComparison.Ordinal) ||
                    line.StartsWith("*** TURN ***", StringComparison.Ordinal) ||
                    line.StartsWith("*** RIVER ***", StringComparison.Ordinal))
                {
                    committedThisStreet = 0;
                    continue;
                }

                var actorMatch = ActorRx.Match(line);
                if (actorMatch.Success && string.Equals(actorMatch.Groups["actor"].Value.Trim(), heroName, StringComparison.Ordinal))
                {
                    if (line.Contains("raises ", StringComparison.OrdinalIgnoreCase))
                    {
                        var raiseMatch = RaiseToRx.Match(line);
                        if (raiseMatch.Success && TryParseAmount(raiseMatch.Groups["amount"].Value, out var raiseTo))
                        {
                            var delta = Math.Max(0, raiseTo - committedThisStreet);
                            committed += delta;
                            committedThisStreet += delta;
                            continue;
                        }
                    }

                    var amountMatch = ActionAmountRx.Match(line);
                    if (amountMatch.Success && TryParseAmount(amountMatch.Groups["amount"].Value, out var amount))
                    {
                        committed += amount;
                        committedThisStreet += amount;
                    }
                }

                var returnedMatch = ReturnedRx.Match(line);
                if (returnedMatch.Success &&
                    string.Equals(returnedMatch.Groups["name"].Value.Trim(), heroName, StringComparison.Ordinal) &&
                    TryParseAmount(returnedMatch.Groups["amount"].Value, out var returned))
                {
                    committed -= returned;
                }

                var collectedMatch = CollectedRx.Match(line);
                if (collectedMatch.Success &&
                    string.Equals(collectedMatch.Groups["name"].Value.Trim(), heroName, StringComparison.Ordinal) &&
                    TryParseAmount(collectedMatch.Groups["amount"].Value, out var collected))
                {
                    won += collected;
                }
            }

            return won - committed;
        }

        private static bool TryParseAmount(string raw, out double value)
        {
            var clean = raw.Replace("$", "", StringComparison.Ordinal)
                .Replace("US", "", StringComparison.OrdinalIgnoreCase)
                .Replace("€", "", StringComparison.Ordinal)
                .Trim();

            if (clean.Contains(',') && clean.Contains('.'))
                clean = clean.Replace(",", "", StringComparison.Ordinal);
            else
                clean = clean.Replace(',', '.');

            return double.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static string NormalizeHand(string cards)
        {
            var parts = cards.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                return "--";

            var first = ParseCard(parts[0]);
            var second = ParseCard(parts[1]);
            if (first.rank == second.rank)
                return $"{first.rank}{second.rank}";

            var firstIndex = Array.IndexOf(Ranks, first.rank);
            var secondIndex = Array.IndexOf(Ranks, second.rank);
            var high = firstIndex < secondIndex ? first : second;
            var low = firstIndex < secondIndex ? second : first;
            return $"{high.rank}{low.rank}{(high.suit == low.suit ? "s" : "o")}";
        }

        private static (string rank, char suit) ParseCard(string raw)
        {
            var card = raw.Trim();
            if (card.Length < 2)
                return (card, '?');

            return (card[..^1].ToUpperInvariant(), char.ToLowerInvariant(card[^1]));
        }

        private static string HandCode(int row, int col)
        {
            if (row == col)
                return $"{Ranks[row]}{Ranks[col]}";
            if (row < col)
                return $"{Ranks[row]}{Ranks[col]}s";
            return $"{Ranks[col]}{Ranks[row]}o";
        }

        private static string CellKey(string street, string handCode) => $"{street}:{handCode}";

        private static string StreetLabel(string street) => street switch
        {
            "PREFLOP" => "PRE-FLOP",
            "FLOP" => "FLOP",
            "TURN" => "TURN",
            "RIVER" => "RIVER",
            _ => street
        };

        private static Brush BrushForAction(HeroAction action) => action switch
        {
            HeroAction.Fold => new SolidColorBrush(Color.FromRgb(80, 86, 96)),
            HeroAction.Check => new SolidColorBrush(Color.FromRgb(75, 102, 122)),
            HeroAction.Call => new SolidColorBrush(Color.FromRgb(0, 148, 198)),
            HeroAction.Bet => new SolidColorBrush(Color.FromRgb(64, 184, 4)),
            HeroAction.Raise => new SolidColorBrush(Color.FromRgb(184, 181, 4)),
            HeroAction.ThreeBet => new SolidColorBrush(Color.FromRgb(226, 137, 0)),
            HeroAction.FourBetPlus => new SolidColorBrush(Color.FromRgb(255, 115, 115)),
            HeroAction.AllIn => new SolidColorBrush(Color.FromRgb(156, 0, 0)),
            _ => Brushes.Transparent
        };

        private static Brush BrushForBb(double bb)
        {
            var abs = Math.Abs(bb);
            if (abs < 0.05)
                return new SolidColorBrush(Color.FromRgb(80, 86, 96));

            if (bb > 0)
            {
                if (abs < 10)
                    return new SolidColorBrush(Color.FromRgb(34, 116, 101));
                if (abs < 30)
                    return new SolidColorBrush(Color.FromRgb(39, 156, 92));
                if (abs < 50)
                    return new SolidColorBrush(Color.FromRgb(76, 184, 4));
                return new SolidColorBrush(Color.FromRgb(184, 181, 4));
            }

            if (abs < 10)
                return new SolidColorBrush(Color.FromRgb(112, 48, 55));
            if (abs < 30)
                return new SolidColorBrush(Color.FromRgb(178, 43, 52));
            if (abs < 50)
                return new SolidColorBrush(Color.FromRgb(210, 34, 45));
            return new SolidColorBrush(Color.FromRgb(156, 0, 0));
        }

        private static string LabelForAction(HeroAction action) => action switch
        {
            HeroAction.Fold => "Fold",
            HeroAction.Check => "Check",
            HeroAction.Call => "Call",
            HeroAction.Bet => "Bet",
            HeroAction.Raise => "Raise",
            HeroAction.ThreeBet => "3Bet",
            HeroAction.FourBetPlus => "4Bet+",
            HeroAction.AllIn => "All-in",
            _ => action.ToString()
        };

        private static HeroAction? ActionFromLabel(string label) => label switch
        {
            "Fold" => HeroAction.Fold,
            "Check" => HeroAction.Check,
            "Call" => HeroAction.Call,
            "Bet" => HeroAction.Bet,
            "Raise" => HeroAction.Raise,
            "3Bet" => HeroAction.ThreeBet,
            "4Bet+" => HeroAction.FourBetPlus,
            "All-in" => HeroAction.AllIn,
            _ => null
        };

        private enum HeroAction
        {
            Fold,
            Check,
            Call,
            Bet,
            Raise,
            ThreeBet,
            FourBetPlus,
            AllIn
        }

        private sealed class HandCellStats
        {
            public HandCellStats(string street, string handCode)
            {
                Street = street;
                HandCode = handCode;
            }

            public string Street { get; }
            public string HandCode { get; }
            public Dictionary<HeroAction, ActionStats> Actions { get; } = new();
            public List<ExactHandItem> Examples { get; } = new();
            public int Total => Actions.Values.Sum(action => action.Count);
            public double AverageBb => Total == 0 ? 0 : Actions.Values.Sum(action => action.TotalBb) / Total;

            public void Add(HeroAction action, double netBb, ExactHandItem example)
            {
                if (!Actions.TryGetValue(action, out var stats))
                {
                    stats = new ActionStats();
                    Actions[action] = stats;
                }

                stats.Count++;
                stats.TotalBb += netBb;
                Examples.Add(example);
            }

            public IReadOnlyList<ActionSummaryItem> SummaryItems(bool colorByProfit)
            {
                if (Total == 0)
                {
                    return new[]
                    {
                        new ActionSummaryItem("Sin muestra", "0 manos", new SolidColorBrush(Color.FromRgb(80, 86, 96)))
                    };
                }

                return ActionDisplayOrder
                    .Where(Actions.ContainsKey)
                    .Select(action =>
                    {
                        var actionStats = Actions[action];
                        var pct = actionStats.Count * 100.0 / Total;
                        var avg = actionStats.TotalBb / actionStats.Count;
                        return new ActionSummaryItem(
                            $"{LabelForAction(action)} {pct:0.#}%",
                            $"{actionStats.Count} manos | {avg:+0.#;-0.#;0} bb media",
                            colorByProfit ? BrushForBb(avg) : BrushForAction(action));
                    })
                    .ToList();
            }
        }

        private sealed class ActionStats
        {
            public int Count { get; set; }
            public double TotalBb { get; set; }
            public double AverageBb => Count == 0 ? 0 : TotalBb / Count;
        }

        private sealed record ActionSummaryItem(string Label, string Detail, Brush Color);

        private sealed record ExactHandItem(
            string Street,
            string HandCode,
            string TableName,
            string SourcePath,
            int HandNumber,
            string Position,
            string GameFormat,
            bool IsCash,
            DateTime PlayedAt,
            DateTime HandDateTime,
            string ExactCards,
            string Spot,
            HeroAction Action,
            double NetBb,
            double CumulativeBb)
        {
            public string HandDateTimeLabel => HandDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            public string ActionLabel => LabelForAction(Action);
            public string NetBbLabel => $"{NetBb:+0.#;-0.#;0} bb";
            public string CumulativeBbLabel => $"{CumulativeBb:+0.#;-0.#;0} bb";
            public string NetTrendIcon => NetBb >= 0 ? "\u25B2" : "\u25BC";
            public string CumulativeTrendIcon => CumulativeBb >= 0 ? "\u25B2" : "\u25BC";
            public IReadOnlyList<TextSegmentViewModel> ExactCardSegments =>
                BuildTextSegments(ExactCards, new SolidColorBrush(Color.FromRgb(143, 211, 244)));
            public IReadOnlyList<TextSegmentViewModel> SpotSegments =>
                BuildTextSegments(Spot, new SolidColorBrush(Color.FromRgb(143, 211, 244)));
            public Brush NetTrendBrush => NetBb >= 0
                ? new SolidColorBrush(Color.FromRgb(33, 192, 122))
                : new SolidColorBrush(Color.FromRgb(226, 78, 91));
            public Brush CumulativeTrendBrush => CumulativeBb >= 0
                ? new SolidColorBrush(Color.FromRgb(33, 192, 122))
                : new SolidColorBrush(Color.FromRgb(226, 78, 91));
        }

        private sealed record TextSegmentViewModel(string Text, Brush Foreground, FontWeight FontWeight);

        private static IReadOnlyList<TextSegmentViewModel> BuildTextSegments(string text, Brush baseForeground)
        {
            var segments = new List<TextSegmentViewModel>();
            var buffer = "";

            void Flush()
            {
                if (buffer.Length == 0)
                    return;

                segments.Add(new TextSegmentViewModel(buffer, baseForeground, FontWeights.SemiBold));
                buffer = "";
            }

            foreach (var ch in text)
            {
                var suitBrush = SuitBrush(ch);
                if (suitBrush is null)
                {
                    buffer += ch;
                    continue;
                }

                Flush();
                segments.Add(new TextSegmentViewModel(ch.ToString(), suitBrush, FontWeights.Bold));
            }

            Flush();
            return segments;
        }

        private static Brush? SuitBrush(char ch) =>
            ch switch
            {
                '\u2665' => new SolidColorBrush(Color.FromRgb(226, 78, 91)),
                '\u2666' => new SolidColorBrush(Color.FromRgb(26, 161, 209)),
                '\u2663' => new SolidColorBrush(Color.FromRgb(33, 192, 122)),
                '\u2660' => new SolidColorBrush(Color.FromRgb(170, 178, 188)),
                _ => null
            };
    }
}
