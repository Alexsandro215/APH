using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HandReader.Core.Models;
using HandReader.Core.Parsing;
using HandReader.Core.Stats;

namespace Hud.App.Services.Trainer
{
    /// <summary>
    /// Sesion de entrenamiento sobre una mesa: lee las cartas en pantalla en vivo, sigue el
    /// historial de la mesa y califica cada mano al terminar. Sin lector de cartas es "solo
    /// correccion": no captura la pantalla ni hace OCR, solo sigue el historial (asi se corrigen
    /// varias mesas a la vez). Solo funciona con dinero ficticio: si la ventana o el historial
    /// indican dinero real o torneo, la sesion se bloquea.
    /// </summary>
    public sealed class TrainerSession : IDisposable
    {
        private const int CaptureIntervalMs = 350;
        private const int HistoryIntervalMs = 300;
        private const int RecalibrationCheckEvery = 15;

        private readonly PokerStarsTableWindow _window;
        // Null = solo correccion (sin capturas ni OCR).
        private readonly TrainerCardReader? _reader;
        private readonly TrainerCoach _coach;
        private readonly TrainerLiveAdvisor _advisor;
        private readonly StatsAggregator _stats = new();
        private readonly PokerStarsParser _statsParser;
        private readonly CancellationTokenSource _cts = new();
        private readonly object _sync = new();
        private string? _handHistoryPath;
        private string? _heroName;
        private TrainerHand? _lastHand;

        /// <summary>Pila del heroe (bb) al terminar la ultima mano del historial, para descartar lecturas imposibles.</summary>
        private double? _heroStackAfterHistory;
        private string? _predictedPosition;
        private CapturedFrame? _lastFrame;
        private string _lastSignature = "";
        private string? _lastPosition;
        private DateTime _lastHandArrival;
        private DateTime _sessionStart;

        // Historial: archivo enlazado, bytes que eran historico al enlazarlo y relectura periodica.
        private AppendedTextReader? _tail;
        private long _historyEnd;
        private bool _historyAnnounced;
        private bool _staleHistory;
        private readonly List<string> _pending = new();
        private DateTime _nextResolve = DateTime.MinValue;
        private int _dealsWithoutHistory;
        private string _lastDealKey = "";
        private int _capturesSinceCheck;
        private int _unreadableStreak;
        private TrainerCalibration? _pendingLayout;
        private DateTime _pendingLayoutSince;
        private volatile string? _lastHeroSlots;
        private volatile string? _frozenHeroSlots;
        private volatile bool _blocked;
        private DateTime? _titleGoneSince;

        // Antes de leer la pantalla se revisa el historial que ya existe (dinero de cada mano): hasta
        // el final que tenia al enlazarlo.
        private volatile bool _historyChecked;
        private bool _historyCheckAnnounced;
        private long _gateEnd;

        // Manos ya corregidas por esta sesion o por las que la precedieron en esta mesa: no se
        // vuelven a corregir (ni a anotar con lo recomendado en vivo).
        private readonly HashSet<string> _processedHands = new(StringComparer.Ordinal);

        // Continuacion de otra sesion de la misma mesa (cambio de en vivo a correccion o al reves):
        // mismo corte entre historico y en vivo, para no perder ni repetir manos.
        private string? _continuePath;
        private long _continueCut;
        private bool _continueStale;
        private DateTime? _continueEpoch;

        // Decision en vivo (OCR de los botones, el bote y las placas en el turno del heroe).
        private readonly LiveTableReader? _tableReader;
        private readonly TrainerLiveDecisionAdvisor _decisionAdvisor;
        private readonly List<LiveDecision> _shownDecisions = new();
        private LiveHandContext _liveHand = new();
        private string _lastTurnKey = "";
        private bool _turnRecorded;
        private DateTime _lastPlatesRead = DateTime.MinValue;

        /// <param name="reader">Lector de cartas para la lectura y la decision en vivo; null = solo correccion.</param>
        public TrainerSession(
            PokerStarsTableWindow window,
            string? handHistoryPath,
            TrainerCardReader? reader,
            TrainerCoach coach,
            TrainerLiveAdvisor advisor)
        {
            _window = window;
            _handHistoryPath = handHistoryPath;
            _heroName = window.HeroName;
            _reader = reader;
            if (reader is not null)
                _tableReader = new LiveTableReader(new TableTextReader());
            _coach = coach;
            _advisor = advisor;
            _statsParser = new PokerStarsParser(_stats);
            _decisionAdvisor = new TrainerLiveDecisionAdvisor(coach.Charts);
            _decisionAdvisor.Thinking = street => LiveThinking?.Invoke(street);
        }

        /// <summary>Se esta calculando el equilibrio GTO de esta calle (tarda unos segundos en el turn).</summary>
        public event Action<string>? LiveThinking;

        /// <summary>Lectura y consejo en vivo (se lanza desde un hilo de fondo).</summary>
        public event Action<LiveReading, LiveAdvice>? LiveChanged;

        /// <summary>
        /// Decision recomendada en el turno del heroe, con lo leido de la mesa (hilo de fondo).
        /// Null cuando deja de ser su turno.
        /// </summary>
        public event Action<LiveDecision?, LiveTurnState?>? LiveDecisionChanged;

        /// <summary>El OCR de Windows esta disponible (sin el no hay decision en vivo).</summary>
        public bool LiveDecisionsAvailable => _tableReader?.OcrAvailable == true;

        /// <summary>Lee la mesa en vivo (cartas y decision); si no, solo corrige con el historial.</summary>
        public bool LiveReading => _reader is not null;

        public IntPtr Handle => _window.Handle;

        /// <summary>Mano terminada y calificada (hilo de fondo).</summary>
        public event Action<TrainerHandReport>? HandGraded;

        public event Action<string>? StatusChanged;

        /// <summary>El lector se calibro solo con la mesa (hilo de fondo).</summary>
        public event Action<TrainerCalibration>? CalibrationChanged;

        /// <summary>
        /// En el turno del heroe hay una carta que el lector no reconoce y no se puede aconsejar:
        /// donde ("HERO" o "BOARD") y la carta que mas se parece (o null).
        /// </summary>
        public event Action<string, string?>? LiveUnreadable;

        /// <summary>La sesion se detuvo porque la mesa no es de dinero ficticio.</summary>
        public event Action<string>? Blocked;

        /// <summary>La sesion se detuvo porque la ventana se cerro o paso a mostrar otra mesa.</summary>
        public event Action<string>? Closed;

        /// <summary>La sesion se detuvo porque la mesa no es de Hold'em sin limite 6-max (etiqueta, motivo).</summary>
        public event Action<string, string>? Unsupported;

        /// <summary>
        /// El bloqueo vino del titulo de la ventana (la mesa dice dinero real o torneo). Uno que viene
        /// del historial lo vuelve a comprobar cada sesion nueva antes de leer nada.
        /// </summary>
        public bool BlockedByTitle { get; private set; }

        public string TableName => _window.TableName;

        public string? PredictedPosition
        {
            get { lock (_sync) return _predictedPosition; }
        }

        public CapturedFrame? LastFrame
        {
            get { lock (_sync) return _lastFrame; }
        }

        public IReadOnlyList<TrainerVillainProfile> Opponents
        {
            get
            {
                TrainerHand? hand;
                lock (_sync) hand = _lastHand;
                if (hand is null)
                    return Array.Empty<TrainerVillainProfile>();

                return hand.ActiveSeats
                    .Where(seat => !PokerStarsHandHistory.SamePlayer(seat.Name, hand.HeroName))
                    .Select(seat => ProfileFor(seat.Name) ?? new TrainerVillainProfile(seat.Name, 0, 0, 0, 0, 0, 0, 0, "-"))
                    .ToList();
            }
        }

