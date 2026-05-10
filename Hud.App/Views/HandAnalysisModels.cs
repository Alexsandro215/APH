using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

namespace Hud.App.Views
{
    public sealed class StreetActionViewModel
    {
        public StreetActionViewModel(string text, bool isHero, bool isSystem, bool isBoardHeader, bool isTrackedVillain)
        {
            Text = text;
            IsHero = isHero;
            IsSystem = isSystem;
            IsBoardHeader = isBoardHeader;
            IsTrackedVillain = isTrackedVillain;

            var baseForeground = isTrackedVillain
                ? new SolidColorBrush(Color.FromRgb(255, 132, 146))
                : isHero
                ? new SolidColorBrush(Color.FromRgb(33, 192, 122))
                : isSystem
                    ? new SolidColorBrush(Color.FromRgb(143, 211, 244))
                    : Brushes.White;

            Background = isTrackedVillain
                ? new SolidColorBrush(Color.FromRgb(61, 22, 30))
                : isHero
                ? new SolidColorBrush(Color.FromRgb(16, 37, 28))
                : isSystem
                    ? new SolidColorBrush(Color.FromRgb(16, 28, 41))
                    : new SolidColorBrush(Color.FromRgb(17, 24, 32));

            VisualParts = BuildActionVisualParts(text, baseForeground, isBoardHeader);
        }

        public string Text { get; }
        public bool IsHero { get; }
        public bool IsSystem { get; }
        public bool IsBoardHeader { get; }
        public bool IsTrackedVillain { get; }
        public Brush Background { get; }
        public IReadOnlyList<ActionVisualPartViewModel> VisualParts { get; }

        private static IReadOnlyList<ActionVisualPartViewModel> BuildActionVisualParts(string text, Brush baseForeground, bool boardHeader)
        {
            var segments = new List<ActionVisualPartViewModel>();
            var buffer = "";
            var textBrush = boardHeader ? new SolidColorBrush(Color.FromRgb(143, 211, 244)) : baseForeground;

            void Flush()
            {
                if (buffer.Length == 0)
                    return;

                segments.Add(new ActionVisualPartViewModel(
                    buffer,
                    textBrush,
                    FontWeights.SemiBold,
                    Array.Empty<CardChipViewModel>()));
                buffer = "";
            }

            foreach (Match token in Regex.Matches(text, @"\S+|\s+"))
            {
                var value = token.Value;
                if (!CardChipViewModel.LooksLikeCardToken(value))
                {
                    buffer += value;
                    continue;
                }

                Flush();
                segments.Add(new ActionVisualPartViewModel(
                    "",
                    textBrush,
                    FontWeights.Bold,
                    CardChipViewModel.FromCards(value)));
            }

            Flush();
            return segments;
        }
    }

    public sealed record ActionVisualPartViewModel(
        string Text,
        Brush Foreground,
        FontWeight FontWeight,
        IReadOnlyList<CardChipViewModel> CardChips)
    {
        public bool IsCards => CardChips.Count > 0;
        public bool IsText => !IsCards;
    }
}
