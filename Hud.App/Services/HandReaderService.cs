using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel; // INotifyPropertyChanged
using System.IO;
using System.Linq;
using System.Text.RegularExpressions; // Regex
using System.Threading.Tasks;
using System.Windows; // Dispatcher (App.Current.Dispatcher)
using System.Globalization;      // <-- para CultureInfo, NumberStyles

// CORE v1.0.4
using HandReader.Core.IO;        // FileTailReader
using HandReader.Core.Parsing;   // PokerStarsParser
using HandReader.Core.Stats;     // StatsAggregator
using HandReader.Core.Models;    // PlayerStats

namespace Hud.App.Services
{


    /// <summary>
    /// Servicio que:
    ///  - Lee histÃ³rico completo al inicio (si existe).
    ///  - Hace tail en tiempo real por lotes (OnLines -> FeedLines).
    ///  - Publica jugadores solo de la Ãºltima mano (CurrentTableOrder).
    ///  - Detecta automÃ¡ticamente el HÃ‰ROE a partir de "Dealt to ...".
    /// </summary>
    public sealed class HandReaderService : IDisposable, INotifyPropertyChanged
    {




        private static readonly Regex BlindsRx =
            new(@"\((?<sb>" + PokerAmountParser.BlindAmountPattern + @")\s*/\s*(?<bb>" + PokerAmountParser.BlindAmountPattern + @")(?:\s+[A-Z]{3})?\)",
                RegexOptions.Compiled);


        private static readonly Regex DealtToRx =
            new Regex(@"^(?:Dealt to|Repartido a)\s+(.+?)\s+\[", RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase);


        private StatsAggregator _agg = new();
        private PokerStarsParser _parser;
        private FileTailReader? _tail;

        /// <summary>ColecciÃ³n observable para la grilla del HUD.</summary>
        public ObservableCollection<PlayerStats> Players { get; }

        /// <summary>Asiento de cada jugador de la ultima mano (para el HUD sobre la mesa).</summary>
        public IReadOnlyList<TableSeat> Seats { get; private set; } = Array.Empty<TableSeat>();

        /// <summary>Asientos de la mesa (2, 6, 9 o 10).</summary>
        public int TableSize { get; private set; } = 6;

        /// <summary>La ultima mano terminada y cuando llego (para el repaso en la mesa).</summary>
        public IReadOnlyList<string> LastHandLines { get; private set; } = Array.Empty<string>();
        public DateTime LastHandAt { get; private set; }

        private string? _heroName;
        /// <summary>Nombre del HÃ©roe para resaltar en la UI.</summary>
        public string? HeroName
        {
            get => _heroName;
            set
            {
                if (!string.Equals(_heroName, value, StringComparison.Ordinal))
                {
                    _heroName = value;
                    OnPropertyChanged(nameof(HeroName));
                }
            }
        }


        private StakeProfile DetectStakeFromFile(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var rd = new StreamReader(fs);
                for (int i = 0; i < 200 && !rd.EndOfStream; i++)
                {
                    var l = rd.ReadLine() ?? "";
                    var m = BlindsRx.Match(l);
                    if (!m.Success) continue;

                    if (!PokerAmountParser.TryParse(m.Groups["bb"].Value, out var bb)) continue;

                    return StakeProfileRules.FromBigBlind(bb, m.Groups["bb"].Value);
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"deteccion de stake fallo en {path}: {ex.Message}");
            }

            return StakeProfile.Low;
        }

        public HandReaderService()
        {
            _parser = new PokerStarsParser(_agg);
            Players = new ObservableCollection<PlayerStats>();
        }

        // --------------- Ciclo de vida ---------------

        public void Start(string path)
        {
            Stop();
            ResetState(clearHero: false);

            var newStake = DetectStakeFromFile(path);
            if (newStake != Stake)
            {
                Stake = newStake;
                OnPropertyChanged(nameof(Stake));
            }

            // 1) Historico + tiempo real: FileTailReader arranca en el byte 0, asi que el
            //    propio tail entrega primero todo el archivo existente y luego lo que se vaya
            //    escribiendo. Leerlo aparte antes de arrancarlo alimentaba el parser dos veces
            //    con las mismas manos y duplicaba cada estadistica del HUD.

            _tail = new FileTailReader(path);
            _tail.OnLines += OnTailLines;
            _tail.OnError += OnTailError;
            _tail.Start();

            PublishSnapshot(); // inicial
        }

