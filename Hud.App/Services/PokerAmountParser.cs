using System.Globalization;
using System.Text.RegularExpressions;

namespace Hud.App.Services
{
    public static class PokerAmountParser
    {
        public const string BlindAmountPattern = @"(?:US)?[$€]?\s*\d+(?:[.,]\d{1,3})*(?:US)?[$€]?";

        public static bool TryParse(string raw, out double value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(raw))
                return false;

            var clean = Clean(raw);
            if (string.IsNullOrWhiteSpace(clean))
                return false;

            clean = NormalizeSeparators(clean);
            return double.TryParse(clean, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
        }

        public static string FormatBlind(string raw)
        {
            if (!TryParse(raw, out var value))
                return raw.Trim();

            var prefix = HasCurrency(raw) ? "$" : "";
            return $"{prefix}{value.ToString("0.##", CultureInfo.InvariantCulture)}";
        }

        public static bool HasCurrency(string raw) =>
            raw.Contains('$') ||
            raw.Contains('€') ||
            raw.Contains("â‚¬", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("US", StringComparison.OrdinalIgnoreCase);

        private static string Clean(string raw) =>
            raw.Replace("$", "", StringComparison.Ordinal)
                .Replace("€", "", StringComparison.Ordinal)
                .Replace("â‚¬", "", StringComparison.OrdinalIgnoreCase)
                .Replace("US", "", StringComparison.OrdinalIgnoreCase)
                .Replace("\u00A0", "", StringComparison.Ordinal)
                .Replace(" ", "", StringComparison.Ordinal)
                .Trim();

        private static string NormalizeSeparators(string clean)
        {
            var dotCount = clean.Count(c => c == '.');
            var commaCount = clean.Count(c => c == ',');

            if (dotCount > 0 && commaCount > 0)
            {
                var lastDot = clean.LastIndexOf('.');
                var lastComma = clean.LastIndexOf(',');
                var decimalSep = lastDot > lastComma ? '.' : ',';
                var thousandSep = decimalSep == '.' ? ',' : '.';
                return clean.Replace(thousandSep.ToString(), "", StringComparison.Ordinal)
                    .Replace(decimalSep, '.');
            }

            if (dotCount > 0)
                return NormalizeSingleSeparator(clean, '.');

            if (commaCount > 0)
                return NormalizeSingleSeparator(clean, ',');

            return clean;
        }

        private static string NormalizeSingleSeparator(string clean, char separator)
        {
            var count = clean.Count(c => c == separator);
            if (count > 1)
                return clean.Replace(separator.ToString(), "", StringComparison.Ordinal);

            var separatorIndex = clean.IndexOf(separator);
            var trailingDigits = clean.Length - separatorIndex - 1;
            if (trailingDigits == 3)
                return clean.Replace(separator.ToString(), "", StringComparison.Ordinal);

            return clean.Replace(separator, '.');
        }
    }
}
