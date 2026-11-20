using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using HandReader.Core.Models;
using HandReader.Core.Stats;
using Hud.App.Services;
using Hud.App.Services.Trainer;

namespace Hud.App.Views
{
    /// <summary>
    /// HUD encima de una mesa de PokerStars. Cada jugador tiene un cuadro junto a su asiento real
    /// (la mesa gira para que el heroe quede abajo) con su tipo (Fish, Nit, TAG...), sus manos y sus
    /// estadisticas en una mini tabla: nombre arriba y numero abajo, con el color de cada valor.
    /// Modo minimo (para jugar) o completo (para estudiar), tres tamanos y un modo "Mover HUD"
    /// para arrastrar los cuadros; todo se guarda en hud.json.
    /// </summary>
    public sealed class PokerTableOverlayWindow : Window
    {
        private static readonly Color NeutralCell = Color.FromRgb(38, 50, 65);
        private static readonly Brush BoxBackground = Frozen(Color.FromArgb(232, 6, 10, 15));
        private static readonly Brush HeroBoxBackground = Frozen(Color.FromArgb(236, 14, 30, 48));
        private static readonly Brush BoxBorder = Frozen(Color.FromArgb(200, 70, 86, 104));
        private static readonly Brush DimBrush = Frozen(Color.FromRgb(174, 186, 198));

        private readonly IntPtr _targetHandle;
        private readonly Func<HudTableSnapshot> _snapshotProvider;
        private readonly string? _heroName;
        private readonly Canvas _canvas = new();
        private readonly Border _moveTint;
        private readonly Border _moveBanner;
        private readonly Dictionary<int, HudBox> _boxes = new();
        private readonly DispatcherTimer _timer;
        private int _tableSize = 6;
        private HudBox? _dragging;
        private Point _dragOffset;

        // Recomendacion del solver de entrenamiento (solo dinero ficticio, en el turno del heroe),
        // el aviso de "calculando" y, al terminar la mano, la calificacion de tus decisiones.
        private readonly Border _solverBox;
        private LiveDecision? _solverDecision;
        private string? _thinking;
        private (string Where, string? Guess)? _unreadable;
        private TrainerHandReport? _grade;
        private DateTime _gradeAt;
        private string? _notice;
        private DateTime _noticeAt;
        private static readonly TimeSpan NoticeTime = TimeSpan.FromSeconds(25);
        private string _solverKey = "";

        // Cuadro de la izquierda en tu turno: tu % de ganar y la tabla de rango que explica el consejo.
        private readonly Border _rangeBox;
        private LiveRangeView? _rangeShown;
        private double _rangeFont;

        // Repaso de la mano anterior (preflop, board, showdown y quien gano) unos segundos.
        private readonly Border _recapBox;
        private DateTime _recapAt;
        private double _recapFont;
        private static readonly TimeSpan RecapTime = TimeSpan.FromSeconds(9);
        private static readonly TimeSpan GradeTime = TimeSpan.FromSeconds(9);
        private static readonly Dictionary<int, (string Symbol, Color Ink)> SuitInk = new()
        {
            [3] = ("\u2660", Color.FromRgb(25, 25, 25)),
            [2] = ("\u2665", Color.FromRgb(211, 47, 47)),
            [1] = ("\u2666", Color.FromRgb(30, 99, 201)),
            [0] = ("\u2663", Color.FromRgb(30, 138, 58)),
        };

        private sealed class HudBox
        {
            public required Border Border { get; init; }
            public required int Place { get; init; }
            public string Signature { get; set; } = "";

            /// <summary>Jugador que muestra ahora (para encontrar la capsula del rival que se explota).</summary>
            public string Player { get; set; } = "";

            /// <summary>Borde normal: gris, o el color de las manos que llevas con el (si ya jugaste con el).</summary>
            public Brush RestBorder { get; set; } = BoxBorder;

            /// <summary>Hasta cuando el borde es el naranja de "explotar".</summary>
            public DateTime BlinkUntil { get; set; }
        }

        public PokerTableOverlayWindow(IntPtr targetHandle, Func<HudTableSnapshot> snapshotProvider, string? heroName)
        {
            _targetHandle = targetHandle;
            _snapshotProvider = snapshotProvider;
            _heroName = string.IsNullOrWhiteSpace(heroName) ? null : heroName.Trim().TrimEnd(':').Trim();

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;

            // En "Mover HUD" un velo casi transparente recibe los clics (las zonas 100%
            // transparentes de una ventana asi dejan pasar el clic a la mesa).
            _moveTint = new Border { Background = Frozen(Color.FromArgb(70, 0, 0, 0)), Visibility = Visibility.Collapsed };
            var bannerText = new TextBlock
            {
                Text = LocalizationManager.Text("Hud.MoveBanner"),
                Foreground = Brushes.White,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            var done = new Button { Content = LocalizationManager.Text("Hud.MoveDone"), Padding = new Thickness(12, 4, 12, 4) };
            System.Windows.Automation.AutomationProperties.SetName(done, LocalizationManager.Text("Hud.MoveDone"));
            done.Click += (_, _) => HudOverlaySettings.SetMoveMode(false);
            var bannerRow = new StackPanel { Orientation = Orientation.Horizontal };
            bannerRow.Children.Add(bannerText);
            bannerRow.Children.Add(done);
            _moveBanner = new Border
            {
                Background = Frozen(Color.FromArgb(235, 20, 30, 44)),
                BorderBrush = BoxBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 34, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Child = bannerRow,
                Visibility = Visibility.Collapsed
            };

            _solverBox = new Border
            {
                Background = Frozen(Color.FromArgb(242, 10, 16, 24)),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 5, 8, 6),
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false
            };
            _canvas.Children.Add(_solverBox);

            _rangeBox = new Border
            {
                Background = Frozen(Color.FromArgb(238, 8, 12, 18)),
                BorderBrush = BoxBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(5, 3, 5, 4),
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false
            };
            _canvas.Children.Add(_rangeBox);

            _recapBox = new Border
            {
                Background = Frozen(Color.FromArgb(238, 8, 12, 18)),
                BorderBrush = BoxBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(9, 5, 9, 6),
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false
            };
            _canvas.Children.Add(_recapBox);

            var root = new Grid();
            root.Children.Add(_moveTint);
            root.Children.Add(_canvas);
            root.Children.Add(_moveBanner);
            Content = root;

            SourceInitialized += (_, _) => SetClickThrough(!HudOverlaySettings.MoveMode);

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
            _timer.Tick += (_, _) => Refresh();
            Loaded += (_, _) =>
            {
                ApplyMoveMode();
                Refresh();
                _timer.Start();
            };
            HudOverlaySettings.MoveModeChanged += OnMoveModeChanged;
            Closed += (_, _) =>
            {
                _timer.Stop();
                HudOverlaySettings.MoveModeChanged -= OnMoveModeChanged;
            };
        }

