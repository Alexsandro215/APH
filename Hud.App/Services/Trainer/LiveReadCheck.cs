using System;
using System.Collections.Generic;
using System.Linq;

namespace Hud.App.Services.Trainer
{
    /// <summary>
    /// Al terminar la mano, el historial dice la verdad: se compara con lo que se leyo en pantalla
    /// cuando se dio el consejo (cartas del heroe, board de esa calle y posicion). Asi se sabe si un
    /// consejo raro vino de una mala lectura y cuantas manos se leen mal en cada mesa.
    /// </summary>
    public static class LiveReadCheck
    {
        /// <summary>Null si todo lo leido coincide con el historial; si no, que se leyo mal.</summary>
        public static string? Compare(LiveDecision read, TrainerHandReport hand) =>
            Compare(read.ReadHero, read.ReadBoard, read.ReadPosition, hand.HeroCards, hand.Board, hand.Position);

        public static string? Compare(string? readHero, string? readBoard, string? readPosition,
            string? realHero, string? realBoard, string? realPosition)
        {
            var problems = new List<string>();

            var hero = Cards(readHero);
            var trueHero = Cards(realHero);
            if (hero.Count > 0 && trueHero.Count == 2 && !hero.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(trueHero))
                problems.Add($"cartas leídas {string.Join(" ", hero)}, eran {string.Join(" ", trueHero)}");

            var board = Cards(readBoard);
            var trueBoard = Cards(realBoard);
            if (board.Count > 0 && trueBoard.Count >= board.Count)
            {
                // El board de esa calle: las primeras cartas del historial (el flop, en cualquier orden).
                var expected = trueBoard.Take(board.Count).ToList();
                var flopOk = board.Take(3).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(expected.Take(3));
                var restOk = board.Skip(3).SequenceEqual(expected.Skip(3), StringComparer.OrdinalIgnoreCase);
                if (!flopOk || !restOk)
                    problems.Add($"board leído {string.Join(" ", board)}, era {string.Join(" ", expected)}");
            }

            if (!string.IsNullOrWhiteSpace(readPosition) && !string.IsNullOrWhiteSpace(realPosition) &&
                !string.Equals(readPosition, realPosition, StringComparison.OrdinalIgnoreCase))
                problems.Add($"posición leída {readPosition}, era {realPosition}");

            return problems.Count == 0 ? null : string.Join("; ", problems);
        }

        private static List<string> Cards(string? text) =>
            string.IsNullOrWhiteSpace(text)
                ? new List<string>()
                : text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(card => card.Trim()).Where(card => card.Length == 2).ToList();
    }
}
