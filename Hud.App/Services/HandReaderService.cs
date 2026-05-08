using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel; // INotifyPropertyChanged
using System.IO;
using System.Linq;
using System.Reflection;
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
    ///  - Lee histórico completo al inicio (si existe).
    ///  - Hace tail en tiempo real por lotes (OnLines -> FeedLines).
    ///  - Publica jugadores solo de la última mano (CurrentTableOrder).
    ///  - Detecta automáticamente el HÉROE a partir de "Dealt to ...".
    /// </summary>
    public sealed class HandReaderService : IDisposable, INotifyPropertyChanged
    {




        private static readonly Regex BlindsRx =
            new(@"\((?<sb>" + PokerAmountParser.BlindAmountPattern + @")\s*/\s*(?<bb>" + PokerAmountParser.BlindAmountPattern + @")(?:\s+[A-Z]{3})?\)",
                RegexOptions.Compiled);


        private static readonly Regex DealtToRx =
            new Regex(@"^(?:Dealt to|Repartido a)\s+(.+?)\s+\[", RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase);


        private StatsAggregator _agg = new();
        private ParserAdapter _adapter;
        private FileTailReader? _tail;

        /// <summary>Colección observable para la grilla del HUD.</summary>
        public ObservableCollection<PlayerStats> Players { get; }

        private string? _heroName;
        /// <summary>Nombre del Héroe para resaltar en la UI.</summary>
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

                    if (PokerAmountParser.TryParse(m.Groups["bb"].Value, out var parsedBb))
                    {
                        if (parsedBb <= 0.10) return StakeProfile.Low;   // NL2-NL25 aprox
                        if (parsedBb >= 2.00) return StakeProfile.High; // NL500+
                        return StakeProfile.Mid;                   // NL50-NL200
                    }

                    var bbTxt = m.Groups["bb"].Value
                        .Replace("$", "")
                        .Replace("€", "")
                        .Replace("US", "", StringComparison.OrdinalIgnoreCase)
                        .Replace("\u00A0", "")
                        .Replace(" ", "")
                        .Replace(",", ".");
                    if (double.TryParse(bbTxt, NumberStyles.Number, CultureInfo.InvariantCulture, out var bb))
                    {
                        if (bb <= 0.10) return StakeProfile.Low;   // NL2–NL25 aprox
                        if (bb >= 2.00) return StakeProfile.High; // NL500+
                        return StakeProfile.Mid;                   // NL50–NL200
                    }
                }
            }
            catch { /* ignore */ }
            return StakeProfile.Low;
        }


        public HandReaderService()
        {
            _adapter = new ParserAdapter(_agg);
            Players = new ObservableCollection<PlayerStats>();
        }

        // --------------- Ciclo de vida ---------------

        public void Start(string path)
        {
            Stop();
            ResetState(clearHero: false);

            var newStake = DetectStakeFromFile(path);
            if (newStake != Stake) {
        Stake = newStake;
        OnPropertyChanged(nameof(Stake));
    }


                // 1) Histórico inicial (si existe) + autodetección del HÉROE
                if (File.Exists(path))
                {
                    try
                    {
                        IEnumerable<string> all = File.ReadLines(path);

                        // Detecta héroe si aún no está
                        if (string.IsNullOrWhiteSpace(HeroName))
                            DetectHeroFromLines(all);

                        _adapter.FeedLines(all, PublishSnapshot);
                    }
                    catch
                    {
                        // opcional: log
                    }
                }

            // 2) Tiempo real
            _tail = new FileTailReader(path);
            _tail.OnLines += OnTailLines;
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

        // --------------- Alimentación manual (tests) ---------------

        public void FeedLine(string line)
        {
            // Detecta héroe si aún no está
            if (string.IsNullOrWhiteSpace(HeroName))
                DetectHeroFromLines(new[] { line });

            _adapter.FeedLines(new[] { line }, PublishSnapshot);
        }

        private void OnTailLines(string[] lines)
        {
            // Detecta héroe si aún no está
            if (string.IsNullOrWhiteSpace(HeroName))
                DetectHeroFromLines(lines);

            _adapter.FeedLines(lines, PublishSnapshot);
        }

        // --------------- Publicación a la UI ---------------

        private void PublishSnapshot()
        {
            var snapshot = _adapter.GetPlayersSnapshot();
            if (snapshot == null) return;

            // Solo los de la última mano, en orden por asiento
            var byName = snapshot.ToDictionary(p => p.Name, StringComparer.Ordinal);
            var tableOrder = new List<PlayerStats>(6);
            foreach (var name in _agg.CurrentTableOrder)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (byName.TryGetValue(name, out var ps))
                    tableOrder.Add(ps);
            }

            Application.Current?.Dispatcher?.Invoke(() =>
            {
                Players.Clear();
                foreach (var ps in tableOrder) Players.Add(ps);
            });
        }

        // --------------- Detección de HÉROE ---------------

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
            _adapter = new ParserAdapter(_agg);
            Stake = StakeProfile.Low;

            Application.Current?.Dispatcher?.Invoke(() =>
            {
                Players.Clear();
                if (clearHero)
                    HeroName = null;
            });

            OnPropertyChanged(nameof(Stake));
        }

        // ==================== ParserAdapter ====================

        private sealed class ParserAdapter
        {
            private readonly StatsAggregator _agg;
            private readonly object _parser;

            private readonly MethodInfo? _feedLines;  // (IEnumerable<string>, Action)
            private readonly MethodInfo? _feedOneStr; // (string)
            private readonly MethodInfo? _feedStrAgg; // (string, StatsAggregator)
            private readonly Func<IEnumerable<PlayerStats>?> _getPlayers;

            public ParserAdapter(StatsAggregator agg)
            {
                _agg = agg;

                var parserType = typeof(PokerStarsParser);
                _parser = Activator.CreateInstance(parserType, _agg)
                          ?? throw new InvalidOperationException("No se pudo instanciar PokerStarsParser.");

                _feedLines = parserType.GetMethod(
                    "FeedLines",
                    BindingFlags.Instance | BindingFlags.Public,
                    binder: null,
                    types: new[] { typeof(IEnumerable<string>), typeof(Action) },
                    modifiers: null
                );

                _feedOneStr = parserType.GetMethod(
                    "ParseLine",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase
                ) ?? parserType.GetMethod(
                    "Feed",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase,
                    binder: null,
                    types: new[] { typeof(string) },
                    modifiers: null
                ) ?? parserType.GetMethod(
                    "OnLine",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase,
                    binder: null,
                    types: new[] { typeof(string) },
                    modifiers: null
                );

                _feedStrAgg = parserType.GetMethod(
                    "Parse",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase,
                    binder: null,
                    types: new[] { typeof(string), typeof(StatsAggregator) },
                    modifiers: null
                ) ?? parserType.GetMethod(
                    "Feed",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase,
                    binder: null,
                    types: new[] { typeof(string), typeof(StatsAggregator) },
                    modifiers: null
                );

                _getPlayers = ResolvePlayersAccessor(_agg);
            }

            public void FeedLines(IEnumerable<string> lines, Action onRendered)
            {
                if (_feedLines != null)
                {
                    _feedLines.Invoke(_parser, new object?[] { lines, onRendered });
                    return;
                }
                foreach (var line in lines) FeedLine(line);
                onRendered();
            }

            public void FeedLine(string line)
            {
                if (_feedStrAgg != null) { _feedStrAgg.Invoke(_parser, new object?[] { line, _agg }); return; }
                if (_feedOneStr != null) { _feedOneStr.Invoke(_parser, new object?[] { line }); return; }
            }

            public IEnumerable<PlayerStats>? GetPlayersSnapshot() => _getPlayers();

            private static Func<IEnumerable<PlayerStats>?> ResolvePlayersAccessor(StatsAggregator agg)
            {
                var prop = agg.GetType().GetProperty("Players", BindingFlags.Instance | BindingFlags.Public);
                if (prop != null)
                {
                    return () =>
                    {
                        var val = prop.GetValue(agg);
                        return ExtractPlayers(val);
                    };
                }
                return () => ExtractPlayers(agg);
            }

            private static IEnumerable<PlayerStats>? ExtractPlayers(object? maybe)
            {
                if (maybe == null) return null;
                if (maybe is IEnumerable<PlayerStats> direct) return direct;

                if (maybe is IEnumerable en)
                {
                    var list = new List<PlayerStats>();
                    foreach (var item in en)
                    {
                        if (item is PlayerStats ps) { list.Add(ps); continue; }

                        var t = item?.GetType();
                        var valueProp = t?.GetProperty("Value", BindingFlags.Instance | BindingFlags.Public);
                        var val = valueProp?.GetValue(item);
                        if (val is PlayerStats ps2) { list.Add(ps2); continue; }

                        var inner = t?.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                                     .Select(p => p.GetValue(item))
                                     .FirstOrDefault(v => v is PlayerStats) as PlayerStats;
                        if (inner != null) list.Add(inner);
                    }
                    return list;
                }

                var props = maybe.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public);
                foreach (var p in props)
                {
                    var val = p.GetValue(maybe);
                    var extracted = ExtractPlayers(val);
                    if (extracted != null) return extracted;
                }
                return null;
            }
        }

        // --------------- INotifyPropertyChanged ---------------
        public StakeProfile Stake { get; set; } = StakeProfile.Low;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

}