        // ---------------------------------------------------------------- dibujo

        public void Refresh()
        {
            if (!PokerStarsWindowDetector.TryGetWindowRect(_targetHandle, out var bounds))
            {
                Hide();
                return;
            }

            if (!IsVisible)
                Show();

            Left = bounds.Left;
            Top = bounds.Top;
            Width = bounds.Width;
            Height = bounds.Height;
            _canvas.Width = Width;
            _canvas.Height = Height;

            HudTableSnapshot snapshot;
            try
            {
                snapshot = _snapshotProvider();
            }
            catch (Exception ex)
            {
                AppLog.Warn("HUD: no se pudieron leer los jugadores de la mesa", ex);
                return;
            }

            var settings = HudOverlaySettings.Current;
            _tableSize = snapshot.TableSize;
            var players = snapshot.Players
                .GroupBy(p => Clean(p.Name), StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var seats = SeatsOf(snapshot);
            var heroSeat = seats.FirstOrDefault(s => IsHero(s.Name)).Seat;
            var font = Math.Clamp(bounds.Width / 85.0, 9, 12.5) * settings.SizeFactor;

            var shown = new HashSet<int>();
            foreach (var seat in seats)
            {
                var hero = IsHero(seat.Name);
                if (hero && settings.HideHero)
                    continue;
                players.TryGetValue(Clean(seat.Name), out var player);
                if (player is null && seat.SittingOut)
                    continue;

                // Lo que ya jugaste con el en tus otras mesas se suma a esta mesa (la mesa en vivo lee
                // su propio historial desde el principio, asi que no hay manos repetidas).
                var elsewhere = hero ? null : PlayerHistoryIndex.Elsewhere(snapshot.SourcePath, Clean(seat.Name));
                var knownHands = elsewhere?.HandsReceived ?? 0;
                if (elsewhere is not null)
                    player = player is null ? elsewhere : player.CombinedWith(elsewhere);

                var place = HudLayout.PlaceOf(seat.Seat, heroSeat, snapshot.TableSize);
                if (!shown.Add(place))
                    continue;

                var box = BoxFor(place);
                box.Player = Clean(seat.Name);
                var signature = Signature(seat, player, hero, settings.FullMode, font, snapshot.Stake) + "|" + knownHands;
                if (box.Signature != signature)
                {
                    box.Border.Child = BuildContent(seat, player, hero, settings.FullMode, font, snapshot.Stake, knownHands);
                    box.Border.Background = hero ? HeroBoxBackground : BoxBackground;
                    box.Border.Opacity = seat.SittingOut ? 0.6 : 1;
                    SetRestBorder(box, knownHands > 0 ? PlayerHistoryIndex.SampleColor(player?.HandsReceived ?? 0) : null);
                    box.Signature = signature;
                }
                box.Border.Visibility = Visibility.Visible;
                if (box != _dragging)
                    PlaceBox(box, settings, bounds.Width, bounds.Height);
            }

            foreach (var (place, box) in _boxes)
                if (!shown.Contains(place))
                    box.Border.Visibility = Visibility.Collapsed;

            PlaceSolver(settings, font, bounds.Width, bounds.Height);
            PlaceRange(settings, font, bounds.Height);
            PlaceRecap(snapshot, settings, font, bounds.Width, bounds.Height);
        }

        // ---------------------------------------------------------------- cuadro de rango (izquierda)

        private static readonly Color RaiseCell = Color.FromRgb(224, 123, 46);
        private static readonly Color CallCell = Color.FromRgb(59, 130, 196);
        private static readonly Color EmptyCell = Color.FromRgb(34, 40, 48);
        private static readonly Color VillainCell = Color.FromRgb(214, 69, 80);
        private static readonly Color AheadCell = Color.FromRgb(56, 176, 96);
        private const string RankLetters = "AKQJT98765432";

        /// <summary>
        /// En tu turno, abajo a la izquierda (la zona negra fuera del tapete): tu % de ganar la mano y la
        /// tabla de rango que explica el consejo. Preflop tu rango con tu mano marcada; despues del flop,
        /// el rango que el solver le da al rival. Sale y se va con el cuadro del solver.
        /// </summary>
        private void PlaceRange(HudOverlaySettings settings, double font, double height)
        {
            if (!settings.ShowSolver || _solverDecision?.RangeView is not { } view)
            {
                _rangeBox.Visibility = Visibility.Collapsed;
                return;
            }

            var factor = settings.RangeCellFactor;
            if (!ReferenceEquals(view, _rangeShown) || Math.Abs(_rangeFont - font) > 0.01 || Math.Abs(_rangeFactor - factor) > 0.001)
            {
                _rangeBox.Child = RangeContent(view, font, factor);
                _rangeShown = view;
                _rangeFont = font;
                _rangeFactor = factor;
            }
            _rangeBox.Visibility = Visibility.Visible;
            _rangeBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(_rangeBox, 3);
            Canvas.SetTop(_rangeBox, Math.Max(2, height - _rangeBox.DesiredSize.Height - 4));
        }

        private double _rangeFactor;

        private static FrameworkElement RangeContent(LiveRangeView view, double font, double factor)
        {
            var cell = Math.Max(6, Math.Round(font * factor));
            // Los textos de la caja crecen con la tabla (la chica conserva el tamaño de antes).
            font *= Math.Clamp(factor / 0.8, 1, 1.6);
            var panel = new StackPanel { Width = cell * 14 };
            panel.Children.Add(new TextBlock
            {
                Text = view.Title,
                FontSize = font * 0.72,
                FontWeight = FontWeights.SemiBold,
                Foreground = DimBrush,
                TextWrapping = TextWrapping.Wrap
            });
            if (view.WinPct is { } win)
            {
                panel.Children.Add(Text(string.Format(CultureInfo.CurrentCulture, LocalizationManager.Text("Hud.RangeWin"),
                    win.ToString("0", CultureInfo.CurrentCulture)), font * 1.1, Brushes.White, FontWeights.Bold));
                panel.Children.Add(new TextBlock
                {
                    Text = view.WinAgainst,
                    FontSize = font * 0.66,
                    Foreground = DimBrush,
                    TextWrapping = TextWrapping.Wrap
                });
            }

            // 13x13 con las letras arriba y a la izquierda: parejas en la diagonal, suited arriba a la
            // derecha y offsuit abajo a la izquierda.
            var grid = new Grid { Margin = new Thickness(0, 3, 0, 2) };
            for (var i = 0; i < 14; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(cell) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(cell) });
            }
            for (var i = 0; i < 13; i++)
            {
                foreach (var (row, col) in new[] { (0, i + 1), (i + 1, 0) })
                {
                    var letter = new TextBlock
                    {
                        Text = RankLetters[i].ToString(),
                        FontSize = cell * 0.85,
                        Foreground = DimBrush,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Grid.SetRow(letter, row);
                    Grid.SetColumn(letter, col);
                    grid.Children.Add(letter);
                }
            }
            for (var row = 0; row < 13; row++)
            for (var col = 0; col < 13; col++)
            {
                var hand = AphPreflopProvider.HandAt(row, col);
                var square = new Border { Background = Frozen(CellColor(view, hand)), Margin = new Thickness(0.5) };
                if (hand == view.HeroClass)
                {
                    square.BorderBrush = Brushes.White;
                    square.BorderThickness = new Thickness(1.3);
                    square.Margin = new Thickness(0);
                }
                Grid.SetRow(square, row + 1);
                Grid.SetColumn(square, col + 1);
                grid.Children.Add(square);
            }
            panel.Children.Add(grid);

            if (view.HeroPlay is { Length: > 0 } play)
                panel.Children.Add(new TextBlock
                {
                    Text = play,
                    FontSize = font * 0.72,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brushes.White,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 2)
                });

