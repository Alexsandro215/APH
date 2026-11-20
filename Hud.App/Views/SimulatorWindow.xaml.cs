using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Hud.App.Services;
using Hud.App.Services.Simulator;
using Hud.App.Services.Trainer;

namespace Hud.App.Views
{
    /// <summary>
    /// Simulador: manos completas contra la maquina (heads-up o mesa de 6) por rubros. La maquina
    /// juega con perfiles medidos en miles de manos; al final de cada mano se revisa cada decision
    /// con el rango real de la maquina, la equity, las pot odds y el valor de cada opcion.
    /// </summary>
    public partial class SimulatorWindow : Window
    {
        private static readonly Dictionary<int, (string Symbol, Brush Brush)> SuitInk = new()
        {
            [3] = ("♠", Frozen(Color.FromRgb(0x1E, 0x1E, 0x1E))),
            [2] = ("♥", Frozen(Color.FromRgb(0xD3, 0x2F, 0x2F))),
            [1] = ("♦", Frozen(Color.FromRgb(0x1E, 0x63, 0xC9))),
            [0] = ("♣", Frozen(Color.FromRgb(0x1E, 0x8A, 0x3A))),
        };
        private static readonly Brush CardFace = Frozen(Color.FromRgb(0xF6, 0xF5, 0xF0));
        private static readonly Color RangeHue = Color.FromRgb(0x39, 0x87, 0xE5);

        private readonly Random _random = new();
        private readonly SimProgress _progress = SimProgress.Load();
        private bool _headsUp = true;
        private SimDrill _drill = SimDrills.All[0];
        private SimProfile _chosen = SimProfile.Tag;
        private SimSession? _session;
        private bool _running;
        private bool _alive = true;
        private int _handNumber;
        private List<SimDecisionReview>? _reviews;
        private int _loggedActions;
        private int _loggedBoard;
        private int _sessionHands;
        private double _sessionNet;
        private int _sessionDecisions;
        private int _sessionGood;

        public SimulatorWindow()
        {
            InitializeComponent();
            Title = T("Sim.Title");
            TitleText.Text = T("Sim.Title");
            SummaryText.Text = T("Sim.Summary");
            TableLabel.Text = T("Sim.Table");
            DrillLabel.Text = T("Sim.Drill");
            RivalLabel.Text = T("Sim.Rival");
            NewHandButton.Content = T("Sim.NewHand");
            SetName(NewHandButton, T("Sim.NewHand"));
            TechToggle.Content = T("Sim.Tech");
            TechToggle.ToolTip = T("Sim.TechDesc");
            SetName(TechToggle, T("Sim.Tech"));
            TechToggle.IsChecked = _progress.ShowTechnical;
            TechToggle.Checked += (_, _) => SetTechnical(true);
            TechToggle.Unchecked += (_, _) => SetTechnical(false);
            RootGrid.Unloaded += (_, _) => _alive = false;
            RootGrid.Loaded += (_, _) => { _alive = true; if (_session?.BotToAct == true) _ = RunBotsAsync(); };

            BuildTableChips();
            BuildDrills();
            BuildRival();
            UpdateSession();
            NewHand();
        }

        // ---------------------------------------------------------------- mesa, rubro y rival

        private void BuildTableChips()
        {
            TablePanel.Children.Clear();
            foreach (var (headsUp, key) in new[] { (true, "Sim.TableHu"), (false, "Sim.Table6") })
            {
                var chip = Chip("table", T(key), headsUp == _headsUp);
                chip.Checked += (_, _) =>
                {
                    _headsUp = headsUp;
                    _drill = SimDrills.All.First(d => d.HeadsUp == headsUp);
                    _session = null;
                    BuildDrills();
                    BuildRival();
                    NewHand();
                };
                TablePanel.Children.Add(chip);
            }
        }

