using Hud.App.Services.Trainer;

namespace Hud.App.Services.Simulator
{
    public enum SimStreet { Preflop, Flop, Turn, River, Showdown }

    public enum SimActionKind { Fold, Check, Call, Bet, Raise }

    /// <summary>Una accion de la mano. Amount: lo que lleva puesto en la calle despues de actuar.</summary>
    public sealed record SimAction(int Seat, SimStreet Street, SimActionKind Kind, double Amount, double Added, bool AllIn, double PotAfter);

    /// <summary>Lo que puede hacer el jugador al que le toca.</summary>
    public sealed record SimOptions(bool CanCheck, double CallAmount, bool CanRaise, double MinRaiseTo, double MaxRaiseTo)
    {
        public bool FacingBet => CallAmount > 1e-9;
    }

    public sealed class SimPlayer
    {
        public SimPlayer(int seat, string name, SimProfile? profile, double stack)
        {
            Seat = seat;
            Name = name;
            Profile = profile;
            Stack = stack;
            StartStack = stack;
        }

        public int Seat { get; }
        public string Name { get; }
        /// <summary>null = el jugador humano.</summary>
        public SimProfile? Profile { get; }
        public bool IsHero => Profile is null;
        public double StartStack { get; }
        public double Stack { get; internal set; }
        public int[] Cards { get; internal set; } = new int[2];
        public string Position { get; internal set; } = "";
        public bool Folded { get; internal set; }
        public double StreetBet { get; internal set; }
        public double TotalBet { get; internal set; }
        public bool HasActed { get; internal set; }
        public bool CanOnlyCall { get; internal set; }
        public string LastActionText { get; internal set; } = "";
        public double Won { get; internal set; }
        public bool IsAllIn => !Folded && Stack <= 1e-9;
        public bool InHand => !Folded;
        public double Net => Won - TotalBet;
    }

    /// <summary>
    /// Una mano de No Limit Hold'em de 2 a 6 jugadores, en ciegas grandes (ciega pequena 0.5,
    /// ciega grande 1). Reglas: subida minima, all-in por menos de una subida completa (no reabre
    /// la apuesta), botes paralelos y reparto en el showdown. En heads-up el boton es la ciega
    /// pequena: habla primero antes del flop y ultimo despues.
    /// </summary>
    public sealed class SimHand
    {
        private readonly List<int> _deck;
        private int _deckIndex;
        private readonly List<int>? _presetBoard;

        public SimHand(IReadOnlyList<SimPlayer> players, int buttonIndex, Random random,
            IReadOnlyDictionary<int, int[]>? presetCards = null, IReadOnlyList<int>? presetBoard = null)
        {
            if (players.Count is < 2 or > 6)
                throw new ArgumentException("De 2 a 6 jugadores", nameof(players));

            Players = players;
            ButtonIndex = buttonIndex;
            _presetBoard = presetBoard?.ToList();

            var used = new HashSet<int>();
            if (presetCards is not null)
                foreach (var cards in presetCards.Values) used.UnionWith(cards);
            if (presetBoard is not null)
                used.UnionWith(presetBoard);
            _deck = Enumerable.Range(0, 52).Where(c => !used.Contains(c)).OrderBy(_ => random.Next()).ToList();

            AssignPositions();
            foreach (var player in Players)
                player.Cards = presetCards is not null && presetCards.TryGetValue(player.Seat, out var preset) ? preset.ToArray() : new[] { Draw(), Draw() };

            PostBlinds();
        }

        public IReadOnlyList<SimPlayer> Players { get; }
        public int ButtonIndex { get; }
        public List<int> Board { get; } = new();
        public List<SimAction> Actions { get; } = new();
        public SimStreet Street { get; private set; } = SimStreet.Preflop;
        public double CurrentBet { get; private set; }
        public double LastRaiseSize { get; private set; } = 1;
        public int ToActIndex { get; private set; } = -1;
        public bool IsOver { get; private set; }
        public bool HeadsUp => Players.Count == 2;

        public double Pot => Players.Sum(p => p.TotalBet);
        public SimPlayer? ToAct => ToActIndex >= 0 ? Players[ToActIndex] : null;
        public SimPlayer Hero => Players.First(p => p.IsHero);
        public IEnumerable<SimPlayer> Active => Players.Where(p => p.InHand);

