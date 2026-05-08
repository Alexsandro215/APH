using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace Hud.App.Views
{
    public sealed record CardChipViewModel(string Rank, char Suit, Brush Background, Brush Foreground)
    {
        public static IReadOnlyList<CardChipViewModel> FromCards(string cards)
        {
            if (string.IsNullOrWhiteSpace(cards) || cards.Trim() == "-")
                return Array.Empty<CardChipViewModel>();

            return NormalizeCardText(cards)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(FromCard)
                .Where(card => card is not null)
                .Cast<CardChipViewModel>()
                .ToList();
        }

        public static bool LooksLikeCardToken(string token)
        {
            var clean = CleanCardToken(token);
            return Regex.IsMatch(clean, @"^(10|[2-9TJQKA])[hdcs\u2665\u2666\u2663\u2660]$", RegexOptions.IgnoreCase);
        }

        private static CardChipViewModel? FromCard(string raw)
        {
            var clean = CleanCardToken(raw);
            if (clean.Length == 0)
                return null;

            var rank = clean.StartsWith("10", StringComparison.OrdinalIgnoreCase)
                ? "T"
                : clean[0].ToString().ToUpperInvariant();
            var suit = clean.Length > 1 ? char.ToLowerInvariant(clean[^1]) : '?';

            return new CardChipViewModel(
                rank,
                suit,
                SuitBackground(suit),
                Brushes.White);
        }

        private static Brush SuitBackground(char suit)
        {
            return suit switch
            {
                'h' or '\u2665' => BrushFrom(216, 31, 49),
                'd' or '\u2666' => BrushFrom(36, 106, 230),
                'c' or '\u2663' => BrushFrom(18, 182, 83),
                's' or '\u2660' => BrushFrom(8, 12, 18),
                _ => BrushFrom(78, 91, 106)
            };
        }

        private static string NormalizeCardText(string cards) =>
            cards
                .Replace("[", " ")
                .Replace("]", " ")
                .Replace("-", " ")
                .Replace(",", " ")
                .Replace("|", " ");

        private static string CleanCardToken(string raw) =>
            raw.Trim()
                .Trim('[', ']', '(', ')', ',', '.', ';', ':', '|')
                .Replace("-", "");

        private static Brush BrushFrom(byte r, byte g, byte b) =>
            new SolidColorBrush(Color.FromRgb(r, g, b));
    }
}

