using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Hud.App.Views
{
    public class PercentToGridLengthConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double val = value is double d ? d : 0;
            // Ensure minimum value to avoid 0-width column issues if needed, 
            // but for Star width 0 is fine.
            return new GridLength(Math.Max(0, val), GridUnitType.Star);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
