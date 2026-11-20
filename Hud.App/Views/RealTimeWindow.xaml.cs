using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Hud.App.Services;
using Hud.App.Services.Solver;
using Hud.App.Services.Trainer;

namespace Hud.App.Views
{
    public partial class RealTimeWindow : Window
    {
        private readonly List<PokerTableOverlayWindow> _overlays = new();

        /// <summary>Mesas ya enlazadas (por nombre) y el reloj que busca las que faltan.</summary>
        private readonly HashSet<string> _linkedTables = new(StringComparer.OrdinalIgnoreCase);
        private readonly System.Windows.Threading.DispatcherTimer _watchTimer = new() { Interval = TimeSpan.FromSeconds(4) };

        // Mesas enlazadas (para poner el solver en una de ellas) y la lectura en vivo del solver.
        private readonly List<(PokerStarsTableWindow Window, string Path, PokerTableOverlayWindow Overlay)> _linked = new();
        private readonly Dictionary<IntPtr, SolverTable> _solvers = new();
        private TrainerCoach? _trainerCoach;
        private TrainerLiveAdvisor? _trainerAdvisor;
        private CardTemplateStore? _cardTemplates;

        // Mesas donde la lectura se bloqueo (dinero real, torneo, otro juego): no se reintenta.
        private readonly HashSet<string> _solverRefused = new(StringComparer.OrdinalIgnoreCase);

        public RealTimeWindow()
        {
            InitializeComponent();
            _watchTimer.Tick += (_, _) => LinkTables();
            Closed += (_, _) => _watchTimer.Stop();
            InitHudOptions();

            // Las mesas se detectan solas al abrir (y despues cada pocos segundos).
            Loaded += (_, _) =>
            {
                UpdateSlotVisibility();
                Dispatcher.BeginInvoke(() => BtnDetectTables_Click(this, new RoutedEventArgs()), System.Windows.Threading.DispatcherPriority.Background);
            };

            // aplica escala inicial segun el checkbox (compacto por defecto)
            ApplyScale(ChkCompact?.IsChecked == true);
            if (ChkCompact != null)
            {
                ChkCompact.Checked += (_, __) => ApplyScale(true);
                ChkCompact.Unchecked += (_, __) => ApplyScale(false);
            }

            Loaded += (_, _) => FitToWorkArea();
            Closed += (_, _) => ClearOverlays();
            Closed += (_, _) => _performance?.Close();

            // Biblioteca GTO del flop: el calculo sigue aunque se cierre esta ventana.
            Action<FlopLibraryProgress> onProgress = progress => Dispatcher.InvokeAsync(() => ShowFlopLibrary(progress));
            FlopLibraryBuilder.Instance.Progress += onProgress;
            Closed += (_, _) => FlopLibraryBuilder.Instance.Progress -= onProgress;
            Loaded += (_, _) => ShowFlopLibrary(FlopLibraryBuilder.Instance.Current);
        }

        // ---------------------------------------------------------------- biblioteca GTO del flop

        private void FlopLibraryButton_Click(object sender, RoutedEventArgs e)
        {
            var builder = FlopLibraryBuilder.Instance;
            if (builder.IsRunning)
            {
                builder.Stop();
                FlopLibraryButton.IsEnabled = false;   // hasta que terminen los flops a medias
                return;
            }

            try
            {
                builder.Start(TrainerCoach.LoadDefaultCharts());
            }
            catch (Exception ex)
            {
                AppLog.Warn("Biblioteca del flop: no se pudo empezar", ex);
                FlopLibraryText.Text = string.Format(LocalizationManager.Text("FlopLib.Error"), ex.Message);
                return;
            }
            ShowFlopLibrary(builder.Current);
        }

