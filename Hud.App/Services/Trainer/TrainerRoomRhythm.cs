using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Hud.App.Services.Trainer
{
    /// <summary>
    /// Ritmo de la sala: cuanto se retira DE VERDAD la gente cuando le apuestan, aprendido de los
    /// historiales de las mesas (todas las apuestas y subidas despues del flop, de cualquier jugador:
    /// quien responde no ve las cartas de quien apuesta). El solver de entrenamiento lo usa en lugar de
    /// su tabla fija: la sala decide cuantos se retiran y el rango estimado decide quienes (las manos
    /// mas flojas primero), asi que tambien baja lo que se gana cuando pagan.
    ///
    /// Por que: medido el 19-11-2026 con 264 apuestas del heroe en ficticio, el solver decia que se
    /// retiraban todos un 18% de las veces y fue un 33% (river 14% contra 50%; con 3 o mas rivales 3%
    /// contra 22%). Aprendida solo con las manos del dia 18, la sala acerto mas en las apuestas del 19
    /// (log-loss 0,60 frente a 0,78 del solver). Las capas de mesa y de rival no mejoraban nada con esa
    /// muestra: no se usan hasta que haya mas manos para volver a medirlas.
    ///
    /// Celdas: apuesta o subida, calle, rivales que pueden retirarse (1, 2, 3+), si todos habian pasado
    /// antes y el tamano. Cada celda se encoge hacia la de arriba (<see cref="Shrink"/> apuestas de
    /// peso), asi una celda con pocas apuestas casi no se mueve del promedio.
    ///
    /// Ficticio y real nunca se mezclan: cada tipo de dinero tiene su tabla, y con menos de
    /// <see cref="MinBets"/> apuestas vistas no se usa (queda el modelo de siempre).
    /// </summary>
    public sealed class TrainerRoomRhythm
    {
        /// <summary>Apuestas vistas de un tipo de dinero para empezar a usar su tabla.</summary>
        public const int MinBets = 400;

        /// <summary>Peso (en apuestas) de la celda de arriba al encoger una celda.</summary>
        public const double Shrink = 30;

        /// <summary>Solo los historiales de los ultimos dias: el ritmo de ahora, no el de hace meses.</summary>
        public const int WindowDays = 30;

        private static readonly Regex FileDateRx = new(@"^HH(?<date>\d{8})", RegexOptions.Compiled);

        /// <summary>Una apuesta o subida despues del flop y si se retiraron todos los que podian hacerlo.</summary>
        /// <param name="Facing">Rivales que podian retirarse (en la mano, sin retirarse y sin estar all-in).</param>
        /// <param name="CheckedTo">Todos ellos habian pasado antes en la calle (apuesta tras "pasan todos").</param>
        /// <param name="Size">Lo que se sube sobre la apuesta, en botes tras igualarla (una apuesta: sobre el bote).</param>
        public sealed record BetResponse(string Street, int Facing, bool CheckedTo, double Size, bool IsRaise, bool AllFolded);

        /// <param name="FoldAll">Probabilidad de que se retiren todos.</param>
        /// <param name="Sample">Apuestas vistas en la celda mas concreta.</param>
        /// <param name="Total">Apuestas vistas de ese tipo de dinero.</param>
        public sealed record Estimate(double FoldAll, int Sample, int Total);

        private sealed class Table
        {
            public Dictionary<string, (int Folds, int Count)> Cells { get; } = new(StringComparer.Ordinal);
            public int Total { get; set; }
        }

        private volatile Dictionary<TrainerMoneyKind, Table> _tables = new();
        private readonly Dictionary<string, (long Length, DateTime Write, List<(TrainerMoneyKind Money, string HandId, BetResponse Bet)> Bets)> _files =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly object _refreshLock = new();
        private int _started;

        /// <summary>
        /// La de la aplicacion: vacia (no cambia nada) hasta que se llama a <see cref="Start"/>; asi las
        /// pruebas y las herramientas no dependen de los historiales de quien las ejecuta.
        /// </summary>
        public static TrainerRoomRhythm Shared { get; } = new();

        /// <summary>Apuestas vistas de ese tipo de dinero.</summary>
        public int Total(TrainerMoneyKind money) => _tables.TryGetValue(money, out var table) ? table.Total : 0;

        /// <summary>
        /// Cuanto se retiran todos ante una apuesta asi en esta sala; null si no hay
        /// <see cref="MinBets"/> apuestas vistas de ese tipo de dinero.
        /// </summary>
        public Estimate? FoldAll(TrainerMoneyKind money, string street, int facing, bool checkedTo, double size, bool isRaise)
        {
            if (facing <= 0 || !_tables.TryGetValue(money, out var table) || table.Total < MinBets)
                return null;

            double? estimate = null;
            var sample = 0;
            foreach (var key in Keys(street, facing, checkedTo && !isRaise, size, isRaise))
            {
                table.Cells.TryGetValue(key, out var cell);
                estimate = estimate is { } parent
                    ? (cell.Folds + Shrink * parent) / (cell.Count + Shrink)
                    : (cell.Folds + 1.0) / (cell.Count + 2.0);
                sample = cell.Count;
            }

            return new Estimate(estimate!.Value, sample, table.Total);
        }

        /// <summary>Empieza a leer los historiales en segundo plano y los relee cada pocos minutos.</summary>
        public void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) == 1)
                return;

            _ = Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        Refresh(TrainerHandHistoryLocator.AllHistoryFiles(), DateTime.Today.AddDays(-WindowDays));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                    {
                        // Se reintenta en la siguiente vuelta: mientras, se usa la tabla anterior.
                    }

                    await Task.Delay(TimeSpan.FromMinutes(5)).ConfigureAwait(false);
                }
            });
        }

        /// <summary>
        /// Una tabla con los historiales dados (pruebas y la herramienta de repeticion). Con
        /// <paramref name="from"/> y <paramref name="before"/> solo cuentan los archivos de esas fechas
        /// (por la fecha del nombre, HHaaaammdd).
        /// </summary>
        public static TrainerRoomRhythm FromFiles(IEnumerable<string> files, DateTime? from = null, DateTime? before = null)
        {
            var rhythm = new TrainerRoomRhythm();
            var chosen = files.Where(file => before is null || FileDate(file) is not { } date || date < before.Value);
            rhythm.Refresh(chosen, from);
            return rhythm;
        }

        /// <summary>Una tabla con respuestas ya extraidas (pruebas).</summary>
        public static TrainerRoomRhythm FromBets(IEnumerable<(TrainerMoneyKind Money, BetResponse Bet)> bets)
        {
            var rhythm = new TrainerRoomRhythm();
            rhythm._tables = Build(bets);
            return rhythm;
        }

        /// <summary>Relee los archivos que han cambiado y rehace las tablas.</summary>
        internal void Refresh(IEnumerable<string> files, DateTime? from)
        {
            lock (_refreshLock)
            {
                var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in files)
                {
                    if (from is { } since && FileDate(file) is { } date && date < since.Date)
                        continue;

                    wanted.Add(file);
                    try
                    {
                        var info = new FileInfo(file);
                        if (_files.TryGetValue(file, out var cached) && cached.Length == info.Length && cached.Write == info.LastWriteTimeUtc)
                            continue;
                        _files[file] = (info.Length, info.LastWriteTimeUtc, ReadFile(file));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or
                                                   ArgumentException or IndexOutOfRangeException or InvalidOperationException)
                    {
                        // Un archivo que no se puede leer (o una mano rota) no frena a los demas.
                    }
                }

                foreach (var gone in _files.Keys.Where(file => !wanted.Contains(file)).ToList())
                    _files.Remove(gone);

                // La misma mano copiada en dos carpetas cuenta una vez (la del primer archivo que la trae).
                var owner = new Dictionary<string, string>(StringComparer.Ordinal);
                var unique = new List<(TrainerMoneyKind, BetResponse)>();
                foreach (var (file, entry) in _files)
                {
                    foreach (var (money, handId, bet) in entry.Bets)
                    {
                        if (!owner.TryGetValue(handId, out var ownerFile))
                            owner[handId] = ownerFile = file;
                        if (string.Equals(ownerFile, file, StringComparison.OrdinalIgnoreCase))
                            unique.Add((money, bet));
                    }
                }

                _tables = Build(unique);
            }
        }

        private static List<(TrainerMoneyKind, string, BetResponse)> ReadFile(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var lines = new List<string>();
            while (reader.ReadLine() is { } line)
                lines.Add(line);

            var bets = new List<(TrainerMoneyKind, string, BetResponse)>();
            foreach (var handLines in TrainerHandParser.SplitHands(lines))
            {
                if (!TrainerHandParser.IsComplete(handLines))
                    continue;
                var hand = TrainerHandParser.Parse(handLines);
                if (hand is null || !hand.IsNoLimitHoldem || hand.Money is not (TrainerMoneyKind.PlayMoney or TrainerMoneyKind.RealMoney))
                    continue;
                bets.AddRange(FromHand(hand).Select(bet => (hand.Money, hand.HandId, bet)));
            }

            return bets;
        }

        /// <summary>
        /// Cada apuesta o subida despues del flop de la mano y si se retiraron todos los que podian.
        /// Solo cuentan los jugadores que estan en la mano (no los "sitting out" ni los que esperan la
        /// ciega: contarlos daba retiradas imposibles). Si alguien no llego a responder (la mano se
        /// corta), la apuesta no cuenta.
        /// </summary>
        public static IEnumerable<BetResponse> FromHand(TrainerHand hand)
        {
            var active = hand.ActiveSeats.Select(seat => seat.Name).ToHashSet(StringComparer.Ordinal);
            var folded = new HashSet<string>(StringComparer.Ordinal);
            var allIn = new HashSet<string>(StringComparer.Ordinal);
            var actions = hand.Actions;
            for (var i = 0; i < actions.Count; i++)
            {
                var action = actions[i];
                if (action.Street is "FLOP" or "TURN" or "RIVER" && action.Kind is TrainerActionKind.Bet or TrainerActionKind.Raise)
                {
                    var facing = active.Where(name => name != action.Player && !folded.Contains(name) && !allIn.Contains(name)).ToList();
                    if (facing.Count > 0 && Response(actions, i, facing) is { } allFolded)
                    {
                        var isRaise = action.Kind == TrainerActionKind.Raise;
                        var checkedTo = !isRaise && facing.All(name => actions.Take(i).Any(item =>
                            item.Street == action.Street && item.Player == name && item.Kind == TrainerActionKind.Check));
                        var size = isRaise
                            ? (action.RaiseTo - action.CurrentBetBefore) / Math.Max(1e-9, action.PotBefore + action.ToCall)
                            : action.Contribution / Math.Max(1e-9, action.PotBefore);
                        yield return new BetResponse(action.Street, facing.Count, checkedTo, size, isRaise, allFolded);
                    }
                }

                if (action.Kind == TrainerActionKind.Fold)
                    folded.Add(action.Player);
                if (action.IsAllIn)
                    allIn.Add(action.Player);
            }
        }

        /// <summary>true si se retiraron todos; false si alguien pago o subio; null si alguien no respondio.</summary>
        private static bool? Response(IReadOnlyList<TrainerAction> actions, int betIndex, IReadOnlyCollection<string> facing)
        {
            var street = actions[betIndex].Street;
            var foldedNow = 0;
            for (var j = betIndex + 1; j < actions.Count && actions[j].Street == street; j++)
            {
                if (!facing.Contains(actions[j].Player))
                    continue;
                if (actions[j].Kind != TrainerActionKind.Fold)
                    return false;
                foldedNow++;
            }

            return foldedNow == facing.Count ? true : null;
        }

        private static Dictionary<TrainerMoneyKind, Table> Build(IEnumerable<(TrainerMoneyKind Money, BetResponse Bet)> bets)
        {
            var tables = new Dictionary<TrainerMoneyKind, Table>();
            foreach (var (money, bet) in bets)
            {
                if (!tables.TryGetValue(money, out var table))
                    tables[money] = table = new Table();
                table.Total++;
                foreach (var key in Keys(bet.Street, bet.Facing, bet.CheckedTo, bet.Size, bet.IsRaise))
                {
                    table.Cells.TryGetValue(key, out var cell);
                    table.Cells[key] = (cell.Folds + (bet.AllFolded ? 1 : 0), cell.Count + 1);
                }
            }

            return tables;
        }

        /// <summary>De lo general a lo concreto: tipo, calle, rivales, si pasaron todos y tamano.</summary>
        private static IEnumerable<string> Keys(string street, int facing, bool checkedTo, double size, bool isRaise)
        {
            var key = isRaise ? "R" : "B";
            yield return key;
            key += "|" + street;
            yield return key;
            key += "|" + Math.Min(facing, 3).ToString(CultureInfo.InvariantCulture);
            yield return key;
            key += checkedTo ? "|pasan" : "|-";
            yield return key;
            yield return key + "|" + SizeBucket(size).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Menos de 0,4 botes; hasta 0,6; hasta 0,85; hasta 1,2; mas.</summary>
        private static int SizeBucket(double size) => size switch
        {
            < 0.4 => 0,
            < 0.6 => 1,
            < 0.85 => 2,
            < 1.2 => 3,
            _ => 4
        };

        private static DateTime? FileDate(string path)
        {
            var match = FileDateRx.Match(Path.GetFileName(path));
            return match.Success && DateTime.TryParseExact(match.Groups["date"].Value, "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date)
                ? date
                : null;
        }
    }
}
