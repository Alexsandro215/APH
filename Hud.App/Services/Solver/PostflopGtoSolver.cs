using System.Buffers;
using System.Diagnostics;
using Hud.App.Services.Trainer;

namespace Hud.App.Services.Solver
{
    public enum GtoKind { Check, Bet, Call, Raise, Fold }

    /// <summary>Una accion del arbol: "To" es lo que el jugador lleva puesto en la calle despues de hacerla (bb).</summary>
    public readonly record struct GtoAction(GtoKind Kind, double To, bool AllIn = false);

    /// <summary>Tamanos del arbol y presupuesto de calculo.</summary>
    public sealed class GtoSettings
    {
        /// <summary>Apuestas de la calle que se resuelve, en fraccion del bote (mas el all-in).</summary>
        public double[] BetSizes { get; init; } = { 0.33, 0.75 };

        /// <summary>En las rivers de un calculo del turn, menos tamanos (el arbol crece 48 veces).</summary>
        public double[] RiverSizesInTurnTree { get; init; } = { 0.75 };

        /// <summary>La primera subida es a X veces la apuesta; despues solo all-in.</summary>
        public double RaiseTimes { get; init; } = 3;

        /// <summary>Apuestas y subidas por calle (apuesta, subida, resubida).</summary>
        public int MaxBets { get; init; } = 3;

        /// <summary>En un calculo del turn, las subidas son solo all-in y en sus rivers hay como mucho apuesta y subida.</summary>
        public bool CompactTurnTree { get; init; } = true;

        public bool AllowAllIn { get; init; } = true;
        public int MaxIterations { get; init; } = 1000;
        public TimeSpan Budget { get; init; } = TimeSpan.FromSeconds(4);

        /// <summary>Guardar la estrategia media en todos los nodos (para medir la explotabilidad en pruebas).</summary>
        public bool TrackAllAverages { get; init; }
    }

    public sealed record GtoChoice(GtoAction Action, double Frequency, double Ev);

    /// <summary>
    /// Resultado para una mano concreta en el nodo pedido: que hace el equilibrio (frecuencia de cada
    /// accion) y lo que gana de media cada una desde ahora (bb).
    /// </summary>
    public sealed record GtoSolution(int Actor, IReadOnlyList<GtoChoice> Choices, int Iterations, TimeSpan Elapsed)
    {
        public GtoChoice Best => Choices.OrderByDescending(c => c.Frequency).First();
    }

    /// <summary>
    /// Solver GTO de una calle del postflop entre DOS jugadores (river, o turn con todas las rivers):
    /// CFR con descuento (DCFR) por vectores sobre los rangos completos de los dos, con los tamanos de GtoSettings. El
    /// jugador 0 es el que actua primero en la calle (fuera de posicion) y el 1 el que esta en posicion.
    /// Los importes van en bb: el bote al empezar la calle, la pila efectiva que queda y lo que lleva
    /// puesto cada uno en la calle. La "historia" son las acciones que ya hubo en la calle: se incluyen
    /// en el arbol con su tamano real y el resultado es la estrategia en el nodo al que llevan.
    /// </summary>
    public sealed class PostflopGtoSolver
    {
        private enum NodeKind { Decision, Fold, Showdown, AllInRunout, Chance }

        private sealed class Node
        {
            public NodeKind Kind;
            public int Player;         // quien decide (o quien se retira)
            public double C0, C1;      // lo puesto en la calle
            public double Pot0;        // bote al empezar la calle
            public int Variant;        // tablero para el showdown: 0 = base, 1 + i = con la river i
            public bool OnRiver;
            public GtoAction[] Actions = Array.Empty<GtoAction>();
            public Node[] Children = Array.Empty<Node>();
            public double[]? Regret;   // [accion * n + mano]
            public double[]? StrategySum;
            public double[]? EvSum;
            public double EvWeight;
        }

        private struct Hand
        {
            public int C1, C2;
            public double W;
        }

        private readonly int[] _board;
        private readonly double _pot;
        private readonly double _stack;
        private readonly GtoSettings _settings;
        private readonly IReadOnlyList<GtoAction> _history;
        private readonly Hand[][] _hands = new Hand[2][];
        private readonly int[] _n = new int[2];
        private readonly int[][] _same = new int[2][];

