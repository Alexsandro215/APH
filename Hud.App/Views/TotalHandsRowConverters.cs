using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Hud.App.Views
{
    public sealed class TotalHandsRowBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var hands = ToInt(value);

            return hands switch
            {
                >= 10000 => new SolidColorBrush(Color.FromRgb(245, 247, 250)),
                >= 5000 => new SolidColorBrush(Color.FromRgb(109, 40, 217)),
                >= 1000 => new SolidColorBrush(Color.FromRgb(183, 144, 255)),
                >= 500 => new SolidColorBrush(Color.FromRgb(215, 198, 0)),
                >= 200 => new SolidColorBrush(Color.FromRgb(33, 192, 122)),
                >= 100 => new SolidColorBrush(Color.FromRgb(159, 232, 182)),
                _ => new SolidColorBrush(Color.FromRgb(18, 20, 24))
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();

        private static int ToInt(object value) =>
            value is int hands
                ? hands
                : int.TryParse(value?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : 0;
    }

    public sealed class TotalHandsTextBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var hands = value is int direct
                ? direct
                : int.TryParse(value?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : 0;

            return hands switch
            {
                >= 10000 => Brushes.Black,
                >= 5000 => Brushes.White,
                >= 1000 => Brushes.Black,
                >= 500 => Brushes.Black,
                >= 200 => Brushes.Black,
                >= 100 => Brushes.Black,
                _ => new SolidColorBrush(Color.FromRgb(242, 244, 248))
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}