            var legend = new WrapPanel();
            void Key(Color color, string text)
            {
                legend.Children.Add(LegendChip(color, cell));
                legend.Children.Add(Text(text + "  ", font * 0.66, DimBrush, FontWeights.Normal));
            }
            if (view.IsHeroRange)
            {
                Key(RaiseCell, LocalizationManager.Text("Hud.RangeRaise"));
                Key(CallCell, LocalizationManager.Text("Hud.RangeCall"));
            }
            else if (view.Ahead is not null)
            {
                Key(AheadCell, LocalizationManager.Text("Hud.RangeAhead"));
                Key(VillainCell, LocalizationManager.Text("Hud.RangeBehind"));
                legend.Children.Add(Text(LocalizationManager.Text("Hud.RangeLikely"), font * 0.62, DimBrush, FontWeights.Normal));
            }
            else
            {
                Key(VillainCell, LocalizationManager.Text("Hud.RangeVillain"));
            }
            panel.Children.Add(legend);
            return panel;
        }

        /// <summary>
        /// Color de una mano en la tabla. Tu rango: naranja sube, azul paga, oscuro fuera. Rango del
        /// rival: de rojo (te gana ahora) a verde (le ganas), mas intenso cuanto mas probable; sin tablero,
        /// rojo por lo probable que es.
        /// </summary>
        private static Color CellColor(LiveRangeView view, string hand)
        {
            if (view.IsHeroRange)
                return view.Raise.ContainsKey(hand) ? RaiseCell : view.Call.ContainsKey(hand) ? CallCell : EmptyCell;
            if (!view.Raise.TryGetValue(hand, out var weight) || weight <= 0)
                return EmptyCell;
            var tone = view.Ahead is { } ahead && ahead.TryGetValue(hand, out var share)
                ? Mix(VillainCell, AheadCell, Math.Clamp(share, 0, 1))
                : VillainCell;
            return Mix(EmptyCell, tone, 0.3 + 0.7 * Math.Clamp(weight, 0, 1));
        }

        private static Border LegendChip(Color color, double cell) => new()
        {
            Background = Frozen(color),
            Width = cell * 0.8,
            Height = cell * 0.8,
            Margin = new Thickness(0, 0, 3, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        private static Color Mix(Color from, Color to, double t) => Color.FromRgb(
            (byte)Math.Round(from.R + (to.R - from.R) * t),
            (byte)Math.Round(from.G + (to.G - from.G) * t),
            (byte)Math.Round(from.B + (to.B - from.B) * t));

        // ---------------------------------------------------------------- solver de entrenamiento

        /// <summary>
        /// Muestra (o quita, con null) la recomendacion del solver de entrenamiento. La manda la
        /// lectura del Modo Entrenador, que solo funciona en mesas de dinero ficticio; aparece en el
        /// turno del heroe y nunca hace clic.
        /// </summary>
        public void ShowSolver(LiveDecision? decision)
        {
            _solverDecision = decision;
            _thinking = null;
            _unreadable = null;
            Refresh();
        }

        /// <summary>El equilibrio GTO se esta calculando (el turn tarda unos segundos).</summary>
        public void ShowThinking(string street)
        {
            _solverDecision = null;
            _thinking = street;
            _unreadable = null;
            Refresh();
        }

        /// <summary>
        /// Es tu turno pero el lector no reconoce una de tus cartas o del board: no hay consejo y se
        /// dice por que (antes el cuadro desaparecia sin explicacion).
        /// </summary>
        public void ShowUnreadable(string where, string? guess)
        {
            _solverDecision = null;
            _thinking = null;
            _unreadable = (where, guess);
            Refresh();
        }

        /// <summary>Aviso de APH (por ejemplo, de hacer una pausa) unos segundos en el cuadro, entre manos.</summary>
        public void ShowNotice(string text)
        {
            _notice = text;
            _noticeAt = DateTime.Now;
            Refresh();
        }

        private static FrameworkElement NoticeContent(string text, double font)
        {
            var panel = new StackPanel { MaxWidth = font * 26 };
            panel.Children.Add(Text("APH", font * 0.75, DimBrush, FontWeights.SemiBold));
            panel.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = font * 0.95,
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });
            return panel;
        }

        /// <summary>Calificacion de la mano que acaba de terminar, unos segundos junto a tu cuadro.</summary>
        public void ShowGrade(TrainerHandReport report)
        {
            if (report.Decisions.Count == 0)
                return;
            _grade = report;
            _gradeAt = DateTime.Now;
            Refresh();
        }