        private void BuildDrills()
        {
            DrillList.Children.Clear();
            foreach (var drill in SimDrills.All.Where(d => d.HeadsUp == _headsUp))
            {
                var name = new TextBlock { Text = drill.Name, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
                var desc = new TextBlock { Text = drill.Description, FontSize = 11, Foreground = ThemeBrushes.TextDim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
                var stats = new TextBlock { Text = DrillStats(drill.Id), FontSize = 11, Foreground = ThemeBrushes.TextDim, Margin = new Thickness(0, 4, 0, 0) };
                var content = new StackPanel();
                content.Children.Add(name);
                content.Children.Add(desc);
                content.Children.Add(stats);
                var item = new RadioButton { Content = content, GroupName = "drill", IsChecked = drill == _drill, Style = (Style)FindResource("ListItem"), Tag = drill.Id };
                SetName(item, drill.Name);
                item.Checked += (_, _) =>
                {
                    _drill = drill;
                    _session = null;
                    BuildRival();
                    NewHand();
                };
                DrillList.Children.Add(item);
            }
        }

        private string DrillStats(string id)
        {
            if (!_progress.Drills.TryGetValue(id, out var row) || row[0] == 0)
                return T("Sim.NotStarted");
            var good = row[2] > 0 ? 100 * row[3] / row[2] : 0;
            return string.Format(CultureInfo.CurrentCulture, T("Sim.DrillStats"), row[0], row[1], good);
        }

        private void BuildRival()
        {
            RivalPanel.Children.Clear();
            if (_drill.ChooseProfile)
            {
                foreach (var profile in Enum.GetValues<SimProfile>())
                {
                    var chip = Chip("rival", SimProfiles.Name(profile), profile == _chosen);
                    chip.ToolTip = SimProfiles.Description(profile);
                    chip.Checked += (_, _) =>
                    {
                        _chosen = profile;
                        RivalText.Text = SimProfiles.Description(profile);
                        _session = null;
                        NewHand();
                    };
                    RivalPanel.Children.Add(chip);
                }
                RivalText.Text = SimProfiles.Description(_chosen);
            }
            else if (_drill.RandomBots)
            {
                RivalText.Text = T("Sim.RandomRivals");
            }
            else
            {
                RivalText.Text = string.Join(" · ", _drill.Bots.GroupBy(p => p).Select(g => (g.Count() > 1 ? $"{g.Count()}× " : "") + SimProfiles.Name(g.Key)))
                    + (_drill.Bots.Length == 1 ? "\n" + SimProfiles.Description(_drill.Bots[0]) : "");
            }
        }

        private RadioButton Chip(string group, string text, bool isChecked)
        {
            var chip = new RadioButton { Content = text, GroupName = group, IsChecked = isChecked, Style = (Style)FindResource("Chip") };
            SetName(chip, text);
            return chip;
        }

        // ---------------------------------------------------------------- flujo de la mano

        private void NewHand()
        {
            _session ??= new SimSession(_drill, _chosen, _random);
            _session.NewHand();
            _handNumber++;
            _reviews = null;
            _loggedActions = 0;
            _loggedBoard = 0;
            DrillTitle.Text = _drill.Name;
            ResultText.Text = "";
            RightTitle.Text = T("Sim.HandLog");
            RightList.Children.Clear();
            RangePanel.Visibility = Visibility.Collapsed;
            RangeZoom.Visibility = Visibility.Collapsed;
            var hero = _session.Hand!.Hero;
            Log(string.Format(CultureInfo.CurrentCulture, T("Sim.Log.Start"), hero.Position, CardsText(hero.Cards)), bold: true);
            LogNewActions();
            Render();
            _ = RunBotsAsync();
        }

        private async Task RunBotsAsync()
        {
            if (_running || _session is null)
                return;
            _running = true;
            var hand = _handNumber;
            try
            {
                while (_alive && hand == _handNumber && _session.BotToAct)
                {
                    var actor = _session.Hand!.ToAct!;
                    StatusText.Text = string.Format(CultureInfo.CurrentCulture, T("Sim.Thinking"), actor.Name, actor.Position);
                    ActionPanel.Children.Clear();
                    await Task.Delay(_session.Hand.Hero.Folded ? 120 : 650);
                    if (!_alive || hand != _handNumber)
                        return;
                    _session.BotStep();
                    LogNewActions();
                    Render();
                }
            }
            finally
            {
                _running = false;
            }

            if (hand != _handNumber || !_alive)
                return;
            if (_session.Hand!.IsOver)
                FinishHand();
            else if (_session.HeroToAct)
                ShowHeroActions();
        }

        private void HeroAct(SimActionKind kind, double to = 0)
        {
            if (_session?.HeroToAct != true)
                return;
            _session.HeroAct(kind, to);
            LogNewActions();
            Render();
            if (_session.Hand!.IsOver)
                FinishHand();
            else
                _ = RunBotsAsync();
        }

        private async void FinishHand()
        {
            var session = _session!;
            var hand = session.Hand!;
            var handNumber = _handNumber;
            ActionPanel.Children.Clear();
            AddButton(T("Sim.NewHandKey"), NewHand, accent: true);
            Render();

            var hero = hand.Hero;
            ResultText.Text = string.Format(CultureInfo.CurrentCulture, T(hero.Net >= 0 ? "Sim.Won" : "Sim.Lost"), Math.Abs(hero.Net));
            ResultText.SetResourceReference(TextBlock.ForegroundProperty, hero.Net >= 0 ? "Brush.Positive" : "Brush.Negative");
            StatusText.Text = Outcome(hand);
            Log(StatusText.Text, bold: true);

            RightTitle.Text = T("Sim.Reviewing");
            List<SimDecisionReview> reviews;
            try
            {
                reviews = await Task.Run(session.ReviewAll);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Simulador: no se pudo revisar la mano", ex);
                reviews = new List<SimDecisionReview>();
            }
            if (handNumber != _handNumber)
                return;

            _reviews = reviews;
            _progress.Record(_drill.Id, hero.Net, reviews);
            _progress.Save();
            _sessionHands++;
            _sessionNet += hero.Net;
            _sessionDecisions += reviews.Count;
            _sessionGood += reviews.Count(r => r.Verdict == SimVerdict.Good);
            UpdateSession();
            RefreshDrillStats();
            ShowReview();
        }

        private string Outcome(SimHand hand)
        {
            if (hand.Active.Count() == 1)
            {
                var w = hand.Active.First();
                return w.IsHero
                    ? string.Format(CultureInfo.CurrentCulture, T("Sim.WinNoShowdownYou"), w.Won - w.TotalBet)
                    : string.Format(CultureInfo.CurrentCulture, T("Sim.WinNoShowdown"), w.Name, w.Won - w.TotalBet);
            }
            var best = hand.Active.OrderByDescending(hand.ScoreOf).First();
            var category = HandInsightReader.CategoryName(FastHandEvaluator.Category(hand.ScoreOf(best)));
            var winners = hand.Active.Where(p => p.Won > 1e-9).ToList();
            if (winners.Count == 1 && winners[0].IsHero)
                return string.Format(CultureInfo.CurrentCulture, T("Sim.WinShowdownYou"), category);
            return string.Format(CultureInfo.CurrentCulture, T("Sim.WinShowdown"), string.Join(", ", winners.Select(p => p.Name)), category);
        }

        private void UpdateSession()
        {
            SessionText.Text = _sessionHands == 0
                ? T("Sim.SessionEmpty")
                : string.Format(CultureInfo.CurrentCulture, T("Sim.Session"), _sessionHands, _sessionNet, _sessionGood, _sessionDecisions);
        }

        private void RefreshDrillStats()
        {
            foreach (var item in DrillList.Children.OfType<RadioButton>())
                if (item.Content is StackPanel { Children.Count: 3 } content && content.Children[2] is TextBlock stats)
                    stats.Text = DrillStats((string)item.Tag);
        }

        // ---------------------------------------------------------------- botones del heroe

        private void ShowHeroActions()
        {
            var hand = _session!.Hand!;
            var options = hand.Options();
            ActionPanel.Children.Clear();
            StatusText.Text = TurnText(hand);

            if (options.FacingBet)
            {
                AddButton(T("Sim.Btn.Fold"), () => HeroAct(SimActionKind.Fold));
                AddButton(string.Format(CultureInfo.CurrentCulture, T("Sim.Btn.Call"), options.CallAmount), () => HeroAct(SimActionKind.Call));
            }
            else
            {
                AddButton(T("Sim.Btn.Check"), () => HeroAct(SimActionKind.Check));
            }

            if (options.CanRaise)
            {
                var first = true;
                foreach (var (label, to) in Presets(hand, options))
                {
                    var key = to >= options.MaxRaiseTo - 1e-9 ? " (A)" : first ? " (R)" : "";
                    AddButton(label + key, () => HeroAct(hand.CurrentBet > 0 ? SimActionKind.Raise : SimActionKind.Bet, to), tag: to >= options.MaxRaiseTo - 1e-9 ? "A" : first ? "R" : null);
                    first = false;
                }
            }
            Dispatcher.BeginInvoke(() => RootGrid.Focus(), DispatcherPriority.Input);
        }

        /// <summary>"Te toca": con la explicación técnica, también la equity que hace falta para pagar.</summary>
        private string TurnText(SimHand hand)
        {
            var o = hand.Options();
            if (!o.FacingBet)
                return T("Sim.YourTurn");
            return _progress.ShowTechnical
                ? string.Format(CultureInfo.CurrentCulture, T("Sim.YourTurnFacing"), o.CallAmount, hand.Pot, 100 * o.CallAmount / (hand.Pot + o.CallAmount))
                : string.Format(CultureInfo.CurrentCulture, T("Sim.YourTurnFacingSimple"), o.CallAmount, hand.Pot);
        }

        /// <summary>El interruptor "Explicación técnica": se recuerda entre sesiones.</summary>
        private void SetTechnical(bool on)
        {
            if (_progress.ShowTechnical == on)
                return;
            _progress.ShowTechnical = on;
            _progress.Save();
            if (_reviews is not null)
                ShowReview();
            if (_session?.HeroToAct == true)
                StatusText.Text = TurnText(_session.Hand!);
            RootGrid.Focus();
        }

        private static List<(string Label, double To)> Presets(SimHand hand, SimOptions o)
        {
            var list = new List<(string, double)>();
            void Add(string key, double to, params object[] extra)
            {
                to = Math.Clamp(Math.Round(to * 2, MidpointRounding.AwayFromZero) / 2, o.MinRaiseTo, o.MaxRaiseTo);
                if (list.Any(x => Math.Abs(x.Item2 - to) < 1e-9)) return;
                var label = to >= o.MaxRaiseTo - 1e-9
                    ? string.Format(CultureInfo.CurrentCulture, T("Sim.Btn.AllIn"), to)
                    : string.Format(CultureInfo.CurrentCulture, T(key), new object[] { to }.Concat(extra).ToArray());
                list.Add((label, to));
            }

            if (hand.Street == SimStreet.Preflop)
            {
                var spot = SimSpot.For(hand, hand.Hero) with { Profile = SimProfile.Tag };
                var standard = Math.Min(SimBot.RaiseTo(spot, o), o.MaxRaiseTo);
                Add("Sim.Btn.RaiseTo", standard);
                Add("Sim.Btn.RaiseTo", o.MinRaiseTo);
            }
            else if (!o.FacingBet)
            {
                foreach (var (fraction, name) in new[] { (0.33, "1/3"), (0.5, "1/2"), (0.75, "2/3"), (1.0, T("Sim.Opt.PotWord")) })
                    Add("Sim.Btn.Bet", fraction * hand.Pot, name);
            }
            else
            {
                Add("Sim.Btn.RaiseTo", hand.CurrentBet * 2.5);
                Add("Sim.Btn.RaiseTo", hand.CurrentBet * 3.5);
            }
            Add("Sim.Btn.AllIn", o.MaxRaiseTo);
            return list;
        }

        private void AddButton(string text, Action onClick, bool accent = false, string? tag = null)
        {
            var button = new Button
            {
                Content = text,
                Padding = new Thickness(16, 10, 16, 10),
                Margin = new Thickness(0, 0, 8, 8),
                FontSize = 14,
                Tag = tag
            };
            if (accent) button.SetResourceReference(StyleProperty, "Btn.Accent");
            SetName(button, text);
            button.Click += (_, _) => onClick();
            ActionPanel.Children.Add(button);
        }

        private void NewHandButton_Click(object sender, RoutedEventArgs e) => NewHand();

        private void RootGrid_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.T)
            {
                TechToggle.IsChecked = TechToggle.IsChecked != true;
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Escape && RangeZoom.Visibility == Visibility.Visible)
            {
                RangeZoom.Visibility = Visibility.Collapsed;
                e.Handled = true;
                return;
            }
            if (_session?.Hand is null)
                return;
            if (_session.Hand.IsOver && e.Key is Key.Enter or Key.Space or Key.N)
            {
                NewHand();
                e.Handled = true;
                return;
            }
            if (!_session.HeroToAct)
                return;

            var options = _session.Hand.Options();
            switch (e.Key)
            {
                case Key.F when options.FacingBet:
                    HeroAct(SimActionKind.Fold);
                    break;
                case Key.C:
                    HeroAct(options.FacingBet ? SimActionKind.Call : SimActionKind.Check);
                    break;
                case Key.R or Key.A when options.CanRaise:
                    var target = e.Key == Key.A ? "A" : "R";
                    var button = ActionPanel.Children.OfType<Button>().FirstOrDefault(b => (string?)b.Tag == target);
                    button?.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    break;
                default:
                    return;
            }
            e.Handled = true;
        }

