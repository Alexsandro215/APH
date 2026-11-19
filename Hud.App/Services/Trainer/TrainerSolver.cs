using System;
using System.Collections.Generic;
using System.Linq;
using Hud.App.Services.Solver;

namespace Hud.App.Services.Trainer
{
    /// <summary>Una opcion valorada por el solver: que hacer, cuanto, lo que gana de media y cuanto se retira el rival.</summary>
    public sealed record SolverOption(string Kind, double Amount, double Ev, double? FoldPct, double? EquityWhenCalled)
    {
        /// <summary>"Fold", "Check", "Call", "Bet" o "Raise".</summary>
        public bool IsAggressive => Kind is "Bet" or "Raise";
    }

    /// <summary>Un rival en el bote: su rango estimado, su perfil (si hay muestra) y lo que ya puso en la calle.</summary>
    /// <param name="AllIn">Ya esta all-in: ante una subida no se retira ni pone mas fichas (sigue con todo su rango).</param>
    public sealed record SolverOpponent(IReadOnlyList<WeightedCombo> Range, TrainerVillainProfile? Profile, double Committed, bool AllIn = false);

    /// <summary>
    /// "Solver" de entrenamiento para la decision en vivo (SOLO dinero ficticio: la puerta la pone la
    /// sesion del Modo Entrenador, que corta en mesas de dinero real).
    ///  - Pocas ciegas y dos jugadores: el equilibrio exacto de all-in o retirarse (PushFoldSolver).
    ///  - Despues del flop: el valor esperado de cada opcion en esta calle contra el rango estimado
    ///    de cada rival, con cuanto se retira cada parte de su rango segun el tamano y su perfil.
    /// No es un solver GTO de arbol completo (eso tarda horas por jugada): es la mejor respuesta
    /// contra lo que el rival probablemente tiene y hace, que es lo que sirve para entrenar.
    /// </summary>
    public static class TrainerSolver
    {
        /// <summary>Con mas de 15 ciegas ya hay otras jugadas ademas del all-in: ahi mandan las tablas.</summary>
        public const double PushStackLimit = 15;

        /// <summary>Pagar o no un all-in se puede resolver hasta 25 ciegas (el limite de la tabla del solver).</summary>
        public const double CallStackLimit = PushFoldSolver.MaxChartStack;

        // ------------------------------------------------------------------ all-in o retirarse

        /// <summary>Del boton (o la ciega pequena con todos fuera): ir all-in o no, con la pila efectiva en bb.</summary>
        public static (bool Push, double Frequency, double GainBb)? PushAdvice(string hand, double effectiveStack)
        {
            var solution = Solve(effectiveStack, PushStackLimit);
            if (solution is null)
                return null;
            var frequency = solution.PushOf(hand);
            return (frequency >= 0.5, frequency, solution.PushGainOf(hand));
        }

        /// <summary>Ciega grande contra el all-in de la ciega pequena (sin nadie mas): pagar o no.</summary>
        public static (bool Call, double Frequency, double GainBb)? CallAdvice(string hand, double effectiveStack)
        {
            var solution = Solve(effectiveStack, CallStackLimit);
            if (solution is null)
                return null;
            var frequency = solution.CallOf(hand);
            return (frequency >= 0.5, frequency, solution.CallGainOf(hand));
        }

        private static PushFoldSolution? Solve(double effectiveStack, double limit)
        {
            if (effectiveStack <= 0 || effectiveStack > limit + 1e-9 || PushFoldSolver.Default is not { } solver)
                return null;
            // Medias ciegas: el solver guarda cada pila y la siguiente consulta es inmediata.
            var stack = Math.Clamp(Math.Round(effectiveStack * 2, MidpointRounding.AwayFromZero) / 2, PushFoldSolver.MinStack, limit);
            return solver.Solve(stack);
        }

        // ------------------------------------------------------------------ despues del flop

        /// <summary>
        /// Valor de cada opcion en esta calle. Sin apuesta delante: pasar y apostar 1/3, 1/2, 2/3,
        /// bote (y all-in si la pila es corta). Con apuesta: retirarse, pagar y subir a 3x (y all-in).
        /// El rival responde a una apuesta retirandose con parte de su rango (FoldChance); si sigue,
        /// se juega la equity contra lo que le queda, como si pagara.
        /// </summary>
        public static IReadOnlyList<SolverOption> Postflop(
            IReadOnlyList<int> hero,
            IReadOnlyList<int> board,
            IReadOnlyList<SolverOpponent> opponents,
            double pot,
            double toCall,
            double heroCommitted,
            double? heroStack,
            double currentBet,
            bool canRaise,
            int iterations = 1500)
        {
            var options = new List<SolverOption>();
            if (opponents.Count == 0 || pot <= 0)
                return options;

            var street = board.Count switch { 3 => "FLOP", 4 => "TURN", _ => "RIVER" };
            var ranges = opponents.Select(o => o.Range).ToList();
            var equity = EquityCalculator.VsRanges(hero, board, ranges, iterations) / 100.0;
            var stackBehind = heroStack is { } s && s > 0 ? s : double.PositiveInfinity;

            if (toCall > 1e-9)
            {
                var call = Math.Min(toCall, stackBehind);
                options.Add(new SolverOption("Fold", 0, 0, null, null));
                options.Add(new SolverOption("Call", call, equity * (pot + call) - call, null, null));
                if (canRaise && stackBehind > call + 1e-9)
                {
                    var maxTo = heroCommitted + stackBehind;
                    foreach (var to in new[] { currentBet * 3, maxTo }.Where(t => t > currentBet * 1.99).Select(t => Math.Min(t, maxTo)).Distinct())
                    {
                        // El all-in solo si no es una sobreapuesta enorme frente al bote.
                        if (to >= maxTo - 1e-9 && to - currentBet > 1.6 * (pot + call) && to > currentBet * 3 + 1e-9)
                            continue;
                        options.Add(Aggression("Raise", hero, board, opponents, pot, heroCommitted, currentBet, to, street, iterations));
                    }
                }
            }
            else
            {
                options.Add(new SolverOption("Check", 0, equity * pot, null, null));
                foreach (var fraction in new[] { 0.33, 0.5, 0.75, 1.0 })
                {
                    // Medias ciegas, como se apuesta en la mesa (minimo 1 bb).
                    var bet = Math.Min(Math.Max(1, Math.Round(pot * fraction * 2, MidpointRounding.AwayFromZero) / 2), stackBehind);
                    options.Add(Aggression("Bet", hero, board, opponents, pot, 0, 0, bet, street, iterations));
                }
                if (stackBehind <= 1.5 * pot && !options.Any(o => Math.Abs(o.Amount - stackBehind) < 0.01))
                    options.Add(Aggression("Bet", hero, board, opponents, pot, 0, 0, stackBehind, street, iterations));
            }

            return options
                .GroupBy(o => (o.Kind, Math.Round(o.Amount, 1)))
                .Select(g => g.First())
                .OrderByDescending(o => o.Ev)
                .ToList();
        }

