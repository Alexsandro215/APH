using System.Globalization;
using System.IO;

namespace Hud.App.Services
{
    /// <summary>
    /// Tablas preflop de APH para la pagina de tablas y la practica: las de referencia
    /// (APH_Reference_100bb_6max.csv, las mismas del Modo Entrenador) mas los rangos borrador de
    /// contra limp, squeeze y heads-up (APH_Rangos_Borrador_6max.txt).
    /// </summary>
    public static class PreflopLibrary
    {
        private static readonly Lazy<AphPreflopProvider> Shared = new(() => Load());

        public static AphPreflopProvider Charts => Shared.Value;

        public static AphPreflopProvider Load(string? dataFolder = null)
        {
            var folder = dataFolder ?? Path.Combine(AppContext.BaseDirectory, "Data");
            var charts = new AphPreflopProvider(Path.Combine(folder, "APH_Reference_100bb_6max.csv"));
            charts.LoadRangeFile(Path.Combine(folder, "APH_Rangos_Borrador_6max.txt"));
            return charts;
        }
    }

    /// <summary>Situaciones preflop: claves, nombres, acciones y tamanos de apuesta.</summary>
    public static class PreflopScenarios
    {
        public const string Open = "OPEN";
        public const string VsLimp = "VS LIMP";
        public const string VsRaise = "VS RAISE";
        public const string Squeeze = "SQUEEZE";
        public const string Vs3Bet = "VS 3BET";
        public const string Vs4Bet = "VS 4BET";
        public const string Vs5Bet = "VS 5BET";
        public const string HuOpen = "HU OPEN";
        public const string HuVsRaise = "HU VS RAISE";
        public const string HuVs3Bet = "HU VS 3BET";
        public const string HuVs4Bet = "HU VS 4BET";

        /// <summary>Heads-up con pocas ciegas: all-in o retirarse (lo resuelve el solver, no una tabla escrita).</summary>
        public const string HuAllIn = "HU ALL-IN";
        public const string AllIn = "ALL-IN";

        public static readonly string[] SixMax = { Open, VsLimp, VsRaise, Squeeze, Vs3Bet, Vs4Bet, Vs5Bet };
        public static readonly string[] HeadsUp = { HuOpen, HuVsRaise, HuVs3Bet, HuVs4Bet };

        /// <summary>Orden en que hablan preflop en 6-max.</summary>
        public static readonly string[] PositionOrder = { "LJ", "HJ", "CO", "BTN", "SB", "BB" };

        public static bool IsHeadsUp(string scenario) => scenario.StartsWith("HU ", StringComparison.Ordinal);

        /// <summary>Tablas escritas por APH como punto de partida, pendientes de revisar por el coach.</summary>
        public static bool IsDraft(string scenario) => scenario is VsLimp or Squeeze || (IsHeadsUp(scenario) && scenario != HuAllIn);

        /// <summary>"HU VS RAISE" -> "VS RAISE": misma situacion, otra mesa.</summary>
        public static string BaseOf(string scenario) => IsHeadsUp(scenario) ? scenario[3..] : scenario;

        private static string Key(string scenario) => BaseOf(scenario) switch
        {
            VsLimp => "VsLimp",
            VsRaise => "VsRaise",
            Squeeze => "Squeeze",
            Vs3Bet => "Vs3Bet",
            Vs4Bet => "Vs4Bet",
            Vs5Bet => "Vs5Bet",
            _ => "Open"
        };

        public static string Label(string scenario) => T("Preflop.Scenario." + Key(scenario));

        public static string Description(string scenario) => T("Preflop.Scenario." + Key(scenario) + ".Desc");

        public static string ChartTitle(string scenario, string hero, string villain) =>
            string.Format(CultureInfo.CurrentCulture,
                T((IsHeadsUp(scenario) ? "Preflop.HuTitle." : "Preflop.ChartTitle.") + Key(scenario)),
                PositionLabel(hero, scenario), PositionLabel(villain, scenario));

        /// <summary>Cuanto subir en esta situacion.</summary>
        public static string Sizing(string scenario, string hero)
        {
            var outOfPosition = hero is "SB" or "BB";
            var key = IsHeadsUp(scenario) ? "Preflop.Size.Hu" + Key(scenario) : "Preflop.Size." + Key(scenario);
            if (!IsHeadsUp(scenario) && BaseOf(scenario) is Open or VsLimp or VsRaise or Squeeze && outOfPosition)
                key += ".Blinds";
            return T(key);
        }

        public static string VillainQuestion(string scenario) => T(BaseOf(scenario) switch
        {
            Vs3Bet => "Preflop.Who3Bet",
            Vs4Bet => "Preflop.Who4Bet",
            Vs5Bet => "Preflop.Who5Bet",
            VsLimp => "Preflop.WhoLimped",
            Squeeze => "Preflop.WhoOpened",
            _ => "Preflop.WhoRaised"
        });

        public static string ActionLabel(string scenario, string hero, string code) => code switch
        {
            // Con pocas ciegas solo hay all-in (el boton), pagarlo (la ciega grande) o retirarse.
            "R" when BaseOf(scenario) == AllIn => T("Preflop.Action.AllIn"),
            "C" when BaseOf(scenario) == AllIn => T("Preflop.Action.CallAllIn"),
            "R" => T(BaseOf(scenario) switch
            {
                Open => "Preflop.Action.Open",
                VsRaise => "Preflop.Action.3Bet",
                Vs3Bet => "Preflop.Action.4Bet",
                Vs4Bet => "Preflop.Action.5Bet",
                VsLimp => "Preflop.Action.Iso",
                Squeeze => "Preflop.Action.Squeeze",
                _ => "Preflop.Action.Raise"
            }),
            "C" => BaseOf(scenario) switch
            {
                VsLimp => T(hero == "BB" ? "Preflop.Action.Check" : hero == "SB" ? "Preflop.Action.Complete" : "Preflop.Action.Overlimp"),
                // Al abrir, "pagar" es entrar solo pagando la ciega: un limp.
                Open => T("Preflop.Action.OpenLimp"),
                _ => T("Preflop.Action.Call")
            },
            _ => T("Preflop.Action.Fold")
        };

        /// <summary>En 6-max LJ es UTG y HJ es MP. En heads-up la ciega pequena es el boton.</summary>
        public static string PositionLabel(string position, string scenario) =>
            IsHeadsUp(scenario)
                ? position switch { "SB" => T("Preflop.HuButton"), "BB" => T("Preflop.HuBigBlind"), _ => position }
                : position switch { "LJ" => "UTG", "HJ" => "MP", _ => position };

        public static string PositionHelp(string position, string scenario) =>
            IsHeadsUp(scenario) ? T("Preflop.HuPos." + position) : T("Preflop.Pos." + position);

        /// <summary>Combinaciones de una clase de mano: pareja 6, del mismo palo 4, de distinto palo 12.</summary>
        public static int Combos(string hand) => hand.Length == 2 ? 6 : hand.EndsWith('s') ? 4 : 12;

        private static string T(string key) => LocalizationManager.Text(key);
    }
}