        // Variantes de tablero: 0 = el tablero dado (river) o, en el turn, 1 + i = con la river _rivers[i].
        private readonly int[] _rivers;
        private readonly int[][][] _rank;      // [variante][jugador][mano]
        private readonly int[][][] _order;     // [variante][jugador] = manos validas por fuerza ascendente
        private readonly bool[][][] _blocked;  // [river i][jugador][mano] la mano contiene esa river

        private readonly Node _root;
        private Node? _target;
        private int _iterations;
        private double _positiveDiscount = 0.5;

        private const double RunoutWeight = 1.0 / 44; // con 4 cartas en la mesa y 4 en las manos quedan 44 rivers

        public PostflopGtoSolver(
            IReadOnlyList<int> board,
            double pot,
            double effectiveStack,
            IReadOnlyList<WeightedCombo> oopRange,
            IReadOnlyList<WeightedCombo> ipRange,
            IReadOnlyList<GtoAction>? history = null,
            GtoSettings? settings = null)
        {
            if (board.Count is not (4 or 5))
                throw new ArgumentException("El solver GTO resuelve el turn o el river.", nameof(board));

            _board = board.ToArray();
            _pot = Math.Max(0.1, pot);
            _stack = Math.Max(0, effectiveStack);
            _settings = settings ?? new GtoSettings();
            _history = history ?? Array.Empty<GtoAction>();

            var dead = 0L;
            foreach (var card in _board) dead |= 1L << card;
            _hands[0] = Filter(oopRange, dead);
            _hands[1] = Filter(ipRange, dead);
            _n[0] = _hands[0].Length;
            _n[1] = _hands[1].Length;
            if (_n[0] == 0 || _n[1] == 0)
                throw new ArgumentException("Hace falta un rango con manos para cada jugador.");
            _same[0] = SameIndex(_hands[0], _hands[1]);
            _same[1] = SameIndex(_hands[1], _hands[0]);

            if (_board.Length == 5)
            {
                _rivers = Array.Empty<int>();
                _rank = new int[1][][];
                _order = new int[1][][];
                _blocked = Array.Empty<bool[][]>();
                _rank[0] = new int[2][];
                _order[0] = new int[2][];
                for (var p = 0; p < 2; p++)
                    (_rank[0][p], _order[0][p]) = Ranks(_hands[p], _board, -1);
            }
            else
            {
                _rivers = Enumerable.Range(0, 52).Where(card => (dead & (1L << card)) == 0).ToArray();
                _rank = new int[_rivers.Length + 1][][];
                _order = new int[_rivers.Length + 1][][];
                _blocked = new bool[_rivers.Length][][];
                var full = new int[5];
                Array.Copy(_board, full, 4);
                for (var i = 0; i < _rivers.Length; i++)
                {
                    full[4] = _rivers[i];
                    _rank[i + 1] = new int[2][];
                    _order[i + 1] = new int[2][];
                    _blocked[i] = new bool[2][];
                    for (var p = 0; p < 2; p++)
                    {
                        (_rank[i + 1][p], _order[i + 1][p]) = Ranks(_hands[p], full, _rivers[i]);
                        _blocked[i][p] = _hands[p].Select(h => h.C1 == _rivers[i] || h.C2 == _rivers[i]).ToArray();
                    }
                }
            }

            _root = BuildStreet(_pot, _stack, onRiver: _board.Length == 5, variant: 0, historyIndex: 0);
            if (_target is null)
                throw new InvalidOperationException("La historia de la calle no lleva a ningun nodo del arbol.");
        }

        /// <summary>Quien decide en el nodo pedido (0 fuera de posicion, 1 en posicion).</summary>
        public int TargetActor => _target!.Player;

        /// <summary>Acciones posibles en el nodo pedido.</summary>
        public IReadOnlyList<GtoAction> TargetActions => _target!.Actions;

        public int DecisionNodes { get; private set; }

        // ------------------------------------------------------------------ preparacion

