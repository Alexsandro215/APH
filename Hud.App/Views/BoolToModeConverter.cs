using System;
using System.Globalization;
using System.Windows.Data;

namespace Hud.App.Views
{
    public class BoolToModeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var isTrue = value is bool b && b;
            var param = parameter as string ?? "ON|OFF";
            var parts = param.Split('|');
            var onText = parts.Length > 0 ? parts[0] : "ON";
            var offText = parts.Length > 1 ? parts[1] : "OFF";

            return isTrue ? onText : offText;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
