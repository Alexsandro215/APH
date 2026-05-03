using System;
using System.Windows.Data;
using System.Globalization;


namespace Hud.App.Views
{
    public sealed class EqualsToBoolConverter : IMultiValueConverter, IValueConverter
    {
        // Multi: [value1, value2] -> value1 == value2
        public object? Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values is { Length: 2 })
            {
                var a = values[0]?.ToString() ?? "";
                var b = values[1]?.ToString() ?? "";
                return string.Equals(a, b, StringComparison.Ordinal);
            }
            return false;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();

        // Single: Border.Background != Transparent ? HeroFg : normal (uso opcional)
        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // si parameter == "fg" y fondo no es transparente, usamos HeroFg
            if ((parameter as string) == "fg")
                return System.Windows.Media.Brushes.White;
            return value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
