using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Hud.App.Services;
using Hud.App.Services.Trainer;

namespace Hud.App.Views
{
    /// <summary>
    /// Modo Entrenador: en UNA mesa el coach ve en vivo las cartas del alumno con su equity, plan
    /// preflop y la decision en su turno; en todas las mesas que siga ("Corregir todas") ve al
    /// terminar cada mano la correccion de cada decision, con el nombre de la mesa. Solo mesas de
    /// dinero ficticio: cada mesa se bloquea por separado si deja de serlo, y una mesa bloqueada no
    /// vuelve a seguirse en lo que queda de sesion.
    /// </summary>
    public partial class TrainerWindow : Window
    {
        private const int MaxDecisions = 2000;
        private const string AllTables = "Todas las mesas";

        private readonly ObservableCollection<DecisionItem> _decisions = new();
        private readonly ObservableCollection<TableItem> _tables = new();
        private readonly ObservableCollection<string> _plan = new();
        private readonly ICollectionView _decisionsView;
        private readonly AphPreflopProvider _charts = TrainerCoach.LoadDefaultCharts();
        private readonly CardTemplateStore _store = CardTemplateStore.Load();
        private readonly TrainerCoach _coach;
        private readonly TrainerLiveAdvisor _advisor;
        private readonly TrainerCardReader _reader;

        // Mesas cuyo titulo dijo dinero real o torneo: no se vuelven a seguir en esta sesion. Un
        // bloqueo por el historial lo vuelve a comprobar cada sesion nueva antes de leer nada (y asi un
        // historial viejo de otra mesa con el mismo nombre no bloquea la de hoy para siempre).
        private readonly HashSet<string> _blockedTables = new(StringComparer.Ordinal);

        // Mesas que resultaron no ser de Hold'em sin limite 6-max: no se vuelven a seguir.
        private readonly HashSet<string> _unsupportedTables = new(StringComparer.Ordinal);

        // Cada deteccion de mesas lleva un numero: una que termine tarde no pisa a otra mas nueva, ni
        // a un mensaje que se escribio despues de lanzarla.
        private int _detection;
        private int _statusVersion;

        // Manos ya anotadas: al pasar una mesa de correccion a en vivo (o al reves) la nueva sesion
        // puede volver a corregir alguna; no se cuenta dos veces.
        private readonly HashSet<string> _reportedHands = new(StringComparer.Ordinal);
        private TrainerCalibration _calibration = TrainerCalibration.Load();

        // La mesa que se lee en vivo (como mucho una: el lector de cartas es de una sola mesa).
        private TrainerSession? _session;
        private int _handsAnalyzed;
        private double _netBb;
        private int _totalDecisions;
        private int _good;
        private int _marginal;
        private int _mistakes;
        private int _withLive;
        private int _followedLive;
        private string? _tableFilter;
        private bool _onlyMistakes;
        private bool _updatingFilter;

        public TrainerWindow()
        {
            InitializeComponent();
            _coach = new TrainerCoach(_charts);
            _advisor = new TrainerLiveAdvisor(_charts);
            _reader = new TrainerCardReader(_calibration, _store);
            _decisionsView = CollectionViewSource.GetDefaultView(_decisions);
            _decisionsView.Filter = item => item is DecisionItem decision && PassesFilter(decision);
            DecisionsList.ItemsSource = _decisionsView;
            TablesList.ItemsSource = _tables;
            PlanList.ItemsSource = _plan;
            RefreshFilterOptions();

            Loaded += async (_, _) =>
            {
                UpdateReaderText();
                // Las tablas de manos se cargan una vez: mejor antes de la primera mano.
                _ = Task.Run(() => PreflopRanking.All.Count);
                await DetectTablesAsync(updateStatus: true);
            };
            Closed += (_, _) => StopAll();
        }

        // ------------------------------------------------------------------ mesas

        private async void BtnDetect_Click(object sender, RoutedEventArgs e) => await DetectTablesAsync(updateStatus: true);

