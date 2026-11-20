using System.Buffers;
using System.Diagnostics;
using System.Numerics;
using Hud.App.Services.Trainer;

namespace Hud.App.Services.Solver
{
    /// <summary>
    /// Solver GTO APROXIMADO del flop entre dos jugadores, para la biblioteca que se calcula de
    /// antemano. Se apuesta en el flop (33%, 75%, all-in; subida a 3x o all-in) y en el turn (75% o
    /// all-in; subida all-in); el river se reparte sin apuestas (showdown con cada river). Es la
    /// abstraccion de "dos calles": los tamanos y las frecuencias del flop salen bien orientados, pero
    /// el valor del juego en el river no esta, por eso se presenta como aproximado.
    /// DCFR por vectores sobre los dos rangos completos, como PostflopGtoSolver.
    /// </summary>
    public sealed class FlopGtoSolver
    {
        private enum NodeKind { Decision, Fold, TurnChance, TurnRunout, FlopRunout }

        private sealed class Node
        {
            public NodeKind Kind;
            public int Player;
            public double C0, C1;
            public double Pot0;
            public int Turn = -1;          // indice de la carta del turn en las calles del turn
            public string Path = "";       // camino de acciones en el flop ("", "x", "b1", "x-b2", "b1-r"...)
            public GtoAction[] Actions = Array.Empty<GtoAction>();
            public string[] Codes = Array.Empty<string>();
            public Node[] Children = Array.Empty<Node>();
            public double[]? Regret;
            public double[]? StrategySum;
            public double[]? EvSum;
            public double EvWeight;
        }

        private struct Hand
        {
            public int C1, C2;
            public long Mask;
            public double W;
        }

        public static readonly double[] FlopSizes = { 0.33, 0.75 };
        private const double TurnSize = 0.75;
        private const double FlopRaiseTimes = 3;

        private readonly int[] _board;
        private readonly double _pot;
        private readonly double _stack;
        private readonly Hand[][] _hands = new Hand[2][];
        private readonly int[] _n = new int[2];
        private readonly int[][] _same = new int[2][];
        private readonly int[] _turns;
        // Por cada turn, la matriz [mano OOP x mano IP] con lo que gana OOP en el showdown sumando
        // los 44 rivers (+1 gana, -1 pierde, 0 empate o manos que chocan): cabe en un byte y asi la
        // matriz de un turn entra en la cache. Y la media de todos los turns para los all-in del
        // flop. Cada showdown es un producto matriz-vector.
        private readonly sbyte[][] _equity;
        private readonly float[] _flopEquity;
        private readonly Node _root;
        private readonly bool _trackTurnAverages;
        private readonly IReadOnlyList<GtoAction> _history;
        private Node? _target;
        private readonly List<Node> _flopNodes = new();
        private int _iterations;
        private double _positiveDiscount = 0.5;

        private const double TurnWeight = 1.0 / 45;          // 49 turns - 4 cartas de las manos
        private const double RiverWeight = 1.0 / 44;         // 48 rivers - 4 cartas de las manos

        public FlopGtoSolver(IReadOnlyList<int> flop, double pot, double effectiveStack,
            IReadOnlyList<WeightedCombo> oopRange, IReadOnlyList<WeightedCombo> ipRange,
            IReadOnlyList<GtoAction>? history = null, bool trackTurnAverages = false)
        {
            _trackTurnAverages = trackTurnAverages;
            _history = history ?? Array.Empty<GtoAction>();
            if (flop.Count != 3)
                throw new ArgumentException("Hacen falta las tres cartas del flop.", nameof(flop));
            _board = flop.ToArray();
            _pot = pot;
            _stack = effectiveStack;

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

            _turns = Enumerable.Range(0, 52).Where(c => (dead & (1L << c)) == 0).ToArray();
            BuildEquity(out _equity, out _flopEquity);
            _root = BuildDecision(0, 0, 0, _pot, _stack, 0, false, -1, "", historyIndex: 0);
        }

        public int DecisionNodes { get; private set; }

        /// <summary>Quien decide en el nodo de la historia (0 = OOP, 1 = IP), o -1 si la historia no esta en el arbol.</summary>
        public int TargetActor => _target?.Player ?? -1;