        private void ShowFlopLibrary(FlopLibraryProgress progress)
        {
            var share = progress.Total == 0 ? 0 : (double)progress.Done / progress.Total;
            FlopLibraryBar.Value = share;
            FlopLibraryButton.IsEnabled = true;
            var complete = progress.Done >= progress.Total;
            FlopLibraryButton.Visibility = complete && !progress.Running ? Visibility.Collapsed : Visibility.Visible;
            FlopLibraryButton.Content = LocalizationManager.Text(progress.Running ? "FlopLib.Stop" : "FlopLib.Start");

            string text;
            if (progress.Error is { } error)
                text = string.Format(LocalizationManager.Text("FlopLib.Error"), error);
            else if (complete)
                text = string.Format(LocalizationManager.Text("FlopLib.Done"), progress.Total.ToString("N0"));
            else if (progress.Running)
                text = string.Format(LocalizationManager.Text("FlopLib.Running"), progress.Done.ToString("N0"), progress.Total.ToString("N0"),
                    share * 100, progress.Remaining is { } left ? Duration(left) : LocalizationManager.Text("FlopLib.Estimating"));
            else
                text = string.Format(LocalizationManager.Text("FlopLib.Idle"), progress.Done.ToString("N0"), progress.Total.ToString("N0"), share * 100);
            FlopLibraryText.Text = text;
        }

        private static string Duration(TimeSpan time) =>
            time.TotalHours >= 1
                ? string.Format(LocalizationManager.Text("FlopLib.Hours"), (int)time.TotalHours, time.Minutes)
                : string.Format(LocalizationManager.Text("FlopLib.Minutes"), Math.Max(1, (int)Math.Ceiling(time.TotalMinutes)));

        /// <summary>
        /// Enlaza cada mesa abierta de PokerStars con su historial. PokerStars crea el archivo de la
        /// mesa al terminar la primera mano: antes, si se detectaba justo al sentarse, la mesa se
        /// quedaba sin enlazar. Ahora, despues de pulsar Detectar mesas, APH sigue mirando cada pocos
        /// segundos: enlaza esas mesas en cuanto aparece su historial y tambien las que se abran despues.
        /// </summary>
        private void BtnDetectTables_Click(object sender, RoutedEventArgs e)
        {
            StopAllSolvers(null);
            _linked.Clear();
            ClearOverlays();
            foreach (var slot in GetSlots())
                slot.ClearSlot();
            _linkedTables.Clear();

            LinkTables();
            if (!_watchTimer.IsEnabled)
                _watchTimer.Start();
        }

        private void LinkTables()
        {
            var slots = GetSlots();
            IReadOnlyList<PokerStarsTableWindow> windows;
            try
            {
                windows = PokerStarsWindowDetector.DetectOpenTables();
            }
            catch (Exception ex)
            {
                AppLog.Warn("Tiempo real: no se pudieron buscar las ventanas de PokerStars", ex);
                return;
            }

            var pending = windows.Where(window => !_linkedTables.Contains(window.TableName)).ToList();
            if (pending.Count > 0 && slots.Any(slot => slot.SourcePath is null))
            {
                IReadOnlyList<PokerStarsTableMatch> matches;
                try
                {
                    matches = PokerStarsWindowDetector.MatchHandHistories(pending);
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Tiempo real: no se pudieron buscar los historiales", ex);
                    matches = Array.Empty<PokerStarsTableMatch>();
                }

                var usedPaths = slots.Where(slot => slot.SourcePath is not null)
                    .Select(slot => slot.SourcePath!)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var match in matches.Where(match => !usedPaths.Contains(match.HandHistoryPath)))
                {
                    var slot = slots.FirstOrDefault(s => s.SourcePath is null);
                    if (slot is null)
                        break;

                    slot.StartAuto(match.HandHistoryPath, match.Window.TableName, match.Window.HeroName);
                    if (slot.SourcePath is null)
                        continue;
                    _linkedTables.Add(match.Window.TableName);

                    var overlay = new PokerTableOverlayWindow(
                        match.Window.Handle,
                        slot.GetHudSnapshot,
                        match.Window.HeroName);
                    overlay.Show();
                    _overlays.Add(overlay);
                    _linked.Add((match.Window, match.HandHistoryPath, overlay));
                }

                EnsureSolver();
            }
            else
            {
                // Sin mesas nuevas: si se libero un hueco (una mesa se cerro), el solver pasa a otra.
                EnsureSolver(quiet: true);
            }