        /// <summary>
        /// Detecta las mesas abiertas. Buscar el historial de cada una recorre las carpetas de
        /// historiales, asi que se hace fuera del hilo de la ventana.
        /// </summary>
        private async Task<List<TableOption>> DetectTablesAsync(bool updateStatus)
        {
            var detection = ++_detection;
            var statusVersion = _statusVersion;
            var windows = PokerStarsWindowDetector.DetectOpenTables();
            var options = await Task.Run(() => windows
                .Select(window => new TableOption(window, TrainerSession.FindHandHistory(window)))
                .OrderByDescending(option => option.Trainable)
                .ThenBy(option => option.Window.TableName, StringComparer.OrdinalIgnoreCase)
                .ToList());

            // De vuelta al hilo de la ventana aunque quien llamo no tuviera su contexto. Si mientras
            // tanto se lanzo otra deteccion, esta ya no se muestra.
            await Dispatcher.InvokeAsync(() =>
            {
                if (detection == _detection)
                    ShowTables(options, windows, updateStatus && statusVersion == _statusVersion);
            });
            return options;
        }

        private void ShowTables(List<TableOption> options, IReadOnlyList<PokerStarsTableWindow> windows, bool updateStatus)
        {
            var previous = (TablesCombo.SelectedItem as TableOption)?.Window.Handle;
            TablesCombo.ItemsSource = options;
            TablesCombo.SelectedItem = options.FirstOrDefault(option => option.Window.Handle == previous)
                                       ?? options.FirstOrDefault(option => option.Trainable && !IsBlocked(option.Window) && !IsUnsupported(option.Window))
                                       ?? options.FirstOrDefault();

            if (updateStatus)
            {
                var trainable = options.Count(option => option.Trainable && !IsDuplicate(option.Window, windows));
                StatusText.Text = options.Count == 0
                    ? "No hay mesas de PokerStars abiertas."
                    : trainable > 0
                        ? $"{options.Count} mesa(s) detectada(s), {trainable} de dinero ficticio. \"Iniciar en vivo\" lee la mesa elegida; \"Corregir todas\" sigue las {trainable}."
                        : "No hay mesas de dinero ficticio de Hold'em sin limite abiertas: el modo Entrenador no se puede usar en las demas.";
            }
        }

        /// <summary>
        /// Ventana fantasma "Mesa X" junto a la mesa real "X" (PokerStars la abre a veces): su
        /// historial no existe y duplicaria la mesa.
        /// </summary>
        private static bool IsDuplicate(PokerStarsTableWindow window, IReadOnlyList<PokerStarsTableWindow> windows)
        {
            var name = window.TableName;
            if (!name.StartsWith("Mesa ", StringComparison.OrdinalIgnoreCase) &&
                !name.StartsWith("Table ", StringComparison.OrdinalIgnoreCase))
                return false;

            var key = TrainerHandHistoryLocator.Key(name[(name.IndexOf(' ') + 1)..]);
            return windows.Any(other => other.Handle != window.Handle && TrainerHandHistoryLocator.Key(other.TableName) == key);
        }

        /// <summary>Ventana fantasma "Mesa X" de una mesa "X" que ya esta en la lista (aunque su ventana ya no este).</summary>
        private bool IsPhantomOfFollowedTable(PokerStarsTableWindow window)
        {
            var name = window.TableName;
            if (!name.StartsWith("Mesa ", StringComparison.OrdinalIgnoreCase) &&
                !name.StartsWith("Table ", StringComparison.OrdinalIgnoreCase))
                return false;

            var key = TableKey(name[(name.IndexOf(' ') + 1)..]);
            return _tables.Any(item => TableKey(item.Name) == key);
        }

        private static string TableKey(string tableName) => TrainerHandHistoryLocator.Key(tableName);

        private bool IsBlocked(PokerStarsTableWindow window) => _blockedTables.Contains(TableKey(window.TableName));

        private bool IsUnsupported(PokerStarsTableWindow window) => _unsupportedTables.Contains(TableKey(window.TableName));

        /// <summary>Mensaje de estado de una accion del coach: las detecciones lanzadas antes no lo pisan.</summary>
        private void SetStatus(string text)
        {
            _statusVersion++;
            StatusText.Text = text;
        }

        /// <summary>
        /// La mesa elegida sigue abierta, con el mismo nombre y con un titulo de dinero ficticio de
        /// Hold'em sin limite (puede haber cambiado desde que se detecto).
        /// </summary>
        private static bool StillValid(PokerStarsTableWindow window)
        {
            if (!TableCapture.IsAlive(window.Handle))
                return false;

            var title = TableCapture.GetWindowTitle(window.Handle);
            var marker = title.IndexOf(" - ", StringComparison.Ordinal);
            return marker > 0 &&
                   TableKey(title[..marker]) == TableKey(window.TableName) &&
                   TrainerSession.IsPlayMoneyTitle(title) &&
                   TrainerSession.IsNoLimitHoldemTitle(title);
        }

        // ------------------------------------------------------------------ sesiones