        private void PlaceSolver(HudOverlaySettings settings, double font, double width, double height)
        {
            // Lo que toca ahora: la decision de tu turno, el aviso de "calculando" o la calificacion
            // de la mano anterior (unos segundos).
            string key;
            Func<FrameworkElement> build;
            Color border;
            if (!settings.ShowSolver)
            {
                _solverBox.Visibility = Visibility.Collapsed;
                return;
            }
            if (_solverDecision is { } decision)
            {
                key = $"d|{decision.Action}|{decision.Reason}|{decision.Exploit}|{font:0.0}";
                build = () => SolverContent(decision, font);
                border = ToneColor(decision.Tone);
            }
            else if (_thinking is { } street)
            {
                key = $"t|{street}|{font:0.0}";
                build = () => ThinkingContent(street, font);
                border = Color.FromRgb(110, 120, 132);
            }
            else if (_unreadable is { } unreadable)
            {
                key = $"u|{unreadable.Where}|{unreadable.Guess}|{font:0.0}";
                build = () => UnreadableContent(unreadable.Where, unreadable.Guess, font);
                border = Color.FromRgb(230, 180, 60);
            }
            else if (_notice is { } notice && DateTime.Now - _noticeAt < NoticeTime)
            {
                key = $"n|{notice}|{font:0.0}";
                build = () => NoticeContent(notice, font);
                border = Color.FromRgb(244, 162, 97);
            }
            else if (settings.ShowGrade && _grade is { } report && DateTime.Now - _gradeAt < GradeTime)
            {
                key = $"g|{report.HandId}|{font:0.0}";
                build = () => GradeContent(report, font);
                border = GradeColor(report);
            }
            else
            {
                _solverBox.Visibility = Visibility.Collapsed;
                return;
            }

            if (key != _solverKey)
            {
                _solverBox.Child = build();
                _solverBox.BorderBrush = Frozen(border);
                _solverKey = key;
                // Consejo nuevo de explotar: tambien parpadea la capsula de ese rival, para verlo rapido.
                if (_solverDecision?.ExploitTarget is { } target)
                    BlinkCapsule(target);
            }
            _solverBox.Visibility = Visibility.Visible;
            _solverBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = _solverBox.DesiredSize;

            // Encima del cuadro del heroe; si esta oculto, en su lugar.
            double left, top;
            if (_boxes.TryGetValue(0, out var hero) && hero.Border.Visibility == Visibility.Visible)
            {
                left = Canvas.GetLeft(hero.Border);
                top = Canvas.GetTop(hero.Border) - size.Height - 4;
            }
            else
            {
                var (cx, cy) = settings.CenterFor(_tableSize, 0);
                left = width * cx - size.Width / 2;
                top = height * cy - size.Height / 2;
            }
            Canvas.SetLeft(_solverBox, Math.Clamp(left, 2, Math.Max(2, width - size.Width - 2)));
            Canvas.SetTop(_solverBox, Math.Clamp(top, 2, Math.Max(2, height - size.Height - 2)));
        }

        private static FrameworkElement ThinkingContent(string street, double font)
        {
            var panel = new StackPanel { MaxWidth = font * 26 };
            panel.Children.Add(Text(LocalizationManager.Text("Hud.SolverTitle"), font * 0.75, DimBrush, FontWeights.SemiBold));
            panel.Children.Add(Text(string.Format(LocalizationManager.Text("Hud.Thinking"), StreetWord(street)), font * 1.05, Brushes.White, FontWeights.SemiBold));
            return panel;
        }