        private static Hand[] Filter(IReadOnlyList<WeightedCombo> range, long dead)
        {
            var max = range.Count == 0 ? 0 : range.Max(c => c.Weight);
            return range
                .Where(c => c.Weight > max * 1e-3 && c.A != c.B && (dead & (1L << c.A)) == 0 && (dead & (1L << c.B)) == 0)
                .GroupBy(c => (Math.Min(c.A, c.B), Math.Max(c.A, c.B)))
                .Select(g => new Hand { C1 = g.Key.Item1, C2 = g.Key.Item2, W = g.Sum(c => c.Weight) / Math.Max(1e-12, max) })
                .ToArray();
        }

        private static int[] SameIndex(Hand[] mine, Hand[] theirs)
        {
            var index = new Dictionary<(int, int), int>();
            for (var i = 0; i < theirs.Length; i++) index[(theirs[i].C1, theirs[i].C2)] = i;
            return mine.Select(h => index.TryGetValue((h.C1, h.C2), out var i) ? i : -1).ToArray();
        }

        private static (int[] Rank, int[] Order) Ranks(Hand[] hands, int[] board, int river)
        {
            var rank = new int[hands.Length];
            Span<int> cards = stackalloc int[7];
            for (var i = 0; i < board.Length; i++) cards[2 + i] = board[i];
            var valid = new List<int>(hands.Length);
            for (var h = 0; h < hands.Length; h++)
            {
                if (hands[h].C1 == river || hands[h].C2 == river)
                {
                    rank[h] = -1;
                    continue;
                }
                cards[0] = hands[h].C1;
                cards[1] = hands[h].C2;
                rank[h] = FastHandEvaluator.Evaluate(cards[..(2 + board.Length)]);
                valid.Add(h);
            }
            var order = valid.OrderBy(h => rank[h]).ToArray();
            return (rank, order);
        }

        // ------------------------------------------------------------------ arbol

        /// <summary>Una calle completa desde su inicio: fuera de posicion habla primero.</summary>
        private Node BuildStreet(double pot0, double stack, bool onRiver, int variant, int historyIndex) =>
            BuildDecision(0, 0, 0, pot0, stack, 0, checkedBefore: false, onRiver, variant, historyIndex);

        private Node BuildDecision(int player, double c0, double c1, double pot0, double stack, int bets, bool checkedBefore,
            bool onRiver, int variant, int historyIndex)
        {
            var node = new Node { Kind = NodeKind.Decision, Player = player, C0 = c0, C1 = c1, Pot0 = pot0, OnRiver = onRiver, Variant = variant };
            DecisionNodes++;
            var mine = player == 0 ? c0 : c1;
            var current = Math.Max(c0, c1);
            var actions = new List<GtoAction>();
            var turnTree = _board.Length == 4;
            var sizes = onRiver && turnTree ? _settings.RiverSizesInTurnTree : _settings.BetSizes;
            var maxBets = turnTree && _settings.CompactTurnTree && onRiver ? Math.Min(2, _settings.MaxBets) : _settings.MaxBets;
            var raiseTimes = turnTree && _settings.CompactTurnTree ? 0 : _settings.RaiseTimes;

            if (current <= mine + 1e-9)
            {
                actions.Add(new GtoAction(GtoKind.Check, mine));
                if (stack > mine + 1e-9)
                {
                    var potNow = pot0 + c0 + c1;
                    foreach (var fraction in sizes)
                    {
                        var to = mine + Math.Max(1, fraction * potNow);
                        if (to < stack * 0.67)
                            actions.Add(new GtoAction(GtoKind.Bet, Math.Round(to, 2)));
                    }
                    if (_settings.AllowAllIn)
                        actions.Add(new GtoAction(GtoKind.Bet, stack, AllIn: true));
                }
            }
            else
            {
                actions.Add(new GtoAction(GtoKind.Fold, mine));
                actions.Add(new GtoAction(GtoKind.Call, Math.Min(current, stack), AllIn: current >= stack - 1e-9));
                if (bets < maxBets && stack > current + 1e-9)
                {
                    if (bets == 1 && raiseTimes > 1)
                    {
                        var to = current * raiseTimes;
                        if (to < stack * 0.67)
                            actions.Add(new GtoAction(GtoKind.Raise, Math.Round(to, 2)));
                    }
                    if (_settings.AllowAllIn || bets == 1 && actions.All(a => a.Kind != GtoKind.Raise))
                        actions.Add(new GtoAction(GtoKind.Raise, stack, AllIn: true));
                }
            }

            // La accion real de la historia, con su tamano (sustituye a un tamano del arbol cercano).
            var onPath = historyIndex >= 0 && variant == 0;
            if (onPath && historyIndex < _history.Count)
                actions = WithHistory(actions, _history[historyIndex], pot0 + c0 + c1, stack);
            if (onPath && historyIndex == _history.Count)
                _target = node;

            node.Actions = actions.ToArray();
            node.Children = new Node[node.Actions.Length];
            for (var a = 0; a < node.Actions.Length; a++)
            {
                var action = node.Actions[a];
                var nextHistory = onPath && historyIndex < _history.Count && SameAction(action, _history[historyIndex]) ? historyIndex + 1 : -1;
                node.Children[a] = Child(node, action, stack, bets, checkedBefore, nextHistory);
            }

            node.Regret = new double[node.Actions.Length * _n[player]];
            if (_settings.TrackAllAverages || node == _target)
                node.StrategySum = new double[node.Actions.Length * _n[player]];
            if (node == _target)
                node.EvSum = new double[node.Actions.Length * _n[player]];
            return node;
        }

