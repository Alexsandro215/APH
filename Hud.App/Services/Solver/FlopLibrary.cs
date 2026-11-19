using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Hud.App.Services.Simulator;
using Hud.App.Services.Trainer;

namespace Hud.App.Services.Solver
{
    /// <summary>Estrategia guardada de un nodo del flop: por mano (del rango de quien decide) la frecuencia de cada accion (0-255).</summary>
    public sealed record FlopNodeStrategy(string Path, int Player, string[] Codes, float[] To, byte[] Frequencies);

    /// <summary>Lo que se guarda de un flop: las manos de cada rango (indice de SimCombos) y cada nodo del flop.</summary>
    public sealed record FlopStrategyTable(ushort[][] Combos, FlopNodeStrategy[] Nodes)
    {
        private const byte Version = 1;

        public void Write(Stream stream)
        {
            using var gzip = new GZipStream(stream, CompressionLevel.SmallestSize, leaveOpen: true);
            using var writer = new BinaryWriter(gzip);
            writer.Write(Version);
            for (var p = 0; p < 2; p++)
            {
                writer.Write((ushort)Combos[p].Length);
                foreach (var combo in Combos[p]) writer.Write(combo);
            }
            writer.Write((byte)Nodes.Length);
            foreach (var node in Nodes)
            {
                writer.Write(node.Path);
                writer.Write((byte)node.Player);
                writer.Write((byte)node.Codes.Length);
                foreach (var code in node.Codes) writer.Write(code);
                foreach (var to in node.To) writer.Write(to);
                writer.Write(node.Frequencies);
            }
        }