        private void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (TablesCombo.SelectedItem is not TableOption option)
            {
                StatusText.Text = "Elige una mesa primero (Detectar mesas).";
                return;
            }

            if (!option.IsPlayMoney)
            {
                StatusText.Text = "Esa mesa es de dinero real: el modo Entrenador esta bloqueado.";
                return;
            }

            if (!option.IsNoLimitHoldem)
            {
                StatusText.Text = "Esa mesa no es de Hold'em sin limite: el entrenador es para NLHE 6-max.";
                return;
            }

            if (IsBlocked(option.Window))
            {
                StatusText.Text = $"{option.Window.TableName} se bloqueo en esta sesion: no se vuelve a leer.";
                return;
            }

            if (IsUnsupported(option.Window))
            {
                StatusText.Text = $"{option.Window.TableName} no es de Hold'em sin limite 6-max: el entrenador no la lee.";
                return;
            }

            if (_session is not null && _session.Handle == option.Window.Handle)
            {
                StatusText.Text = $"{option.Window.TableName} ya se esta leyendo en vivo.";
                return;
            }

            if (!StillValid(option.Window))
            {
                StatusText.Text = "La mesa ha cambiado o se ha cerrado desde que se detecto: pulsa Detectar mesas.";
                return;
            }

            // La mesa que estaba en vivo pasa a solo correccion: se sigue corrigiendo sin perder manos.
            var previousLive = _tables.FirstOrDefault(item => !item.Stopped && ReferenceEquals(item.Session, _session));
            if (previousLive is not null)
            {
                var downgraded = new TrainerSession(previousLive.Window, null, null, _coach, _advisor);
                Replace(previousLive, downgraded, previousLive.Window, live: false);
                downgraded.Start();
            }

