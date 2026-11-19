using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Hud.App.Services;
using S = Hud.App.Services.PreflopScenarios;

namespace Hud.App.Views
{
    /// <summary>
    /// Practica preflop por temas (abrir desde UTG, defensa de la ciega grande, contra limp,
    /// heads-up...) y por nivel, de facil a experto. Las respuestas salen de las tablas de APH;
    /// las preguntas pueden salir tambien de las manos reales de los jugadores. Cada respuesta se
    /// explica: cuanto se juega en esa situacion, como se juega la misma mano en otras posiciones,
    /// la mano vecina que ya se juega distinto y como juega la poblacion real.
    /// </summary>
    public partial class PreflopPracticeWindow : Window
    {
        private const double ExpertSeconds = 10;

        // Baraja de 4 colores, como la de PokerStars que usan los alumnos.
        private static readonly Dictionary<char, (string Symbol, Brush Brush)> Suits = new()
        {
            ['s'] = ("♠", Frozen(Color.FromRgb(0x1E, 0x1E, 0x1E))),
            ['h'] = ("♥", Frozen(Color.FromRgb(0xD3, 0x2F, 0x2F))),
            ['d'] = ("♦", Frozen(Color.FromRgb(0x1E, 0x63, 0xC9))),
            ['c'] = ("♣", Frozen(Color.FromRgb(0x1E, 0x8A, 0x3A))),
        };
        private static readonly Brush CardFace = Frozen(Color.FromRgb(0xF6, 0xF5, 0xF0));

        private readonly AphPreflopProvider _charts = PreflopLibrary.Charts;
        private readonly IReadOnlyList<string> _handFiles;
        private readonly PracticeProgress _progress = PracticeProgress.Load();
        private readonly Random _random = new();
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
        private readonly Dictionary<string, (TextBlock Stats, Grid Bar)> _topicStats = new();

        private List<PreflopSpot> _allSpots = new();
        private string? _player;
        private PreflopPracticeEngine _engine;
        private PracticeLevel _level = PracticeLevel.Easy;
        private string _topic = "open-LJ";
        private PracticeQuestion? _question;
        private bool _answered;
        private DateTime _askedAt;
        private int _sessionAnswered;
        private int _sessionCorrect;
        private int _streak;
        private bool _spotsLoaded;

        public PreflopPracticeWindow() : this(Array.Empty<string>())
        {
        }

        public PreflopPracticeWindow(IEnumerable<string> handFiles)
        {
            InitializeComponent();
            _handFiles = handFiles.ToList();
            _engine = new PreflopPracticeEngine(_charts, _allSpots, _random);
            Title = T("Practice.Title");
            TitleText.Text = T("Practice.Title");
            SummaryText.Text = T("Practice.Summary");
            LevelLabel.Text = T("Practice.Level");
            TopicLabel.Text = T("Practice.Topic");
            PlayerLabel.Text = T("Practice.Player");
            NextButton.Content = T("Practice.Next");
            System.Windows.Automation.AutomationProperties.SetName(NextButton, T("Practice.Next"));
            _timer.Tick += Timer_Tick;
            // Dentro de APH la pagina se oculta al ir a otra seccion: el reloj de experto se para.
            RootGrid.Unloaded += (_, _) => _timer.Stop();

            BuildLevels();
            BuildTopics();
            NextQuestion();
            _ = LoadSpotsAsync();
        }

        // ---------------------------------------------------------------- nivel, temas y jugador

        private void BuildLevels()
        {
            LevelPanel.Children.Clear();
            foreach (var level in Enum.GetValues<PracticeLevel>())
            {
                var chip = new RadioButton
                {
                    Content = T("Practice.Level." + level),
                    ToolTip = T("Practice.Level." + level + ".Desc"),
                    GroupName = "level",
                    IsChecked = level == _level,
                    Style = (Style)FindResource("Chip")
                };
                System.Windows.Automation.AutomationProperties.SetName(chip, T("Practice.Level." + level));
                chip.Checked += (_, _) =>
                {
                    _level = level;
                    LevelHint.Text = T("Practice.Level." + level + ".Desc");
                    RefreshTopicStats();
                    NextQuestion();
                };
                LevelPanel.Children.Add(chip);
            }
            LevelHint.Text = T("Practice.Level." + _level + ".Desc");
        }