        private Node Child(Node node, GtoAction action, double stack, int bets, bool checkedBefore, int historyIndex)
        {
            var player = node.Player;
            var c0 = player == 0 ? action.To : node.C0;
            var c1 = player == 1 ? action.To : node.C1;
            switch (action.Kind)
            {
                case GtoKind.Fold:
                    return new Node { Kind = NodeKind.Fold, Player = player, C0 = node.C0, C1 = node.C1, Pot0 = node.Pot0, Variant = node.Variant, OnRiver = node.OnRiver };
                case GtoKind.Check:
                    return player == 0 && !checkedBefore
                        ? BuildDecision(1, c0, c1, node.Pot0, stack, bets, checkedBefore: true, node.OnRiver, node.Variant, historyIndex)
                        : CloseStreet(node, c0, c1, stack);
                case GtoKind.Call:
                    return action.AllIn
                        ? AllIn(node, c0, c1)
                        : CloseStreet(node, c0, c1, stack);
                default:
                    return BuildDecision(1 - player, c0, c1, node.Pot0, stack, bets + 1, checkedBefore, node.OnRiver, node.Variant, historyIndex);
            }
        }

        private Node AllIn(Node node, double c0, double c1) => new()
        {
            Kind = node.OnRiver ? NodeKind.Showdown : NodeKind.AllInRunout,
            C0 = c0, C1 = c1, Pot0 = node.Pot0, Variant = node.Variant, OnRiver = node.OnRiver
        };

        private Node CloseStreet(Node node, double c0, double c1, double stack)
        {
            if (node.OnRiver)
                return new Node { Kind = NodeKind.Showdown, C0 = c0, C1 = c1, Pot0 = node.Pot0, Variant = node.Variant, OnRiver = true };

            var left = stack - c0;
            if (left <= 0.01)
                return AllIn(node, c0, c1);

            // Turn cerrado: cada river posible con su propia calle.
            var chance = new Node { Kind = NodeKind.Chance, C0 = c0, C1 = c1, Pot0 = node.Pot0 };
            chance.Children = new Node[_rivers.Length];
            for (var i = 0; i < _rivers.Length; i++)
                chance.Children[i] = BuildStreet(node.Pot0 + c0 + c1, left, onRiver: true, variant: i + 1, historyIndex: -1);
            return chance;
        }

        private static List<GtoAction> WithHistory(List<GtoAction> actions, GtoAction real, double potNow, double stack)
        {
            if (actions.Any(a => SameAction(a, real)))
                return actions;
            var list = actions.ToList();
            var closest = list
                .Select((a, i) => (a, i))
                .Where(x => x.a.Kind == real.Kind && !x.a.AllIn)
                .OrderBy(x => Math.Abs(x.a.To - real.To))
                .FirstOrDefault();
            var fixedAction = real with { To = Math.Min(real.To, stack), AllIn = real.AllIn || real.To >= stack - 1e-9 };
            if (closest.a.Kind == real.Kind && closest.a.To > 0 && Math.Abs(closest.a.To - real.To) <= 0.08 * potNow)
                list[closest.i] = fixedAction;
            else if (!list.Any(a => SameAction(a, fixedAction)))
                list.Add(fixedAction);
            return list.OrderBy(a => a.Kind is GtoKind.Fold ? 0 : a.Kind is GtoKind.Check or GtoKind.Call ? 1 : 2).ThenBy(a => a.To).ToList();
        }