        /// <summary>Historial exacto de la mesa y de la cuenta del heroe (ver TrainerHandHistoryLocator).</summary>
        public static string? FindHandHistory(PokerStarsTableWindow window) =>
            TrainerHandHistoryLocator.Find(window.TableName, window.HeroName)?.Path;

        // Coletilla final del titulo con la cuenta: "Sesion iniciada con el nombre de usuario X",
        // "Iniciaste sesion como X", "Logged In as X"... El nombre lo elige el jugador, asi que no puede
        // contar para decidir que una mesa es de dinero ficticio.
        private static readonly Regex LoginSuffixRx = new(
            @"(?:Iniciaste sesi\S*n como|Sesi\S*n iniciada con el nombre de usuario|Logged in as|Connect\S* en tant que|Angemeldet als|Conectado como|Sess\S*o iniciada como)\s.*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Las mesas de dinero real llevan la moneda en las ciegas ($0.01/$0.02, 0,01 EUR).
        private static readonly Regex CurrencyRx = new(@"[$\u20AC\u00A3\u20B9]|\b(?:USD|EUR|GBP|CAD|INR)\b", RegexOptions.Compiled);

        /// <summary>
        /// Dinero ficticio segun el titulo de la ventana (primer filtro, antes del historial). Las
        /// marcas positivas se buscan sin la coletilla de la cuenta; las que bloquean (moneda,
        /// torneo), en el titulo entero: un nombre de usuario solo puede bloquear de mas, nunca dejar
        /// pasar una mesa de dinero real.
        /// </summary>
        public static bool IsPlayMoneyTitle(string title)
        {
            if (IsTournamentTitle(title) || CurrencyRx.IsMatch(title))
                return false;

            var table = WithoutLogin(title);
            return table.Contains("Dinero ficticio", StringComparison.OrdinalIgnoreCase) ||
                   table.Contains("Play Money", StringComparison.OrdinalIgnoreCase) ||
                   table.Contains("Jeu fictif", StringComparison.OrdinalIgnoreCase) ||
                   table.Contains("Spielgeld", StringComparison.OrdinalIgnoreCase) ||
                   table.Contains("Dinheiro ficticio", StringComparison.OrdinalIgnoreCase) ||
                   table.Contains("Dinheiro fict\u00EDcio", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Hold'em sin limite segun el titulo: las tablas y las reglas del entrenador son de NLHE.</summary>
        public static bool IsNoLimitHoldemTitle(string title)
        {
            var table = WithoutLogin(title);
            return table.Contains("Hold'em", StringComparison.OrdinalIgnoreCase) &&
                   (table.Contains("Sin l", StringComparison.OrdinalIgnoreCase) ||
                    table.Contains("No Limit", StringComparison.OrdinalIgnoreCase) ||
                    table.Contains("Sem Limite", StringComparison.OrdinalIgnoreCase));
        }

        private static string WithoutLogin(string title)
        {
            var login = LoginSuffixRx.Match(title);
            return login.Success ? title[..login.Index] : title;
        }

        /// <summary>
        /// Torneos (tambien los de dinero ficticio) fuera: el entrenador es para cash y sus archivos
        /// de historial no se pueden emparejar con la ventana, asi que el titulo es el unico filtro.
        /// </summary>
        public static bool IsTournamentTitle(string title) =>
            title.Contains("Torneo", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Tournament", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Turnier", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Tournoi", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Torneio", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Sigue donde iba otra sesion de esta mesa (que se va a detener): mismo historial y mismo
        /// corte entre lo historico y lo que se corrige, asi no se pierde ni se repite ninguna mano
        /// al cambiar entre lectura en vivo y solo correccion. Se llama antes de Start.
        /// </summary>
        public void ContinueFrom(TrainerSession previous)
        {
            lock (previous._sync)
            {
                DateTime? previousStart = previous._sessionStart == default ? null : previous._sessionStart;
                _continueEpoch = previous._continueEpoch is { } inherited && (previousStart is null || inherited < previousStart)
                    ? inherited
                    : previousStart;

                if (previous._tail is { } tail)
                {
                    _continuePath = tail.Path;
                    _continueCut = previous._historyEnd;
                    _continueStale = previous._staleHistory;
                }
                else
                {
                    // La anterior aun no habia enlazado su historial: se hereda lo que ella heredo.
                    _continuePath = previous._continuePath;
                    _continueCut = previous._continueCut;
                    _continueStale = previous._continueStale;
                }

                _processedHands.UnionWith(previous._processedHands);
            }
        }

        public void Start()
        {
            // Con el titulo de ahora, no el de cuando se detecto la mesa.
            var current = TableCapture.GetWindowTitle(_window.Handle);
            if (!IsPlayMoneyTitle(string.IsNullOrWhiteSpace(current) ? _window.Title : current))
            {
                Block("La ventana no indica dinero ficticio. El modo Entrenador solo funciona en mesas de dinero ficticio.", byTitle: true);
                return;
            }

            _sessionStart = DateTime.Now;
            _lastHandArrival = _sessionStart;
            // Solo correccion: no se captura la pantalla, pero la ventana se sigue vigilando (titulo).
            _ = Task.Run(() => RunSafely(_reader is null ? WatchLoop : CaptureLoop));
            _ = Task.Run(() => RunSafely(HistoryLoop));
        }

        public void Stop() => _cts.Cancel();

        public void Dispose()
        {
            _cts.Cancel();
            _cts.Dispose();
        }

        // ------------------------------------------------------------------ captura en vivo

        private async Task CaptureLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                // Un fallo en una captura (ventana cerrandose, imagen rara) no debe parar la lectura.
                try
                {
                    if (!_blocked)
                        CaptureOnce();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    StatusChanged?.Invoke($"Error leyendo la mesa: {ex.Message}");
                }

                await Task.Delay(CaptureIntervalMs, token);
            }
        }

        /// <summary>Solo correccion: cada segundo se comprueba que la ventana sigue siendo esta mesa.</summary>
        private async Task WatchLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (!_blocked)
                        CheckWindow();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    StatusChanged?.Invoke($"Error vigilando la mesa: {ex.Message}");
                }

                await Task.Delay(1000, token);
            }
        }

        /// <summary>
        /// La ventana sigue siendo esta mesa de dinero ficticio. El titulo se mira en cada captura:
        /// un titulo vacio o de transicion no se lee (si dura, la mesa se da por cerrada), otra mesa
        /// en la misma ventana cierra la sesion, y la misma mesa sin dinero ficticio la bloquea.
        /// </summary>
        private bool CheckWindow()
        {
            if (!TableCapture.IsAlive(_window.Handle))
            {
                Close("La mesa se cerro.");
                return false;
            }

            var title = TableCapture.GetWindowTitle(_window.Handle);
            var marker = title.IndexOf(" - ", StringComparison.Ordinal);
            if (marker <= 0)
            {
                _titleGoneSince ??= DateTime.Now;
                if (DateTime.Now - _titleGoneSince.Value > TimeSpan.FromSeconds(5))
                    Close("La ventana ya no muestra la mesa.");
                return false;
            }

            _titleGoneSince = null;
            var shownTable = title[..marker].Trim();
            if (TrainerHandHistoryLocator.Key(shownTable) != TrainerHandHistoryLocator.Key(_window.TableName))
            {
                Close($"La ventana paso a mostrar otra mesa ({shownTable}).");
                return false;
            }

            if (!IsPlayMoneyTitle(title))
            {
                Block("La ventana ya no es de dinero ficticio. Sesion detenida.", byTitle: true);
                return false;
            }

            return true;
        }