        public static FlopStrategyTable Read(Stream stream)
        {
            using var gzip = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true);
            using var reader = new BinaryReader(gzip);
            if (reader.ReadByte() != Version)
                throw new InvalidDataException("Version de la biblioteca del flop desconocida.");
            var combos = new ushort[2][];
            for (var p = 0; p < 2; p++)
            {
                combos[p] = new ushort[reader.ReadUInt16()];
                for (var i = 0; i < combos[p].Length; i++) combos[p][i] = reader.ReadUInt16();
            }
            var nodes = new FlopNodeStrategy[reader.ReadByte()];
            for (var k = 0; k < nodes.Length; k++)
            {
                var path = reader.ReadString();
                var player = reader.ReadByte();
                var codes = new string[reader.ReadByte()];
                for (var a = 0; a < codes.Length; a++) codes[a] = reader.ReadString();
                var to = new float[codes.Length];
                for (var a = 0; a < codes.Length; a++) to[a] = reader.ReadSingle();
                var frequencies = reader.ReadBytes(combos[player].Length * codes.Length);
                nodes[k] = new FlopNodeStrategy(path, player, codes, to, frequencies);
            }
            return new FlopStrategyTable(combos, nodes);
        }
    }

    /// <summary>Un bote subido estandar: quien abre y la ciega grande paga, 100 bb.</summary>
    public sealed record FlopSpot(string Opener, double Pot, double Stack)
    {
        /// <summary>La ciega pequena habla primero despues del flop; contra las demas, la ciega grande.</summary>
        public bool OpenerIsOop => Opener == "SB";
    }

    public readonly record struct FlopLibraryProgress(int Done, int Total, bool Running, TimeSpan? Remaining, string? Error);

    /// <summary>
    /// Biblioteca GTO del flop: el equilibrio de cada uno de los 1.755 flops distintos (por palos)
    /// en los botes subidos estandar contra la ciega grande (LJ, HJ, CO, BTN y SB, 100 bb), con los
    /// rangos de las tablas APH. Se calcula de antemano (de noche) y se consulta al instante en vivo.
    /// Un archivo por posicion en %APPDATA%\APH\gto-flop; cada flop se anade al terminar, asi que se
    /// puede parar y seguir otro dia.
    /// </summary>
    public static class FlopLibrary
    {
        /// <summary>Vueltas por flop: con 100 queda a ~0,2% del bote del equilibrio de la abstraccion.</summary>
        public const int Iterations = 100;

        public static readonly FlopSpot[] Spots =
        {
            new("BTN", 5.5, 97.5),
            new("CO", 5.5, 97.5),
            new("SB", 6.0, 97.0),
            new("HJ", 5.5, 97.5),
            new("LJ", 5.5, 97.5),
        };

        private static readonly byte[] Magic = "APHF"u8.ToArray();
        private static readonly object Gate = new();
        private static readonly Dictionary<string, Dictionary<int, (long Offset, int Length)>> Indexes = new();
        private static readonly Dictionary<(string, int), FlopStrategyTable> Cache = new();
        private static readonly Queue<(string, int)> CacheOrder = new();
        private static string? _folderOverride;

        public static string Folder => _folderOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "APH", "gto-flop");

        /// <summary>Para las pruebas: otra carpeta (y olvida lo cargado).</summary>
        public static void UseFolder(string? folder)
        {
            lock (Gate)
            {
                _folderOverride = folder;
                Indexes.Clear();
                Cache.Clear();
                CacheOrder.Clear();
            }
        }

        // ------------------------------------------------------------------ flops canonicos

        private static readonly Lazy<int[][]> Permutations = new(() =>
        {
            var list = new List<int[]>();
            for (var a = 0; a < 4; a++)
                for (var b = 0; b < 4; b++)
                    for (var c = 0; c < 4; c++)
                        for (var d = 0; d < 4; d++)
                            if (new[] { a, b, c, d }.Distinct().Count() == 4)
                                list.Add(new[] { a, b, c, d });
            return list.ToArray();
        });

        private static readonly Lazy<int[]> CanonicalKeys = new(() =>
        {
            var keys = new HashSet<int>();
            for (var a = 0; a < 52; a++)
                for (var b = a + 1; b < 52; b++)
                    for (var c = b + 1; c < 52; c++)
                        keys.Add(Canonical(new[] { a, b, c }).Key);
            return keys.OrderBy(k => k).ToArray();
        });

        /// <summary>Los 1.755 flops distintos (por palos), como clave.</summary>
        public static IReadOnlyList<int> CanonicalFlops => CanonicalKeys.Value;

        public static int TotalFlops => Spots.Length * CanonicalKeys.Value.Length;

        public static int[] FlopOf(int key) => new[] { key / 2704, key / 52 % 52, key % 52 };

        /// <summary>
        /// La forma canonica del flop: se prueban los 24 cambios de palos y se queda la menor (con las
        /// cartas de mayor a menor). Devuelve tambien el cambio de palos, para pasar las cartas del heroe.
        /// </summary>
        public static (int Key, int[] SuitMap) Canonical(IReadOnlyList<int> flop)
        {
            var best = int.MaxValue;
            int[] bestMap = Permutations.Value[0];
            Span<int> mapped = stackalloc int[3];
            foreach (var map in Permutations.Value)
            {
                for (var i = 0; i < 3; i++)
                    mapped[i] = TrainerCards.Rank(flop[i]) * 4 + map[TrainerCards.Suit(flop[i])];
                mapped.Sort();
                var key = mapped[2] * 2704 + mapped[1] * 52 + mapped[0];
                if (key < best)
                {
                    best = key;
                    bestMap = map;
                }
            }
            return (best, bestMap);
        }

        public static int MapCard(int card, int[] suitMap) => TrainerCards.Rank(card) * 4 + suitMap[TrainerCards.Suit(card)];

        // ------------------------------------------------------------------ archivo

        private static string FileOf(string opener) => Path.Combine(Folder, $"{opener}.bin");

        private static Dictionary<int, (long Offset, int Length)> IndexOf(string opener)
        {
            if (Indexes.TryGetValue(opener, out var index))
                return index;
            index = new Dictionary<int, (long, int)>();
            var path = FileOf(opener);
            if (File.Exists(path))
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new BinaryReader(stream);
                if (stream.Length >= 4 && reader.ReadBytes(4).AsSpan().SequenceEqual(Magic))
                {
                    while (stream.Position + 8 <= stream.Length)
                    {
                        var key = reader.ReadInt32();
                        var length = reader.ReadInt32();
                        if (length <= 0 || stream.Position + length > stream.Length)
                            break;   // registro a medias (se corto la luz): se ignora
                        index[key] = (stream.Position, length);
                        stream.Seek(length, SeekOrigin.Current);
                    }
                }
            }
            Indexes[opener] = index;
            return index;
        }

        public static int Count(string opener)
        {
            lock (Gate) return IndexOf(opener).Count;
        }

        public static int CountAll()
        {
            lock (Gate) return Spots.Sum(spot => IndexOf(spot.Opener).Count);
        }

        public static bool Has(string opener, int key)
        {
            lock (Gate) return IndexOf(opener).ContainsKey(key);
        }

        public static void Save(string opener, int key, FlopStrategyTable table)
        {
            using var buffer = new MemoryStream();
            table.Write(buffer);
            var payload = buffer.ToArray();
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                var index = IndexOf(opener);
                var path = FileOf(opener);
                using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                // Si quedo un registro a medias al final, se escribe encima.
                long end;
                if (index.Count == 0)
                {
                    stream.SetLength(0);
                    stream.Write(Magic);
                    end = 4;
                }
                else
                {
                    end = index.Values.Max(v => v.Offset + v.Length);
                }
                stream.SetLength(end);
                stream.Seek(end, SeekOrigin.Begin);
                using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
                writer.Write(key);
                writer.Write(payload.Length);
                writer.Write(payload);
                index[key] = (end + 8, payload.Length);
            }
        }

        public static FlopStrategyTable? Load(string opener, int key)
        {
            lock (Gate)
            {
                if (Cache.TryGetValue((opener, key), out var cached))
                    return cached;
                if (!IndexOf(opener).TryGetValue(key, out var entry))
                    return null;
                var bytes = new byte[entry.Length];
                using (var stream = new FileStream(FileOf(opener), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    stream.Seek(entry.Offset, SeekOrigin.Begin);
                    stream.ReadExactly(bytes);
                }
                var table = FlopStrategyTable.Read(new MemoryStream(bytes));
                Cache[(opener, key)] = table;
                CacheOrder.Enqueue((opener, key));
                while (CacheOrder.Count > 24)
                    Cache.Remove(CacheOrder.Dequeue());
                return table;
            }
        }

        // ------------------------------------------------------------------ consulta en vivo

        /// <summary>
        /// La jugada del heroe en el flop segun la biblioteca, o null si no esta ese flop, la mano no
        /// esta en el rango de la tabla o alguna accion del rival no se parece a los tamanos guardados
        /// (entonces se resuelve en vivo). La historia va en bb reales (como en PostflopGtoSolver); las
        /// acciones devueltas se escalan al bote real.
        /// </summary>
        public static GtoSolution? Lookup(string opener, IReadOnlyList<int> flop, int card1, int card2,
            IReadOnlyList<GtoAction> history, double potStart, double stack)
        {
            var spot = Spots.FirstOrDefault(s => s.Opener == opener);
            if (spot is null || flop.Count != 3)
                return null;
            var (key, suitMap) = Canonical(flop);
            var table = Load(opener, key);
            if (table is null)
                return null;
            var nodes = table.Nodes.ToDictionary(n => n.Path);

            // Se recorre la historia con los importes reales y los de la biblioteca a la vez.
            var path = "";
            double a0 = 0, a1 = 0, l0 = 0, l1 = 0;
            foreach (var real in history)
            {
                if (!nodes.TryGetValue(path, out var node))
                    return null;
                var player = node.Player;
                var mineA = player == 0 ? a0 : a1;
                var mineL = player == 0 ? l0 : l1;
                var potA = potStart + a0 + a1;
                var potL = spot.Pot + l0 + l1;
                var currentA = Math.Max(a0, a1);
                var currentL = Math.Max(l0, l1);
                var code = MatchCode(node, real, mineA, mineL, potA, potL, currentA, currentL, stack);
                if (code < 0)
                    return null;
                if (player == 0) { a0 = real.To; l0 = node.To[code]; }
                else { a1 = real.To; l1 = node.To[code]; }
                path = path.Length == 0 ? node.Codes[code] : path + "-" + node.Codes[code];
            }

            if (!nodes.TryGetValue(path, out var target))
                return null;
            var actor = target.Player;
            var combo = (ushort)SimCombos.IndexOf(MapCard(card1, suitMap), MapCard(card2, suitMap));
            var h = Array.IndexOf(table.Combos[actor], combo);
            if (h < 0)
                return null;

            var actions = target.Codes.Length;
            var total = 0.0;
            for (var a = 0; a < actions; a++) total += target.Frequencies[h * actions + a];
            var mine = actor == 0 ? a0 : a1;
            var current = Math.Max(a0, a1);
            var potNow = potStart + a0 + a1;
            var mineLib = actor == 0 ? l0 : l1;
            var currentLib = Math.Max(l0, l1);
            var potLib = spot.Pot + l0 + l1;
            var choices = new List<GtoChoice>();
            for (var a = 0; a < actions; a++)
            {
                var frequency = total > 0 ? target.Frequencies[h * actions + a] / total : 1.0 / actions;
                var action = target.Codes[a] switch
                {
                    "x" => new GtoAction(GtoKind.Check, mine),
                    "f" => new GtoAction(GtoKind.Fold, mine),
                    "c" => new GtoAction(GtoKind.Call, Math.Min(current, stack), current >= stack - 0.01),
                    "a" => new GtoAction(current > mine + 0.01 ? GtoKind.Raise : GtoKind.Bet, stack, AllIn: true),
                    "r" => new GtoAction(GtoKind.Raise, Math.Min(stack, Math.Round(current * target.To[a] / Math.Max(0.01, currentLib), 1)), false),
                    _ => new GtoAction(GtoKind.Bet, Math.Min(stack, Math.Round(mine + (target.To[a] - mineLib) / potLib * potNow, 1)), false)
                };
                if (action.To >= stack - 0.01)
                    action = action with { To = stack, AllIn = true };
                choices.Add(new GtoChoice(action, frequency, double.NaN));
            }
            return new GtoSolution(actor, choices, Iterations, TimeSpan.Zero);
        }

        /// <summary>Que accion guardada corresponde a la real: igual tipo y tamano parecido (en proporcion del bote).</summary>
        private static int MatchCode(FlopNodeStrategy node, GtoAction real, double mineA, double mineL, double potA, double potL,
            double currentA, double currentL, double stack)
        {
            int Find(string code) => Array.IndexOf(node.Codes, code);
            switch (real.Kind)
            {
                case GtoKind.Check: return Find("x");
                case GtoKind.Call: return Find("c");
                case GtoKind.Fold: return -1;
            }

            if (real.AllIn || real.To >= stack * 0.6)
                return Find("a");

            if (real.Kind == GtoKind.Raise || currentA > mineA + 0.01)
            {
                var r = Find("r");
                if (r < 0) return -1;
                var ratio = real.To / Math.Max(0.01, currentA);
                var libRatio = node.To[r] / Math.Max(0.01, currentL);
                return Math.Abs(ratio - libRatio) <= 1.0 ? r : -1;
            }

            var fraction = (real.To - mineA) / Math.Max(0.01, potA);
            var best = -1;
            var bestDiff = double.MaxValue;
            for (var a = 0; a < node.Codes.Length; a++)
            {
                if (!node.Codes[a].StartsWith('b')) continue;
                var diff = Math.Abs((node.To[a] - mineL) / potL - fraction);
                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    best = a;
                }
            }
            return bestDiff <= 0.15 ? best : -1;
        }

        // ------------------------------------------------------------------ calculo

        /// <summary>Los rangos de la tabla APH para un bote subido contra la ciega grande (OOP, IP).</summary>
        public static (HandRange Oop, HandRange Ip) RangesFor(FlopSpot spot, TrainerRangeEstimator ranges)
        {
            var open = ranges.PreflopRangeFor(spot.Opener, TrainerActionKind.Raise, 0, "-", "-", null);
            var bb = ranges.PreflopRangeFor("BB", TrainerActionKind.Call, 1, spot.Opener, spot.Opener, null);
            return spot.OpenerIsOop ? (open, bb) : (bb, open);
        }

        /// <summary>Resuelve y guarda un flop (lo usa el constructor; tambien las pruebas con pocas vueltas).</summary>
        public static void BuildOne(FlopSpot spot, int key, HandRange oop, HandRange ip, int iterations, CancellationToken token)
        {
            var solver = new FlopGtoSolver(FlopOf(key), spot.Pot, spot.Stack, oop.Combos, ip.Combos);
            solver.Solve(iterations, TimeSpan.MaxValue, 0, token);
            if (token.IsCancellationRequested)
                return;
            Save(spot.Opener, key, solver.Export());
        }
    }

    /// <summary>
    /// Calcula la biblioteca del flop en segundo plano, con prioridad baja y varios hilos. Mientras
    /// trabaja, Windows no suspende la PC. Se puede parar cuando se quiera: lo hecho queda guardado.
    /// </summary>
    public sealed class FlopLibraryBuilder
    {
        public static FlopLibraryBuilder Instance { get; } = new();

        private CancellationTokenSource? _cancel;
        private Thread? _coordinator;

        public event Action<FlopLibraryProgress>? Progress;

        public bool IsRunning => _coordinator is { IsAlive: true };

        public FlopLibraryProgress Current => new(FlopLibrary.CountAll(), FlopLibrary.TotalFlops, IsRunning, null, null);

        [DllImport("kernel32.dll")]
        private static extern uint SetThreadExecutionState(uint flags);
        private const uint EsContinuous = 0x80000000;
        private const uint EsSystemRequired = 0x00000001;

        public void Start(AphPreflopProvider charts, int? workers = null)
        {
            if (IsRunning)
                return;
            var cancel = new CancellationTokenSource();
            _cancel = cancel;
            var count = workers ?? Math.Clamp(Environment.ProcessorCount / 3, 1, 4);
            _coordinator = new Thread(() => Run(charts, count, cancel.Token))
            {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
                Name = "APH biblioteca del flop"
            };
            _coordinator.Start();
        }

        public void Stop() => _cancel?.Cancel();

        private void Run(AphPreflopProvider charts, int workers, CancellationToken token)
        {
            SetThreadExecutionState(EsContinuous | EsSystemRequired);
            string? error = null;
            try
            {
                var ranges = new TrainerRangeEstimator(charts);
                var spotRanges = FlopLibrary.Spots.ToDictionary(s => s.Opener, s => FlopLibrary.RangesFor(s, ranges));

                // Orden: flops mezclados (siempre igual) y, en cada uno, todas las posiciones; asi, a
                // medio hacer, ya hay de todas las texturas en todas las posiciones.
                var flops = FlopLibrary.CanonicalFlops.ToArray();
                new Random(1755).Shuffle(flops);
                var pending = flops
                    .SelectMany(key => FlopLibrary.Spots.Select(spot => (Spot: spot, Key: key)))
                    .Where(item => !FlopLibrary.Has(item.Spot.Opener, item.Key))
                    .ToArray();

                var next = -1;
                var doneNow = 0;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                Report(null, null);
                var threads = Enumerable.Range(0, workers).Select(_ => new Thread(() =>
                {
                    while (!token.IsCancellationRequested)
                    {
                        var i = Interlocked.Increment(ref next);
                        if (i >= pending.Length)
                            return;
                        var (spot, key) = pending[i];
                        var (oop, ip) = spotRanges[spot.Opener];
                        try
                        {
                            FlopLibrary.BuildOne(spot, key, oop, ip, FlopLibrary.Iterations, token);
                        }
                        catch (Exception ex)
                        {
                            AppLog.Warn($"Biblioteca del flop: no se pudo calcular {spot.Opener} {TrainerCards.ToText(FlopLibrary.FlopOf(key))}", ex);
                            continue;
                        }
                        var finished = Interlocked.Increment(ref doneNow);
                        var left = pending.Length - finished;
                        Report(TimeSpan.FromSeconds(watch.Elapsed.TotalSeconds / finished * left), null);
                    }
                })
                {
                    IsBackground = true,
                    Priority = ThreadPriority.BelowNormal,
                    Name = "APH flop"
                }).ToList();
                threads.ForEach(t => t.Start());
                threads.ForEach(t => t.Join());
            }
            catch (Exception ex)
            {
                error = ex.Message;
                AppLog.Warn("Biblioteca del flop: el calculo se detuvo", ex);
            }
            finally
            {
                SetThreadExecutionState(EsContinuous);
                _coordinator = null;
                Report(null, error);
            }
        }

        private void Report(TimeSpan? remaining, string? error)
        {
            try
            {
                Progress?.Invoke(new FlopLibraryProgress(FlopLibrary.CountAll(), FlopLibrary.TotalFlops, IsRunning, remaining, error));
            }
            catch (Exception ex)
            {
                AppLog.Warn("Biblioteca del flop: aviso de progreso", ex);
            }
        }
    }
}