            UpdateDetectionStatus(windows);
            UpdateSlotVisibility();
        }

        // ---------------------------------------------------------------- opciones del HUD

        /// <summary>Modo, tamano, ocultar el cuadro propio y "Mover HUD". Los HUD abiertos lo leen solos.</summary>
        private void InitHudOptions()
        {
            var settings = HudOverlaySettings.Current;
            HudFull.IsChecked = settings.FullMode;
            HudMinimal.IsChecked = !settings.FullMode;
            HudSmall.IsChecked = settings.Size == 0;
            HudNormal.IsChecked = settings.Size == 1;
            HudLarge.IsChecked = settings.Size == 2;
            HudHideHero.IsChecked = settings.HideHero;
            HudMoveToggle.IsChecked = HudOverlaySettings.MoveMode;

            void Save(Action<HudOverlaySettings> change)
            {
                change(HudOverlaySettings.Current);
                HudOverlaySettings.Current.Save();
            }

            HudFull.Checked += (_, _) => Save(s => s.FullMode = true);
            HudMinimal.Checked += (_, _) => Save(s => s.FullMode = false);
            HudSmall.Checked += (_, _) => Save(s => s.Size = 0);
            HudNormal.Checked += (_, _) => Save(s => s.Size = 1);
            HudLarge.Checked += (_, _) => Save(s => s.Size = 2);
            HudHideHero.Checked += (_, _) => Save(s => s.HideHero = true);
            HudHideHero.Unchecked += (_, _) => Save(s => s.HideHero = false);
            HudSolver.IsChecked = settings.ShowSolver;
            HudSolver.Checked += (_, _) => { Save(s => s.ShowSolver = true); EnsureSolver(); };
            HudSolver.Unchecked += (_, _) => { Save(s => s.ShowSolver = false); StopAllSolvers(LocalizationManager.Text("Hud.SolverOff")); };
            Closed += (_, _) => StopAllSolvers(null);
            HudRangeSmall.IsChecked = settings.RangeSize == 0;
            HudRangeNormal.IsChecked = settings.RangeSize == 1;
            HudRangeLarge.IsChecked = settings.RangeSize == 2;
            HudRangeSmall.Checked += (_, _) => Save(s => s.RangeSize = 0);
            HudRangeNormal.Checked += (_, _) => Save(s => s.RangeSize = 1);
            HudRangeLarge.Checked += (_, _) => Save(s => s.RangeSize = 2);
            HudRecap.IsChecked = settings.ShowRecap;
            HudRecap.Checked += (_, _) => Save(s => s.ShowRecap = true);
            HudRecap.Unchecked += (_, _) => Save(s => s.ShowRecap = false);
            HudGrade.IsChecked = settings.ShowGrade;
            HudGrade.Checked += (_, _) => Save(s => s.ShowGrade = true);
            HudGrade.Unchecked += (_, _) => Save(s => s.ShowGrade = false);
            HudMoveToggle.Checked += (_, _) => HudOverlaySettings.SetMoveMode(true);
            HudMoveToggle.Unchecked += (_, _) => HudOverlaySettings.SetMoveMode(false);

            // "Listo" en la mesa apaga el modo: el boton de aqui se pone al dia.
            EventHandler sync = (_, _) => Dispatcher.BeginInvoke(() => HudMoveToggle.IsChecked = HudOverlaySettings.MoveMode);
            HudOverlaySettings.MoveModeChanged += sync;
            Closed += (_, _) =>
            {
                HudOverlaySettings.MoveModeChanged -= sync;
                HudOverlaySettings.SetMoveMode(false);
            };
        }

        // ---------------------------------------------------------------- solver de entrenamiento

        /// <summary>Lectura en vivo de una mesa de dinero ficticio y su HUD.</summary>
        private sealed record SolverTable(TrainerSession Session, PokerTableOverlayWindow Overlay, string TableName);