        // ---------------------------------------------------------------- inicio

        private void AssignPositions()
        {
            var n = Players.Count;
            for (var offset = 0; offset < n; offset++)
            {
                var player = Players[(ButtonIndex + offset) % n];
                player.Position = n == 2
                    ? (offset == 0 ? "SB" : "BB")
                    : offset switch
                    {
                        0 => "BTN",
                        1 => "SB",
                        2 => "BB",
                        _ when offset == n - 1 => "CO",
                        _ when offset == n - 2 => "HJ",
                        _ => "LJ"
                    };
            }
        }

        private int SmallBlindIndex => HeadsUp ? ButtonIndex : (ButtonIndex + 1) % Players.Count;
        private int BigBlindIndex => HeadsUp ? (ButtonIndex + 1) % 2 : (ButtonIndex + 2) % Players.Count;

        private void PostBlinds()
        {
            Put(Players[SmallBlindIndex], 0.5);
            Players[SmallBlindIndex].LastActionText = "SB";
            Put(Players[BigBlindIndex], 1);
            Players[BigBlindIndex].LastActionText = "BB";
            CurrentBet = 1;
            LastRaiseSize = 1;
            ToActIndex = HeadsUp ? SmallBlindIndex : NextCanAct(BigBlindIndex);
        }

        private int Draw() => _deck[_deckIndex++];

        private void Put(SimPlayer player, double amount)
        {
            var real = Math.Min(amount, player.Stack);
            player.Stack -= real;
            player.StreetBet += real;
            player.TotalBet += real;
        }

        // ---------------------------------------------------------------- acciones

        public SimOptions Options()
        {
            var p = ToAct ?? throw new InvalidOperationException("No le toca a nadie");
            var call = Math.Min(Math.Max(0, CurrentBet - p.StreetBet), p.Stack);
            var maxTo = p.StreetBet + p.Stack;
            var others = Players.Where(o => o != p && o.InHand && !o.IsAllIn).ToList();
            var canRaise = !p.CanOnlyCall && maxTo > CurrentBet + 1e-9 && others.Count > 0;
            var minTo = Math.Min(maxTo, CurrentBet + Math.Max(LastRaiseSize, 1));
            return new SimOptions(call <= 1e-9, call, canRaise, minTo, maxTo);
        }

        public void Fold() => Apply(SimActionKind.Fold, 0);

        public void CheckOrCall() => Apply(Options().CanCheck ? SimActionKind.Check : SimActionKind.Call, 0);

        /// <summary>Apuesta o subida "hasta" ese total en la calle (se ajusta a lo permitido).</summary>
        public void RaiseTo(double to) => Apply(CurrentBet > 1e-9 ? SimActionKind.Raise : SimActionKind.Bet, to);

        private void Apply(SimActionKind kind, double to)
        {
            var p = ToAct ?? throw new InvalidOperationException("La mano termino");
            var options = Options();
            var before = p.StreetBet;

            switch (kind)
            {
                case SimActionKind.Fold:
                    if (options.CanCheck) { Apply(SimActionKind.Check, 0); return; }
                    p.Folded = true;
                    p.LastActionText = "Fold";
                    break;
                case SimActionKind.Check:
                    if (!options.CanCheck) { Apply(SimActionKind.Call, 0); return; }
                    p.LastActionText = "Check";
                    break;
                case SimActionKind.Call:
                    if (options.CanCheck) { Apply(SimActionKind.Check, 0); return; }
                    Put(p, options.CallAmount);
                    p.LastActionText = "Call";
                    break;
                default:
                    if (!options.CanRaise) { Apply(options.CanCheck ? SimActionKind.Check : SimActionKind.Call, 0); return; }
                    to = Math.Clamp(Math.Round(to * 2, MidpointRounding.AwayFromZero) / 2, options.MinRaiseTo, options.MaxRaiseTo);
                    Put(p, to - p.StreetBet);
                    var raiseSize = p.StreetBet - CurrentBet;
                    var full = raiseSize >= LastRaiseSize - 1e-9;
                    if (full)
                        LastRaiseSize = raiseSize;
                    CurrentBet = Math.Max(CurrentBet, p.StreetBet);
                    foreach (var other in Players.Where(o => o != p && o.InHand && !o.IsAllIn))
                    {
                        // Una subida incompleta (all-in por menos) no reabre la apuesta a quien ya actuo.
                        if (!full && other.HasActed) other.CanOnlyCall = true;
                        other.HasActed = false;
                    }
                    p.LastActionText = kind == SimActionKind.Bet ? "Bet" : "Raise";
                    break;
            }

            p.HasActed = true;
            Actions.Add(new SimAction(p.Seat, Street, kind, p.StreetBet, p.StreetBet - before, p.IsAllIn, Pot));
            Advance();
        }

