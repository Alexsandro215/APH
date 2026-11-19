using System.IO;
using System.Text.Json;
using Hud.App.Services.Trainer;

namespace Hud.App.Services.Simulator
{
    /// <summary>
    /// Un rubro de entrenamiento: contra quien se juega, en que mesa y que manos o mesas se reparten
    /// mas para que salga la situacion que se quiere practicar.
    /// </summary>
    public sealed record SimDrill(
        string Id,
        bool HeadsUp,
        SimProfile[] Bots,
        bool ChooseProfile,
        IReadOnlyList<string>? HeroHands,
        double HeroHandShare,
        bool DrawBoards,
        bool HeroAlwaysButton,
        bool RandomBots)
    {
        public string Name => LocalizationManager.Text("Sim.Drill." + Id);
        public string Description => LocalizationManager.Text("Sim.Drill." + Id + ".Desc");
    }

    public static class SimDrills
    {
        private static readonly string[] StrongButBeatable =
            { "QQ", "JJ", "TT", "AKs", "AKo", "AQs", "AQo", "AJs", "KQs", "KQo", "AJo" };

        private static readonly string[] Medium = TrainerCards.AllClasses
            .Where(c => PreflopRanking.TopPercentOf(c) is > 12 and < 45).ToArray();

        private static readonly string[] Premiumish = PreflopRanking.TopPercent(18).ToArray();

        private static readonly string[] Suited = TrainerCards.AllClasses
            .Where(c => c.EndsWith('s') && PreflopRanking.TopPercentOf(c) < 60).ToArray();

        public static readonly IReadOnlyList<SimDrill> All = new SimDrill[]
        {
            new("hu-nit", true, new[] { SimProfile.Nit }, false, StrongButBeatable, 0.6, false, false, false),
            new("hu-maniac", true, new[] { SimProfile.Maniac }, false, Medium, 0.6, false, false, false),
            new("hu-station", true, new[] { SimProfile.Station }, false, Premiumish, 0.6, false, false, false),
            new("hu-blinds", true, new[] { SimProfile.Tag }, false, null, 0, false, false, true),
            new("hu-cbet", true, new[] { SimProfile.Tag }, false, TrainerCards.AllClasses.Where(c => PreflopRanking.TopPercentOf(c) < 55).ToArray(), 1, false, true, false),
            new("hu-draws", true, new[] { SimProfile.Lag }, false, Suited, 0.85, true, false, false),
            new("hu-free", true, new[] { SimProfile.Tag }, true, null, 0, false, false, false),
            new("6-playmoney", false, new[] { SimProfile.Fish, SimProfile.Fish, SimProfile.Station, SimProfile.Maniac, SimProfile.Nit }, false, null, 0, false, false, false),
            new("6-limpers", false, new[] { SimProfile.Fish, SimProfile.Fish, SimProfile.Station, SimProfile.Station, SimProfile.Tag }, false, null, 0, false, false, false),
            new("6-squeeze", false, new[] { SimProfile.Lag, SimProfile.Fish, SimProfile.Station, SimProfile.Lag, SimProfile.Fish }, false, null, 0, false, false, false),
            new("6-nit", false, new[] { SimProfile.Nit, SimProfile.Nit, SimProfile.Tag, SimProfile.Tag, SimProfile.Fish }, false, StrongButBeatable, 0.5, false, false, false),
            new("6-tough", false, new[] { SimProfile.Tag, SimProfile.Tag, SimProfile.Lag, SimProfile.Lag, SimProfile.Maniac }, false, null, 0, false, false, false),
            new("6-free", false, new[] { SimProfile.Tag, SimProfile.Tag, SimProfile.Tag, SimProfile.Tag, SimProfile.Tag }, false, null, 0, false, false, true),
        };
    }

    /// <summary>
    /// Una sesion del simulador: reparte manos del rubro elegido, hace jugar a las maquinas, lee sus
    /// rangos y guarda cada decision del heroe para revisarla al final de la mano.
    /// </summary>
    public sealed class SimSession
    {
        public const double StartingStack = 100;
        private readonly Random _random;
        private int _button;

        public SimSession(SimDrill drill, SimProfile? chosenProfile = null, Random? random = null)
        {
            Drill = drill;
            ChosenProfile = chosenProfile;
            _random = random ?? new Random();
            _button = _random.Next(drill.HeadsUp ? 2 : 6);
        }

        public SimDrill Drill { get; }
        public SimProfile? ChosenProfile { get; }
        public SimHand? Hand { get; private set; }
        public SimRangeTracker? Tracker { get; private set; }
        public List<SimDecision> Decisions { get; } = new();

        public bool HeroToAct => Hand is { IsOver: false } && Hand.ToAct?.IsHero == true;
        public bool BotToAct => Hand is { IsOver: false } && Hand.ToAct is { IsHero: false };