        // ---------------------------------------------------------------- registro de la mano

        private void LogNewActions()
        {
            var hand = _session!.Hand!;
            for (; _loggedActions < hand.Actions.Count; _loggedActions++)
            {
                var action = hand.Actions[_loggedActions];
                LogBoard(hand, action.Street);
                var player = hand.Players.First(p => p.Seat == action.Seat);
                Log($"{player.Name} ({player.Position}): {ActionText(action)}", bold: player.IsHero);
            }
            if (hand.IsOver)
                LogBoard(hand, SimStreet.River);
        }

        private void LogBoard(SimHand hand, SimStreet upTo)
        {
            var visible = upTo switch { SimStreet.Preflop => 0, SimStreet.Flop => 3, SimStreet.Turn => 4, _ => 5 };
            visible = Math.Min(visible, hand.Board.Count);
            while (_loggedBoard < visible)
            {
                var count = _loggedBoard == 0 ? 3 : 1;
                var streetKey = _loggedBoard == 0 ? "Sim.Street.Flop" : _loggedBoard == 3 ? "Sim.Street.Turn" : "Sim.Street.River";
                Log($"{T(streetKey)}: {CardsText(hand.Board.Take(_loggedBoard + count))}", bold: true);
                _loggedBoard += count;
            }
        }

