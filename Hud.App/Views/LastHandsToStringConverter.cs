using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Data;

namespace Hud.App.Views
{
    public sealed class LastHandsToStringConverter : IValueConverter
    {
        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not IEnumerable<string> seq) return string.Empty;

            var list = seq.ToList();
            while (list.Count < 9) list.Insert(0, "--");

            var nine = list.TakeLast(9)
                           .Select(t => (t ?? "--").PadRight(3).Substring(0, 3))
                           .ToList();
            nine.Reverse(); // antiguaâ† â€¦ â†’nueva

            return string.Join(" ", nine);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}