        private void CaptureOnce()
        {
            if (_reader is null || !CheckWindow())
                return;

            // No se lee la pantalla hasta revisar el historial que ya existe: si tiene una mano de
            // dinero real o de torneo, la sesion se bloquea antes de dar ningun consejo.
            if (!_historyChecked)
            {
                if (!_historyCheckAnnounced)
                {
                    _historyCheckAnnounced = true;
                    StatusChanged?.Invoke("Comprobando el historial de la mesa antes de leer la pantalla...");
                }

                return;
            }

            var frame = TableCapture.CaptureClient(_window.Handle);
            if (frame is null)
            {
                StatusChanged?.Invoke("No se puede capturar la mesa (minimizada, o tapada por otra ventana sin que PokerStars deje copiarla).");
                return;
            }

            string? position;
            lock (_sync)
            {
                _lastFrame = frame;
                position = _predictedPosition is not null && _lastHand is { } last
                    ? PredictNextPosition(last, HandsSinceHistory)
                    : _predictedPosition;
            }

            if (!_reader.Calibration.HasHero && !TryAutoCalibrate(frame, replaceExisting: true))
                return;

            // Cada ~5 s, solo si la calibracion dejo de servir (otro tamano de ventana, o las cartas
            // llevan un rato sin poder leerse), se mira si las cartas estan en otro sitio. Con una
            // calibracion que funciona no se toca: asi el realce del showdown nunca la cambia.
            if (++_capturesSinceCheck >= RecalibrationCheckEvery)
            {
                _capturesSinceCheck = 0;
                if (!_reader.Calibration.MatchesSize(frame) || _unreadableStreak >= RecalibrationCheckEvery)
                    TryAutoCalibrate(frame, replaceExisting: false);
            }

            var reading = _reader.Observe(frame);
            _unreadableStreak = reading.HeroUnreadable || reading.BoardUnreadable && reading.VisibleBoardSlots >= 3
                ? _unreadableStreak + 1
                : 0;
            var heroSlots = $"{reading.Slots[0] ?? "-"}|{reading.Slots[1] ?? "-"}";
            _lastHeroSlots = heroSlots;
            NoteDeal(heroSlots);

            // Al terminar la mano PokerStars ensena el resto del board ("lo que habria salido"):
            // el panel se congela hasta que cambian las cartas del heroe (nueva mano).
            var frozen = _frozenHeroSlots;
            if (frozen is not null && frozen != heroSlots)
                _frozenHeroSlots = frozen = null;
            var finished = frozen is not null;

            if (!finished)
                UpdateLiveDecision(frame, reading, heroSlots, position);

            var signature = $"{reading.Signature}#{finished}";
            if (signature == _lastSignature && position == _lastPosition)
                return;

            _lastSignature = signature;
            _lastPosition = position;

            LiveAdvice advice;
            if (finished)
                advice = LiveAdvice.Waiting("Mano terminada: la correccion esta en la lista. Esperando la siguiente mano...");
            else if (!reading.HeroVisible)
                advice = LiveAdvice.Waiting("Esperando la siguiente mano...");
            else if (reading.HeroDealing)
                advice = LiveAdvice.Waiting("Repartiendo...");
            else if (reading.BoardUnreadable)
                advice = LiveAdvice.Waiting(reading.VisibleBoardSlots is 1 or 2
                    ? "Repartiendo el board..."
                    : "Hay una carta del board que el lector aun no conoce (o se lee repetida). La aprende al terminar la mano.");
            else if (reading.HeroUnreadable)
                advice = LiveAdvice.Waiting("Hay cartas en pantalla que el lector aun no conoce. Las aprende al terminar cada mano (o ensenalas en Calibrar).");
            else
                advice = _advisor.Analyze(reading.HeroCards, reading.BoardCards, position);

            LiveChanged?.Invoke(reading, advice);
        }

