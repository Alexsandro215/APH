using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Hud.App.Views
{
    public sealed class StatTextBrushConverter : IValueConverter
    {
        private const int MinHands = 30;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var hands = value switch
            {
                int i => i,
                long l when l <= int.MaxValue => (int)l,
                _ => int.TryParse(value?.ToString() ?? "", NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : 0
            };

            if (hands < MinHands)
                return Application.Current.TryFindResource("Brush.Text") as Brush
                    ?? new SolidColorBrush(Color.FromRgb(231, 238, 245));

            return Brushes.Black;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}