        /// <summary>Hasta cuantas mesas a la vez se lee en vivo (cada una captura su ventana varias veces por segundo): una 6-max entera de mesas.</summary>
        private const int MaxSolverTables = 6;

        /// <summary>
        /// Pone el solver en CADA mesa enlazada de DINERO FICTICIO (Hold'em sin limite, no torneo), cada
        /// una con su propio lector de cartas: asi aparece en la mesa donde te toca. Usa la lectura del
        /// Modo Entrenador: si una mesa resulta ser de dinero real (titulo o historial), su lectura se
        /// bloquea y el solver desaparece de ella. Nunca hace clic. Se llama tambien en cada vuelta de la
        /// vigilancia: cuando una mesa con solver se cierra, su hueco pasa a otra mesa abierta.
        /// </summary>
        /// <param name="quiet">Sin nada que arrancar, no toca el texto de estado (guarda los avisos).</param>
        private void EnsureSolver(bool quiet = false)
        {
            if (!HudOverlaySettings.Current.ShowSolver)
                return;

            var candidates = _linked
                .Where(item => TrainerSession.IsPlayMoneyTitle(item.Window.Title) &&
                               TrainerSession.IsNoLimitHoldemTitle(item.Window.Title) &&
                               !_solverRefused.Contains(item.Window.TableName) &&
                               _overlays.Contains(item.Overlay) &&
                               TableCapture.IsAlive(item.Window.Handle) &&
                               !_solvers.ContainsKey(item.Window.Handle))
                .ToList();
            if (quiet && (candidates.Count == 0 || _solvers.Count >= MaxSolverTables))
                return;
            if (_solvers.Count == 0 && candidates.Count == 0)
            {
                SolverStatusText.Text = _linked.Count == 0 ? "" : LocalizationManager.Text("Hud.SolverNoPlayMoney");
                return;
            }

            foreach (var candidate in candidates)
            {
                if (_solvers.Count >= MaxSolverTables)
                    break;
                StartSolver(candidate.Window, candidate.Path, candidate.Overlay);
            }
            UpdateSolverStatus();
        }

        private void StartSolver(PokerStarsTableWindow window, string path, PokerTableOverlayWindow overlay)
        {
            try
            {
                if (_trainerCoach is null)
                {
                    var charts = TrainerCoach.LoadDefaultCharts();
                    _trainerCoach = new TrainerCoach(charts);
                    _trainerAdvisor = new TrainerLiveAdvisor(charts);
                    _cardTemplates = CardTemplateStore.Load();
                }

                var reader = new TrainerCardReader(TrainerCalibration.Load(), _cardTemplates!);
                var session = new TrainerSession(window, path, reader, _trainerCoach, _trainerAdvisor!);
                var handle = window.Handle;
                bool Current() => _solvers.TryGetValue(handle, out var item) && ReferenceEquals(item.Session, session);

                session.LiveDecisionChanged += (decision, _) => Dispatcher.InvokeAsync(() =>
                {
                    if (Current())
                        overlay.ShowSolver(decision);
                });
                session.LiveThinking += street => Dispatcher.InvokeAsync(() =>
                {
                    if (Current())
                        overlay.ShowThinking(street);
                });
                session.LiveUnreadable += (where, guess) => Dispatcher.InvokeAsync(() =>
                {
                    if (Current())
                        overlay.ShowUnreadable(where, guess);
                });
                session.HandGraded += report => Dispatcher.InvokeAsync(() =>
                {
                    if (!Current())
                        return;
                    overlay.ShowGrade(report);
                    AddToSession(report);
                });
                session.Blocked += reason => Dispatcher.InvokeAsync(() =>
                {
                    _solverRefused.Add(window.TableName);
                    StopSolver(handle, reason);
                });
                session.Unsupported += (_, reason) => Dispatcher.InvokeAsync(() =>
                {
                    _solverRefused.Add(window.TableName);
                    StopSolver(handle, reason);
                });
                session.Closed += reason => Dispatcher.InvokeAsync(() => StopSolver(handle, reason));

                _solvers[handle] = new SolverTable(session, overlay, window.TableName);
                session.Start();
                if (Current() && !session.LiveDecisionsAvailable)
                    StopSolver(handle, LocalizationManager.Text("Hud.SolverNoOcr"));
            }
            catch (Exception ex)
            {
                AppLog.Warn("Tiempo real: no se pudo iniciar el solver en la mesa", ex);
                SolverStatusText.Text = LocalizationManager.Text("Hud.SolverError");
            }
        }