        /// <summary>Nodos del flop (los que se guardan en la biblioteca), con su camino de acciones.</summary>
        public IEnumerable<(string Path, int Player, IReadOnlyList<GtoAction> Actions, IReadOnlyList<string> Codes)> FlopNodes =>
            _flopNodes.Select(n => (n.Path, n.Player, (IReadOnlyList<GtoAction>)n.Actions, (IReadOnlyList<string>)n.Codes));

        // ------------------------------------------------------------------ arbol

        private Node BuildDecision(int player, double c0, double c1, double pot0, double stack, int bets, bool checkedBefore, int turn, string path,
            int historyIndex = -1)
        {
            var node = new Node { Kind = NodeKind.Decision, Player = player, C0 = c0, C1 = c1, Pot0 = pot0, Turn = turn, Path = path };
            DecisionNodes++;
            var onTurn = turn >= 0;
            var mine = player == 0 ? c0 : c1;
            var current = Math.Max(c0, c1);
            var actions = new List<(GtoAction Action, string Code)>();
            var maxBets = onTurn ? 2 : 3;
            // El all-in solo entra si no es un disparate (100 bb en un bote de 5): hasta 3,5 veces el
            // bote en el flop y 2 en el turn, o cuando no queda otro tamano.
            var allInRatio = onTurn ? 2.0 : 3.5;
            var potNow = pot0 + c0 + c1;

            if (current <= mine + 1e-9)
            {
                actions.Add((new GtoAction(GtoKind.Check, mine), "x"));
                if (stack > mine + 1e-9)
                {
                    var sizes = onTurn ? new[] { TurnSize } : FlopSizes;
                    var sized = 0;
                    for (var i = 0; i < sizes.Length; i++)
                    {
                        var to = mine + Math.Max(1, sizes[i] * potNow);
                        if (to < stack * 0.67)
                        {
                            actions.Add((new GtoAction(GtoKind.Bet, Math.Round(to, 2)), "b" + (i + 1)));
                            sized++;
                        }
                    }
                    if (sized == 0 || stack - mine <= allInRatio * potNow)
                        actions.Add((new GtoAction(GtoKind.Bet, stack, AllIn: true), "a"));
                }
            }
            else
            {
                actions.Add((new GtoAction(GtoKind.Fold, mine), "f"));
                actions.Add((new GtoAction(GtoKind.Call, Math.Min(current, stack), AllIn: current >= stack - 1e-9), "c"));
                if (bets < maxBets && stack > current + 1e-9)
                {
                    var raised = false;
                    if (!onTurn && bets == 1)
                    {
                        var to = current * FlopRaiseTimes;
                        if (to < stack * 0.67)
                        {
                            actions.Add((new GtoAction(GtoKind.Raise, Math.Round(to, 2)), "r"));
                            raised = true;
                        }
                    }
                    var afterCall = potNow + (current - mine);
                    if (stack - mine <= allInRatio * afterCall || (!onTurn && bets == 1 && !raised))
                        actions.Add((new GtoAction(GtoKind.Raise, stack, AllIn: true), "a"));
                }
            }

            // La accion real de la historia, con su tamano (sustituye a un tamano del arbol cercano).
            var onPath = historyIndex >= 0 && !onTurn;
            if (onPath && historyIndex < _history.Count)
                actions = WithHistory(actions, _history[historyIndex], potNow, stack);
            if (onPath && historyIndex == _history.Count)
                _target = node;

            node.Actions = actions.Select(a => a.Action).ToArray();
            node.Codes = actions.Select(a => a.Code).ToArray();
            node.Children = new Node[node.Actions.Length];
            for (var a = 0; a < node.Actions.Length; a++)
            {
                var action = node.Actions[a];
                var childPath = onTurn ? "" : path.Length == 0 ? node.Codes[a] : path + "-" + node.Codes[a];
                var nextHistory = onPath && historyIndex < _history.Count && SameAction(action, _history[historyIndex]) ? historyIndex + 1 : -1;
                node.Children[a] = Child(node, action, stack, bets, checkedBefore, childPath, nextHistory);
            }

            node.Regret = new double[node.Actions.Length * _n[player]];
            if (!onTurn || _trackTurnAverages)
                node.StrategySum = new double[node.Actions.Length * _n[player]];
            if (node == _target)
                node.EvSum = new double[node.Actions.Length * _n[player]];
            if (!onTurn)
                _flopNodes.Add(node);
            return node;
        }

