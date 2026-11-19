using System;
using System.Collections.Generic;
using System.Linq;

namespace Hud.App.Services.Trainer
{
    /// <summary>Lo que se muestra en vivo mientras la mano esta en juego (solo cartas + posicion).</summary>
    public sealed record LiveAdvice(
        string HeroCards,
        string Board,
        string Street,
        string Position,
        string HandText,
        IReadOnlyList<string> Plan,
        double? EquityVsAny,
        double? EquityVsRange,
        string RangeNote,
        string PotOddsNote,
        string OutsNote)
    {
        public static LiveAdvice Waiting(string message) =>
            new("", "", "", "", message, Array.Empty<string>(), null, null, "", "", "");
    }

    /// <summary>
    /// Consejo en vivo a partir de lo que el lector ve en pantalla. No conoce todavia las acciones
    /// de la mano (PokerStars solo escribe el historial al terminar), asi que muestra el plan
    /// preflop por escenario y, con board, la equity y hasta que tamano de apuesta se puede pagar.
    /// </summary>
    public sealed class TrainerLiveAdvisor
    {
        private static readonly string[] OpenOrder = { "LJ", "HJ", "CO", "BTN", "SB" };

        // Peso por clase de fuerza [aire, debil, media, fuerte] de un rango que ha pagado una apuesta.
        private static readonly double[] CalledBetWeights = { 0.2, 0.75, 1.0, 1.0 };
        private readonly AphPreflopProvider _charts;

        public TrainerLiveAdvisor(AphPreflopProvider charts) => _charts = charts;

        public LiveAdvice Analyze(IReadOnlyList<int> hero, IReadOnlyList<int> board, string? chartPosition)
        {
            if (hero.Count < 2)
                return LiveAdvice.Waiting("Esperando las cartas del heroe...");

            var heroText = TrainerCards.ToText(hero);
            var boardText = TrainerCards.ToText(board);
            var position = string.IsNullOrWhiteSpace(chartPosition) ? "?" : chartPosition!;

            if (board.Count < 3)
            {
                var combo = TrainerCards.ComboName(hero[0], hero[1]);
                var top = PreflopRanking.TopPercentOf(combo);
                var equity = PreflopRanking.EquityVsRandom(combo);
                return new LiveAdvice(
                    heroText,
                    "",
                    "PREFLOP",
                    position,
                    $"{combo}: top {top:0.#}% de las manos",
                    PreflopPlan(combo, position),
                    equity,
                    null,
                    "",
                    "",
                    "");
            }

            var street = board.Count switch { 3 => "FLOP", 4 => "TURN", _ => "RIVER" };
            var insight = HandInsightReader.Read(hero, board);
            var vsAny = EquityCalculator.VsRandom(hero, board, 2500);

            // Rango tipico que sigue en la mano: top 40% preflop y, en cada calle vista,
            // pesado como si hubiera pagado una apuesta (menos aire, mas pares y proyectos).
            var range = HandRange.FromClasses("rango tipico que continua", PreflopRanking.TopPercent(40));
            for (var cards = 3; cards <= board.Count; cards++)
            {
                var visible = board.Take(cards).ToList();
                range = range.WithWeights(range.Description, combo =>
                    CalledBetWeights[HandInsightReader.StrengthClass(combo.A, combo.B, visible)]);
            }

            var vsRange = EquityCalculator.VsRange(hero, board, range.Combos, 2500);
            var outsNote = insight.Outs > 0 && board.Count < 5
                ? board.Count == 3
                    ? $"{insight.Outs} outs: ~{insight.Outs * 2}% por carta, ~{Math.Min(insight.Outs * 4, 100)}% hasta el river (regla 2/4)"
                    : $"{insight.Outs} outs: ~{insight.Outs * 2}% de ligar en el river"
                : "";

            return new LiveAdvice(
                heroText,
                boardText,
                street,
                position,
                insight.Describe(),
                Array.Empty<string>(),
                vsAny,
                vsRange,
                "vs rango tipico que paga (top 40% estrechado por el board)",
                PotOddsNote(vsRange),
                outsNote);
        }

        /// <summary>
        /// Plan preflop completo para la mano: que hacer si nadie abrio y que hacer contra un open
        /// de cada posicion anterior, segun las tablas APH.
        /// </summary>
        private IReadOnlyList<string> PreflopPlan(string combo, string position)
        {
            var plan = new List<string>();
            if (position == "?")
            {
                var opens = OpenOrder.Select(pos => $"{pos} {CodeName(_charts.GetCode("Open", pos, "", combo), "R")}");
                plan.Add("Posicion desconocida. Open por posicion: " + string.Join(" | ", opens));
                return plan;
            }

            if (position != "BB")
            {
                var open = _charts.GetCode("Open", position, "", combo);
                plan.Add($"Si nadie abrio: {CodeName(open, "OPEN RAISE")}");
            }
            else
            {
                plan.Add("Si nadie sube: check (o iso-raise con manos top contra limpers)");
            }

            var heroOrder = TrainerHandParser.PreflopOrder(position);
            foreach (var opener in OpenOrder.Where(pos => TrainerHandParser.PreflopOrder(pos) < heroOrder))
            {
                var code = _charts.GetCode("vs Raise", position, opener, combo);
                if (code is not null)
                    plan.Add($"Si abre {opener}: {CodeName(code, "3BET")}");
            }

            return plan;
        }

        private static string CodeName(string? code, string raiseName) => code switch
        {
            "R" => raiseName,
            "C" => "CALL",
            "F" => "FOLD",
            _ => "sin tabla"
        };

        /// <summary>
        /// Mayor apuesta (en % del bote) que se puede pagar con esa equity: equity = b / (1 + 2b).
        /// </summary>
        private static string PotOddsNote(double equityPct)
        {
            var e = equityPct / 100.0;
            if (e >= 0.5)
                return $"Con {equityPct:0}% puedes pagar cualquier tamano (y apostar por valor).";

            var maxBet = e / (1 - 2 * e);
            if (maxBet >= 1.5)
                return $"Con {equityPct:0}% puedes pagar incluso overbets.";
            if (maxBet < 0.2)
                return $"Con {equityPct:0}% no llegas al precio ni de una apuesta de 1/4 de bote.";
            return $"Con {equityPct:0}% puedes pagar apuestas de hasta ~{maxBet * 100:0}% del bote.";
        }
    }
}