        /// <summary>
        /// Decision en vivo. En el turno del heroe (barra de botones roja) se leen una vez los
        /// botones, el bote y las placas y se recomienda que hacer; fuera de su turno se vigilan las
        /// placas cada 1,5 s para saber que hizo cada rival en cada calle.
        /// </summary>
        private void UpdateLiveDecision(CapturedFrame frame, LiveReading reading, string heroSlots, string? position)
        {
            if (_tableReader is not { OcrAvailable: true } tableReader)
                return;

            if (heroSlots != _liveHand.HeroKey)
            {
                _liveHand = new LiveHandContext { HeroKey = heroSlots };
                _lastPlatesRead = DateTime.MinValue;
            }

            // Con una carta propia o del board sin leer no se sabe la mano ni en que calle va: mejor
            // no recomendar nada que recomendar para la calle equivocada. Pero en el turno del heroe
            // se le dice por que no hay consejo (antes el cuadro desaparecia sin mas).
            if (reading.HeroCards.Count != 2 || reading.BoardUnreadable)
            {
                var unreadable = reading.HeroCards.Count != 2
                    ? (reading.HeroUnreadable ? FirstUnknown(reading, 0, 2) : -1)
                    : FirstUnknown(reading, 2, reading.Slots.Count);
                if (unreadable >= 0 && LiveTableReader.IsHeroTurn(frame))
                    NoticeUnreadable(unreadable < 2 ? "HERO" : "BOARD", reading.Matches.Count > unreadable ? reading.Matches[unreadable].Card : null);
                else
                    ClearLiveDecision();
                return;
            }

            var street = reading.BoardCards.Count switch { 3 => "FLOP", 4 => "TURN", 5 => "RIVER", _ => "PREFLOP" };
            var bigBlind = _lastHand?.BigBlind ?? 1;
            if (!LiveTableReader.IsHeroTurn(frame))
            {
                ClearLiveDecision();
                // Fuera del turno se vigilan las placas cada 1,5 s en todas las calles: quien sube,
                // apuesta, paga o pasa en cada una (y cuanto pone) sirve para reconstruir el spot.
                if (DateTime.Now - _lastPlatesRead >= TimeSpan.FromSeconds(1.5))
                {
                    _lastPlatesRead = DateTime.Now;
                    var platesNow = tableReader.ReadPlates(frame, bigBlind, heroHasCards: true).ToList();
                    // La pila propia imposible tampoco se guarda fuera del turno (daria fichas "puestas" que no hay).
                    double? expectedNow;
                    lock (_sync) expectedNow = _heroStackAfterHistory;
                    if (platesNow.Count > 0 && ImplausibleHeroStack(platesNow[0].StackBb, expectedNow, null, HandsSinceHistory))
                        platesNow[0] = platesNow[0] with { StackBb = null };
                    _liveHand.Merge(street, platesNow, PlateLayout().Positions);
                }

                return;
            }

            var key = $"{LiveTableReader.BarSignature(frame)}#{reading.Signature}";
            if (key == _lastTurnKey)
                return;
            _lastTurnKey = key;

            var turn = tableReader.ReadTurn(frame, bigBlind, heroHasCards: true);
            // Pila propia imposible (el OCR la parte: "13 2, 5 BB" -> 5 bb): si con el bote de ahora no
            // se explica lo que falta respecto al ultimo historial, no se usa (mejor "?" que un all-in
            // de 0,03 bb o un "no ir" por creer que solo quedan 8 bb).
            double? expectedHero;
            lock (_sync) expectedHero = _heroStackAfterHistory;
            // Lo que se corrigio o se adivino en este turno: se avisa en el cuadro ("lectura dudosa").
            var doubtful = new List<string>();
            if (ImplausibleHeroStack(turn.HeroStackBb, expectedHero, turn.PotBb, HandsSinceHistory) && turn.Plates.Count > 0)
            {
                doubtful.Add("tu pila");
                AppLog.Warn($"Decision en vivo: pila propia leida {turn.HeroStackBb} bb no cuadra con el historial (~{expectedHero:0.#} bb); se ignora.");
                var plates = turn.Plates.ToList();
                plates[0] = plates[0] with { StackBb = null };
                turn = new LiveTurnState(turn.Options, turn.PotBb, plates, turn.ButtonCount);
            }
            // Pila propia sin leer en el turno: la ultima leida en esta mano (fuera del turno se leen las
            // placas cada 1,5 s). Sin ella no se sabia que "apostar 55,5 bb" con 52,8 detras era un all-in.
            if (turn.HeroStackBb is null && turn.Plates.Count > 0 && _liveHand.LastStack(0) is { } lastHero && lastHero > 0 &&
                !ImplausibleHeroStack(lastHero, expectedHero, turn.PotBb, HandsSinceHistory))
            {
                if (!doubtful.Contains("tu pila"))
                    doubtful.Add("tu pila");
                var plates = turn.Plates.ToList();
                plates[0] = plates[0] with { StackBb = lastHero };
                turn = new LiveTurnState(turn.Options, turn.PotBb, plates, turn.ButtonCount);
            }
            var (positions, names) = PlateLayout();
            // Posiciones con lo que se ve en la mesa (quien esta ausente ahora y, preflop, si puedes pasar
            // o pagar media ciega); se guardan para el resto de la mano.
            if (_liveHand.HeroPosition is null && LivePositions(frame, turn, street) is { Hero: { } liveHero } live)
            {
                _liveHand.HeroPosition = liveHero;
                _liveHand.PlatePositions = live.Plates;
            }
            if (_liveHand.HeroPosition is { } knownHero)
                position = knownHero;
            if (_liveHand.PlatePositions is { Count: > 0 } knownPlates)
                positions = knownPlates;
            _liveHand.Merge(street, turn.Plates, positions);
            _liveHand.BluffFolders = TableBluffModes.IsOn(_window.TableName);
            var decision = _decisionAdvisor.Decide(reading.HeroCards, reading.BoardCards, position, turn, _liveHand, positions, names, ProfileFor);
            if (turn.HeroStackBb is null && !doubtful.Contains("tu pila"))
                doubtful.Add("tu pila");
            if (position is null or "?")
                doubtful.Add("tu posición");
            doubtful.AddRange(TrainerLiveDecisionAdvisor.DoubtfulReads(decision.Details).Where(item => !doubtful.Contains(item)));
            if (doubtful.Count > 0)
            {
                var warning = $"Lectura dudosa ({string.Join(", ", doubtful)}): revisa la mesa antes de seguir el consejo.";
                decision = decision with { ReadWarning = warning, Details = decision.Details.Append(warning).ToList() };
            }
            // Para el registro de revisiones: lo que se leyo en pantalla en este turno. Si un consejo sale
            // raro (AKo "no ir", un all-in con una pareja floja) se ve si fallo la lectura o el calculo.
            var stored = decision with
            {
                Details = decision.Details.Append(ReadSummary(reading, turn, position)).ToList(),
                ReadHero = TrainerCards.ToText(reading.HeroCards),
                ReadBoard = TrainerCards.ToText(reading.BoardCards),
                ReadPosition = position
            };
            lock (_sync)
            {
                // Una recomendacion por turno para compararla al terminar la mano. Si el mismo turno
                // se relee (la barra cambia al mover el raton) se queda la ultima lectura, que es la
                // que el alumno tenia delante al decidir.
                if (_turnRecorded && _shownDecisions.Count > 0 &&
                    _shownDecisions[^1].Street == decision.Street && _shownDecisions[^1].FacingBet == decision.FacingBet)
                    _shownDecisions[^1] = stored;
                else
                    _shownDecisions.Add(stored);
                _turnRecorded = true;
            }

            LiveDecisionChanged?.Invoke(decision, turn);
        }

        /// <summary>Primera zona entre las dadas con una carta que el lector no reconoce ("?"), o -1.</summary>
        private static int FirstUnknown(LiveReading reading, int from, int to)
        {
            for (var slot = from; slot < to && slot < reading.Slots.Count; slot++)
                if (reading.Slots[slot] == "?")
                    return slot;
            return -1;
        }

        /// <summary>En tu turno hay una carta que el lector no reconoce: se avisa una vez (HERO o BOARD, y la carta que mas se parece).</summary>
        private void NoticeUnreadable(string where, string? guess)
        {
            var key = $"unreadable:{where}:{guess}";
            if (key == _lastTurnKey)
                return;
            _lastTurnKey = key;
            _turnRecorded = false;
            LiveUnreadable?.Invoke(where, guess);
        }

        private void ClearLiveDecision()
        {
            _turnRecorded = false;
            if (_lastTurnKey.Length == 0)
                return;
            _lastTurnKey = "";
            LiveDecisionChanged?.Invoke(null, null);
        }

        /// <summary>
        /// Posicion y nombre de cada placa para la mano en curso. Las placas van en el sentido de las
        /// agujas del reloj desde el heroe, que es el orden de asientos de PokerStars: placa i =
        /// asiento del heroe + i. Las posiciones salen de la rotacion del boton de la ultima mano.
        /// </summary>
        private (IReadOnlyDictionary<int, string> Positions, IReadOnlyDictionary<int, string> Names) PlateLayout()
        {
            TrainerHand? hand;
            lock (_sync) hand = _lastHand;
            var positions = new Dictionary<int, string>();
            var names = new Dictionary<int, string>();
            var heroSeat = hand?.SeatOf(hand.HeroName)?.Seat;
            if (hand is null || heroSeat is null || hand.MaxSeats != 6)
                return (positions, names);

            var seatPositions = PredictSeatPositions(hand, HandsSinceHistory);
            for (var plate = 0; plate < 6; plate++)
            {
                var seat = (heroSeat.Value - 1 + plate) % 6 + 1;
                if (seatPositions.TryGetValue(seat, out var chart))
                    positions[plate] = chart;
                var player = hand.Seats.FirstOrDefault(item => item.Seat == seat);
                if (player is not null)
                    names[plate] = player.Name;
            }

            return (positions, names);
        }