        private Node Child(Node node, GtoAction action, double stack, int bets, bool checkedBefore, string path, int historyIndex)
        {
            var player = node.Player;
            var c0 = player == 0 ? action.To : node.C0;
            var c1 = player == 1 ? action.To : node.C1;
            switch (action.Kind)
            {
                case GtoKind.Fold:
                    return new Node { Kind = NodeKind.Fold, Player = player, C0 = node.C0, C1 = node.C1, Pot0 = node.Pot0, Turn = node.Turn };
                case GtoKind.Check:
                    return player == 0 && !checkedBefore
                        ? BuildDecision(1, c0, c1, node.Pot0, stack, bets, true, node.Turn, path, historyIndex)
                        : CloseStreet(node, c0, c1, stack);
                case GtoKind.Call:
                    return action.AllIn ? AllIn(node, c0, c1) : CloseStreet(node, c0, c1, stack);
                default:
                    return BuildDecision(1 - player, c0, c1, node.Pot0, stack, bets + 1, checkedBefore, node.Turn, path, historyIndex);
            }
        }

        private Node AllIn(Node node, double c0, double c1) => new()
        {
            Kind = node.Turn >= 0 ? NodeKind.TurnRunout : NodeKind.FlopRunout,
            C0 = c0, C1 = c1, Pot0 = node.Pot0, Turn = node.Turn
        };

        private Node CloseStreet(Node node, double c0, double c1, double stack)
        {
            // Cierra el turn: el river se reparte sin apuestas.
            if (node.Turn >= 0)
                return new Node { Kind = NodeKind.TurnRunout, C0 = c0, C1 = c1, Pot0 = node.Pot0, Turn = node.Turn };

            var left = stack - c0;
            if (left <= 0.01)
                return AllIn(node, c0, c1);
            var chance = new Node { Kind = NodeKind.TurnChance, C0 = c0, C1 = c1, Pot0 = node.Pot0 };
            chance.Children = new Node[_turns.Length];
            for (var t = 0; t < _turns.Length; t++)
                chance.Children[t] = BuildDecision(0, 0, 0, node.Pot0 + c0 + c1, left, 0, false, t, "");
            return chance;
        }

        private static List<(GtoAction Action, string Code)> WithHistory(List<(GtoAction Action, string Code)> actions, GtoAction real,
            double potNow, double stack)
        {
            if (actions.Any(a => SameAction(a.Action, real)))
                return actions;
            var list = actions.ToList();
            var fixedAction = real with { To = Math.Min(real.To, stack), AllIn = real.AllIn || real.To >= stack - 1e-9 };
            var closest = list
                .Select((a, i) => (a.Action, i))
                .Where(x => x.Action.Kind == real.Kind && !x.Action.AllIn && x.Action.To > 0)
                .OrderBy(x => Math.Abs(x.Action.To - real.To))
                .FirstOrDefault();
            if (closest.Action.Kind == real.Kind && closest.Action.To > 0 && Math.Abs(closest.Action.To - real.To) <= 0.08 * potNow)
                list[closest.i] = (fixedAction, list[closest.i].Code);
            else if (!list.Any(a => SameAction(a.Action, fixedAction)))
                list.Add((fixedAction, fixedAction.AllIn ? "a" : "h"));
            return list
                .OrderBy(a => a.Action.Kind is GtoKind.Fold ? 0 : a.Action.Kind is GtoKind.Check or GtoKind.Call ? 1 : 2)
                .ThenBy(a => a.Action.To)
                .ToList();
        }

        private static bool SameAction(GtoAction a, GtoAction b) =>
            a.Kind == b.Kind && (a.Kind is GtoKind.Fold or GtoKind.Check or GtoKind.Call || Math.Abs(a.To - b.To) < 0.011 || a.AllIn && b.AllIn);

        // ------------------------------------------------------------------ preparacion