        private static bool SameAction(GtoAction a, GtoAction b) =>
            a.Kind == b.Kind && (a.Kind is GtoKind.Fold or GtoKind.Check or GtoKind.Call || Math.Abs(a.To - b.To) < 0.011 || a.AllIn && b.AllIn);

        // ------------------------------------------------------------------ CFR+

        /// <summary>
        /// Resuelve hasta el presupuesto (o las iteraciones maximas) y devuelve la estrategia de la
        /// mano dada en el nodo pedido. La mano debe estar en el rango de quien decide ahi.
        /// </summary>
        public GtoSolution Solve(int card1, int card2, CancellationToken token = default)
        {
            var watch = Stopwatch.StartNew();
            var reach0 = _hands[0].Select(h => h.W).ToArray();
            var reach1 = _hands[1].Select(h => h.W).ToArray();
            var buffer0 = new double[_n[0]];
            var buffer1 = new double[_n[1]];
            while (_iterations < _settings.MaxIterations && !token.IsCancellationRequested)
            {
                _iterations++;
                var t15 = Math.Pow(_iterations, 1.5);
                _positiveDiscount = t15 / (t15 + 1);
                Traverse(_root, 0, reach1, reach0, buffer0);
                Traverse(_root, 1, reach0, reach1, buffer1);
                if (_iterations >= 30 && watch.Elapsed > _settings.Budget)
                    break;
            }

            return Read(card1, card2, watch.Elapsed);
        }

        private GtoSolution Read(int card1, int card2, TimeSpan elapsed)
        {
            var target = _target!;
            var player = target.Player;
            var (a, b) = (Math.Min(card1, card2), Math.Max(card1, card2));
            var h = Array.FindIndex(_hands[player], x => x.C1 == a && x.C2 == b);
            if (h < 0)
                throw new ArgumentException("La mano no esta en el rango de quien decide.");

            var n = _n[player];
            var actions = target.Actions.Length;
            var total = 0.0;
            for (var k = 0; k < actions; k++) total += target.StrategySum![k * n + h];
            var mine = player == 0 ? target.C0 : target.C1;
            var choices = new List<GtoChoice>();
            for (var k = 0; k < actions; k++)
            {
                var frequency = total > 0 ? target.StrategySum![k * n + h] / total : 1.0 / actions;
                // Valor desde ahora: utilidad de suma cero mas medio bote inicial y lo ya puesto (retirarse = 0).
                var ev = target.EvWeight > 0 ? target.EvSum![k * n + h] / target.EvWeight + target.Pot0 / 2 + mine : 0;
                choices.Add(new GtoChoice(target.Actions[k], frequency, ev));
            }
            return new GtoSolution(player, choices, _iterations, elapsed);
        }