        private void StopSolver(IntPtr handle, string? reason)
        {
            if (!_solvers.Remove(handle, out var item))
                return;
            item.Overlay.ShowSolver(null);
            try
            {
                item.Session.Stop();
                item.Session.Dispose();
            }
            catch (Exception ex)
            {
                AppLog.Warn("Tiempo real: no se pudo detener el solver", ex);
            }
            UpdateSolverStatus(reason is null ? null : $"{item.TableName}: {reason}");
        }

        private void StopAllSolvers(string? reason)
        {
            foreach (var handle in _solvers.Keys.ToList())
                StopSolver(handle, null);
            if (reason is not null)
                SolverStatusText.Text = reason;
        }

        private void UpdateSolverStatus(string? extra = null)
        {
            var text = _solvers.Count == 0
                ? ""
                : string.Format(LocalizationManager.Text("Hud.SolverOn"), string.Join(", ", _solvers.Values.Select(s => s.TableName)));
            if (!string.IsNullOrWhiteSpace(extra))
                text = (text + " " + extra).Trim();
            SolverStatusText.Text = text;
        }

        // ---------------------------------------------------------------- tu sesion

        private int _sessionHands;
        private double _sessionNetBb;
        private int _sessionDecisions;
        private int _sessionGood;
        private int _sessionWithLive;
        private int _sessionFollowed;

        /// <summary>Manos, ciegas ganadas, decisiones correctas y cuantas veces seguiste al solver (mesas de dinero ficticio).</summary>
        private DateTime? _playStartedAt;
        private DateTime _lastGradedAt;
        private readonly Queue<DateTime> _bigDeviations = new();
        private DateTime _lastPauseNotice = DateTime.MinValue;

        /// <summary>
        /// Aviso de pausa: tras 3 horas seguidas jugando, o 3 botes grandes (30 bb o mas) contra el consejo
        /// en la ultima hora. En la noche del 18-11 la ultima hora de cada sesion larga fue la peor sin la
        /// suerte, y ahi se concentraron los botes grandes jugados contra el consejo. Un aviso cada 30 min.
        /// </summary>
        private void CheckPause(TrainerHandReport report)
        {
            var now = DateTime.Now;
            if (_playStartedAt is null || now - _lastGradedAt > TimeSpan.FromMinutes(30))
            {
                _playStartedAt = now;
                _bigDeviations.Clear();
            }
            _lastGradedAt = now;
            if (Math.Abs(report.NetBb) >= 30 && report.Decisions.Any(d => d.LiveRecommendation is not null && d.FollowedLive == false))
                _bigDeviations.Enqueue(now);
            while (_bigDeviations.Count > 0 && now - _bigDeviations.Peek() > TimeSpan.FromHours(1))
                _bigDeviations.Dequeue();

            var hours = (now - _playStartedAt.Value).TotalHours;
            string? pause = _bigDeviations.Count >= 3
                ? $"En la última hora jugaste {_bigDeviations.Count} botes grandes contra el consejo. Haz una pausa de unos minutos."
                : hours >= 3
                    ? $"Llevas {hours.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture)} horas jugando: al final de las sesiones largas juegas peor. Considera una pausa."
                    : null;
            if (pause is null || now - _lastPauseNotice < TimeSpan.FromMinutes(30))
                return;
            _lastPauseNotice = now;
            foreach (var solver in _solvers.Values)
                solver.Overlay.ShowNotice(pause);
            SolverStatusText.Text = pause;
            AppLog.Info("Aviso de pausa: " + pause);
        }