        private static string ActionText(SimAction a) => a.Kind switch
        {
            SimActionKind.Fold => T("Sim.Act.Fold"),
            SimActionKind.Check => T("Sim.Act.Check"),
            SimActionKind.Call => string.Format(CultureInfo.CurrentCulture, T("Sim.Act.Call"), a.Added) + (a.AllIn ? " (all-in)" : ""),
            SimActionKind.Bet => string.Format(CultureInfo.CurrentCulture, T("Sim.Act.Bet"), a.Amount) + (a.AllIn ? " (all-in)" : ""),
            _ => string.Format(CultureInfo.CurrentCulture, T("Sim.Act.Raise"), a.Amount) + (a.AllIn ? " (all-in)" : "")
        };

        private void Log(string text, bool bold = false)
        {
            if (_reviews is not null)
                return;
            RightList.Children.Add(new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                Margin = new Thickness(0, 3, 0, 0)
            });
            RightScroll.ScrollToEnd();
        }

        // ---------------------------------------------------------------- revision

        /// <summary>
        /// Revisión de cada decisión: qué era mejor y por qué, en palabras sencillas. Con el interruptor
        /// "Explicación técnica" encendido se añaden el rango, la equity, las pot odds y el valor de cada opción.
        /// </summary>
        private void ShowReview()
        {
            RightList.Children.Clear();
            RangePanel.Visibility = Visibility.Collapsed;
            RangeZoom.Visibility = Visibility.Collapsed;
            RightTitle.Text = T("Sim.ReviewTitle");
            var reviews = _reviews!;
            if (reviews.Count == 0)
            {
                RightList.Children.Add(Dim(T("Sim.NoDecisions")));
                return;
            }

            var technical = _progress.ShowTechnical;
            RightList.Children.Add(new TextBlock
            {
                Text = string.Format(CultureInfo.CurrentCulture, T("Sim.ReviewSummary"), reviews.Count(r => r.Verdict == SimVerdict.Good), reviews.Count)
                       + (technical ? " " + T("Sim.ReviewRangeHint") : ""),
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 8),
                TextWrapping = TextWrapping.Wrap
            });