        /// <summary>
        /// Anota en cada decision calificada lo que se le recomendo en vivo y si lo siguio.
        /// </summary>
        private void AnnotateLiveRecommendations(TrainerHandReport report)
        {
            var readErrors = new List<string>();
            List<LiveDecision> shown;
            lock (_sync)
            {
                shown = _shownDecisions.ToList();
                _shownDecisions.Clear();
            }

            foreach (var group in report.Decisions.GroupBy(decision => decision.Street))
            {
                var live = shown.Where(item => item.Street == group.Key).ToList();
                var index = 0;
                foreach (var decision in group)
                {
                    if (index >= live.Count)
                        break;
                    // Un turno que no llego a leerse en vivo no desplaza a los demas: la recomendacion
                    // tiene que ser del mismo tipo de spot (con apuesta delante o sin ella).
                    if (live[index].FacingBet != decision.FacingBet)
                        continue;
                    var recommendation = live[index++];
                    decision.LiveRecommendation = recommendation.Action;
                    decision.LiveSource = recommendation.Exploit is { } leak ? $"Explotar ({leak})" : recommendation.Source;
                    decision.LiveReason = recommendation.Reason;
                    decision.LiveDetails = string.Join(" | ", recommendation.Details);
                    decision.LiveReadCheck = LiveReadCheck.Compare(recommendation, report);
                    if (decision.LiveReadCheck is not null)
                        readErrors.Add($"{decision.Street}: {decision.LiveReadCheck}");
                    decision.FollowedLive = SameAction(recommendation.Kind, decision.ChosenKind);

                    // Seguir la jugada GTO que se mostro en vivo nunca es un error.
                    if (decision.FollowedLive == true && recommendation.IsGto && decision.Grade != TrainerGrade.Good)
                    {
                        decision.Grade = TrainerGrade.Good;
                        decision.Explanation += " Seguiste la jugada GTO que se te mostró en vivo (equilibrio de la mano): no es un error.";
                    }
                    // Seguir el consejo en vivo no es un error del alumno: si la revision (con los rangos del
                    // historial) opina distinto, queda como dudosa con su explicacion. Antes salia "error" en
                    // el informe y en el vigilante aunque se hubiera hecho lo que decia APH (KQo, ATs...).
                    else if (decision.FollowedLive == true && decision.Grade == TrainerGrade.Mistake)
                    {
                        decision.Grade = TrainerGrade.Marginal;
                        decision.Explanation = "Seguiste el consejo en vivo; la revisión con los rangos del historial opina distinto: " + decision.Explanation;
                    }
                }
            }

            Interlocked.Increment(ref _readChecks);
            if (readErrors.Count > 0)
            {
                Interlocked.Increment(ref _readMisses);
                AppLog.Warn($"Lectura en vivo distinta del historial en {_window.TableName}, mano {report.HandId}: {string.Join("; ", readErrors)}");
                StatusChanged?.Invoke($"Ojo: en la mano {report.HandId} se leyó mal ({string.Join("; ", readErrors)}). " +
                                      $"Manos con lectura mal: {_readMisses} de {_readChecks}.");
            }
        }

        private int _readChecks;
        private int _readMisses;

        /// <summary>
        /// Si la pila propia leida es imposible: con la mano anterior ya en el historial, lo que falta
        /// respecto a la pila con la que termino tiene que estar en el bote. Se exige que falte mas de
        /// la mitad sin explicar (margen para recompras y errores del bote). Tampoco puede ser 5 veces
        /// mayor (la coma perdida).
        /// </summary>
        public static bool ImplausibleHeroStack(double? read, double? expected, double? pot, int handsSinceHistory) =>
            read is { } stack && expected is { } before && before >= 10 && handsSinceHistory <= 1 &&
            (stack + (pot ?? 0) < before * 0.5 ||
             // Coma perdida ("92,5" leido 925): ni anadiendo fichas se llega a 5 veces la pila.
             before >= 20 && stack > Math.Max(before * 5, 250));

        private static string ReadSummary(LiveReading reading, LiveTurnState turn, string? position)
        {
            static string N(double? value) => value?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? "?";
            var plates = string.Join(", ", turn.Plates.Where(plate => plate.Index != 0 && !plate.EmptySeat)
                .Select(plate => $"{plate.Index}:{plate.Action ?? "-"}/{N(plate.StackBb)}{(plate.HasCards ? "" : " sin cartas")}"));
            return $"Leído en pantalla: cartas {TrainerCards.ToText(reading.HeroCards)}" +
                   (reading.BoardCards.Count > 0 ? $" · board {TrainerCards.ToText(reading.BoardCards)}" : "") +
                   $" · posición {position ?? "?"} · bote {N(turn.PotBb)} · pagar {N(turn.ToCallBb)} · stack {N(turn.HeroStackBb)}" +
                   $" · botones {string.Join("/", turn.Options.Select(option => option.Text))} · placas [{plates}]";
        }

        private static bool SameAction(string liveKind, string chosenKind) =>
            liveKind == chosenKind || liveKind == "Bet" && chosenKind == "Raise";

        /// <summary>
        /// Autocalibracion con la captura: hace falta ver a la vez el board (flop o mas) y las dos
        /// cartas del heroe. Sin <paramref name="replaceExisting"/> solo sustituye la calibracion
        /// actual si las cartas aparecen claramente en otro sitio.
        /// </summary>
        private bool TryAutoCalibrate(CapturedFrame frame, bool replaceExisting)
        {
            var located = CardLocator.Locate(frame);
            if (located.Calibration is null || located.Hero.Count != 2)
            {
                if (replaceExisting)
                    StatusChanged?.Invoke("Calibrando el lector: en cuanto se vean a la vez el flop y tus dos cartas queda listo (o usa \"Calibrar lector\").");
                return false;
            }

            if (!replaceExisting && !CardsMoved(_reader!.Calibration, located.Calibration))
            {
                _pendingLayout = null;
                return false;
            }

            // Nunca se adopta una colocacion vista una sola vez: tiene que repetirse al menos 2,5 s
            // despues. El realce del showdown dura menos, asi que no puede confirmarse a si mismo.
            if (_pendingLayout is null || !_pendingLayout.SameLayout(located.Calibration))
            {
                _pendingLayout = located.Calibration;
                _pendingLayoutSince = DateTime.Now;
                if (replaceExisting)
                    StatusChanged?.Invoke("Calibrando el lector: comprobando la posicion de las cartas...");
                return false;
            }

            if (DateTime.Now - _pendingLayoutSince < TimeSpan.FromSeconds(2.5))
                return false;
            _pendingLayout = null;

            try
            {
                located.Calibration.Save();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Sin guardar sigue valiendo para esta sesion.
            }

            _reader!.Calibration = located.Calibration;
            CalibrationChanged?.Invoke(located.Calibration);
            StatusChanged?.Invoke(replaceExisting
                ? "Lector calibrado automaticamente con esta mesa."
                : "Las cartas estaban en otro sitio (tamano o tema de la mesa): lector recalibrado.");
            return true;
        }

        /// <summary>Las zonas del heroe y del flop se han movido mas de un tercio de su ancho.</summary>
        private static bool CardsMoved(TrainerCalibration current, TrainerCalibration located)
        {
            for (var slot = 0; slot < 5; slot++)
            {
                var a = current.Slots[slot];
                var b = located.Slots[slot];
                if (!a.IsValid)
                    return true;

                var dx = Math.Abs(a.X + a.W / 2 - (b.X + b.W / 2));
                var dy = Math.Abs(a.Y + a.H / 2 - (b.Y + b.H / 2));
                if (dx > b.W / 3 || dy > b.H / 3)
                    return true;
            }

            return false;
        }

        // ------------------------------------------------------------------ historial

        private async Task HistoryLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                // El archivo se vuelve a buscar periodicamente: PokerStars abre uno nuevo cada dia y,
                // al empezar, puede que el de hoy aun no exista (se crea al terminar la primera mano).
                if (DateTime.Now >= _nextResolve)
                {
                    ResolveHistoryFile();
                    _nextResolve = DateTime.Now.AddSeconds(_tail is null ? 3 : 10);
                }

                if (_tail is not null)
                    ReadHistory();