        public void Stop()
        {
            try
            {
                if (_tail != null)
                {
                    _tail.OnLines -= OnTailLines;
                    _tail.OnError -= OnTailError;
                    _tail.Dispose();
                    _tail = null;
                }
            }
            catch { /* ignore */ }
        }

        public void StopAndClear()
        {
            Stop();
            ResetState(clearHero: true);
        }

        public void Dispose() => Stop();

        // --------------- AlimentaciÃ³n manual (tests) ---------------

        public void FeedLine(string line)
        {
            // Detecta hÃ©roe si aÃºn no estÃ¡
            if (string.IsNullOrWhiteSpace(HeroName))
                DetectHeroFromLines(new[] { line });

            FeedAndPublish(new[] { line });
        }

        private static void OnTailError(Exception ex) =>
            AppLog.Warn("HUD: no se pudo leer el historial en vivo", ex);

        private void OnTailLines(string[] lines)
        {
            // Detecta hÃ©roe si aÃºn no estÃ¡
            if (string.IsNullOrWhiteSpace(HeroName))
                DetectHeroFromLines(lines);

            FeedAndPublish(lines);
        }

        private void FeedAndPublish(IEnumerable<string> lines)
        {
            // Una publicacion por lote y no por mano: el historico entra por el tail en lotes de
            // miles de manos y publicar cada una encolaba miles de vaciados y rellenos de la
            // grilla que saturaban el hilo de la UI. En el finally para que, si el parser falla
            // a mitad de lote, las manos ya procesadas lleguen igual a la grilla.
            var rendered = false;
            try
            {
                _parser.FeedLines(lines, () => rendered = true);
            }
            finally
            {
                if (rendered)
                    PublishSnapshot();
            }
        }

        // --------------- PublicaciÃ³n a la UI ---------------

        private void PublishSnapshot()
        {
            // Capturar el agregador una sola vez: ResetState lo sustituye y leerlo dos veces
            // podria mezclar la tabla de un archivo con el orden de asientos de otro.
            var agg = _agg;

            // Solo los de la ultima mano, en orden por asiento
            var byName = agg.Players;
            var tableOrder = new List<PlayerStats>(StatsAggregator.MaxSeats);
            foreach (var name in agg.CurrentTableOrder)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (byName.TryGetValue(name, out var ps))
                    tableOrder.Add(ps);
            }

            // InvokeAsync y no Invoke: esto corre en el hilo del tail y Dispose() espera a ese
            // hilo desde la UI, asi que un Invoke sincrono cierra el circulo del interbloqueo.
            var seats = agg.CurrentSeats.ToArray();
            var tableSize = agg.TableSize;
            var lastHand = _parser.LastHandLines;
            Application.Current?.Dispatcher?.InvokeAsync(() =>
            {
                Players.Clear();
                foreach (var ps in tableOrder) Players.Add(ps);
                Seats = seats;
                TableSize = tableSize;
                if (!ReferenceEquals(LastHandLines, lastHand) && lastHand.Count > 0)
                {
                    LastHandLines = lastHand;
                    LastHandAt = DateTime.Now;
                }
            });
        }

        // --------------- DetecciÃ³n de HÃ‰ROE ---------------

        private void DetectHeroFromLines(IEnumerable<string> lines)
        {
            // Primer match "Dealt to <Name> [..]"
            foreach (var l in lines)
            {
                var m = DealtToRx.Match(l);
                if (m.Success)
                {
                    HeroName = m.Groups[1].Value.Trim();
                    break;
                }
            }
        }

        private void ResetState(bool clearHero)
        {
            _agg = new StatsAggregator();
            _parser = new PokerStarsParser(_agg);
            Stake = StakeProfile.Low;

            // El heroe se limpia aqui mismo y no en el dispatcher: encolado podia ejecutarse
            // cuando el tail nuevo ya lo habia usado (o ya lo habia fijado quien arranca la
            // mesa) y dejaba la sesion nueva sin heroe.
            if (clearHero)
                HeroName = null;

            // La grilla si se vacia en cola, como publica PublishSnapshot: el dispatcher ejecuta
            // en orden lo de igual prioridad, asi que el vaciado llega despues de lo que el tail
            // anterior dejo pendiente y antes de lo del nuevo. Con Invoke se vaciaria al instante
            // y una publicacion vieja aun en cola repintaria la mesa anterior.
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null)
                dispatcher.InvokeAsync(() => Players.Clear());
            else
                Players.Clear(); // sin WPF (consola/tests)

            OnPropertyChanged(nameof(Stake));
        }

        // --------------- INotifyPropertyChanged ---------------
        public StakeProfile Stake { get; set; } = StakeProfile.Low;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

}