        // ---------------------------------------------------------------- ventana de rendimiento

        private SessionPerformanceWindow? _performance;
        private bool _performanceDismissed;

        /// <summary>Las mesas enlazadas con su historial y tu usuario (lo que lee la ventana de rendimiento).</summary>
        private IReadOnlyList<(string TableName, string Path, string? Hero)> LinkedHistories() =>
            _linked.Select(item => (item.Window.TableName, item.Path, item.Window.HeroName)).ToList();

        private void BtnPerformance_Click(object sender, RoutedEventArgs e)
        {
            _performanceDismissed = false;
            OpenPerformance(activate: true);
        }

        /// <summary>
        /// Abre (o trae) la ventana de rendimiento. Sin dueno: Tiempo real suele estar minimizada mientras
        /// se juega y una ventana con dueno se esconderia con ella. Se cierra al cerrar Tiempo real.
        /// </summary>
        private void OpenPerformance(bool activate)
        {
            if (_performance is { IsLoaded: true })
            {
                if (_performance.WindowState == WindowState.Minimized)
                    _performance.WindowState = WindowState.Normal;
                if (activate)
                    _performance.Activate();
                _performance.Refresh();
                return;
            }

            _performance = new SessionPerformanceWindow(LinkedHistories);
            _performance.Closed += (_, _) =>
            {
                // Si la cierras tu, no vuelve a abrirse sola en esta sesion (el boton la vuelve a abrir).
                if (IsLoaded)
                    _performanceDismissed = true;
                _performance = null;
            };
            _performance.Show();
            if (activate)
                _performance.Activate();
        }

        private void AddToSession(TrainerHandReport report)
        {
            // La ventana de rendimiento sale sola con la primera mano corregida y se actualiza en cada una.
            if (_performance is not null)
                _performance.Refresh();
            else if (!_performanceDismissed)
                OpenPerformance(activate: false);
            CheckPause(report);
            _sessionHands++;
            _sessionNetBb += report.NetBb;
            _sessionDecisions += report.Decisions.Count;
            _sessionGood += report.Decisions.Count(d => d.Grade == TrainerGrade.Good);
            _sessionWithLive += report.Decisions.Count(d => d.LiveRecommendation is not null);
            _sessionFollowed += report.Decisions.Count(d => d.FollowedLive == true);
            SessionBarText.Text = string.Format(System.Globalization.CultureInfo.CurrentCulture, LocalizationManager.Text("RT.SessionBar"),
                _sessionHands, _sessionNetBb,
                _sessionDecisions == 0 ? 0 : 100.0 * _sessionGood / _sessionDecisions,
                _sessionFollowed, _sessionWithLive);
            SessionBar.Visibility = Visibility.Visible;
        }

        // ---------------------------------------------------------------- espacios de mesa

        /// <summary>Solo se ven los espacios con mesa y uno libre (para enlazar a mano con +).</summary>
        private void UpdateSlotVisibility()
        {
            var slots = GetSlots();
            var shownFree = false;
            foreach (var slot in slots)
            {
                var used = slot.SourcePath is not null;
                var visible = used || !shownFree;
                if (!used && !shownFree)
                    shownFree = true;
                slot.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            }
            RtHeaderText.Text = string.Format(LocalizationManager.Text("RT.HeaderCount"), slots.Count(s => s.SourcePath is not null));
        }

        private void HudResetButton_Click(object sender, RoutedEventArgs e) =>
            HudOverlaySettings.Current.ResetPositions();