        private void BuildEquity(out sbyte[][] equity, out float[] flopEquity)
        {
            var n0 = _n[0];
            var n1 = _n[1];
            var h0 = _hands[0];
            var h1 = _hands[1];
            equity = new sbyte[_turns.Length][];
            flopEquity = new float[n0 * n1];
            var rank0 = new int[n0];
            var rank1 = new int[n1];
            var sum = new int[n0 * n1];
            Span<int> cards = stackalloc int[7];
            cards[2] = _board[0];
            cards[3] = _board[1];
            cards[4] = _board[2];

            for (var t = 0; t < _turns.Length; t++)
            {
                var turn = _turns[t];
                cards[5] = turn;
                Array.Clear(sum);
                foreach (var river in _turns)
                {
                    if (river == turn)
                        continue;
                    cards[6] = river;
                    var blocked = (1L << turn) | (1L << river);
                    for (var h = 0; h < n0; h++)
                    {
                        if ((h0[h].Mask & blocked) != 0) { rank0[h] = -1; continue; }
                        cards[0] = h0[h].C1;
                        cards[1] = h0[h].C2;
                        rank0[h] = FastHandEvaluator.Evaluate(cards);
                    }
                    for (var o = 0; o < n1; o++)
                    {
                        if ((h1[o].Mask & blocked) != 0) { rank1[o] = int.MinValue; continue; }
                        cards[0] = h1[o].C1;
                        cards[1] = h1[o].C2;
                        rank1[o] = FastHandEvaluator.Evaluate(cards);
                    }
                    for (var h = 0; h < n0; h++)
                    {
                        var mine = rank0[h];
                        if (mine < 0) continue;
                        var row = h * n1;
                        for (var o = 0; o < n1; o++)
                        {
                            var theirs = rank1[o];
                            if (theirs == int.MinValue) continue;
                            sum[row + o] += mine > theirs ? 1 : mine < theirs ? -1 : 0;
                        }
                    }
                }

                var matrix = new sbyte[n0 * n1];
                for (var h = 0; h < n0; h++)
                {
                    var row = h * n1;
                    for (var o = 0; o < n1; o++)
                    {
                        if ((h0[h].Mask & h1[o].Mask) != 0) continue;   // manos que chocan
                        matrix[row + o] = (sbyte)sum[row + o];
                        flopEquity[row + o] += (float)(sum[row + o] * RiverWeight * TurnWeight);
                    }
                }
                equity[t] = matrix;
            }
        }

        private static Hand[] Filter(IReadOnlyList<WeightedCombo> range, long dead)
        {
            var max = range.Count == 0 ? 0 : range.Max(c => c.Weight);
            return range
                .Where(c => c.Weight > max * 1e-3 && c.A != c.B && (dead & (1L << c.A)) == 0 && (dead & (1L << c.B)) == 0)
                .GroupBy(c => (Math.Min(c.A, c.B), Math.Max(c.A, c.B)))
                .Select(g => new Hand
                {
                    C1 = g.Key.Item1,
                    C2 = g.Key.Item2,
                    Mask = (1L << g.Key.Item1) | (1L << g.Key.Item2),
                    W = g.Sum(c => c.Weight) / Math.Max(1e-12, max)
                })
                .ToArray();
        }

        private static int[] SameIndex(Hand[] mine, Hand[] theirs)
        {
            var index = new Dictionary<(int, int), int>();
            for (var i = 0; i < theirs.Length; i++) index[(theirs[i].C1, theirs[i].C2)] = i;
            return mine.Select(h => index.TryGetValue((h.C1, h.C2), out var i) ? i : -1).ToArray();
        }

        // ------------------------------------------------------------------ resolver

        /// <summary>
        /// Resuelve (hasta las iteraciones o el tiempo, con un minimo de vueltas) y devuelve la
        /// estrategia de la mano dada en el nodo de la historia. La mano debe estar en el rango de
        /// quien decide ahi.
        /// </summary>
        public GtoSolution Solve(int card1, int card2, int maxIterations, TimeSpan budget, int minIterations = 30, CancellationToken token = default)
        {
            var watch = Stopwatch.StartNew();
            Solve(maxIterations, budget, minIterations, token);
            var target = _target ?? throw new InvalidOperationException("La historia no esta en el arbol del flop.");
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
                var ev = target.EvWeight > 0 ? target.EvSum![k * n + h] / target.EvWeight + target.Pot0 / 2 + mine : 0;
                choices.Add(new GtoChoice(target.Actions[k], frequency, ev));
            }
            return new GtoSolution(player, choices, _iterations, watch.Elapsed);
        }