            foreach (var review in reviews)
            {
                var card = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 8), BorderThickness = new Thickness(1), Cursor = technical ? Cursors.Hand : null };
                card.SetResourceReference(Border.BackgroundProperty, "Brush.Inset");
                card.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
                var panel = new StackPanel();
                var header = new DockPanel();
                var verdict = new TextBlock { FontWeight = FontWeights.Bold, Text = T("Sim.Verdict." + review.Verdict) };
                verdict.SetResourceReference(TextBlock.ForegroundProperty, review.Verdict switch
                {
                    SimVerdict.Good => "Brush.Positive",
                    SimVerdict.Doubtful => "Brush.Warning",
                    _ => "Brush.Negative"
                });
                DockPanel.SetDock(verdict, Dock.Right);
                header.Children.Add(verdict);
                header.Children.Add(new TextBlock
                {
                    Text = $"{T("Sim.StreetName." + review.Decision.Street)} · {review.TakenLabel}",
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                });
                panel.Children.Add(header);

                // Lo que era mejor y por qué, para quien empieza.
                if (review.Headline.Length > 0)
                    panel.Children.Add(new TextBlock { Text = review.Headline, FontWeight = FontWeights.SemiBold, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
                if (review.Reason.Length > 0)
                {
                    var why = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, review.Headline.Length > 0 ? 3 : 6, 0, 0) };
                    why.Inlines.Add(new Run(T("Sim.Sum.Why") + " ") { FontWeight = FontWeights.SemiBold });
                    why.Inlines.Add(new Run(review.Reason));
                    panel.Children.Add(why);
                }

                // El detalle técnico, solo con el interruptor encendido.
                if (technical)
                {
                    var tech = new StackPanel();
                    foreach (var line in review.Lines)
                        tech.Children.Add(new TextBlock { Text = "• " + line, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = ThemeBrushes.TextDim, Margin = new Thickness(0, 3, 0, 0) });
                    var box = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 5, 0, 0), Margin = new Thickness(0, 8, 0, 0), Child = tech };
                    box.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
                    panel.Children.Add(box);
                    var selected = review;
                    card.MouseLeftButtonUp += (_, _) => ShowRange(selected);
                }
                card.Child = panel;
                RightList.Children.Add(card);
            }

            if (!technical)
                return;

            // Se muestra primero un rango que diga algo: el de un error, o el ultimo donde la maquina ya habia actuado.
            bool Informative(SimDecisionReview r) => r.Decision.Opponents.Count > 0 && !SimReviewer.IsUntouched(r.Decision.Opponents[0].Weights);
            ShowRange(reviews.LastOrDefault(r => r.Verdict == SimVerdict.Mistake && Informative(r))
                      ?? reviews.LastOrDefault(Informative) ?? reviews[^1]);
        }

        /// <summary>El rango de la maquina en esa decision, como mapa de calor; recuadro = su mano real.</summary>
        private void ShowRange(SimDecisionReview review)
        {
            if (review.Ranges.Count == 0)
                return;
            var (seat, range) = review.Ranges[0];
            var opponent = review.Decision.Opponents.First(o => o.Seat == seat);
            var shares = SimRangeTracker.ClassShares(range).ToDictionary(x => x.Class, x => x.Share);
            var max = shares.Count == 0 ? 1 : shares.Values.Max();
            var trueClass = TrainerCards.ComboName(opponent.TrueCards[0], opponent.TrueCards[1]);

            _rangeShares = shares;
            _rangeMax = max;
            _rangeTrueClass = trueClass;
            RangeTitle.Text = SimReviewer.IsUntouched(opponent.Weights)
                ? string.Format(CultureInfo.CurrentCulture, T("Sim.RangeUntouched"), SimProfiles.Name(opponent.Profile), trueClass)
                : string.Format(CultureInfo.CurrentCulture, T("Sim.RangeTitle"), SimProfiles.Name(opponent.Profile), T("Sim.StreetName." + review.Decision.Street), trueClass);
            FillRangeGrid(RangeGrid, 12.5);
            RangePanel.Visibility = Visibility.Visible;
            if (RangeZoom.Visibility == Visibility.Visible)
                ShowRangeZoom();
        }

        // Rango de la decision que se esta viendo, para la tabla de la derecha y la tabla en grande.
        private Dictionary<string, double> _rangeShares = new();
        private double _rangeMax = 1;
        private string _rangeTrueClass = "";

        private void RangePanel_Click(object sender, MouseButtonEventArgs e) => ShowRangeZoom();

        private void RangeZoom_Click(object sender, MouseButtonEventArgs e) => RangeZoom.Visibility = Visibility.Collapsed;

        /// <summary>La tabla de rango en grande encima de la mesa (clic o Esc para cerrarla).</summary>
        private void ShowRangeZoom()
        {
            RangeZoomTitle.Text = RangeTitle.Text;
            FillRangeGrid(RangeGridLarge, 15);
            var wasOpen = RangeZoom.Visibility == Visibility.Visible;
            RangeZoom.Visibility = Visibility.Visible;
            if (!wasOpen)
                UiMotion.Pop(RangeZoom);
        }

        /// <summary>Las 169 manos con la intensidad de azul segun lo probable que sea cada una.</summary>
        private void FillRangeGrid(System.Windows.Controls.Primitives.UniformGrid grid, double fontSize)
        {
            var shares = _rangeShares;
            var max = _rangeMax;
            var trueClass = _rangeTrueClass;
            grid.Children.Clear();
            for (var row = 0; row < 13; row++)
            {
                for (var col = 0; col < 13; col++)
                {
                    var hand = AphPreflopProvider.HandAt(row, col);
                    var share = shares.GetValueOrDefault(hand) / max;
                    var alpha = share <= 0 ? 0 : 0.12 + 0.88 * share;
                    var label = new TextBlock
                    {
                        Text = hand,
                        FontSize = fontSize,
                        FontWeight = share > 0 ? FontWeights.SemiBold : FontWeights.Normal,
                        // Sin partir en dos lineas: con el recuadro de la mano real, "KQo" quedaba "KQ / o".
                        TextWrapping = TextWrapping.NoWrap,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    var cell = new Border
                    {
                        Margin = new Thickness(0.5),
                        CornerRadius = new CornerRadius(2),
                        BorderThickness = new Thickness(hand == trueClass ? 2.5 : 0),
                        Child = label,
                        ToolTip = $"{hand}: {shares.GetValueOrDefault(hand) * 100:0.#}%"
                    };
                    if (alpha > 0)
                        cell.Background = new SolidColorBrush(Color.FromArgb((byte)(alpha * 255), RangeHue.R, RangeHue.G, RangeHue.B));
                    else
                        cell.SetResourceReference(Border.BackgroundProperty, "Brush.Inset");
                    if (hand == trueClass) cell.BorderBrush = ThemeBrushes.Text;
                    if (alpha > 0.55) label.Foreground = PreflopChartsWindow.InkOnColor;
                    else label.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDim");
                    grid.Children.Add(cell);
                }
            }
        }

        // ---------------------------------------------------------------- dibujo de la mesa

        private void Render()
        {
            var hand = _session?.Hand;
            TableCanvas.Children.Clear();
            if (hand is null)
                return;

            var rail = new Ellipse { Width = 780, Height = 430, StrokeThickness = 14 };
            rail.SetResourceReference(Shape.FillProperty, "Brush.Inset");
            rail.SetResourceReference(Shape.StrokeProperty, "Brush.Border");
            Place(rail, 80, 65);
            var felt = new Ellipse { Width = 740, Height = 390, Opacity = 0.22 };
            felt.SetResourceReference(Shape.FillProperty, "Brush.Club");
            Place(felt, 100, 85);

            var pot = new TextBlock
            {
                Text = string.Format(CultureInfo.CurrentCulture, T("Sim.Pot"), hand.Pot),
                FontSize = 26,
                FontWeight = FontWeights.SemiBold
            };
            Center(pot, 470, 188);

            for (var i = 0; i < 5; i++)
            {
                FrameworkElement slot = i < hand.Board.Count ? CardView(hand.Board[i], 72, 100) : EmptySlot(72, 100);
                Place(slot, 470 - 2.5 * 80 + i * 80 + 4, 222);
            }

            var seats = SeatPoints(hand.Players.Count);
            for (var i = 0; i < hand.Players.Count; i++)
            {
                var player = hand.Players[i];
                var (x, y) = seats[i];
                Center(SeatView(hand, player), x, y);

                if (player.StreetBet > 1e-9 && !hand.IsOver)
                {
                    var bet = Pill(string.Format(CultureInfo.CurrentCulture, T("Sim.Bb"), player.StreetBet));
                    Center(bet, x + (470 - x) * 0.42, y + (280 - y) * 0.42);
                }
                if (i == hand.ButtonIndex)
                {
                    var dealer = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(16), Background = CardFace, Child = new TextBlock { Text = "D", FontWeight = FontWeights.Bold, Foreground = Brushes.Black, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
                    Center(dealer, x + (470 - x) * 0.25 + 60, y + (280 - y) * 0.25);
                }
            }
        }

        /// <summary>Puntos de los asientos: el heroe abajo y los demas en el sentido de las agujas del reloj.</summary>
        private static (double X, double Y)[] SeatPoints(int count) => count == 2
            ? new[] { (470.0, 492.0), (470.0, 66.0) }
            : new[] { (470.0, 492.0), (142.0, 392.0), (142.0, 152.0), (470.0, 66.0), (798.0, 152.0), (798.0, 392.0) };

        private FrameworkElement SeatView(SimHand hand, SimPlayer player)
        {
            var reveal = player.IsHero || hand.IsOver;
            var size = player.IsHero ? (W: 66.0, H: 92.0) : (W: 50.0, H: 70.0);
            var cards = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 8, 0) };
            if (!player.Folded || hand.IsOver)
            {
                cards.Children.Add(reveal ? CardView(player.Cards[0], size.W, size.H) : CardBack(size.W, size.H));
                cards.Children.Add(reveal ? CardView(player.Cards[1], size.W, size.H) : CardBack(size.W, size.H));
            }

            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var nameLine = new DockPanel();
            var position = Pill(player.Position);
            position.Margin = new Thickness(6, 0, 0, 0);
            DockPanel.SetDock(position, Dock.Right);
            nameLine.Children.Add(position);
            nameLine.Children.Add(new TextBlock { Text = player.Name, FontWeight = FontWeights.SemiBold, FontSize = 19, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 165 });
            texts.Children.Add(nameLine);
            texts.Children.Add(new TextBlock { Text = string.Format(CultureInfo.CurrentCulture, T("Sim.Bb"), player.Stack), FontSize = 17 });
            var last = hand.Actions.LastOrDefault(a => a.Seat == player.Seat && (a.Street == hand.Street || hand.IsOver));
            var lastText = player.Folded ? T("Sim.Act.Fold") : last is not null ? ActionText(last) : "";
            texts.Children.Add(new TextBlock { Text = lastText, FontSize = 15, Foreground = ThemeBrushes.TextDim, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 190 });
            if (hand.IsOver && Math.Abs(player.Net) > 1e-9)
            {
                var net = new TextBlock { Text = $"{player.Net:+0.#;-0.#} bb", FontWeight = FontWeights.SemiBold, FontSize = 15 };
                net.SetResourceReference(TextBlock.ForegroundProperty, player.Net > 0 ? "Brush.Positive" : "Brush.Negative");
                texts.Children.Add(net);
            }

            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(cards);
            content.Children.Add(texts);

            var border = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8),
                BorderThickness = new Thickness(hand.ToAct == player ? 3 : 1),
                Child = content,
                Opacity = player.Folded && !hand.IsOver ? 0.45 : player.Folded ? 0.6 : 1
            };
            border.SetResourceReference(Border.BackgroundProperty, "Brush.Card");
            border.SetResourceReference(Border.BorderBrushProperty, hand.ToAct == player ? "Brush.Accent" : hand.IsOver && player.Net > 0 ? "Brush.Positive" : "Brush.Border");
            if (!player.IsHero)
                border.ToolTip = SimProfiles.Description(player.Profile!.Value);
            return border;
        }

        private static FrameworkElement CardView(int card, double width, double height)
        {
            var (symbol, ink) = SuitInk[TrainerCards.Suit(card)];
            var rank = TrainerCards.Ranks[TrainerCards.Rank(card)];
            var grid = new Grid();
            grid.Children.Add(new TextBlock { Text = rank == 'T' ? "10" : rank.ToString(), FontSize = height * 0.32, FontWeight = FontWeights.Bold, Foreground = ink, Margin = new Thickness(width * 0.1, 0, 0, 0) });
            grid.Children.Add(new TextBlock { Text = symbol, FontSize = height * 0.42, Foreground = ink, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, height * 0.04) });
            var view = new Border { Width = width, Height = height, CornerRadius = new CornerRadius(5), Background = CardFace, Margin = new Thickness(0, 0, 4, 0), Child = grid };
            System.Windows.Automation.AutomationProperties.SetName(view, TrainerCards.ToText(card));
            return view;
        }

        private static FrameworkElement CardBack(double width, double height)
        {
            var back = new Border { Width = width, Height = height, CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(3), Margin = new Thickness(0, 0, 4, 0) };
            back.SetResourceReference(Border.BackgroundProperty, "Brush.Accent");
            back.SetResourceReference(Border.BorderBrushProperty, "Brush.Card");
            return back;
        }

        private static FrameworkElement EmptySlot(double width, double height)
        {
            var slot = new Border { Width = width, Height = height, CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1), Opacity = 0.5 };
            slot.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
            return slot;
        }

        private static Border Pill(string text)
        {
            var pill = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(7, 1, 7, 1), Child = new TextBlock { Text = text, FontSize = 14, FontWeight = FontWeights.SemiBold } };
            pill.SetResourceReference(Border.BackgroundProperty, "Brush.AccentSoft");
            return pill;
        }

        private void Place(UIElement element, double x, double y)
        {
            Canvas.SetLeft(element, x);
            Canvas.SetTop(element, y);
            TableCanvas.Children.Add(element);
        }

        private void Center(FrameworkElement element, double x, double y)
        {
            element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Place(element, x - element.DesiredSize.Width / 2, y - element.DesiredSize.Height / 2);
        }

        // ---------------------------------------------------------------- utilidades

        private static string CardsText(IEnumerable<int> cards) =>
            string.Join(" ", cards.Select(c => TrainerCards.Ranks[TrainerCards.Rank(c)] + SuitInk[TrainerCards.Suit(c)].Symbol));

        private static TextBlock Dim(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = ThemeBrushes.TextDim };

        private static void SetName(DependencyObject element, string name) =>
            System.Windows.Automation.AutomationProperties.SetName(element, name);

        private static string T(string key) => LocalizationManager.Text(key);

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