        /// <summary>
        /// Valores contrafactuales del jugador p (por mano, sin multiplicar por su propio alcance) con
        /// el alcance del rival dado. Actualiza las regrets de p (CFR+) y la estrategia media.
        /// </summary>
        private void Traverse(Node node, int p, double[] reachOpp, double[] reachSelf, double[] result)
        {
            var n = _n[p];
            switch (node.Kind)
            {
                case NodeKind.Fold:
                {
                    var folder = node.Player;
                    var folderPut = folder == 0 ? node.C0 : node.C1;
                    var payoff = folder == p ? -(node.Pot0 / 2 + folderPut) : node.Pot0 / 2 + folderPut;
                    Compatible(p, reachOpp, result, node.Variant);
                    for (var h = 0; h < n; h++) result[h] *= payoff;
                    return;
                }
                case NodeKind.Showdown:
                    Showdown(p, node.Variant, reachOpp, result, node.Pot0 / 2 + node.C0);
                    return;
                case NodeKind.AllInRunout:
                    Runout(p, reachOpp, result, node.Pot0 / 2 + node.C0);
                    return;
                case NodeKind.Chance:
                {
                    Array.Clear(result, 0, n);
                    var opp = 1 - p;
                    var children = new double[node.Children.Length][];
                    Parallel.For(0, node.Children.Length, CardParallelism, i =>
                    {
                        var blockedOpp = _blocked[i][opp];
                        var blockedSelf = _blocked[i][p];
                        var masked = Rent(_n[opp]);
                        var maskedSelf = Rent(n);
                        var child = Rent(n);
                        for (var o = 0; o < _n[opp]; o++) masked[o] = blockedOpp[o] ? 0 : reachOpp[o];
                        for (var h = 0; h < n; h++) maskedSelf[h] = blockedSelf[h] ? 0 : reachSelf[h];
                        Traverse(node.Children[i], p, masked, maskedSelf, child);
                        Return(masked);
                        Return(maskedSelf);
                        children[i] = child;
                    });
                    for (var i = 0; i < node.Children.Length; i++)
                    {
                        var blockedSelf = _blocked[i][p];
                        var child = children[i];
                        for (var h = 0; h < n; h++)
                            if (!blockedSelf[h]) result[h] += child[h] * RunoutWeight;
                        Return(child);
                    }
                    return;
                }
            }

            var actions = node.Actions.Length;
            var owner = node.Player;
            var ownerN = _n[owner];
            var strategy = Rent(actions * ownerN);
            CurrentStrategy(node, strategy, ownerN);

            if (owner == p)
            {
                Array.Clear(result, 0, n);
                var values = Rent(actions * n);
                var childReach = Rent(n);
                var childValue = Rent(n);
                for (var a = 0; a < actions; a++)
                {
                    for (var h = 0; h < n; h++) childReach[h] = reachSelf[h] * strategy[a * n + h];
                    Traverse(node.Children[a], p, reachOpp, childReach, childValue);
                    for (var h = 0; h < n; h++)
                    {
                        values[a * n + h] = childValue[h];
                        result[h] += strategy[a * n + h] * childValue[h];
                    }
                }

                // CFR con descuento (DCFR, alfa 1.5 y beta 0): las regrets viejas pesan menos y
                // converge en muchas menos vueltas que CFR+.
                var regret = node.Regret!;
                for (var a = 0; a < actions; a++)
                    for (var h = 0; h < n; h++)
                    {
                        var r = regret[a * n + h];
                        r *= r > 0 ? _positiveDiscount : 0.5;
                        regret[a * n + h] = r + values[a * n + h] - result[h];
                    }

                // Estrategia media con peso t^2, y el valor de cada accion en el nodo pedido.
                var weight = (double)_iterations * _iterations;
                if (node.StrategySum is { } sum)
                    for (var a = 0; a < actions; a++)
                        for (var h = 0; h < n; h++)
                            sum[a * n + h] += weight * reachSelf[h] * strategy[a * n + h];
                if (node.EvSum is { } evSum)
                {
                    var compat = Rent(n);
                    Compatible(p, reachOpp, compat, node.Variant);
                    for (var a = 0; a < actions; a++)
                        for (var h = 0; h < n; h++)
                            if (compat[h] > 1e-12)
                                evSum[a * n + h] += weight * values[a * n + h] / compat[h];
                    node.EvWeight += weight;
                    Return(compat);
                }

                Return(values);
                Return(childReach);
                Return(childValue);
            }
            else
            {
                Array.Clear(result, 0, n);
                var childReach = Rent(ownerN);
                var childValue = Rent(n);
                for (var a = 0; a < actions; a++)
                {
                    for (var o = 0; o < ownerN; o++) childReach[o] = reachOpp[o] * strategy[a * ownerN + o];
                    Traverse(node.Children[a], p, childReach, reachSelf, childValue);
                    for (var h = 0; h < n; h++) result[h] += childValue[h];
                }
                Return(childReach);
                Return(childValue);
            }

            Return(strategy);
        }