            // Si la mesa elegida ya se corregia, la sesion en vivo sigue donde iba aquella.
            var session = new TrainerSession(option.Window, option.HandHistoryPath, _reader, _coach, _advisor);
            Replace(FindItem(option.Window), session, option.Window, live: true);
            session.LiveChanged += (reading, advice) => Dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(_session, session))
                    ShowLive(reading, advice);
            });
            session.CalibrationChanged += calibration => Dispatcher.InvokeAsync(() =>
            {
                // Una autocalibracion tardia de la mesa que dejo de leerse no vale para la nueva.
                if (!ReferenceEquals(_session, session))
                    return;
                _calibration = calibration;
                UpdateReaderText();
            });
            session.LiveDecisionChanged += (decision, _) => Dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(_session, session))
                    ShowDecision(decision);
            });
            ClearLivePanel();
            _session = session;
            LiveTitle.Text = $"EN VIVO: {option.Window.TableName}";

            SetStatus(_calibration.HasHero
                ? $"Leyendo {option.Window.TableName}..."
                : $"Leyendo {option.Window.TableName}. El lector se calibra solo en cuanto se vean el flop y tus dos cartas.");
            if (!session.LiveDecisionsAvailable)
                StatusText.Text += " (Este Windows no tiene el OCR integrado: no habra decision en vivo, solo la correccion al terminar cada mano.)";
            session.Start();
            UpdateButtons();
        }

        /// <summary>
        /// Sigue el historial de cada mesa de dinero ficticio de Hold'em sin limite abierta y corrige
        /// sus decisiones al terminar cada mano. Sin captura de pantalla ni OCR: no carga el PC ni toca
        /// PokerStars. Las mesas que ya se siguen no se duplican y las bloqueadas no se retoman.
        /// </summary>
        private void BtnGradeAll_Click(object sender, RoutedEventArgs e)
        {
            // Cada sesion busca su historial en segundo plano: aqui solo hacen falta las ventanas.
            var windows = PokerStarsWindowDetector.DetectOpenTables();
            var candidates = windows
                .Where(window => TrainerSession.IsPlayMoneyTitle(window.Title) &&
                                 TrainerSession.IsNoLimitHoldemTitle(window.Title) &&
                                 !IsDuplicate(window, windows) &&
                                 !IsPhantomOfFollowedTable(window))
                .ToList();
            var blocked = candidates.Where(IsBlocked).Select(window => window.TableName).ToList();
            var unsupported = candidates.Where(IsUnsupported).Select(window => window.TableName).ToList();

            var added = 0;
            foreach (var window in candidates.Where(window => !IsBlocked(window) && !IsUnsupported(window)))
            {
                var existing = FindItem(window);
                if (existing is { Stopped: false })
                    continue;

                var session = new TrainerSession(window, null, null, _coach, _advisor);
                Replace(existing, session, window, live: false);
                session.Start();
                added++;
            }

            var following = _tables.Count(item => !item.Stopped);
            SetStatus((candidates.Count == 0
                    ? "No hay mesas de dinero ficticio de Hold'em sin limite abiertas para corregir."
                    : added == 0
                        ? $"Ya se siguen todas las mesas abiertas ({following})."
                        : $"Corrigiendo {following} mesa(s): cada decision se corrige al terminar la mano. Las que no tienen historial empiezan en cuanto PokerStars lo cree (al acabar su primera mano).") +
                (blocked.Count > 0 ? $" No se retoman las bloqueadas: {string.Join(", ", blocked)}." : "") +
                (unsupported.Count > 0 ? $" No son NLHE 6-max: {string.Join(", ", unsupported)}." : ""));
            UpdateButtons();
            _ = DetectTablesAsync(updateStatus: false);
        }

        /// <summary>
        /// La fila de esa mesa: misma mesa (por nombre), preferiblemente en la misma ventana. Una ventana
        /// que ahora muestra otra mesa no hereda la fila de la anterior.
        /// </summary>
        private TableItem? FindItem(PokerStarsTableWindow window)
        {
            var key = TableKey(window.TableName);
            return _tables.FirstOrDefault(item => item.Session.Handle == window.Handle && TableKey(item.Name) == key) ??
                   _tables.FirstOrDefault(item => TableKey(item.Name) == key);
        }

        /// <summary>
        /// Pone una sesion nueva en la fila de su mesa. Si la fila ya existia (otra sesion de la misma
        /// mesa), la sesion nueva sigue donde iba la anterior y la fila conserva su marcador.
        /// </summary>
        private void Replace(TableItem? previous, TrainerSession session, PokerStarsTableWindow window, bool live)
        {
            var item = new TableItem(window, session, live);
            if (previous is not null)
            {
                // Solo se continua una sesion que sigue activa (relevo sin huecos). Una mesa que se
                // vuelve a seguir despues de detenerla empieza de cero, como antes; conserva su marcador.
                if (!previous.Stopped)
                    session.ContinueFrom(previous.Session);
                item.CopyScoreFrom(previous);
                if (!previous.Stopped)
                {
                    previous.Stop("SUSTITUIDA", "Sustituida por otra sesion de la misma mesa.", blocked: false);
                    previous.Session.Stop();
                    previous.Session.Dispose();
                    if (ReferenceEquals(_session, previous.Session))
                    {
                        _session = null;
                        ClearLivePanel();
                    }
                }

                var index = _tables.IndexOf(previous);
                _tables[index] = item;
            }
            else if (live)
            {
                _tables.Insert(0, item);
            }
            else
            {
                _tables.Add(item);
            }

            session.HandGraded += report => Dispatcher.InvokeAsync(() => AddReport(report, item));
            session.StatusChanged += text => Dispatcher.InvokeAsync(() =>
            {
                if (item.Stopped)
                    return;
                item.Status = text;
                if (ReferenceEquals(_session, session))
                    StatusText.Text = text;
            });
            session.Blocked += reason => Dispatcher.InvokeAsync(() => OnBlocked(item, reason));
            session.Closed += reason => Dispatcher.InvokeAsync(() => OnStopped(item, "CERRADA", reason));
            session.Unsupported += (label, reason) => Dispatcher.InvokeAsync(() =>
            {
                _unsupportedTables.Add(TableKey(item.Name));
                OnStopped(item, label, reason);
            });

            TablesPanel.Visibility = Visibility.Visible;
            RefreshFilterOptions();
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            StopAll();
            StatusText.Text = "Sesion detenida.";
        }

        private void StopAll()
        {
            foreach (var item in _tables.Where(item => !item.Stopped).ToList())
                Detach(item, "DETENIDA", "Detenida por el coach.", blocked: false);
        }

        /// <summary>
        /// Una mesa bloqueada (dinero real, torneo o sin confirmar) se para sola, las demas siguen, y
        /// no se vuelve a seguir en esta sesion. Sin ventanas modales: un aviso que robe el foco en
        /// mitad de una mano de otra mesa es peor.
        /// </summary>
        private void OnBlocked(TableItem item, string reason)
        {
            if (item.Session.BlockedByTitle)
                _blockedTables.Add(TableKey(item.Name));
            if (item.Stopped)
                return;

            Detach(item, "BLOQUEADA", reason, blocked: true);
            SetStatus($"{item.Name} BLOQUEADA: {reason}");
        }

        private void OnStopped(TableItem item, string label, string reason)
        {
            if (item.Stopped)
                return;

            Detach(item, label, reason, blocked: false);
            SetStatus($"{item.Name}: {reason}");
        }

        private void Detach(TableItem item, string label, string reason, bool blocked)
        {
            item.Stop(label, reason, blocked);
            item.Session.Stop();
            item.Session.Dispose();
            if (ReferenceEquals(_session, item.Session))
            {
                _session = null;
                ClearLivePanel();
            }

            UpdateButtons();
        }

        private void UpdateButtons() => BtnStop.IsEnabled = _tables.Any(item => !item.Stopped);

        // ------------------------------------------------------------------ decision en vivo

        private static readonly Brush FoldTone = FrozenBrush(Color.FromRgb(226, 78, 91));
        private static readonly Brush CheckTone = FrozenBrush(Color.FromRgb(96, 110, 128));
        private static readonly Brush CallTone = FrozenBrush(Color.FromRgb(33, 160, 104));
        private static readonly Brush RaiseTone = FrozenBrush(Color.FromRgb(45, 108, 223));

        /// <summary>Cartel de la decision en vivo: visible solo en el turno del alumno.</summary>
        private void ShowDecision(LiveDecision? decision)
        {
            if (decision is null)
            {
                DecisionBanner.Visibility = Visibility.Collapsed;
                DecisionDetailsList.Visibility = Visibility.Collapsed;
                return;
            }

            DecisionBanner.Background = decision.Tone switch
            {
                "fold" => FoldTone,
                "check" => CheckTone,
                "call" => CallTone,
                _ => RaiseTone
            };
            DecisionAction.Text = decision.Action;
            DecisionReason.Text = decision.Reason;
            DecisionDetailsList.ItemsSource = decision.Details;
            DecisionBanner.Visibility = Visibility.Visible;
            DecisionDetailsList.Visibility = Visibility.Visible;
        }

        private static Brush FrozenBrush(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        // ------------------------------------------------------------------ calibracion

        private void BtnCalibrate_Click(object sender, RoutedEventArgs e)
        {
            if (TablesCombo.SelectedItem is not TableOption option)
            {
                StatusText.Text = "Abre una mesa y pulsa Detectar mesas antes de calibrar.";
                return;
            }

            if (!option.IsPlayMoney || IsBlocked(option.Window) || !StillValid(option.Window))
            {
                StatusText.Text = "La calibracion se hace sobre una mesa de dinero ficticio abierta (y no bloqueada): pulsa Detectar mesas.";
                return;
            }

            var window = new TrainerCalibrationWindow(option.Window.Handle, _calibration, _reader) { Owner = this };
            if (window.ShowDialog() == true && window.Result is not null)
            {
                _calibration = window.Result;
                _reader.Calibration = _calibration;
                try
                {
                    _calibration.Save();
                    StatusText.Text = "Calibracion guardada.";
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    StatusText.Text = $"Calibracion aplicada, pero no se pudo guardar: {ex.Message}";
                }
            }

            UpdateReaderText();
        }

        private void UpdateReaderText()
        {
            var calibration = _calibration.IsComplete ? "completa" : _calibration.HasHero ? "solo cartas del heroe" : "automatica al iniciar";
            ReaderText.Text =
                $"Lector: {_store.LearnedCards}/52 cartas aprendidas ({_store.LearnedRanks}/13 rangos, {_store.LearnedSuits}/4 palos). " +
                $"Calibracion: {calibration}. Aprende solo al terminar cada mano; con rangos y palos conocidos ya lee cartas nuevas.";
        }

        // ------------------------------------------------------------------ en vivo

        private void ShowLive(LiveReading reading, LiveAdvice advice)
        {
            HeroCardsView.Content = CardChipViewModel.FromCards(SlotsText(reading, 0, 2));
            BoardCardsView.Content = CardChipViewModel.FromCards(SlotsText(reading, 2, 5));
            HandText.Text = advice.HandText;

            EquityText.Text = advice.EquityVsRange is { } vsRange
                ? $"Equity: {vsRange:0}% vs rango tipico  |  {advice.EquityVsAny:0}% vs cualquier mano"
                : advice.EquityVsAny is { } vsAny
                    ? $"Equity preflop vs mano aleatoria: {vsAny:0}%"
                    : "";
            PotOddsText.Text = advice.PotOddsNote;
            OutsText.Text = advice.OutsNote;

            _plan.Clear();
            foreach (var line in advice.Plan)
                _plan.Add(line);

            PositionText.Text = string.IsNullOrEmpty(advice.Position) || advice.Position == "?"
                ? "Posicion: ?"
                : $"Posicion: {advice.Position} (estimada)";
        }

        private void ClearLivePanel()
        {
            ShowDecision(null);
            HeroCardsView.Content = null;
            BoardCardsView.Content = null;
            HandText.Text = "Esperando...";
            EquityText.Text = "";
            PotOddsText.Text = "";
            OutsText.Text = "";
            _plan.Clear();
            PositionText.Text = "Posicion: ?";
            OpponentsList.ItemsSource = null;
            LiveTitle.Text = "EN VIVO";
        }

        private static string SlotsText(LiveReading reading, int start, int count) =>
            string.Join(" ", reading.Slots.Skip(start).Take(count).Where(slot => slot is not null));

        // ------------------------------------------------------------------ decisiones

        private void AddReport(TrainerHandReport report, TableItem table)
        {
            // La misma mano puede llegar dos veces si una mesa paso de correccion a en vivo: una vez.
            if (!string.IsNullOrEmpty(report.HandId) && !_reportedHands.Add(report.HandId))
                return;

            _handsAnalyzed++;
            _netBb += report.NetBb;
            // Una mano que llega de una sesion ya sustituida cuenta en la fila actual de su mesa.
            var row = table.Stopped
                ? _tables.Where(item => TableKey(item.Name) == TableKey(table.Name)).OrderBy(item => item.Stopped).FirstOrDefault() ?? table
                : table;
            row.Count(report);

            // Contadores de toda la sesion: la lista solo guarda las ultimas MaxDecisions.
            foreach (var decision in report.Decisions)
            {
                _totalDecisions++;
                if (decision.Grade == TrainerGrade.Good) _good++;
                else if (decision.Grade == TrainerGrade.Marginal) _marginal++;
                else _mistakes++;
                if (decision.LiveRecommendation is not null)
                {
                    _withLive++;
                    if (decision.FollowedLive == true)
                        _followedLive++;
                }
            }

            foreach (var decision in report.Decisions.Reverse())
                _decisions.Insert(0, new DecisionItem(decision, report));
            while (_decisions.Count > MaxDecisions)
                _decisions.RemoveAt(_decisions.Count - 1);

            UpdateScore();
            if (table.Live)
            {
                UpdateReaderText();
                OpponentsList.ItemsSource = _session?.Opponents;
            }
        }

        private void UpdateScore()
        {
            var tables = _tables.Count;
            if (_totalDecisions == 0)
            {
                ScoreText.Text = _handsAnalyzed == 0 ? "Aun no hay manos analizadas." : $"{_handsAnalyzed} manos sin decisiones del alumno.";
                return;
            }

            ScoreText.Text = string.Format(
                CultureInfo.InvariantCulture,
                "{0} manos{1} | {2} bien, {3} dudosas, {4} errores | precision {5:0}% | neto {6:+0.#;-0.#;0} bb",
                _handsAnalyzed, tables > 1 ? $" en {tables} mesas" : "", _good, _marginal, _mistakes, _good * 100.0 / _totalDecisions, _netBb) +
                (_withLive > 0 ? $" | siguio la decision en vivo {_followedLive}/{_withLive}" : "");
        }

        private void RefreshFilterOptions()
        {
            var names = _tables.Select(item => item.Name)
                .Concat(_decisions.Select(item => item.TableName))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var selected = _tableFilter is null
                ? null
                : names.FirstOrDefault(name => string.Equals(name, _tableFilter, StringComparison.OrdinalIgnoreCase));

            _updatingFilter = true;
            FilterCombo.ItemsSource = new[] { AllTables }.Concat(names).ToList();
            FilterCombo.SelectedItem = selected ?? AllTables;
            _updatingFilter = false;

            // La mesa filtrada ya no esta: se vuelve a ver todo (el filtro no puede quedarse escondido).
            if (_tableFilter is not null && selected is null)
            {
                _tableFilter = null;
                _decisionsView.Refresh();
            }
        }

        private void FilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingFilter)
                return;

            _tableFilter = FilterCombo.SelectedItem is string name && name != AllTables ? name : null;
            _decisionsView.Refresh();
        }

        private void OnlyMistakesCheck_Changed(object sender, RoutedEventArgs e)
        {
            _onlyMistakes = OnlyMistakesCheck.IsChecked == true;
            _decisionsView.Refresh();
        }

        private bool PassesFilter(DecisionItem item) =>
            (_tableFilter is null || string.Equals(item.TableName, _tableFilter, StringComparison.OrdinalIgnoreCase)) &&
            (!_onlyMistakes || item.Decision.Grade != TrainerGrade.Good);

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            _decisions.Clear();
            _handsAnalyzed = 0;
            _netBb = 0;
            _totalDecisions = _good = _marginal = _mistakes = _withLive = _followedLive = 0;
            // Las bloqueadas se quedan a la vista: es un aviso, no un resultado.
            foreach (var stopped in _tables.Where(item => item.Stopped && !item.Blocked).ToList())
                _tables.Remove(stopped);
            foreach (var item in _tables)
                item.ResetScore();
            TablesPanel.Visibility = _tables.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            RefreshFilterOptions();
            UpdateScore();
        }

        private void BtnCopy_Click(object sender, RoutedEventArgs e)
        {
            var visible = _decisionsView.Cast<DecisionItem>().ToList();
            if (visible.Count == 0)
            {
                StatusText.Text = "No hay decisiones para copiar.";
                return;
            }

            var filtered = _tableFilter is not null || _onlyMistakes;
            var text = new StringBuilder();
            text.AppendLine($"APH - Resumen del modo Entrenador ({DateTime.Now:yyyy-MM-dd HH:mm})");
            text.AppendLine(ScoreText.Text);
            if (_tables.Count > 1)
            {
                foreach (var table in _tables)
                    text.AppendLine($"  {table.Name}: {table.ScoreLine}");
            }

            if (filtered)
                text.AppendLine($"(filtro: {_tableFilter ?? AllTables}{(_onlyMistakes ? ", solo errores y dudosas" : "")}: se listan {visible.Count} de las {_decisions.Count} decisiones guardadas)");
            if (_totalDecisions > _decisions.Count)
                text.AppendLine($"(se guardan las ultimas {_decisions.Count} de {_totalDecisions} decisiones)");
            text.AppendLine();
            foreach (var item in Enumerable.Reverse(visible))
            {
                var decision = item.Decision;
                text.AppendLine($"[{decision.GradeLabel}] {item.HandLine} | {decision.Headline}");
                text.AppendLine($"  {item.CardsLine}");
                text.AppendLine($"  {decision.ChoiceLine}");
                if (!string.IsNullOrEmpty(decision.LiveLine))
                    text.AppendLine($"  {decision.LiveLine}");
                text.AppendLine($"  {decision.Explanation}");
                text.AppendLine();
            }

            try
            {
                Clipboard.SetText(text.ToString());
                StatusText.Text = "Resumen copiado al portapapeles.";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"No se pudo copiar: {ex.Message}";
            }
        }

        // ------------------------------------------------------------------ modelos de la vista

        public sealed class TableOption
        {
            public TableOption(PokerStarsTableWindow window, string? handHistoryPath)
            {
                Window = window;
                HandHistoryPath = handHistoryPath;
            }

            public PokerStarsTableWindow Window { get; }
            public string? HandHistoryPath { get; }
            public bool IsPlayMoney => TrainerSession.IsPlayMoneyTitle(Window.Title);
            public bool IsNoLimitHoldem => TrainerSession.IsNoLimitHoldemTitle(Window.Title);
            public bool Trainable => IsPlayMoney && IsNoLimitHoldem;

            public string Label =>
                $"{Window.TableName}  |  {(!IsPlayMoney ? "DINERO REAL - bloqueada" : !IsNoLimitHoldem ? "no es Hold'em sin limite" : "Dinero ficticio")}" +
                (HandHistoryPath is null ? "  (sin historial todavia)" : "");
        }

        /// <summary>Una mesa seguida: su sesion, su estado y su marcador.</summary>
        public sealed class TableItem : INotifyPropertyChanged
        {
            private static readonly Brush LiveBrush = Frozen(Color.FromRgb(45, 108, 223));
            private static readonly Brush GradingBrush = Frozen(Color.FromRgb(33, 192, 122));
            private static readonly Brush BlockedBrush = Frozen(Color.FromRgb(226, 78, 91));
            private static readonly Brush StoppedBrush = Frozen(Color.FromRgb(96, 110, 128));

            private string _status;
            private string _stateLabel;
            private Brush _stateBrush;
            private int _hands;
            private double _netBb;
            private int _good;
            private int _marginal;
            private int _mistakes;

            public TableItem(PokerStarsTableWindow window, TrainerSession session, bool live)
            {
                Window = window;
                Session = session;
                Live = live;
                _stateLabel = live ? "EN VIVO" : "CORRIGIENDO";
                _stateBrush = live ? LiveBrush : GradingBrush;
                _status = live ? "Leyendo la mesa..." : "Siguiendo el historial...";
            }

            public event PropertyChangedEventHandler? PropertyChanged;

            public PokerStarsTableWindow Window { get; }
            public TrainerSession Session { get; }
            public bool Live { get; }
            public bool Stopped { get; private set; }
            public bool Blocked { get; private set; }
            public string Name => Window.TableName;
            public string StateLabel => _stateLabel;
            public Brush StateBrush => _stateBrush;

            public string Status
            {
                get => _status;
                set
                {
                    _status = value;
                    Notify(nameof(Status));
                }
            }

            public string ScoreLine
            {
                get
                {
                    var decisions = _good + _marginal + _mistakes;
                    if (_hands == 0)
                        return "Sin manos corregidas todavia";
                    return string.Format(CultureInfo.InvariantCulture,
                        "{0} manos | {1} bien, {2} dudosas, {3} errores{4} | neto {5:+0.#;-0.#;0} bb",
                        _hands, _good, _marginal, _mistakes,
                        decisions > 0 ? $" | precision {_good * 100.0 / decisions:0}%" : "", _netBb);
                }
            }

            public void Count(TrainerHandReport report)
            {
                _hands++;
                _netBb += report.NetBb;
                foreach (var decision in report.Decisions)
                {
                    if (decision.Grade == TrainerGrade.Good) _good++;
                    else if (decision.Grade == TrainerGrade.Marginal) _marginal++;
                    else _mistakes++;
                }

                Notify(nameof(ScoreLine));
            }

            /// <summary>Al sustituir la sesion de una mesa, la fila nueva conserva su marcador.</summary>
            public void CopyScoreFrom(TableItem previous)
            {
                _hands = previous._hands;
                _netBb = previous._netBb;
                _good = previous._good;
                _marginal = previous._marginal;
                _mistakes = previous._mistakes;
                Notify(nameof(ScoreLine));
            }

            public void ResetScore()
            {
                _hands = _good = _marginal = _mistakes = 0;
                _netBb = 0;
                Notify(nameof(ScoreLine));
            }

            public void Stop(string label, string reason, bool blocked)
            {
                Stopped = true;
                Blocked = blocked;
                _stateLabel = label;
                _stateBrush = blocked ? BlockedBrush : StoppedBrush;
                _status = reason;
                Notify(nameof(StateLabel));
                Notify(nameof(StateBrush));
                Notify(nameof(Status));
            }

            private void Notify(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

            private static Brush Frozen(Color color)
            {
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                return brush;
            }
        }

        public sealed class DecisionItem
        {
            private static readonly Brush GoodBrush = Frozen(Color.FromRgb(33, 192, 122));
            private static readonly Brush MarginalBrush = Frozen(Color.FromRgb(227, 179, 65));
            private static readonly Brush MistakeBrush = Frozen(Color.FromRgb(226, 78, 91));

            public DecisionItem(TrainerDecision decision, TrainerHandReport report)
            {
                Decision = decision;
                TableName = report.TableName;
                HandLine = (string.IsNullOrWhiteSpace(report.TableName) ? "" : report.TableName + " | ") +
                           string.Format(CultureInfo.InvariantCulture, "Mano #{0} | {1:+0.#;-0.#;0} bb", report.HandId, report.NetBb);
            }

            public TrainerDecision Decision { get; }
            public string TableName { get; }
            public string HandLine { get; }

            public string CardsLine => string.IsNullOrWhiteSpace(Decision.Board)
                ? $"Heroe: {Decision.HeroCards}"
                : $"Heroe: {Decision.HeroCards}    Board: {Decision.Board}";

            public Visibility LiveVisibility => Decision.LiveRecommendation is null ? Visibility.Collapsed : Visibility.Visible;

            public Brush LiveBrush => Decision.FollowedLive == true ? GoodBrush : MarginalBrush;

            public Brush GradeBrush => Decision.Grade switch
            {
                TrainerGrade.Good => GoodBrush,
                TrainerGrade.Marginal => MarginalBrush,
                _ => MistakeBrush
            };

            private static Brush Frozen(Color color)
            {
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                return brush;
            }
        }
    }
}