        /// <summary>Resuelve hasta las iteraciones o el tiempo dados (el tiempo cuenta a partir del minimo de vueltas).</summary>
        public int Solve(int maxIterations, TimeSpan budget, int minIterations = 0, CancellationToken token = default)
        {
            var watch = Stopwatch.StartNew();
            var reach0 = _hands[0].Select(h => h.W).ToArray();
            var reach1 = _hands[1].Select(h => h.W).ToArray();
            var buffer0 = new double[_n[0]];
            var buffer1 = new double[_n[1]];
            while (_iterations < maxIterations && !token.IsCancellationRequested)
            {
                _iterations++;
                var t15 = Math.Pow(_iterations, 1.5);
                _positiveDiscount = t15 / (t15 + 1);
                Traverse(_root, 0, reach1, reach0, buffer0);
                Traverse(_root, 1, reach0, reach1, buffer1);
                if (_iterations >= minIterations && watch.Elapsed > budget)
                    break;
            }
            return _iterations;
        }

        /// <summary>La estrategia media de los nodos del flop, para guardarla en la biblioteca (frecuencias de 0 a 255).</summary>
        public FlopStrategyTable Export()
        {
            var combos = new ushort[2][];
            for (var p = 0; p < 2; p++)
                combos[p] = _hands[p].Select(h => (ushort)Hud.App.Services.Simulator.SimCombos.IndexOf(h.C1, h.C2)).ToArray();
            var nodes = _flopNodes.Select(node =>
            {
                var n = _n[node.Player];
                var actions = node.Actions.Length;
                var frequencies = new byte[n * actions];
                var sum = node.StrategySum!;
                for (var h = 0; h < n; h++)
                {
                    var total = 0.0;
                    for (var a = 0; a < actions; a++) total += sum[a * n + h];
                    for (var a = 0; a < actions; a++)
                    {
                        var f = total > 0 ? sum[a * n + h] / total : 1.0 / actions;
                        frequencies[h * actions + a] = (byte)Math.Round(f * 255);
                    }
                }
                return new FlopNodeStrategy(node.Path, node.Player, node.Codes.ToArray(),
                    node.Actions.Select(a => (float)a.To).ToArray(), frequencies);
            }).ToArray();
            return new FlopStrategyTable(combos, nodes);
        }

        /// <summary>
        /// Estrategia media de un nodo del flop para cada una de las 1326 manos (indice de
        /// SimCombos): frecuencia de cada accion, o null si la mano no esta en el rango.
        /// </summary>
        public double[]?[] Strategy(string path)
        {
            var node = _flopNodes.FirstOrDefault(n => n.Path == path) ?? throw new ArgumentException("Nodo desconocido: " + path);
            var player = node.Player;
            var n = _n[player];
            var actions = node.Actions.Length;
            var result = new double[]?[1326];
            for (var h = 0; h < n; h++)
            {
                var total = 0.0;
                for (var a = 0; a < actions; a++) total += node.StrategySum![a * n + h];
                var frequencies = new double[actions];
                for (var a = 0; a < actions; a++) frequencies[a] = total > 0 ? node.StrategySum![a * n + h] / total : 1.0 / actions;
                var hand = _hands[player][h];
                result[Hud.App.Services.Simulator.SimCombos.IndexOf(hand.C1, hand.C2)] = frequencies;
            }
            return result;
        }