                await Task.Delay(HistoryIntervalMs, token);
            }
        }

        /// <summary>
        /// Enlaza el historial exacto de la mesa. Si aparece otro mas nuevo (cambio de dia) se pasa a
        /// el. Lo que habia en el archivo al enlazarlo es historico (stats y posicion); un archivo
        /// creado durante la sesion, o al que se cambia, es todo en vivo.
        /// </summary>
        private void ResolveHistoryFile()
        {
            var located = TrainerHandHistoryLocator.Find(_window.TableName, _window.HeroName ?? _heroName);
            if (located is null && _tail is null && _handHistoryPath is not null && File.Exists(_handHistoryPath))
            {
                // El archivo que se encontro al detectar la mesa, mientras no aparezca otro. Lo que
                // tenga de otra mesa no se analiza (ver ProcessHand).
                var info = new FileInfo(_handHistoryPath);
                located = new TrainerHandHistoryLocator.Located(_handHistoryPath, info.LastWriteTime.ToString("yyyyMMdd"), info.LastWriteTime, info.CreationTime);
            }

            if (located is null)
            {
                if (_tail is null)
                {
                    // Aun no hay historial que revisar: mientras tanto manda el titulo.
                    _historyChecked = true;
                    StatusChanged?.Invoke("Esperando el historial de la mesa (PokerStars lo crea al terminar la primera mano)...");
                }

                return;
            }

            if (_tail is not null && string.Equals(_tail.Path, located.Path, StringComparison.OrdinalIgnoreCase))
                return;

            var switching = _tail is not null;
            var epoch = _continueEpoch is { } previousStart && previousStart < _sessionStart ? previousStart : _sessionStart;
            var createdDuringSession = located.Created >= epoch.AddMinutes(-1);
            var continuing = !switching && _continuePath is not null &&
                             string.Equals(_continuePath, located.Path, StringComparison.OrdinalIgnoreCase);
            var length = SafeLength(located.Path);
            var historyEnd = switching ? 0
                : continuing ? Math.Min(_continueCut, length)
                : createdDuringSession ? 0
                : length;
            // Un historial que no se toca desde hace rato (otro dia) solo sirve para las stats: su
            // ultima mano no dice nada de la posicion de ahora.
            var stale = continuing
                ? _continueStale
                : !switching && !createdDuringSession && located.LastWrite < epoch.AddMinutes(-15);

            // Todo junto: otra sesion que continue a esta lee el archivo, el corte y si es viejo a la vez.
            lock (_sync)
            {
                _tail = new AppendedTextReader(located.Path);
                _pending.Clear();
                _historyEnd = historyEnd;
                _gateEnd = length;
                _staleHistory = stale;
                _handHistoryPath = located.Path;
            }

            _historyAnnounced = _historyEnd == 0;

            if (switching)
                StatusChanged?.Invoke($"Historial nuevo para {_window.TableName}: {Path.GetFileName(located.Path)}.");
            else if (_historyAnnounced)
                StatusChanged?.Invoke("Sesion iniciada. Esperando la primera mano...");
        }

        private void ReadHistory()
        {
            var tail = _tail!;
            var historical = tail.Position < _historyEnd;
            List<string> lines;
            try
            {
                lines = historical ? tail.ReadNewLines(_historyEnd) : tail.ReadNewLines();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // PokerStars puede tener el archivo bloqueado un instante: se reintenta sin cambiar
                // nada de lo que es historico y lo que es en vivo.
                StatusChanged?.Invoke($"No se pudo leer el historial ({ex.Message}); reintentando...");
                return;
            }

            if (lines.Count > 0)
            {
                _pending.AddRange(lines);
                ProcessPending(_pending, historical);
            }

            // La pantalla se lee cuando ya se reviso todo lo que el historial tenia al enlazarlo (una
            // ultima linea sin terminar no puede dejarlo cerrado para siempre).
            var gate = Math.Max(_historyEnd, _gateEnd);
            if (tail.Position >= gate || lines.Count == 0 && gate - tail.Position < 256)
                _historyChecked = true;

            if (!_historyAnnounced && tail.Position >= _historyEnd)
            {
                _historyAnnounced = true;
                StatusChanged?.Invoke(_lastHand is null || _staleHistory
                    ? $"Sesion iniciada en {_window.TableName}. Esperando la primera mano..."
                    : $"Sesion iniciada en {_window.TableName}. Posicion estimada para la proxima mano: {PredictedPosition ?? "?"}.");
            }
        }

        private static long SafeLength(string path)
        {
            try
            {
                return new FileInfo(path).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return 0;
            }
        }

        private void ProcessPending(List<string> pending, bool historical)
        {
            // Las manos son tramos seguidos de "pending" desde su cabecera: lo consumido se cuenta
            // por longitudes, no buscando lineas (una linea como "Hero: folds" se repite entre manos).
            var firstStart = pending.FindIndex(line => TrainerHandParser.HandStartRx.IsMatch(line));
            if (firstStart < 0)
            {
                // Un historial con contenido pero sin ninguna cabecera de mano conocida no deja
                // comprobar el dinero de la mesa: se bloquea en vez de seguir sin comprobar.
                if (pending.Count(line => line.Length > 0) >= 20)
                {
                    Block("El historial de la mesa no tiene un formato conocido: no se puede confirmar que sea de dinero ficticio.");
                    return;
                }

                pending.Clear();
                return;
            }

            var hands = TrainerHandParser.SplitHands(pending).ToList();
            var complete = hands.Count > 0 && TrainerHandParser.IsComplete(hands[^1]) ? hands.Count : hands.Count - 1;
            var consumed = firstStart;
            for (var i = 0; i < complete; i++)
            {
                consumed += hands[i].Count;
                // Una mano que no se pueda analizar se salta: no puede bloquear las siguientes.
                try
                {
                    ProcessHand(hands[i], historical, isLastOfBatch: i == complete - 1);
                }
                catch (Exception ex)
                {
                    StatusChanged?.Invoke($"No se pudo analizar una mano: {ex.Message}");
                }

                if (_blocked)
                    return;
            }

            pending.RemoveRange(0, Math.Min(consumed, pending.Count));
        }

        private void ProcessHand(IReadOnlyList<string> lines, bool historical, bool isLastOfBatch)
        {
            // El dinero se comprueba en todas las manos, tambien las historicas: solo mira la
            // cabecera, y una mesa con historial de dinero real o de torneo no se entrena.
            switch (TrainerHandParser.DetectMoney(lines))
            {
                case TrainerMoneyKind.RealMoney:
                    Block("El historial muestra una mano de DINERO REAL. El modo Entrenador se detuvo.");
                    return;
                case TrainerMoneyKind.Tournament:
                    Block("El historial es de un torneo. El modo Entrenador es solo para cash de dinero ficticio.");
                    return;
                case TrainerMoneyKind.Unknown:
                    Block("No se pudo confirmar en el historial que la mesa sea de dinero ficticio. Sesion detenida por seguridad.");
                    return;
            }

            // Una mano de otra mesa no se analiza nunca (y obliga a buscar otra vez el archivo).
            var table = TrainerHandHistoryLocator.TableOfHand(lines);
            if (table is not null && TrainerHandHistoryLocator.Key(table) != TrainerHandHistoryLocator.Key(_window.TableName))
            {
                _nextResolve = DateTime.MinValue;
                StatusChanged?.Invoke($"El historial leido es de la mesa '{table}', no de '{_window.TableName}': se busca el correcto.");
                return;
            }

            _statsParser.FeedLines(lines, () => { });

            // Del historico solo interesa la ultima mano (posicion y rivales); las demas solo
            // alimentan las stats. Parsear miles de manos viejas no aporta nada.
            if (historical && !isLastOfBatch)
                return;

            var hand = TrainerHandParser.Parse(lines, _heroName);
            if (hand is null)
                return;

            // Las tablas y las reglas son de Hold'em sin limite 6-max: en otra modalidad la sesion se
            // para (corregir con tablas que no son las suyas solo confundiria al alumno).
            if (!hand.IsNoLimitHoldem || hand.MaxSeats is not (0 or 6))
            {
                // Un historial viejo (otro dia, quiza otra mesa con el mismo nombre) no decide nada.
                if (historical && _staleHistory)
                    return;

                if (hand.IsNoLimitHoldem)
                    StopUnsupported("NO 6-MAX", $"La mesa es de {hand.MaxSeats} jugadores: las tablas del entrenador son de 6-max.");
                else
                    StopUnsupported("NO NLHE", "La mesa no es de Hold'em sin limite: las tablas del entrenador son de NLHE 6-max.");
                return;
            }

            // El titulo dice quien es el heroe: una mano sin sus cartas es de otra cuenta y no se
            // adopta. Sin nombre en el titulo vale el "Dealt to" (en cash solo salen las propias).
            if (!hand.HasHero)
            {
                if (!string.IsNullOrWhiteSpace(_window.HeroName))
                    return;
                var fallback = TrainerHandParser.Parse(lines);
                if (fallback?.HasHero != true)
                    return;
                hand = fallback;
            }

            if (string.IsNullOrWhiteSpace(_heroName))
                _heroName = hand.HeroName;

            bool known;
            lock (_sync)
                known = !historical && _processedHands.Contains(hand.HandId);

            if (!(historical && _staleHistory))
            {
                lock (_sync)
                {
                    _lastHand = hand;
                    _predictedPosition = PredictNextPosition(hand);
                    _heroStackAfterHistory = hand.SeatOf(hand.HeroName) is { } heroSeat && hand.BigBlind > 0
                        ? (heroSeat.Stack + PokerStarsHandHistory.EstimateNetForPlayer(hand.Lines, hand.HeroName)) / hand.BigBlind
                        : null;
                }
            }

            if (historical)
                return;

            Interlocked.Exchange(ref _dealsWithoutHistory, 0);

            // Ya la corrigio la sesion que precedio a esta en la misma mesa: solo sirve para la
            // posicion y las stats (sin corregir, aprender ni anotar lo recomendado en vivo).
            if (known)
                return;
            var since = _lastHandArrival;
            _lastHandArrival = DateTime.Now;

            // Si llegan varias manos juntas solo la ultima es la que acaba de verse en pantalla.
            var learnNote = isLastOfBatch && _reader is { } reader && reader.Calibration.HasHero ? reader.LearnFromHand(hand, since) : "";

            // Congela el panel en vivo con las cartas de la mano que acaba de terminar; si en
            // pantalla ya hay otras (la siguiente mano se repartio antes de leer el historial),
            // no se congela nada.
            var shownHero = _lastHeroSlots;
            var finishedHero = hand.HeroCards.Select(TrainerCards.ToText).ToHashSet();
            var shownCards = (shownHero ?? "").Split('|').Where(card => card is not "-" and not "?").ToList();
            if (isLastOfBatch && shownHero is not null && shownCards.All(finishedHero.Contains))
                _frozenHeroSlots = shownHero;

            var report = _coach.Grade(hand, ProfileFor);
            report.TableName = _window.TableName;
            lock (_sync)
                _processedHands.Add(hand.HandId);
            if (isLastOfBatch)
            {
                AnnotateLiveRecommendations(report);
                TrainerReviewLog.Append(report);
            }
            HandGraded?.Invoke(report);
            StatusChanged?.Invoke(_reader is null
                ? $"Mano {hand.HandId} corregida."
                : $"Mano {hand.HandId} analizada. {learnNote} Plantillas: {_reader.Store.LearnedCards}/52 cartas. " +
                  $"Proxima posicion estimada: {PredictedPosition ?? "?"}.");
        }

        /// <summary>
        /// Una mano nueva repartida en pantalla. Si pasan dos sin que el historial avance, algo va
        /// mal (archivo nuevo del dia, mesa cambiada): la posicion deja de estimarse y se busca el
        /// archivo otra vez.
        /// </summary>
        private void NoteDeal(string heroSlots)
        {
            if (heroSlots == _lastDealKey)
                return;
            var dealt = !heroSlots.Contains('-') && _lastDealKey.Contains('-');
            _lastDealKey = heroSlots;
            if (!dealt)
                return;

            if (Interlocked.Increment(ref _dealsWithoutHistory) >= 3)
            {
                lock (_sync)
                    _predictedPosition = null;
                _nextResolve = DateTime.MinValue;
                StatusChanged?.Invoke("El historial no avanza; buscando el archivo nuevo de la mesa...");
            }
        }

        private TrainerVillainProfile? ProfileFor(string name)
        {
            _stats.Players.TryGetValue(name, out var session);
            session ??= _stats.Players.Values.FirstOrDefault(stats => PokerStarsHandHistory.SamePlayer(stats.Name, name));
            return TrainerVillainProfile.Best(name, session);
        }

        /// <summary>
        /// El boton pasa al siguiente asiento activo. Es una estimacion (alguien puede sentarse o
        /// irse entre manos), por eso la ventana la marca como "estimada".
        /// </summary>
        /// <summary>
        /// Posiciones de esta mano con lo que se ve en la mesa: el boton avanza desde el de la ultima mano
        /// del historial (una vez por mano repartida desde entonces) y solo cuentan los asientos que no
        /// estan "ausentes" ni vacios ahora. Si alguien se ausentaba o volvia entre manos la prediccion se
        /// recorria (Ks7h en la BB salia "HJ"). Preflop, si puedes pasar eres la BB y si pagar cuesta
        /// media ciega sin subidas eres la SB. Null si no hay historial o no se leyeron las seis placas.
        /// </summary>
        private (string? Hero, IReadOnlyDictionary<int, string> Plates)? LivePositions(CapturedFrame frame, LiveTurnState turn, string street)
        {
            TrainerHand? hand;
            lock (_sync)
                hand = _lastHand;
            var heroSeat = hand?.SeatOf(hand.HeroName)?.Seat;
            if (hand is null || heroSeat is null || hand.MaxSeats != 6 || turn.Plates.Count < 6)
                return null;

            int SeatOf(int plate) => (heroSeat.Value - 1 + plate) % 6 + 1;
            // Quien juega: los que jugaron la mano anterior (el historial sabe quien estaba ausente, aunque
            // la placa no diga "Ausente"), menos quien ahora se ve ausente o con el asiento libre, mas quien
            // volvio y tiene cartas. Contar a todas las placas metia a tres ausentes de Oceax V y la BB
            // salia "CO".
            var plateBySeat = turn.Plates.ToDictionary(plate => SeatOf(plate.Index));
            var active = hand.ActiveSeats.Select(seat => seat.Seat)
                .Where(seat => !(plateBySeat.TryGetValue(seat, out var plate) && plate.Index != 0 && (plate.SittingOut || plate.EmptySeat)))
                .Concat(turn.Plates.Where(plate => plate.Index != 0 && plate.HasCards).Select(plate => SeatOf(plate.Index)))
                // Quien ya actuo en esta mano (aunque se retirara y ya no tenga cartas) tambien juega: un
                // jugador nuevo que puso la ciega chica y se retiro no estaba en el historial, y sin el la
                // BB salia "SB" (J9o en Beatrix: "no ir" de la SB en vez de pagar desde la BB).
                .Concat(turn.Plates.Where(plate => plate.Index != 0 && plate.Action is not null && !plate.SittingOut && !plate.EmptySeat)
                    .Select(plate => SeatOf(plate.Index)))
                .Distinct();
            // El boton del dealer visto en la mesa manda; si no se ve, se deduce de la mano anterior.
            int? buttonNow = LiveTableReader.DealerPlate(frame) is { } dealerPlate ? SeatOf(dealerPlate) : null;
            return ComputeLivePositions(hand.ButtonSeat, heroSeat.Value, active, HandsSinceHistory,
                street == "PREFLOP", turn.CanCheck, turn.ToCallBb, buttonNow);
        }

        /// <summary>
        /// Calculo de <see cref="LivePositions"/>: boton anterior, asiento del heroe, asientos que juegan
        /// ahora y manos repartidas desde el historial. Devuelve la posicion del heroe y la de cada placa
        /// (placa 0 = heroe, en el sentido de las agujas del reloj).
        /// </summary>
        public static (string? Hero, IReadOnlyDictionary<int, string> Plates)? ComputeLivePositions(
            int previousButton, int heroSeat, IEnumerable<int> activeSeats, int steps, bool preflop, bool canCheck, double? toCall,
            int? buttonSeatNow = null)
        {
            var active = activeSeats.Append(heroSeat).Distinct().OrderBy(seat => seat).ToList();
            if (active.Count < 2)
                return null;

            var button = previousButton;
            if (buttonSeatNow is { } seenButton)
            {
                // Boton visto en la mesa: manda (aunque ese asiento este ausente, es el boton de la mano).
                if (!active.Contains(seenButton))
                    active = active.Append(seenButton).Distinct().OrderBy(seat => seat).ToList();
                button = seenButton;
            }
            else
            {
                for (var step = 0; step < Math.Max(1, steps); step++)
                {
                    var next = active.FirstOrDefault(seat => seat > button);
                    button = next != 0 ? next : active[0];
                }
            }

            var start = active.IndexOf(button);
            var plates = new Dictionary<int, string>();
            for (var offset = 0; offset < active.Count; offset++)
            {
                var seat = active[(start + offset) % active.Count];
                plates[(seat - heroSeat + 6) % 6] = TrainerHandParser.ChartPosition(offset, active.Count);
            }

            var hero = plates.GetValueOrDefault(0);
            if (preflop)
            {
                if (canCheck)
                    hero = "BB";
                else if (toCall is { } owed && Math.Abs(owed - 0.5) < 0.01)
                    hero = "SB";
            }
            return (hero, plates);
        }

        private static string? PredictNextPosition(TrainerHand hand, int steps = 1)
        {
            var heroSeat = hand.SeatOf(hand.HeroName)?.Seat;
            return heroSeat is { } seat && PredictSeatPositions(hand, steps).TryGetValue(seat, out var position) ? position : null;
        }

        /// <summary>
        /// Manos repartidas en pantalla desde la ultima del historial (al menos 1: la que se juega). Si
        /// el historial llega tarde y ya empezo otra mano, el boton avanzo mas de un asiento: antes se
        /// usaba la posicion de la mano anterior (99 en la BB salia "HJ").
        /// </summary>
        private int HandsSinceHistory => Math.Max(1, Volatile.Read(ref _dealsWithoutHistory));

        /// <summary>Posicion de cada asiento activo dentro de <paramref name="steps"/> manos (el boton avanza un asiento activo por mano).</summary>
        private static Dictionary<int, string> PredictSeatPositions(TrainerHand hand, int steps = 1)
        {
            var active = hand.ActiveSeats.OrderBy(seat => seat.Seat).ToList();
            var result = new Dictionary<int, string>();
            if (active.Count < 2)
                return result;

            var nextButton = active.FindIndex(seat => seat.Seat > hand.ButtonSeat);
            if (nextButton < 0)
                nextButton = 0;
            nextButton = (nextButton + Math.Max(0, steps - 1)) % active.Count;

            for (var i = 0; i < active.Count; i++)
            {
                var offset = (i - nextButton + active.Count) % active.Count;
                result[active[i].Seat] = TrainerHandParser.ChartPosition(offset, active.Count);
            }

            return result;
        }

        private void Block(string reason, bool byTitle = false)
        {
            if (_blocked)
                return;

            _blocked = true;
            BlockedByTitle = byTitle;
            Cancel();
            Blocked?.Invoke(reason);
        }

        /// <summary>Cancela los bucles; si la sesion ya se libero, el aviso tiene que salir igual.</summary>
        private void Cancel()
        {
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        /// <summary>La mesa ya no esta (ventana cerrada u otra mesa en ella): se para sin bloqueo.</summary>
        private void Close(string reason)
        {
            if (_blocked)
                return;

            _blocked = true;
            Cancel();
            Closed?.Invoke(reason);
        }

        private void StopUnsupported(string label, string reason)
        {
            if (_blocked)
                return;

            _blocked = true;
            Cancel();
            Unsupported?.Invoke(label, reason);
        }

        private async Task RunSafely(Func<CancellationToken, Task> loop)
        {
            try
            {
                await loop(_cts.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"Error en el modo Entrenador: {ex.Message}");
            }
        }

        /// <summary>
        /// Lee lo que se va anadiendo al historial. La primera lectura trae el archivo entero (se usa
        /// para stats y posicion, no se califica); despues, solo lineas completas nuevas.
        /// </summary>
        private sealed class AppendedTextReader
        {
            private readonly string _path;
            private long _position;
            private Encoding _encoding = Encoding.UTF8;
            private byte[] _newline = { (byte)'\n' };

            public AppendedTextReader(string path) => _path = path;

            public string Path => _path;

            /// <summary>Bytes ya consumidos (lineas completas).</summary>
            public long Position => _position;

            /// <summary>Lineas completas nuevas; con <paramref name="limit"/> no lee mas alla de ese byte.</summary>
            public List<string> ReadNewLines(long? limit = null)
            {
                var result = new List<string>();
                if (!File.Exists(_path))
                    return result;

                using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length < _position)
                    _position = 0;
                var available = limit is { } max ? Math.Min(stream.Length, max) : stream.Length;
                if (available <= _position)
                    return result;

                stream.Seek(_position, SeekOrigin.Begin);
                var bytes = new byte[available - _position];
                var filled = 0;
                while (filled < bytes.Length)
                {
                    var read = stream.Read(bytes, filled, bytes.Length - filled);
                    if (read <= 0) break;
                    filled += read;
                }

                var start = 0;
                if (_position == 0)
                {
                    (_encoding, start) = DetectEncoding(bytes);
                    _newline = _encoding.GetBytes("\n");
                }

                var end = LastNewlineEnd(bytes, start, filled);
                if (end <= start)
                    return result;

                var text = _encoding.GetString(bytes, start, end - start);
                _position += end;

                foreach (var line in text.Split('\n'))
                {
                    var clean = line.TrimEnd('\r');
                    if (clean.Length > 0)
                        result.Add(clean);
                }

                return result;
            }

            private int LastNewlineEnd(byte[] bytes, int start, int filled)
            {
                for (var i = filled - _newline.Length; i >= start; i--)
                {
                    var match = true;
                    for (var j = 0; j < _newline.Length; j++)
                    {
                        if (bytes[i + j] != _newline[j])
                        {
                            match = false;
                            break;
                        }
                    }

                    if (match && (i - start) % _newline.Length == 0)
                        return i + _newline.Length;
                }

                return -1;
            }

            private static (Encoding Encoding, int Skip) DetectEncoding(byte[] bytes)
            {
                if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                    return (Encoding.UTF8, 3);
                if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                    return (Encoding.Unicode, 2);
                if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                    return (Encoding.BigEndianUnicode, 2);
                return (Encoding.UTF8, 0);
            }
        }
    }
}