        private static void CurrentStrategy(Node node, double[] strategy, int n)
        {
            var actions = node.Actions.Length;
            var regret = node.Regret!;
            for (var h = 0; h < n; h++)
            {
                var total = 0.0;
                for (var a = 0; a < actions; a++)
                {
                    var r = regret[a * n + h];
                    if (r > 0) total += r;
                }
                for (var a = 0; a < actions; a++)
                {
                    var r = regret[a * n + h];
                    strategy[a * n + h] = total > 0 ? (r > 0 ? r / total : 0) : 1.0 / actions;
                }
            }
        }

        /// <summary>Alcance del rival compatible con cada mano de p (sin cartas repetidas).</summary>
        private void Compatible(int p, double[] reachOpp, double[] result, int variant)
        {
            var opp = 1 - p;
            Span<double> card = stackalloc double[52];
            var total = 0.0;
            var hands = _hands[opp];
            for (var o = 0; o < _n[opp]; o++)
            {
                var r = reachOpp[o];
                if (r == 0) continue;
                total += r;
                card[hands[o].C1] += r;
                card[hands[o].C2] += r;
            }

            var mine = _hands[p];
            var same = _same[p];
            var blocked = variant > 0 ? _blocked[variant - 1][p] : null;
            for (var h = 0; h < _n[p]; h++)
            {
                if (blocked is not null && blocked[h])
                {
                    result[h] = 0;
                    continue;
                }
                var value = total - card[mine[h].C1] - card[mine[h].C2];
                if (same[h] >= 0) value += reachOpp[same[h]];
                result[h] = value;
            }
        }

        /// <summary>Showdown con el tablero de la variante: gana +payoff, pierde -payoff, empate 0.</summary>
        private void Showdown(int p, int variant, double[] reachOpp, double[] result, double payoff)
        {
            var opp = 1 - p;
            var rankP = _rank[variant][p];
            var rankO = _rank[variant][opp];
            var orderP = _order[variant][p];
            var orderO = _order[variant][opp];
            var mine = _hands[p];
            var theirs = _hands[opp];
            Span<double> card = stackalloc double[52];
            Array.Clear(result, 0, _n[p]);

            // Rivales con menos fuerza (gana p).
            var total = 0.0;
            var j = 0;
            foreach (var h in orderP)
            {
                while (j < orderO.Length && rankO[orderO[j]] < rankP[h])
                {
                    var o = orderO[j++];
                    var r = reachOpp[o];
                    total += r;
                    card[theirs[o].C1] += r;
                    card[theirs[o].C2] += r;
                }
                result[h] = payoff * (total - card[mine[h].C1] - card[mine[h].C2]);
            }

            // Rivales con mas fuerza (pierde p).
            card.Clear();
            total = 0;
            j = orderO.Length - 1;
            for (var k = orderP.Length - 1; k >= 0; k--)
            {
                var h = orderP[k];
                while (j >= 0 && rankO[orderO[j]] > rankP[h])
                {
                    var o = orderO[j--];
                    var r = reachOpp[o];
                    total += r;
                    card[theirs[o].C1] += r;
                    card[theirs[o].C2] += r;
                }
                result[h] -= payoff * (total - card[mine[h].C1] - card[mine[h].C2]);
            }
        }

        /// <summary>All-in pagado en el turn: la media de los showdowns con cada river.</summary>
        private void Runout(int p, double[] reachOpp, double[] result, double payoff)
        {
            var n = _n[p];
            var opp = 1 - p;
            Array.Clear(result, 0, n);
            var masked = Rent(_n[opp]);
            var value = Rent(n);
            for (var i = 0; i < _rivers.Length; i++)
            {
                var blockedOpp = _blocked[i][opp];
                var blockedSelf = _blocked[i][p];
                for (var o = 0; o < _n[opp]; o++) masked[o] = blockedOpp[o] ? 0 : reachOpp[o];
                Showdown(p, i + 1, masked, value, payoff);
                for (var h = 0; h < n; h++)
                    if (!blockedSelf[h]) result[h] += value[h] * RunoutWeight;
            }
            Return(masked);
            Return(value);
        }

        // ------------------------------------------------------------------ explotabilidad (pruebas)