        private void Traverse(Node node, int p, double[] reachOpp, double[] reachSelf, double[] result)
        {
            var n = _n[p];
            var opp = 1 - p;
            switch (node.Kind)
            {
                case NodeKind.Fold:
                {
                    var folderPut = node.Player == 0 ? node.C0 : node.C1;
                    var payoff = node.Player == p ? -(node.Pot0 / 2 + folderPut) : node.Pot0 / 2 + folderPut;
                    Compatible(p, reachOpp, result, node.Turn >= 0 ? 1L << _turns[node.Turn] : 0);
                    for (var h = 0; h < n; h++) result[h] *= payoff;
                    return;
                }
                case NodeKind.TurnRunout:
                    TurnRunout(p, _equity[node.Turn], reachOpp, result, (node.Pot0 / 2 + node.C0) * RiverWeight);
                    return;
                case NodeKind.FlopRunout:
                    Runout(p, _flopEquity, reachOpp, result, node.Pot0 / 2 + node.C0);
                    return;
                case NodeKind.TurnChance:
                {
                    Array.Clear(result, 0, n);
                    var children = new double[_turns.Length][];
                    Parallel.For(0, _turns.Length, CardParallelism, t =>
                    {
                        var turnBit = 1L << _turns[t];
                        var masked = Rent(_n[opp]);
                        var maskedSelf = Rent(n);
                        var child = Rent(n);
                        Mask(opp, reachOpp, masked, turnBit);
                        Mask(p, reachSelf, maskedSelf, turnBit);
                        Traverse(node.Children[t], p, masked, maskedSelf, child);
                        Return(masked);
                        Return(maskedSelf);
                        children[t] = child;
                    });
                    for (var t = 0; t < _turns.Length; t++)
                    {
                        var turnBit = 1L << _turns[t];
                        var child = children[t];
                        for (var h = 0; h < n; h++)
                            if ((_hands[p][h].Mask & turnBit) == 0) result[h] += child[h] * TurnWeight;
                        Return(child);
                    }
                    return;
                }
            }

            var actions = node.Actions.Length;
            var owner = node.Player;
            var ownerN = _n[owner];
            var strategy = Rent(actions * ownerN);
            var regret = node.Regret!;
            for (var h = 0; h < ownerN; h++)
            {
                var total = 0.0;
                for (var a = 0; a < actions; a++)
                {
                    var r = regret[a * ownerN + h];
                    if (r > 0) total += r;
                }
                for (var a = 0; a < actions; a++)
                {
                    var r = regret[a * ownerN + h];
                    strategy[a * ownerN + h] = total > 0 ? (r > 0 ? r / total : 0) : 1.0 / actions;
                }
            }

            Array.Clear(result, 0, n);
            if (owner == p)
            {
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
                for (var a = 0; a < actions; a++)
                    for (var h = 0; h < n; h++)
                    {
                        var r = regret[a * n + h];
                        r *= r > 0 ? _positiveDiscount : 0.5;
                        regret[a * n + h] = r + values[a * n + h] - result[h];
                    }
                if (node.StrategySum is { } sum)
                {
                    var weight = (double)_iterations * _iterations;
                    for (var a = 0; a < actions; a++)
                        for (var h = 0; h < n; h++)
                            sum[a * n + h] += weight * reachSelf[h] * strategy[a * n + h];
                }
                if (node.EvSum is { } evSum)
                {
                    // Lo que gana cada accion por mano (dividido por el peso de las manos rivales posibles).
                    var weight = (double)_iterations * _iterations;
                    var compat = Rent(n);
                    Compatible(p, reachOpp, compat, 0);
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

        // ------------------------------------------------------------------ explotabilidad (pruebas)

        /// <summary>
        /// Lo que pierde la estrategia media contra la mejor respuesta de cada jugador, en % del bote
        /// inicial (hace falta trackTurnAverages). Cerca de 0 = equilibrio de esta abstraccion.
        /// </summary>
        public double ExploitabilityPercentOfPot()
        {
            if (!_trackTurnAverages)
                throw new InvalidOperationException("Hace falta trackTurnAverages para medir la explotabilidad.");
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

            var pairs = new double[_n[0]];
            Compatible(0, reach1, pairs, 0);
            var pairWeight = 0.0;
            for (var h = 0; h < _n[0]; h++) pairWeight += reach0[h] * pairs[h];
            return value / 2 / Math.Max(1e-12, pairWeight) / _pot * 100;
        }

        private void BestResponse(Node node, int p, double[] reachOpp, double[] result)
        {
            var n = _n[p];
            var opp = 1 - p;
            if (node.Kind is NodeKind.Fold or NodeKind.TurnRunout or NodeKind.FlopRunout)
            {
                Traverse(node, p, reachOpp, new double[n], result);
                return;
            }

            if (node.Kind == NodeKind.TurnChance)
            {
                Array.Clear(result, 0, n);
                var masked = new double[_n[opp]];
                var child = new double[n];
                for (var t = 0; t < _turns.Length; t++)
                {
                    var turnBit = 1L << _turns[t];
                    Mask(opp, reachOpp, masked, turnBit);
                    BestResponse(node.Children[t], p, masked, child);
                    for (var h = 0; h < n; h++)
                        if ((_hands[p][h].Mask & turnBit) == 0) result[h] += child[h] * TurnWeight;
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

            var ownerN = _n[node.Player];
            var sum = node.StrategySum!;
            Array.Clear(result, 0, n);
            var childReach = new double[ownerN];
            for (var a = 0; a < actions; a++)
            {
                for (var o = 0; o < ownerN; o++)
                {
                    var total = 0.0;
                    for (var b = 0; b < actions; b++) total += sum[b * ownerN + o];
                    childReach[o] = reachOpp[o] * (total > 0 ? sum[a * ownerN + o] / total : 1.0 / actions);
                }
                BestResponse(node.Children[a], p, childReach, childValue);
                for (var h = 0; h < n; h++) result[h] += childValue[h];
            }
        }

        private void Mask(int player, double[] reach, double[] masked, long bits)
        {
            var hands = _hands[player];
            for (var h = 0; h < _n[player]; h++) masked[h] = (hands[h].Mask & bits) != 0 ? 0 : reach[h];
        }

        /// <summary>
        /// Showdown repartiendo lo que falta (river, o turn y river): con la matriz de equity es un
        /// producto matriz-vector. Para OOP cada fila por el alcance de IP; para IP, la suma de las
        /// filas por el alcance de OOP, con el signo cambiado.
        /// </summary>
        private void Runout(int p, float[] matrix, double[] reachOpp, double[] result, double payoff)
        {
            var n0 = _n[0];
            var n1 = _n[1];
            if (p == 0)
            {
                var reach = ArrayPool<float>.Shared.Rent(n1);
                for (var o = 0; o < n1; o++) reach[o] = (float)reachOpp[o];
                var r = new ReadOnlySpan<float>(reach, 0, n1);
                for (var h = 0; h < n0; h++)
                    result[h] = payoff * Dot(new ReadOnlySpan<float>(matrix, h * n1, n1), r);
                ArrayPool<float>.Shared.Return(reach);
            }
            else
            {
                var sum = ArrayPool<float>.Shared.Rent(n1);
                var acc = new Span<float>(sum, 0, n1);
                acc.Clear();
                for (var h = 0; h < n0; h++)
                {
                    var w = (float)reachOpp[h];
                    if (w == 0) continue;
                    Axpy(acc, new ReadOnlySpan<float>(matrix, h * n1, n1), w);
                }
                for (var o = 0; o < n1; o++) result[o] = -payoff * acc[o];
                ArrayPool<float>.Shared.Return(sum);
            }
        }

        /// <summary>Como Runout, con la matriz de un turn en bytes (la suma de los rivers; el peso va en payoff).</summary>
        private void TurnRunout(int p, sbyte[] matrix, double[] reachOpp, double[] result, double payoff)
        {
            var n0 = _n[0];
            var n1 = _n[1];
            if (p == 0)
            {
                var reach = ArrayPool<float>.Shared.Rent(n1);
                for (var o = 0; o < n1; o++) reach[o] = (float)reachOpp[o];
                var r = new ReadOnlySpan<float>(reach, 0, n1);
                for (var h = 0; h < n0; h++)
                    result[h] = payoff * Dot(new ReadOnlySpan<sbyte>(matrix, h * n1, n1), r);
                ArrayPool<float>.Shared.Return(reach);
            }
            else
            {
                var sum = ArrayPool<float>.Shared.Rent(n1);
                var acc = new Span<float>(sum, 0, n1);
                acc.Clear();
                for (var h = 0; h < n0; h++)
                {
                    var w = (float)reachOpp[h];
                    if (w == 0) continue;
                    Axpy(acc, new ReadOnlySpan<sbyte>(matrix, h * n1, n1), w);
                }
                for (var o = 0; o < n1; o++) result[o] = -payoff * acc[o];
                ArrayPool<float>.Shared.Return(sum);
            }
        }

        private static float Dot(ReadOnlySpan<sbyte> a, ReadOnlySpan<float> b)
        {
            var width = Vector<sbyte>.Count;
            var f = Vector<float>.Count;
            var total = Vector<float>.Zero;
            var i = 0;
            for (; i <= a.Length - width; i += width)
            {
                Vector.Widen(new Vector<sbyte>(a.Slice(i, width)), out var s0, out var s1);
                Vector.Widen(s0, out var i0, out var i1);
                Vector.Widen(s1, out var i2, out var i3);
                total += Vector.ConvertToSingle(i0) * new Vector<float>(b.Slice(i, f));
                total += Vector.ConvertToSingle(i1) * new Vector<float>(b.Slice(i + f, f));
                total += Vector.ConvertToSingle(i2) * new Vector<float>(b.Slice(i + 2 * f, f));
                total += Vector.ConvertToSingle(i3) * new Vector<float>(b.Slice(i + 3 * f, f));
            }
            var result = Vector.Dot(total, Vector<float>.One);
            for (; i < a.Length; i++) result += a[i] * b[i];
            return result;
        }

        private static void Axpy(Span<float> acc, ReadOnlySpan<sbyte> row, float w)
        {
            var width = Vector<sbyte>.Count;
            var f = Vector<float>.Count;
            var scale = new Vector<float>(w);
            var i = 0;
            for (; i <= acc.Length - width; i += width)
            {
                Vector.Widen(new Vector<sbyte>(row.Slice(i, width)), out var s0, out var s1);
                Vector.Widen(s0, out var i0, out var i1);
                Vector.Widen(s1, out var i2, out var i3);
                (new Vector<float>(acc.Slice(i, f)) + Vector.ConvertToSingle(i0) * scale).CopyTo(acc.Slice(i, f));
                (new Vector<float>(acc.Slice(i + f, f)) + Vector.ConvertToSingle(i1) * scale).CopyTo(acc.Slice(i + f, f));
                (new Vector<float>(acc.Slice(i + 2 * f, f)) + Vector.ConvertToSingle(i2) * scale).CopyTo(acc.Slice(i + 2 * f, f));
                (new Vector<float>(acc.Slice(i + 3 * f, f)) + Vector.ConvertToSingle(i3) * scale).CopyTo(acc.Slice(i + 3 * f, f));
            }
            for (; i < acc.Length; i++) acc[i] += row[i] * w;
        }

        private static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
        {
            var width = Vector<float>.Count;
            var total = Vector<float>.Zero;
            var i = 0;
            for (; i <= a.Length - width; i += width)
                total += new Vector<float>(a.Slice(i, width)) * new Vector<float>(b.Slice(i, width));
            var result = Vector.Dot(total, Vector<float>.One);
            for (; i < a.Length; i++) result += a[i] * b[i];
            return result;
        }

        private static void Axpy(Span<float> acc, ReadOnlySpan<float> row, float w)
        {
            var width = Vector<float>.Count;
            var scale = new Vector<float>(w);
            var i = 0;
            for (; i <= acc.Length - width; i += width)
                (new Vector<float>(acc.Slice(i, width)) + new Vector<float>(row.Slice(i, width)) * scale).CopyTo(acc.Slice(i, width));
            for (; i < acc.Length; i++) acc[i] += row[i] * w;
        }

        private void Compatible(int p, double[] reachOpp, double[] result, long dead)
        {
            var opp = 1 - p;
            Span<double> card = stackalloc double[52];
            var total = 0.0;
            var theirs = _hands[opp];
            for (var o = 0; o < _n[opp]; o++)
            {
                var r = reachOpp[o];
                if (r == 0) continue;
                total += r;
                card[theirs[o].C1] += r;
                card[theirs[o].C2] += r;
            }
            var mine = _hands[p];
            var same = _same[p];
            for (var h = 0; h < _n[p]; h++)
            {
                if ((mine[h].Mask & dead) != 0)
                {
                    result[h] = 0;
                    continue;
                }
                var value = total - card[mine[h].C1] - card[mine[h].C2];
                if (same[h] >= 0) value += reachOpp[same[h]];
                result[h] = value;
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

        private static double[] Rent(int length) => ArrayPool<double>.Shared.Rent(Math.Max(1, length));

        private static void Return(double[] array) => ArrayPool<double>.Shared.Return(array);
    }
}