        // ---------------------------------------------------------------- avance de la mano

        private void Advance()
        {
            if (Active.Count() == 1)
            {
                Finish();
                return;
            }

            var pending = Players.Where(o => o.InHand && !o.IsAllIn && (!o.HasActed || o.StreetBet < CurrentBet - 1e-9)).ToList();
            if (pending.Count > 0)
            {
                ToActIndex = NextCanAct(ToActIndex, requirePending: true);
                return;
            }

            NextStreet();
        }

        private int NextCanAct(int fromIndex, bool requirePending = false)
        {
            var n = Players.Count;
            for (var step = 1; step <= n; step++)
            {
                var index = (fromIndex + step) % n;
                var p = Players[index];
                if (!p.InHand || p.IsAllIn) continue;
                if (requirePending && p.HasActed && p.StreetBet >= CurrentBet - 1e-9) continue;
                return index;
            }
            return -1;
        }

        private void NextStreet()
        {
            foreach (var p in Players)
            {
                p.StreetBet = 0;
                p.HasActed = false;
                p.CanOnlyCall = false;
                if (p.InHand && !p.IsAllIn) p.LastActionText = "";
            }
            CurrentBet = 0;
            LastRaiseSize = 1;

            if (Street == SimStreet.River)
            {
                Finish();
                return;
            }

            Street++;
            DealBoard(Street == SimStreet.Flop ? 3 : 1);

            // Si a lo sumo uno puede seguir apostando, se reparten las cartas que faltan.
            if (Players.Count(p => p.InHand && !p.IsAllIn) <= 1)
            {
                while (Street < SimStreet.River)
                {
                    Street++;
                    DealBoard(1);
                }
                Finish();
                return;
            }

            // Despues del flop habla primero el primero activo a la izquierda del boton.
            ToActIndex = NextCanAct(ButtonIndex);
        }

        private void DealBoard(int count)
        {
            for (var i = 0; i < count; i++)
            {
                var index = Board.Count;
                Board.Add(_presetBoard is not null && index < _presetBoard.Count ? _presetBoard[index] : Draw());
            }
        }

        private void Finish()
        {
            ToActIndex = -1;
            IsOver = true;
            var contenders = Active.ToList();
            if (contenders.Count == 1)
            {
                contenders[0].Won = Pot;
                Street = SimStreet.Showdown;
                return;
            }

            while (Board.Count < 5) DealBoard(1);
            Street = SimStreet.Showdown;
            var scores = contenders.ToDictionary(p => p, p => FastHandEvaluator.Evaluate(p.Cards.Concat(Board).ToArray()));

            // Botes paralelos por niveles de lo que puso cada uno.
            var levels = Players.Select(p => p.TotalBet).Where(t => t > 1e-9).Distinct().OrderBy(t => t).ToList();
            var previous = 0.0;
            foreach (var level in levels)
            {
                var layer = Players.Sum(p => Math.Max(0, Math.Min(p.TotalBet, level) - previous));
                var eligible = contenders.Where(p => p.TotalBet >= level - 1e-9).ToList();
                if (eligible.Count == 0)
                {
                    // Nadie que siga en la mano puso tanto (lo puso alguien que se retiro): va al mejor de los que quedan.
                    eligible = contenders;
                }
                var best = eligible.Max(p => scores[p]);
                var winners = eligible.Where(p => scores[p] == best).ToList();
                foreach (var w in winners) w.Won += layer / winners.Count;
                previous = level;
            }

            // Lo que alguien puso de mas y nadie pago vuelve a ese jugador: ya va incluido en su capa.
        }

        /// <summary>Puntuacion de la mano de un jugador al final (para mostrar quien gano).</summary>
        public int ScoreOf(SimPlayer player) =>
            Board.Count == 5 ? FastHandEvaluator.Evaluate(player.Cards.Concat(Board).ToArray()) : -1;
    }
}