        /// <summary>
        /// Lo que pierde la estrategia media contra la mejor respuesta de cada jugador, en % del bote
        /// inicial (necesita TrackAllAverages). Cerca de 0 = equilibrio.
        /// </summary>
        public double ExploitabilityPercentOfPot()
        {
            var reach0 = _hands[0].Select(h => h.W).ToArray();
            var reach1 = _hands[1].Select(h => h.W).ToArray();
            var value = 0.0;
            for (var p = 0; p < 2; p++)
            {
                var self = p == 0 ? reach0 : reach1;
                var opp = p == 0 ? reach1 : reach0;
                var result = new double[_n[p]];
                BestResponse(_root, p, opp, result);
                for (var h = 0; h < _n[p]; h++) value += self[h] * result[h];
            }

            // Peso total de las parejas de manos compatibles.
            var pairs = new double[_n[0]];
            Compatible(0, reach1, pairs, 0);
            var pairWeight = 0.0;
            for (var h = 0; h < _n[0]; h++) pairWeight += reach0[h] * pairs[h];
            return value / 2 / Math.Max(1e-12, pairWeight) / _pot * 100;
        }

        private void BestResponse(Node node, int p, double[] reachOpp, double[] result)
        {
            var n = _n[p];
            if (node.Kind is NodeKind.Fold or NodeKind.Showdown or NodeKind.AllInRunout)
            {
                // Mismos terminales que en CFR.
                Traverse(node, p, reachOpp, new double[n], result);
                return;
            }

            if (node.Kind == NodeKind.Chance)
            {
                Array.Clear(result, 0, n);
                var opp = 1 - p;
                var masked = new double[_n[opp]];
                var child = new double[n];
                for (var i = 0; i < node.Children.Length; i++)
                {
                    for (var o = 0; o < _n[opp]; o++) masked[o] = _blocked[i][opp][o] ? 0 : reachOpp[o];
                    BestResponse(node.Children[i], p, masked, child);
                    for (var h = 0; h < n; h++)
                        if (!_blocked[i][p][h]) result[h] += child[h] * RunoutWeight;
                }
                return;
            }

            var actions = node.Actions.Length;
            var childValue = new double[n];
            if (node.Player == p)
            {
                for (var h = 0; h < n; h++) result[h] = double.NegativeInfinity;
                for (var a = 0; a < actions; a++)
                {
                    BestResponse(node.Children[a], p, reachOpp, childValue);
                    for (var h = 0; h < n; h++) result[h] = Math.Max(result[h], childValue[h]);
                }
                return;
            }

            var owner = node.Player;
            var ownerN = _n[owner];
            var average = new double[actions * ownerN];
            var sum = node.StrategySum ?? throw new InvalidOperationException("Hace falta TrackAllAverages para medir la explotabilidad.");
            for (var o = 0; o < ownerN; o++)
            {
                var total = 0.0;
                for (var a = 0; a < actions; a++) total += sum[a * ownerN + o];
                for (var a = 0; a < actions; a++) average[a * ownerN + o] = total > 0 ? sum[a * ownerN + o] / total : 1.0 / actions;
            }

            Array.Clear(result, 0, n);
            var childReach = new double[ownerN];
            for (var a = 0; a < actions; a++)
            {
                for (var o = 0; o < ownerN; o++) childReach[o] = reachOpp[o] * average[a * ownerN + o];
                BestResponse(node.Children[a], p, childReach, childValue);
                for (var h = 0; h < n; h++) result[h] += childValue[h];
            }
        }

        /// <summary>
        /// Cada carta que puede salir es un subarbol independiente: se recorren en paralelo, con todos
        /// los hilos del procesador menos dos (PokerStars y la lectura de las mesas siguen fluidas).
        /// Los resultados se suman despues en el mismo orden que antes, asi que la estrategia sale
        /// identica a la de un solo hilo, solo que mucho antes.
        /// </summary>
        private static readonly ParallelOptions CardParallelism = new()
        {
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 2)
        };

        private static double[] Rent(int length)
        {
            var array = ArrayPool<double>.Shared.Rent(Math.Max(1, length));
            return array;
        }

        private static void Return(double[] array) => ArrayPool<double>.Shared.Return(array);
    }
}