        /// <summary>Apostar o subir hasta "to": se retiran todos con FoldAll; si alguien sigue, se juega lo que le queda.</summary>
        private static SolverOption Aggression(
            string kind, IReadOnlyList<int> hero, IReadOnlyList<int> board, IReadOnlyList<SolverOpponent> opponents,
            double pot, double heroCommitted, double currentBet, double to, string street, int iterations)
        {
            var add = to - heroCommitted;
            // Tamano como se mide en poker: lo que se sube por encima de la apuesta, sobre el bote
            // despues de igualarla (una apuesta sin nada delante: sobre el bote).
            var call = Math.Max(0, currentBet - heroCommitted);
            var size = (to - currentBet) / Math.Max(0.5, pot + call);
            var foldAll = 1.0;
            var continuing = new List<IReadOnlyList<WeightedCombo>>();
            var expectedCalls = 0.0;
            foreach (var opponent in opponents)
            {
                // Quien ya esta all-in no puede retirarse ni pagar mas: sigue con todo su rango y lo que
                // puso ya esta en el bote. Antes se le suponia retirarse con parte del rango y pagar la
                // subida entera (Q-high contra un all-in "ganaba" por esa fold equity imposible).
                if (opponent.AllIn)
                {
                    foldAll = 0;
                    continuing.Add(opponent.Range);
                    continue;
                }

                var total = 0.0;
                var folded = 0.0;
                var kept = new List<WeightedCombo>(opponent.Range.Count);
                foreach (var combo in opponent.Range)
                {
                    var fold = FoldChance(HandInsightReader.StrengthClass(combo.A, combo.B, board), size, street, opponent.Profile);
                    total += combo.Weight;
                    folded += combo.Weight * fold;
                    if (fold < 1)
                        kept.Add(combo with { Weight = combo.Weight * (1 - fold) });
                }

                var share = total > 0 ? folded / total : 1;
                foldAll *= share;
                expectedCalls += (1 - share) * Math.Max(0, to - opponent.Committed);
                if (kept.Count > 0 && share < 1)
                    continuing.Add(kept);
            }

            if (continuing.Count == 0 || foldAll >= 0.9999)
                return new SolverOption(kind, to, pot, 1, null);

            var whenCalled = EquityCalculator.VsRanges(hero, board, continuing, iterations) / 100.0;
            var called = expectedCalls / Math.Max(1e-9, 1 - foldAll);
            var ev = foldAll * pot + (1 - foldAll) * (whenCalled * (pot + add + called) - add);
            return new SolverOption(kind, to, ev, foldAll, whenCalled);
        }

        /// <summary>
        /// Cuanto se retira una mano del rango ante una apuesta de "size" botes, por su fuerza: el aire
        /// casi siempre, una mano debil a menudo, una media poco y una fuerte nunca. Mas grande la
        /// apuesta, mas se retira. Se ajusta al rival: su fold a c-bet si hay muestra, menos si es un
        /// pagador y mas si es un nit. En el river el aire ya no tiene nada que esperar.
        /// </summary>
        public static double FoldChance(int strengthClass, double size, string street, TrainerVillainProfile? profile)
        {
            size = Math.Clamp(size, 0.1, 2.5);
            // Apuesta de 1/3, 1/2 y bote: aire 55/60/75%, debil 28/35/55%, media 8/11/20%, fuerte 0%.
            var fold = strengthClass switch
            {
                0 => 0.45 + 0.30 * size + (street == "RIVER" ? 0.08 : 0),
                1 => 0.15 + 0.40 * size,
                2 => 0.02 + 0.18 * size,
                _ => 0
            };

            if (profile is { HasSample: true } p)
            {
                var factor = p.FoldVsCBet > 0 ? Math.Clamp(p.FoldVsCBet / 45.0, 0.5, 1.6) : 1.0;
                if (p.IsCallingStation) factor *= 0.6;
                if (p.IsNit) factor *= 1.2;
                fold *= factor;
            }

            return Math.Clamp(fold, 0, strengthClass switch { 0 => 0.9, 1 => 0.75, 2 => 0.4, _ => 0 });
        }
    }
}