        private void BuildTopics()
        {
            TopicList.Children.Clear();
            _topicStats.Clear();
            foreach (var topic in PreflopPracticeEngine.Topics)
            {
                var name = new TextBlock { Text = T(topic.NameKey), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
                var desc = new TextBlock { Text = T(topic.DescKey), FontSize = 11, Foreground = ThemeBrushes.TextDim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
                var stats = new TextBlock { FontSize = 11, Foreground = ThemeBrushes.TextDim, HorizontalAlignment = HorizontalAlignment.Right };
                var bar = new Grid { Height = 4, Margin = new Thickness(0, 6, 0, 0) };

                var header = new DockPanel();
                DockPanel.SetDock(stats, Dock.Right);
                header.Children.Add(stats);
                header.Children.Add(name);

                var content = new StackPanel();
                content.Children.Add(header);
                content.Children.Add(desc);
                content.Children.Add(bar);

                var item = new RadioButton
                {
                    Content = content,
                    GroupName = "topic",
                    IsChecked = topic.Id == _topic,
                    Style = (Style)FindResource("TopicItem")
                };
                System.Windows.Automation.AutomationProperties.SetName(item, T(topic.NameKey));
                item.Checked += (_, _) =>
                {
                    _topic = topic.Id;
                    NextQuestion();
                };
                _topicStats[topic.Id] = (stats, bar);
                TopicList.Children.Add(item);
            }
            RefreshTopicStats();
        }

        /// <summary>Aciertos de cada tema en el nivel elegido, con una barra fina.</summary>
        private void RefreshTopicStats()
        {
            foreach (var (topicId, (stats, bar)) in _topicStats)
            {
                bar.Children.Clear();
                bar.ColumnDefinitions.Clear();
                if (topicId == PreflopPracticeEngine.MistakesTopic)
                {
                    stats.Text = string.Format(CultureInfo.CurrentCulture, T("Practice.ToReview"), _progress.Mistakes.Count);
                    continue;
                }
                if (topicId == PreflopPracticeEngine.MyHandsTopic && !_spotsLoaded)
                {
                    stats.Text = T("Practice.Loading");
                    continue;
                }

                var (answered, correct) = _progress.Totals(topicId, _level);
                if (answered == 0)
                {
                    stats.Text = topicId == PreflopPracticeEngine.MyHandsTopic
                        ? string.Format(CultureInfo.CurrentCulture, T("Practice.RealCount"), _engine.RealSpotCount)
                        : T("Practice.NotStarted");
                    continue;
                }

                var pct = 100.0 * correct / answered;
                stats.Text = string.Format(CultureInfo.CurrentCulture, T("Practice.TopicStats"), pct, answered);
                bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(pct, 0.01), GridUnitType.Star) });
                bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(100 - pct, 0.01), GridUnitType.Star) });
                var fill = new Border { CornerRadius = new CornerRadius(2) };
                fill.SetResourceReference(Border.BackgroundProperty, "Brush.Accent");
                var track = new Border { CornerRadius = new CornerRadius(2) };
                track.SetResourceReference(Border.BackgroundProperty, "Brush.Border");
                Grid.SetColumn(track, 1);
                bar.Children.Add(fill);
                bar.Children.Add(track);
            }
        }

        private async Task LoadSpotsAsync()
        {
            try
            {
                _allSpots = _handFiles.Count == 0 ? new List<PreflopSpot>() : await PreflopSpotReader.LoadAsync(_handFiles);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Practica preflop: no se pudieron leer las manos", ex);
                _allSpots = new List<PreflopSpot>();
            }

            _spotsLoaded = true;
            var players = _allSpots.GroupBy(s => s.Player).OrderByDescending(g => g.Count()).Select(g => g.Key).ToList();
            PlayerCombo.Items.Clear();
            PlayerCombo.Items.Add(new ComboBoxItem { Content = T("Preflop.AllPlayers"), Tag = null });
            foreach (var player in players)
                PlayerCombo.Items.Add(new ComboBoxItem { Content = player, Tag = player });
            var showPlayers = players.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            PlayerCombo.Visibility = showPlayers;
            PlayerLabel.Visibility = showPlayers;
            System.Windows.Automation.AutomationProperties.SetName(PlayerCombo, T("Practice.Player"));
            PlayerCombo.SelectedIndex = 0; // reconstruye el motor con las manos
        }

        private void PlayerCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _player = (PlayerCombo.SelectedItem as ComboBoxItem)?.Tag as string;
            var spots = _player is null ? _allSpots : _allSpots.Where(s => s.Player == _player).ToList();
            _engine = new PreflopPracticeEngine(_charts, spots, _random);
            RefreshTopicStats();
            if (_topic == PreflopPracticeEngine.MyHandsTopic || _question is null)
                NextQuestion();
        }

        // ---------------------------------------------------------------- preguntas

        private void NextQuestion()
        {
            _timer.Stop();
            _answered = false;
            FeedbackCard.Visibility = Visibility.Hidden;
            TimerTrack.Visibility = Visibility.Hidden;
            AnswerPanel.Children.Clear();
            CardsPanel.Children.Clear();

            var topic = PreflopPracticeEngine.Topics.First(t => t.Id == _topic);
            TopicTitle.Text = $"{T(topic.NameKey)} · {T("Practice.Level." + _level)}";
            UpdateScore();

            _question = _engine.Next(_topic, _level, _progress.Mistakes);
            if (_question is null)
            {
                SituationText.Text = _topic switch
                {
                    PreflopPracticeEngine.MistakesTopic => T("Practice.NoMistakes"),
                    PreflopPracticeEngine.MyHandsTopic => T(_spotsLoaded ? "Practice.NoRealHands" : "Practice.Loading"),
                    _ => T("Practice.NoQuestion")
                };
                return;
            }

            SituationText.Text = PreflopPracticeEngine.Situation(_question);
            CardsPanel.Children.Add(CardView(_question.Cards.Substring(0, 2)));
            CardsPanel.Children.Add(CardView(_question.Cards.Substring(2, 2)));
            CardsPanel.Children.Add(new TextBlock
            {
                Text = _question.Hand,
                FontSize = 15,
                Foreground = ThemeBrushes.TextDim,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(6, 0, 0, 6)
            });

            var keys = new Dictionary<string, string> { ["F"] = "F", ["C"] = "C", ["R"] = "R" };
            foreach (var code in _question.Options)
            {
                var label = S.ActionLabel(_question.Scenario, _question.Hero, code);
                var button = new Button
                {
                    Content = $"{label}   ({keys[code]})",
                    Padding = new Thickness(22, 12, 22, 12),
                    Margin = new Thickness(0, 0, 10, 10),
                    FontSize = 15,
                    MinWidth = 150,
                    Tag = code
                };
                System.Windows.Automation.AutomationProperties.SetName(button, label);
                button.Click += (_, _) => Answer(code);
                AnswerPanel.Children.Add(button);
            }

            _askedAt = DateTime.Now;
            if (_level == PracticeLevel.Expert)
            {
                TimerTrack.Visibility = Visibility.Visible;
                SetTimer(1);
                _timer.Start();
            }

            Dispatcher.BeginInvoke(() => RootGrid.Focus(), DispatcherPriority.Input);
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            var left = 1 - (DateTime.Now - _askedAt).TotalSeconds / ExpertSeconds;
            SetTimer(Math.Max(0, left));
            if (left <= 0)
                Answer(null);
        }

        private void SetTimer(double left)
        {
            TimerLeft.Width = new GridLength(Math.Max(left, 0.0001), GridUnitType.Star);
            TimerUsed.Width = new GridLength(Math.Max(1 - left, 0.0001), GridUnitType.Star);
        }

        /// <summary>code = null: se acabo el tiempo.</summary>
        private void Answer(string? code)
        {
            if (_question is null || _answered)
                return;

            _timer.Stop();
            _answered = true;
            var q = _question;
            var correct = code == q.Correct;
            _sessionAnswered++;
            if (correct) { _sessionCorrect++; _streak++; } else { _streak = 0; }
            _progress.BestStreak = Math.Max(_progress.BestStreak, _streak);
            _progress.Record(q, correct);
            _progress.Save();

            foreach (var button in AnswerPanel.Children.OfType<Button>())
            {
                var buttonCode = (string)button.Tag;
                button.IsEnabled = false;
                if (buttonCode == q.Correct)
                {
                    button.Background = buttonCode == "C" ? PreflopChartsWindow.CallBrush : PreflopChartsWindow.RaiseBrush;
                    if (buttonCode == "F") button.SetResourceReference(BackgroundProperty, "Brush.Border");
                    button.Foreground = buttonCode == "F" ? ThemeBrushes.Text : PreflopChartsWindow.InkOnColor;
                    button.Opacity = 1;
                }
                else
                {
                    button.Opacity = buttonCode == code ? 0.85 : 0.4;
                }
            }

            var right = S.ActionLabel(q.Scenario, q.Hero, q.Correct);
            VerdictText.Text = correct
                ? string.Format(CultureInfo.CurrentCulture, T("Practice.Correct"), right)
                : code is null
                    ? string.Format(CultureInfo.CurrentCulture, T("Practice.TimeUp"), right)
                    : string.Format(CultureInfo.CurrentCulture, T("Practice.Wrong"), right);
            VerdictText.SetResourceReference(TextBlock.ForegroundProperty, correct ? "Brush.Positive" : "Brush.Negative");

            ExplainList.Children.Clear();
            foreach (var line in _engine.Explain(q))
            {
                ExplainList.Children.Add(new TextBlock
                {
                    Text = "• " + line,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 0)
                });
            }

            BuildMiniChart(q);
            FeedbackCard.Visibility = Visibility.Visible;
            RefreshTopicStats();
            UpdateScore();
            NextButton.Focus();
        }

        private void UpdateScore()
        {
            ScoreText.Text = _sessionAnswered == 0
                ? string.Format(CultureInfo.CurrentCulture, T("Practice.BestStreak"), _progress.BestStreak)
                : string.Format(CultureInfo.CurrentCulture, T("Practice.Score"), _sessionCorrect, _sessionAnswered, _streak, _progress.BestStreak);
        }

        private void NextButton_Click(object sender, RoutedEventArgs e) => NextQuestion();

        private void RootGrid_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.OriginalSource is TextBox or ComboBox)
                return;

            if (_answered && e.Key is Key.Enter or Key.Space or Key.N)
            {
                NextQuestion();
                e.Handled = true;
                return;
            }

            var code = e.Key switch { Key.F => "F", Key.C => "C", Key.R => "R", _ => null };
            if (!_answered && code is not null && _question?.Options.Contains(code) == true)
            {
                Answer(code);
                e.Handled = true;
            }
        }

        // ---------------------------------------------------------------- dibujo

        private static FrameworkElement CardView(string card)
        {
            var (symbol, brush) = Suits.TryGetValue(card[1], out var suit) ? suit : ("?", ThemeBrushes.Text);
            var rank = card[0] == 'T' ? "10" : card[0].ToString();
            var grid = new Grid();
            grid.Children.Add(new TextBlock
            {
                Text = rank,
                FontSize = 30,
                FontWeight = FontWeights.Bold,
                Foreground = brush,
                Margin = new Thickness(10, 4, 0, 0)
            });
            grid.Children.Add(new TextBlock
            {
                Text = symbol,
                FontSize = 52,
                Foreground = brush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 22, 0, 0)
            });

            var view = new Border
            {
                Width = 92,
                Height = 128,
                CornerRadius = new CornerRadius(8),
                Background = CardFace,
                Margin = new Thickness(0, 0, 10, 0),
                Child = grid
            };
            System.Windows.Automation.AutomationProperties.SetName(view, card);
            return view;
        }

        /// <summary>La tabla de la situacion en pequeno, con la mano de la pregunta marcada.</summary>
        private void BuildMiniChart(PracticeQuestion q)
        {
            MiniTitle.Text = PreflopPracticeEngine.ChartTitle(q);
            MiniGrid.Children.Clear();
            for (var row = 0; row < 13; row++)
            {
                for (var col = 0; col < 13; col++)
                {
                    var hand = AphPreflopProvider.HandAt(row, col);
                    var label = new TextBlock
                    {
                        Text = hand,
                        FontSize = 9,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    var cell = new Border { Margin = new Thickness(0.5), CornerRadius = new CornerRadius(2), BorderThickness = new Thickness(1), Child = label };
                    PreflopChartsWindow.PaintCell(cell, label, _engine.CodeAt(q, hand));
                    if (hand == q.Hand)
                    {
                        cell.BorderBrush = ThemeBrushes.Text;
                        cell.BorderThickness = new Thickness(3);
                    }
                    MiniGrid.Children.Add(cell);
                }
            }
        }

        private static string T(string key) => LocalizationManager.Text(key);

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        protected override void OnClosed(EventArgs e)
        {
            _timer.Stop();
            base.OnClosed(e);
        }
    }
}