        private void UpdateDetectionStatus(IReadOnlyList<PokerStarsTableWindow> windows)
        {
            var linked = GetSlots().Count(slot => slot.SourcePath is not null);
            var waiting = windows.Where(window => !_linkedTables.Contains(window.TableName))
                .Select(window => window.TableName)
                .ToList();

            string text;
            if (windows.Count == 0 && linked == 0)
            {
                text = LocalizationManager.Text("Common.RtNoWindows") + " " + LocalizationManager.Text("RT.WatchNew");
            }
            else if (waiting.Count == 0)
            {
                text = string.Format(LocalizationManager.Text("Common.RtStartedComplete"), linked) + " " + LocalizationManager.Text("RT.WatchNew");
            }
            else if (GetSlots().All(slot => slot.SourcePath is not null))
            {
                text = string.Format(LocalizationManager.Text("RT.NoFreeSlot"), string.Join(", ", waiting.Take(4)));
            }
            else
            {
                var folders = string.Join(" · ", PokerStarsWindowDetector.ResolveHandHistoryRoots());
                text = string.Format(LocalizationManager.Text("RT.WaitingFirstHand"), linked, string.Join(", ", waiting.Take(4)),
                    folders.Length > 0 ? folders : "(sin carpeta)");
            }

            DetectionStatusText.Text = text;
        }

        private void BtnFinishSession_Click(object sender, RoutedEventArgs e)
        {
            var slots = GetSlots();
            var snapshots = slots
                .Select(slot => slot.GetReportSnapshot())
                .Where(table => !string.IsNullOrWhiteSpace(table.SourcePath) || table.Players.Count > 0)
                .ToList();

            if (snapshots.Count == 0)
            {
                MessageBox.Show(
                    LocalizationManager.Text("RT.NoSessionToReport"),
                    LocalizationManager.Text("RT.FinishSession"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var active = snapshots.Where(table => table.IsRunning).ToList();
            if (active.Count > 0)
            {
                var details = string.Join(
                    Environment.NewLine,
                    active.Select(table => string.Format(
                        LocalizationManager.Text("RT.ActiveTableLine"),
                        table.TableName,
                        FormatDate(table.LastHandTime))));
                var message = string.Format(LocalizationManager.Text("RT.FinishWarning"), active.Count, details);
                var result = MessageBox.Show(
                    message,
                    LocalizationManager.Text("RT.FinishSession"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (result != MessageBoxResult.Yes)
                    return;
            }

            try
            {
                // Solo el ultimo tramo de juego seguido de estas mesas (no el historial entero del dia).
                var path = RealTimeSessionReportService.SavePdfForLastStretch(snapshots
                               .Where(table => !string.IsNullOrWhiteSpace(table.SourcePath))
                               .Select(table => (table.TableName, table.SourcePath!, table.HeroName)))
                           ?? RealTimeSessionReportService.SavePdf(snapshots);
                foreach (var slot in slots)
                    slot.StopSession();
                ClearOverlays();

                MessageBox.Show(
                    string.Format(LocalizationManager.Text("RT.ReportSaved"), path),
                    LocalizationManager.Text("RT.FinishSession"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    string.Format(LocalizationManager.Text("RT.ReportError"), ex.Message),
                    LocalizationManager.Text("RT.FinishSession"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private SlotTail[] GetSlots() =>
            new[] { Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8 };

        private void ClearOverlays()
        {
            foreach (var overlay in _overlays.ToList())
            {
                try { overlay.Close(); } catch { }
            }
            _overlays.Clear();
        }

        private void ApplyScale(bool compact)
        {
            // 0.80 en compacto, 1.00 en normal
            if (ScaleProxy != null)
                ScaleProxy.Tag = compact ? 0.80 : 1.00;
        }

        private void FitToWorkArea()
        {
            var workArea = SystemParameters.WorkArea;
            MaxHeight = Math.Max(520, workArea.Height - 24);
            Height = Math.Min(MaxHeight, Math.Max(720, workArea.Height - 48));

            if (Top + Height > workArea.Bottom)
                Top = Math.Max(workArea.Top + 12, workArea.Bottom - Height - 12);
        }

        private void BtnDictionary_Click(object sender, RoutedEventArgs e)
        {
            var window = new RealTimeDictionaryWindow
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowInTaskbar = true
            };

            window.Show();
        }

        private static string FormatDate(DateTime? value) =>
            value.HasValue ? value.Value.ToString("yyyy-MM-dd HH:mm:ss") : "-";
    }
}