        public SimHand NewHand()
        {
            var bots = BotProfiles();
            var players = new List<SimPlayer> { new(0, LocalizationManager.Text("Sim.You"), null, StartingStack) };
            for (var i = 0; i < bots.Count; i++)
                players.Add(new SimPlayer(i + 1, SimProfiles.Name(bots[i]), bots[i], StartingStack));

            var n = players.Count;
            if (Drill.HeroAlwaysButton)
                _button = 0;
            else
                _button = (_button + 1) % n;

            var preset = new Dictionary<int, int[]>();
            if (Drill.HeroHands is { Count: > 0 } && _random.NextDouble() < Drill.HeroHandShare)
            {
                var cls = Drill.HeroHands[_random.Next(Drill.HeroHands.Count)];
                var combos = TrainerCards.ExpandClass(cls).ToList();
                var (a, b) = combos[_random.Next(combos.Count)];
                preset[0] = new[] { a, b };
            }

            int[]? board = null;
            if (Drill.DrawBoards && preset.TryGetValue(0, out var heroCards))
                board = DrawFlop(heroCards);

            Hand = new SimHand(players, _button, _random, preset, board);
            Tracker = new SimRangeTracker(Hand);
            Decisions.Clear();
            return Hand;
        }

        private List<SimProfile> BotProfiles()
        {
            if (Drill.ChooseProfile && ChosenProfile is { } chosen)
                return new List<SimProfile> { chosen };
            if (Drill.RandomBots)
            {
                var all = Enum.GetValues<SimProfile>();
                return Enumerable.Range(0, Drill.Bots.Length).Select(_ => all[_random.Next(all.Length)]).ToList();
            }
            return Drill.Bots.OrderBy(_ => _random.Next()).ToList();
        }

        /// <summary>Un flop con proyecto para el heroe: dos cartas de su palo, o conectado con sus cartas.</summary>
        private int[] DrawFlop(int[] hero)
        {
            var used = new HashSet<int>(hero);
            int Pick(Func<int, bool> filter)
            {
                var options = Enumerable.Range(0, 52).Where(c => !used.Contains(c) && filter(c)).ToList();
                if (options.Count == 0)
                    options = Enumerable.Range(0, 52).Where(c => !used.Contains(c)).ToList();
                var card = options[_random.Next(options.Count)];
                used.Add(card);
                return card;
            }

            var suit = TrainerCards.Suit(hero[0]);
            if (TrainerCards.Suit(hero[1]) == suit && _random.NextDouble() < 0.7)
                return new[] { Pick(c => TrainerCards.Suit(c) == suit), Pick(c => TrainerCards.Suit(c) == suit), Pick(c => TrainerCards.Suit(c) != suit) };

            // Proyecto de escalera: dos cartas que rodean las del heroe.
            var high = Math.Max(TrainerCards.Rank(hero[0]), TrainerCards.Rank(hero[1]));
            var ranks = new[] { high - 2, high - 3, high + 1 }.Where(r => r is >= 0 and <= 12).ToList();
            var first = Pick(c => ranks.Contains(TrainerCards.Rank(c)) && TrainerCards.Suit(c) != suit);
            var second = Pick(c => ranks.Contains(TrainerCards.Rank(c)) && TrainerCards.Rank(c) != TrainerCards.Rank(first) && TrainerCards.Suit(c) != suit);
            return new[] { first, second, Pick(c => TrainerCards.Rank(c) < 5 && TrainerCards.Suit(c) != suit) };
        }

        /// <summary>Hace hablar a la maquina a la que le toca. Devuelve que hizo.</summary>
        public (SimPlayer Player, SimActionKind Kind) BotStep()
        {
            var hand = Hand!;
            var player = hand.ToAct!;
            var (kind, to) = SimBot.Decide(hand, _random);
            Tracker!.Observe(hand, player, kind);
            SimBot.Apply(hand, kind, to);
            return (player, kind);
        }

        /// <summary>El heroe actua: se guarda la foto de la decision para revisarla.</summary>
        public void HeroAct(SimActionKind kind, double to = 0)
        {
            var hand = Hand!;
            var decision = SimReviewer.Capture(hand, Tracker!, Decisions.Count);
            Decisions.Add(decision with { Taken = kind, TakenTo = to });
            SimBot.Apply(hand, kind, to);
        }

        public List<SimDecisionReview> ReviewAll() =>
            Decisions.Select(d => SimReviewer.Review(d)).ToList();
    }

    /// <summary>Progreso guardado del simulador por rubro.</summary>
    public sealed class SimProgress
    {
        public Dictionary<string, double[]> Drills { get; set; } = new(); // id -> [manos, bb, decisiones, buenas]

        /// <summary>Interruptor "Explicación técnica": rango, equity, pot odds y valor de cada opción. Apagado de inicio.</summary>
        public bool ShowTechnical { get; set; }

        public static string FilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "APH", "simulador.json");

        public static SimProgress Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<SimProgress>(File.ReadAllText(FilePath)) ?? new SimProgress();
            }
            catch (Exception ex)
            {
                AppLog.Warn("No se pudo leer el progreso del simulador", ex);
            }
            return new SimProgress();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                AppLog.Warn("No se pudo guardar el progreso del simulador", ex);
            }
        }

        public void Record(string drillId, double net, IReadOnlyList<SimDecisionReview> reviews)
        {
            if (!Drills.TryGetValue(drillId, out var row))
                Drills[drillId] = row = new double[4];
            row[0] += 1;
            row[1] += net;
            row[2] += reviews.Count;
            row[3] += reviews.Count(r => r.Verdict == SimVerdict.Good);
        }
    }
}