        private static FrameworkElement UnreadableContent(string where, string? guess, double font)
        {
            var panel = new StackPanel { MaxWidth = font * 26 };
            panel.Children.Add(Text(LocalizationManager.Text("Hud.ReaderTitle"), font * 0.75, DimBrush, FontWeights.SemiBold));
            var card = guess is { Length: 2 } ? string.Format(LocalizationManager.Text("Hud.UnreadableGuess"), CardWord(guess)) : "";
            panel.Children.Add(new TextBlock
            {
                Text = string.Format(LocalizationManager.Text(where == "HERO" ? "Hud.UnreadableHero" : "Hud.UnreadableBoard"), card),
                FontSize = font * 0.95,
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });
            panel.Children.Add(new TextBlock
            {
                Text = LocalizationManager.Text("Hud.UnreadableHint"),
                FontSize = font * 0.8,
                Foreground = DimBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0)
            });
            return panel;
        }

        /// <summary>"Jh" -> "J♥", "Td" -> "10♦".</summary>
        private static string CardWord(string card) =>
            (card[0] == 'T' ? "10" : card[0].ToString()) + card[1] switch
            {
                'h' => "\u2665",
                'd' => "\u2666",
                'c' => "\u2663",
                's' => "\u2660",
                _ => ""
            };

        private static string StreetWord(string street) => LocalizationManager.Text(street switch
        {
            "RIVER" => "Hud.Street.River",
            "TURN" => "Hud.Street.Turn",
            "FLOP" => "Hud.Street.Flop",
            _ => "Hud.Street.Preflop"
        });

        /// <summary>Tu mano anterior: cuantas decisiones buenas, dudosas y errores, y el peor fallo.</summary>
        private static FrameworkElement GradeContent(TrainerHandReport report, double font)
        {
            var good = report.Decisions.Count(d => d.Grade == TrainerGrade.Good);
            var marginal = report.Decisions.Count(d => d.Grade == TrainerGrade.Marginal);
            var mistakes = report.Decisions.Count(d => d.Grade == TrainerGrade.Mistake);
            var panel = new StackPanel { MaxWidth = font * 28 };
            panel.Children.Add(Text(LocalizationManager.Text("Hud.LastHandGrade"), font * 0.75, DimBrush, FontWeights.SemiBold));
            var counts = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            counts.Children.Add(Text($"\u2713 {good}   ", font * 1.1, Frozen(Color.FromRgb(70, 190, 110)), FontWeights.Bold));
            counts.Children.Add(Text($"~ {marginal}   ", font * 1.1, Frozen(Color.FromRgb(230, 180, 60)), FontWeights.Bold));
            counts.Children.Add(Text($"\u2717 {mistakes}", font * 1.1, Frozen(Color.FromRgb(225, 80, 90)), FontWeights.Bold));
            panel.Children.Add(counts);

            var worst = report.Decisions.FirstOrDefault(d => d.Grade == TrainerGrade.Mistake)
                        ?? report.Decisions.FirstOrDefault(d => d.Grade == TrainerGrade.Marginal);
            string line;
            if (worst is null)
                line = LocalizationManager.Text("Hud.AllGood");
            else if (ActionGroup(worst.Recommended) == worst.ChosenKind)
                // Misma accion que la recomendada (la nota va por el tamano o por poco margen): se
                // explica en vez de decir "elegiste igualar; mejor: igualar".
                line = $"{worst.StreetLabel}: {FirstSentence(worst.Explanation)}";
            else
                line = string.Format(LocalizationManager.Text("Hud.WorstDecision"), worst.StreetLabel, ActionWords(worst.Chosen), ActionWords(worst.Recommended));
            panel.Children.Add(new TextBlock { Text = line, FontSize = font * 0.88, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap });
            return panel;
        }

        /// <summary>Fold, Check, Call o Raise (apostar, subir, 3bet, iso-raise...), como TrainerDecision.ChosenKind.</summary>
        private static string ActionGroup(string text)
        {
            var first = text.Split(' ', 2)[0].ToLowerInvariant();
            return first switch
            {
                "fold" or "no" => "Fold",
                "check" or "pasar" => "Check",
                "call" or "igualar" or "limp" => "Call",
                _ => "Raise"
            };
        }

        /// <summary>"Call 6 bb" -> "Igualar 6 bb", "Raise a 3 bb" -> "Subir a 3 bb" (la correccion escribe en jerga inglesa).</summary>
        private static string ActionWords(string text)
        {
            var parts = text.Split(' ', 2);
            var key = parts[0].ToLowerInvariant() switch
            {
                "fold" => "Hud.Act.Fold",
                "check" => "Hud.Act.Check",
                "call" => "Hud.Act.Call",
                "bet" => "Hud.Act.Bet",
                "raise" => "Hud.Act.Raise",
                _ => null
            };
            return key is null ? text : LocalizationManager.Text(key) + (parts.Length > 1 ? " " + parts[1] : "");
        }

        private static string FirstSentence(string text)
        {
            var clean = text.Trim();
            var end = clean.IndexOf(". ", StringComparison.Ordinal);
            return end > 0 ? clean[..(end + 1)] : clean;
        }

        private static Color GradeColor(TrainerHandReport report) =>
            report.Decisions.Any(d => d.Grade == TrainerGrade.Mistake) ? Color.FromRgb(225, 80, 90)
            : report.Decisions.Any(d => d.Grade == TrainerGrade.Marginal) ? Color.FromRgb(230, 180, 60)
            : Color.FromRgb(70, 190, 110);

        // ---------------------------------------------------------------- repaso de la mano anterior

        private void PlaceRecap(HudTableSnapshot snapshot, HudOverlaySettings settings, double font, double width, double height)
        {
            if (!settings.ShowRecap || snapshot.LastHand is not { Count: > 0 } lines || DateTime.Now - snapshot.LastHandAt > RecapTime)
            {
                _recapBox.Visibility = Visibility.Collapsed;
                return;
            }

            if (_recapAt != snapshot.LastHandAt || Math.Abs(_recapFont - font) > 0.01)
            {
                var recap = HandRecap.Build(lines, _heroName);
                if (recap is null)
                {
                    _recapBox.Visibility = Visibility.Collapsed;
                    return;
                }
                _recapBox.Child = RecapContent(recap, font);
                _recapAt = snapshot.LastHandAt;
                _recapFont = font;
            }

            _recapBox.Visibility = Visibility.Visible;
            _recapBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = _recapBox.DesiredSize;
            Canvas.SetLeft(_recapBox, Math.Clamp(width * 0.5 - size.Width / 2, 2, Math.Max(2, width - size.Width - 2)));
            Canvas.SetTop(_recapBox, Math.Clamp(height * 0.47 - size.Height / 2, 2, Math.Max(2, height - size.Height - 2)));
        }

        /// <summary>Preflop, board, cartas vistas con su jugada y quien gano.</summary>
        private static FrameworkElement RecapContent(HandRecap recap, double font)
        {
            var panel = new StackPanel { MaxWidth = font * 36 };
            panel.Children.Add(Text(string.Format(LocalizationManager.Text("Recap.Title"), recap.HandId), font * 0.75, DimBrush, FontWeights.SemiBold));
            if (recap.Preflop.Length > 0)
                panel.Children.Add(new TextBlock
                {
                    Text = LocalizationManager.Text("Recap.Preflop") + " " + recap.Preflop,
                    FontSize = font * 0.88,
                    Foreground = Brushes.White,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 0)
                });

            var boardRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
            boardRow.Children.Add(Text(LocalizationManager.Text("Recap.Board") + " ", font * 0.88, DimBrush, FontWeights.SemiBold));
            if (recap.Board.Count == 0)
                boardRow.Children.Add(Text(LocalizationManager.Text("Recap.NoFlop"), font * 0.88, Brushes.White, FontWeights.Normal));
            for (var i = 0; i < recap.Board.Count; i++)
                boardRow.Children.Add(CardChip(recap.Board[i], font, i is 3 or 4 ? 5 : 1));
            panel.Children.Add(boardRow);

            foreach (var shown in recap.Shown)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
                row.Children.Add(Text(shown.Name + " ", font * 0.9, Brushes.White, FontWeights.Bold));
                foreach (var card in shown.Cards)
                    row.Children.Add(CardChip(card, font, 1));
                if (shown.Hand.Length > 0)
                    row.Children.Add(Text("  " + shown.Hand, font * 0.85, DimBrush, FontWeights.Normal));
                panel.Children.Add(row);
            }

            foreach (var (name, bb) in recap.Winners)
            {
                var you = name == LocalizationManager.Text("Recap.You");
                var key = (you ? "Recap.WinsYou" : "Recap.Wins") + (recap.Showdown ? "" : "NoShowdown");
                panel.Children.Add(Text(string.Format(System.Globalization.CultureInfo.CurrentCulture, LocalizationManager.Text(key), name, bb),
                    font * 0.95, Frozen(Color.FromRgb(90, 210, 130)), FontWeights.Bold));
            }
            return panel;
        }

        private static Border CardChip(int card, double font, double leftMargin)
        {
            var (symbol, ink) = SuitInk[TrainerCards.Suit(card)];
            var rank = TrainerCards.Ranks[TrainerCards.Rank(card)];
            return new Border
            {
                Background = Frozen(Color.FromRgb(246, 245, 240)),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(3, 0, 3, 0),
                Margin = new Thickness(leftMargin, 0, 1, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = $"{(rank == 'T' ? "10" : rank.ToString())}{symbol}", FontSize = font * 0.95, FontWeight = FontWeights.Bold, Foreground = Frozen(ink) }
            };
        }

        private static FrameworkElement SolverContent(LiveDecision decision, double font)
        {
            var panel = new StackPanel { MaxWidth = font * 26 };
            // De donde sale: el equilibrio GTO, la tabla preflop de APH o la mejor jugada contra su rango.
            // Con un rival conocido y una fuga clara: "EXPLOTAR · pagador · 80 manos".
            var exploiting = decision.Exploit is not null;
            var title = exploiting
                ? string.Format(LocalizationManager.Text("Hud.ExploitLeakTitle"), decision.Exploit)
                : LocalizationManager.Text(decision.IsGto ? "Hud.SolverTitle" : decision.Street == "PREFLOP" ? "Hud.ChartTitle" : "Hud.ExploitTitle");
            var titleBlock = Text(title, font * 0.75, exploiting ? ExploitBrush : DimBrush, exploiting ? FontWeights.Bold : FontWeights.SemiBold);
            if (exploiting)
                titleBlock.TextWrapping = TextWrapping.Wrap; // multiway lleva el nombre del rival
            panel.Children.Add(titleBlock);
            var color = ToneColor(decision.Tone);
            panel.Children.Add(new Border
            {
                Background = Frozen(color),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 2, 8, 3),
                Margin = new Thickness(0, 2, 0, 3),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock { Text = decision.Action, FontSize = font * 1.35, FontWeight = FontWeights.Bold, Foreground = InkFor(color) }
            });
            panel.Children.Add(new TextBlock
            {
                Text = decision.Reason,
                FontSize = font * 0.88,
                Foreground = Brushes.White,
                TextWrapping = TextWrapping.Wrap
            });
            // Rival explotado: como se le explota, en una linea bajo el motivo.
            TextBlock? planBlock = null;
            if (decision.ExploitPlan is { Length: > 0 } plan)
            {
                planBlock = new TextBlock
                {
                    Text = char.ToUpper(plan[0]) + plan[1..] + ".",
                    FontSize = font * 0.8,
                    FontStyle = FontStyles.Italic,
                    Foreground = Frozen(Color.FromRgb(255, 214, 140)),
                    Margin = new Thickness(0, 3, 0, 0),
                    TextWrapping = TextWrapping.Wrap
                };
                panel.Children.Add(planBlock);
            }

            // Lectura dudosa (pila, bote, importe o posicion corregidos o adivinados): en amarillo.
            if (decision.ReadWarning is { Length: > 0 } warning)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = warning,
                    FontSize = font * 0.8,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Frozen(Color.FromRgb(255, 205, 60)),
                    Margin = new Thickness(0, 3, 0, 0),
                    TextWrapping = TextWrapping.Wrap
                });
            }

            // Rival que se deja farolear: aviso para activar "Farol" en la mesa (o que ya esta activo).
            if (decision.BluffNote is { Length: > 0 } bluff)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = bluff,
                    FontSize = font * 0.8,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Frozen(Color.FromRgb(120, 200, 255)),
                    Margin = new Thickness(0, 3, 0, 0),
                    TextWrapping = TextWrapping.Wrap
                });
            }

            // Al aparecer, el aviso de explotar parpadea unos 4 s y luego se queda fijo (el cuadro solo
            // se reconstruye cuando cambia el consejo, asi que no parpadea en cada refresco).
            if (exploiting)
            {
                Blink(titleBlock);
                if (planBlock is not null)
                    Blink(planBlock);
            }

            return panel;
        }

        private static readonly Brush ExploitBrush = Frozen(Color.FromRgb(255, 140, 40));

        /// <summary>
        /// La capsula del HUD del rival que se explota parpadea unos 4 s con el borde naranja (el mismo
        /// grosor de borde: la capsula no cambia de tamano).
        /// </summary>
        private void BlinkCapsule(string player)
        {
            var name = Clean(player);
            var box = _boxes.Values.FirstOrDefault(item =>
                item.Border.Visibility == Visibility.Visible && string.Equals(item.Player, name, StringComparison.OrdinalIgnoreCase));
            if (box is null)
                return;

            box.Border.BorderBrush = ExploitBrush;
            box.BlinkUntil = DateTime.Now.AddSeconds(4);
            Blink(box.Border);
            var restore = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            restore.Tick += (_, _) =>
            {
                restore.Stop();
                if (!HudOverlaySettings.MoveMode)
                    box.Border.BorderBrush = box.RestBorder;
            };
            restore.Start();
        }

        private static void Blink(UIElement element)
        {
            // Parpadeo leve: baja poco la opacidad y despacio (el usuario lo prefiere asi, sin distraer).
            var blink = new System.Windows.Media.Animation.DoubleAnimation(1, 0.55, TimeSpan.FromMilliseconds(650))
            {
                AutoReverse = true,
                RepeatBehavior = new System.Windows.Media.Animation.RepeatBehavior(3),
                FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop
            };
            element.BeginAnimation(UIElement.OpacityProperty, blink);
        }

        /// <summary>Color de la accion: retirarse gris, pasar turquesa, pagar azul, apostar o subir naranja.</summary>
        private static Color ToneColor(string tone) => tone switch
        {
            "fold" => Color.FromRgb(110, 120, 132),
            "check" => Color.FromRgb(74, 144, 164),
            "call" => Color.FromRgb(59, 130, 196),
            _ => Color.FromRgb(224, 123, 46)
        };

        /// <summary>Asientos de la ultima mano; si todavia no los hay, el orden de la lista.</summary>
        private static IReadOnlyList<TableSeat> SeatsOf(HudTableSnapshot snapshot) =>
            snapshot.Seats.Count > 0
                ? snapshot.Seats
                : snapshot.Players.Select((p, i) => new TableSeat(i + 1, p.Name, false)).ToList();

        private HudBox BoxFor(int place)
        {
            if (_boxes.TryGetValue(place, out var existing))
                return existing;

            var border = new Border
            {
                BorderBrush = BoxBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(5, 3, 5, 4),
                Visibility = Visibility.Collapsed
            };
            var box = new HudBox { Border = border, Place = place };
            border.MouseLeftButtonDown += (_, e) => StartDrag(box, e);
            border.MouseMove += (_, e) => Drag(box, e);
            border.MouseLeftButtonUp += (_, e) => EndDrag(box, e);
            _boxes[place] = box;
            _canvas.Children.Add(border);
            ApplyMoveMode();
            return box;
        }

        private void PlaceBox(HudBox box, HudOverlaySettings settings, double width, double height)
        {
            box.Border.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = box.Border.DesiredSize;
            var (cx, cy) = settings.CenterFor(_tableSize, box.Place);
            var left = width * cx - size.Width / 2;
            var top = height * cy - size.Height / 2;
            Canvas.SetLeft(box.Border, Math.Clamp(left, 2, Math.Max(2, width - size.Width - 2)));
            Canvas.SetTop(box.Border, Math.Clamp(top, 2, Math.Max(2, height - size.Height - 2)));
        }

        private string Signature(TableSeat seat, PlayerStats? p, bool hero, bool full, double font, StakeProfile stake) =>
            p is null
                ? $"{seat.Name}|{seat.SittingOut}|{hero}|{full}|{font:0.0}|{PlayerNotes.Version}"
                : string.Join("|", seat.Name, seat.SittingOut, hero, full, font.ToString("0.0", CultureInfo.InvariantCulture), stake, PlayerNotes.Version,
                    p.HandsReceived, p.VpipCount, p.PfrCount, p.ThreeBetCount, p.ThreeBetOpportunities, p.PostflopBetsAndRaises,
                    p.PostflopCalls, p.CBetFlopOpportunities, p.CBetFlopMade, p.FacedCBetFlop, p.FoldToCBetFlop, p.SawFlop,
                    p.WentToShowdown, p.WonAtShowdown, p.WonWhenSawFlop, LastKnownHand(p));

        /// <summary>
        /// Borde de la capsula: con el color de las manos que llevas con el jugador (si ya jugaste con el
        /// en otras mesas y van 30 o mas), un poco mas grueso para que se vea de un vistazo; si no, gris.
        /// No pisa el naranja de "explotar" ni el blanco de mover el HUD.
        /// </summary>
        private static void SetRestBorder(HudBox box, Color? sample)
        {
            box.RestBorder = sample is { } color ? Frozen(color) : BoxBorder;
            box.Border.BorderThickness = new Thickness(sample is null ? 1 : 2);
            if (!HudOverlaySettings.MoveMode && DateTime.Now >= box.BlinkUntil)
                box.Border.BorderBrush = box.RestBorder;
        }

        /// <summary>
        /// Encabezado (nombre, tipo, manos, ultima mano vista) y la mini tabla de estadisticas.
        /// <paramref name="knownHands"/> son las manos con el en tus otras mesas: entonces las manos
        /// dicen "N contigo" (todas, de todas tus mesas) con el color de la muestra.
        /// </summary>
        private FrameworkElement BuildContent(TableSeat seat, PlayerStats? player, bool hero, bool full, double font, StakeProfile stake, int knownHands = 0)
        {
            var panel = new StackPanel();
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            var name = hero ? LocalizationManager.Text("Hud.You") : Shorten(Clean(seat.Name), 14);
            var note = hero ? null : PlayerNotes.Get(seat.Name);
            if (PlayerNotes.ColorOf(note?.Color) is { } tag)
                header.Children.Add(new System.Windows.Shapes.Ellipse
                {
                    Width = font * 0.75,
                    Height = font * 0.75,
                    Fill = Frozen(tag),
                    Margin = new Thickness(0, 0, 4, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
            header.Children.Add(Text(name, font, Brushes.White, FontWeights.Bold));

            var (label, color) = seat.SittingOut ? (LocalizationManager.Text("Hud.Away"), Color.FromRgb(90, 98, 110)) : TypeOf(player);
            header.Children.Add(Pill(label, color, font * 0.82));

            var hands = player?.HandsReceived ?? 0;
            if (knownHands > 0)
            {
                var withYou = string.Format(CultureInfo.CurrentCulture, LocalizationManager.Text("Hud.HandsWithYou"), hands);
                if (PlayerHistoryIndex.SampleColor(hands) is { } sample)
                    header.Children.Add(Pill(withYou, sample, font * 0.82));
                else
                    header.Children.Add(Text(" " + withYou, font * 0.85, DimBrush, FontWeights.SemiBold));
            }
            else
            {
                var handsText = hands == 1
                    ? LocalizationManager.Text("Hud.OneHand")
                    : string.Format(CultureInfo.CurrentCulture, LocalizationManager.Text("Hud.Hands"), hands);
                header.Children.Add(Text(" " + handsText, font * 0.85, DimBrush, FontWeights.Normal));
            }
            var last = player is null ? null : LastKnownHand(player);
            if (full && last is not null)
                header.Children.Add(Text(" · " + last, font * 0.85, DimBrush, FontWeights.SemiBold));
            panel.Children.Add(header);
            if (!string.IsNullOrWhiteSpace(note?.Note))
                panel.Children.Add(new TextBlock
                {
                    Text = Shorten(note!.Note, 46),
                    FontSize = font * 0.8,
                    FontStyle = FontStyles.Italic,
                    Foreground = Frozen(Color.FromRgb(240, 214, 120)),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = font * 18
                });

            if (player is null)
                return panel;

            var rows = full
                ? new[] { StatsRow1(player), StatsRow2(player) }
                : new[] { StatsRow1(player).Take(3).ToArray() };
            foreach (var row in rows)
            {
                var grid = new Grid { Margin = new Thickness(0, 3, 0, 0) };
                for (var i = 0; i < row.Length; i++)
                {
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(font * 3.5) });
                    var cell = StatCell(row[i], player, font, stake);
                    Grid.SetColumn(cell, i);
                    grid.Children.Add(cell);
                }
                panel.Children.Add(grid);
            }
            return panel;
        }

        private sealed record StatDef(string Label, double Value, int Sample, string Key, string Format = "0");

        private static StatDef[] StatsRow1(PlayerStats p) => new[]
        {
            new StatDef("VPIP", p.VPIPPct, p.HandsReceived, "VPIP"),
            new StatDef("PFR", p.PFRPct, p.HandsReceived, "PFR"),
            new StatDef("3B", p.ThreeBetPct, p.ThreeBetOpportunities, "THREEBET"),
            new StatDef("AF", p.AF, p.PostflopBetsAndRaises + p.PostflopCalls, "AF", "0.#"),
            new StatDef("AFq", p.AFqPct, p.PostflopBetsAndRaises + p.PostflopCalls, "AFQ"),
        };

        private static StatDef[] StatsRow2(PlayerStats p) => new[]
        {
            new StatDef("CB", p.CBetFlopPct, p.CBetFlopOpportunities, "CBF"),
            new StatDef("FvCB", p.FoldVsCBetFlopPct, p.FacedCBetFlop, "FVCBF"),
            new StatDef("WTSD", p.WTSDPct, p.SawFlop, "WTSD"),
            new StatDef("W$SD", p.WSDPct, p.WentToShowdown, "WSD"),
            new StatDef("WWSF", p.WWSFPct, p.SawFlop, "WWSF"),
        };

        /// <summary>
        /// Nombre arriba y numero abajo. El fondo es el color de la estadistica (con 30 manos o mas,
        /// como en el resto de APH); con menos, gris. El texto, blanco o negro segun el fondo.
        /// Antes el numero se pintaba con el color de fondo y con pocas manos no se veia.
        /// </summary>
        private Border StatCell(StatDef stat, PlayerStats player, double font, StakeProfile stake)
        {
            // La misma escala que el resto de APH (StatScale): azul menos que lo normal, verde normal,
            // naranja y rojo mas que lo normal; gris con pocas manos.
            var background = NeutralCell;
            if (stat.Sample > 0 &&
                StatScale.ColorOf(StatScale.Level(stat.Key, stat.Value, player.HandsReceived, stake)) is { } band)
            {
                background = band;
            }

            var ink = InkFor(background);
            var cell = new StackPanel();
            cell.Children.Add(new TextBlock
            {
                Text = stat.Label,
                FontSize = font * 0.68,
                Foreground = ink,
                Opacity = 0.8,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            cell.Children.Add(new TextBlock
            {
                Text = stat.Sample == 0 ? "–" : stat.Value.ToString(stat.Format, CultureInfo.CurrentCulture),
                FontSize = font * 1.02,
                FontWeight = FontWeights.Bold,
                Foreground = ink,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            return new Border
            {
                Background = Frozen(background),
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(1, 0, 1, 0),
                Padding = new Thickness(1, 0, 1, 1),
                Child = cell
            };
        }

        /// <summary>
        /// Tipo de jugador con su color: con 30 manos o mas, el de APH; de 15 a 29, el mismo con "?"
        /// (todavia es una sospecha); con menos, "Poca muestra".
        /// </summary>
        private static (string Label, Color Color) TypeOf(PlayerStats? player) =>
            player is null
                ? PlayerProfileClassifier.Tag(0, 0, 0, 0, 0)
                : PlayerProfileClassifier.Tag(player.HandsReceived, player.VPIPPct, player.PFRPct, player.ThreeBetPct, player.AF);

        private static Border Pill(string text, Color color, double font) => new()
        {
            Background = Frozen(color),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(5, 0, 5, 1),
            Margin = new Thickness(5, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = text, FontSize = font, FontWeight = FontWeights.SemiBold, Foreground = InkFor(color) }
        };

        private static TextBlock Text(string text, double size, Brush brush, FontWeight weight) => new()
        {
            Text = text,
            FontSize = size,
            FontWeight = weight,
            Foreground = brush,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap
        };

        /// <summary>La ultima mano que se le vio (si ensenó cartas), o null.</summary>
        private static string? LastKnownHand(PlayerStats player)
        {
            var last = player.LastHands.Reverse()
                .FirstOrDefault(hand => !string.IsNullOrWhiteSpace(hand) && hand != "--" &&
                                        !hand.Equals("xx", StringComparison.OrdinalIgnoreCase));
            return string.IsNullOrWhiteSpace(last) ? null : last;
        }

        private static string Shorten(string text, int max) => text.Length > max ? text[..(max - 1)] + "…" : text;

        private static string Clean(string name) => name.Trim().TrimEnd(':').Trim();

        private bool IsHero(string name) =>
            _heroName is not null && string.Equals(Clean(name), _heroName, StringComparison.Ordinal);

        private static Brush InkFor(Color background)
        {
            static double Channel(byte value)
            {
                var c = value / 255.0;
                return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            }

            var luminance = 0.2126 * Channel(background.R) + 0.7152 * Channel(background.G) + 0.0722 * Channel(background.B);
            return (luminance + 0.05) / 0.05 >= 1.05 / (luminance + 0.05) ? Frozen(Color.FromRgb(15, 15, 15)) : Brushes.White;
        }

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        // ---------------------------------------------------------------- mover HUD

        private void OnMoveModeChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(ApplyMoveMode);

        private void ApplyMoveMode()
        {
            var moving = HudOverlaySettings.MoveMode;
            _moveTint.Visibility = moving ? Visibility.Visible : Visibility.Collapsed;
            _moveBanner.Visibility = moving ? Visibility.Visible : Visibility.Collapsed;
            foreach (var box in _boxes.Values)
            {
                box.Border.Cursor = moving ? Cursors.SizeAll : null;
                box.Border.BorderBrush = moving ? Brushes.White : box.RestBorder;
            }
            SetClickThrough(!moving);
        }

        private void StartDrag(HudBox box, MouseButtonEventArgs e)
        {
            if (!HudOverlaySettings.MoveMode)
                return;
            _dragging = box;
            _dragOffset = e.GetPosition(box.Border);
            box.Border.CaptureMouse();
            e.Handled = true;
        }

        private void Drag(HudBox box, MouseEventArgs e)
        {
            if (_dragging != box)
                return;
            var position = e.GetPosition(_canvas);
            Canvas.SetLeft(box.Border, position.X - _dragOffset.X);
            Canvas.SetTop(box.Border, position.Y - _dragOffset.Y);
        }

        private void EndDrag(HudBox box, MouseButtonEventArgs e)
        {
            if (_dragging != box)
                return;
            box.Border.ReleaseMouseCapture();
            _dragging = null;
            if (_canvas.Width > 0 && _canvas.Height > 0)
            {
                var x = (Canvas.GetLeft(box.Border) + box.Border.ActualWidth / 2) / _canvas.Width;
                var y = (Canvas.GetTop(box.Border) + box.Border.ActualHeight / 2) / _canvas.Height;
                HudOverlaySettings.Current.SetCenter(_tableSize, box.Place, x, y);
            }
            e.Handled = true;
        }

        /// <summary>Normalmente los clics pasan a la mesa; en "Mover HUD" los recibe el HUD.</summary>
        private void SetClickThrough(bool clickThrough)
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero)
                return;

            var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64() | WsExToolWindow;
            style = clickThrough
                ? style | WsExTransparent | WsExNoActivate
                : style & ~WsExTransparent & ~WsExNoActivate;
            SetWindowLongPtr(handle, GwlExStyle, new IntPtr(style));
        }

        private const int GwlExStyle = -20;
        private const long WsExTransparent = 0x00000020L;
        private const long WsExToolWindow = 0x00000080L;
        private const long WsExNoActivate = 0x08000000L;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    }
}
