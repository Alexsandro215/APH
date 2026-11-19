using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Hud.App.Services
{
    public class AphPreflopProvider
    {
        private static readonly string[] Ranks = { "A", "K", "Q", "J", "T", "9", "8", "7", "6", "5", "4", "3", "2" };
        private readonly Dictionary<string, string[,]> _ranges = new();

        public AphPreflopProvider(string csvPath)
        {
            if (File.Exists(csvPath))
            {
                LoadCsv(csvPath);
            }
        }

        private void LoadCsv(string path)
        {
            var lines = File.ReadAllLines(path);
            int i = 0;
            while (i < lines.Length)
            {
                var line = lines[i];
                if (line.StartsWith("Players,Stacks,Scenario"))
                {
                    // Header line
                    i++;
                    if (i >= lines.Length) break;

                    var dataLine = lines[i];
                    var parts = dataLine.Split(',');
                    if (parts.Length < 5) { i++; continue; }

                    string scenario = parts[2].Trim();
                    string hero = parts[3].Trim();
                    string villain = parts[4].Trim();
                    string key = $"{scenario}:{hero}:{villain}".ToUpper();

                    // Now parse the 13x13 matrix
                    string[,] matrix = new string[13, 13];
                    for (int row = 0; row < 13; row++)
                    {
                        if (i >= lines.Length) break;
                        var rowLine = lines[i];
                        var rowParts = rowLine.Split(',');
                        
                        // The action codes start at index 6 in the CSV
                        // Column 6 is 'As', 7 is 'Ks', etc.
                        for (int col = 0; col < 13; col++)
                        {
                            if (rowParts.Length > 6 + col)
                            {
                                matrix[row, col] = rowParts[6 + col].Trim();
                            }
                        }
                        i++;
                    }

                    // Una tabla repetida no debe sustituir en silencio a la primera (asi se perdieron
                    // las tablas BB vs 4bet de CO/BTN/SB, etiquetadas todas como BB,HJ).
                    if (_ranges.ContainsKey(key))
                        AppLog.Warn($"CSV: tabla repetida {key}; se conserva la primera.");
                    else
                        _ranges[key] = matrix;
                }
                else
                {
                    i++;
                }
            }
        }

        public string GetRecommendation(string scenario, string heroPos, string villainPos, string hand)
        {
            string key = $"{scenario}:{heroPos}:{villainPos}".ToUpper();
            if (!_ranges.ContainsKey(key))
            {
                // Fallback for Open scenarios where Villain is empty
                key = $"{scenario}:{heroPos}:".ToUpper();
                if (!_ranges.ContainsKey(key)) return "Unknown";
            }

            var matrix = _ranges[key];
            var (row, col) = GetHandIndices(hand);
            if (row == -1 || col == -1) return "Unknown";

            string actionCode = matrix[row, col];
            return actionCode switch
            {
                "R" => "Raise",
                "C" => "Call",
                "F" => "Fold",
                // Las tablas de open dejan en blanco las manos que no se abren: celda vacia = fold.
                null or "" => "Fold",
                _ => actionCode
            };
        }

        /// <summary>True si el CSV trae la tabla exacta de ese escenario (sin el respaldo a Open).</summary>
        public bool HasChart(string scenario, string heroPos, string villainPos) =>
            _ranges.ContainsKey(ChartKey(scenario, heroPos, villainPos));

        /// <summary>
        /// Codigo de la celda ("R", "C" o "F") en la tabla exacta del escenario, o null si el CSV no
        /// tiene esa tabla. A diferencia de GetRecommendation no cae a la tabla de Open: para el
        /// entrenador es mejor decir "sin referencia" que corregir con la tabla equivocada.
        /// </summary>
        public string? GetCode(string scenario, string heroPos, string villainPos, string hand)
        {
            if (!_ranges.TryGetValue(ChartKey(scenario, heroPos, villainPos), out var matrix))
                return null;

            var (row, col) = GetHandIndices(hand);
            if (row == -1 || col == -1)
                return null;

            var code = matrix[row, col];
            return string.IsNullOrWhiteSpace(code) ? "F" : code.Trim().ToUpperInvariant();
        }

        /// <summary>Clases de mano ("AKs", "QQ"...) cuya celda tiene alguno de los codigos pedidos.</summary>
        public IReadOnlyList<string> GetClasses(string scenario, string heroPos, string villainPos, params string[] codes)
        {
            if (!_ranges.TryGetValue(ChartKey(scenario, heroPos, villainPos), out var matrix))
                return Array.Empty<string>();

            var wanted = new HashSet<string>(codes, StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();
            for (int row = 0; row < 13; row++)
            {
                for (int col = 0; col < 13; col++)
                {
                    var code = string.IsNullOrWhiteSpace(matrix[row, col]) ? "F" : matrix[row, col].Trim();
                    if (!wanted.Contains(code))
                        continue;

                    if (row == col)
                        result.Add(Ranks[row] + Ranks[col]);
                    else if (row < col)
                        result.Add(Ranks[row] + Ranks[col] + "s");
                    else
                        result.Add(Ranks[col] + Ranks[row] + "o");
                }
            }

            return result;
        }

        /// <summary>Tablas que trae el CSV: (escenario, posicion del heroe, posicion del rival), en mayusculas.</summary>
        public IReadOnlyList<(string Scenario, string Hero, string Villain)> AvailableCharts() =>
            _ranges.Keys
                .Select(key => key.Split(':'))
                .Where(parts => parts.Length == 3)
                .Select(parts => (parts[0], parts[1], parts[2]))
                .ToList();

        /// <summary>Clase de mano ("AA", "AKs", "AKo") de una celda de la tabla 13x13.</summary>
        public static string HandAt(int row, int col) =>
            row == col ? Ranks[row] + Ranks[col]
            : row < col ? Ranks[row] + Ranks[col] + "s"
            : Ranks[col] + Ranks[row] + "o";

        /// <summary>Errores al leer un archivo de rangos (notacion que no se entiende, tablas repetidas).</summary>
        public List<string> LoadErrors { get; } = new();

        /// <summary>
        /// Agrega tablas escritas como rangos (Data/APH_Rangos_Borrador_6max.txt), mas faciles de
        /// revisar que una tabla 13x13. Cada linea: SITUACION | HEROE | RIVALES | SUBIR | PAGAR.
        /// Las tablas del CSV tienen prioridad si se repite una situacion.
        /// </summary>
        public void LoadRangeFile(string path)
        {
            if (!File.Exists(path))
                return;

            var lineNumber = 0;
            foreach (var raw in File.ReadAllLines(path))
            {
                lineNumber++;
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                    continue;

                var parts = line.Split('|');
                if (parts.Length < 4)
                {
                    LoadErrors.Add($"linea {lineNumber}: faltan columnas");
                    continue;
                }

                var scenario = parts[0].Trim();
                var hero = parts[1].Trim();
                var villains = parts[2].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(v => v != "-").DefaultIfEmpty("").ToList();
                var raise = ParseRange(parts[3], null, out var raiseErrors);
                var call = ParseRange(parts.Length > 4 ? parts[4] : "", raise, out var callErrors);
                foreach (var error in raiseErrors.Concat(callErrors))
                    LoadErrors.Add($"linea {lineNumber}: no se entiende \"{error}\"");

                var matrix = new string[13, 13];
                for (int row = 0; row < 13; row++)
                    for (int col = 0; col < 13; col++)
                    {
                        var hand = HandAt(row, col);
                        matrix[row, col] = raise.Contains(hand) ? "R" : call.Contains(hand) ? "C" : "F";
                    }

                foreach (var villain in villains)
                {
                    var key = ChartKey(scenario, hero, villain);
                    if (_ranges.ContainsKey(key))
                        LoadErrors.Add($"linea {lineNumber}: tabla repetida {key}");
                    else
                        _ranges[key] = (string[,])matrix.Clone();
                }
            }

            foreach (var error in LoadErrors)
                AppLog.Warn($"Rangos {Path.GetFileName(path)}: {error}");
        }

        /// <summary>
        /// Notacion de rangos: "QQ+", "TT-77", "ATs+", "A5s-A2s", "T9s-54s", "AK", "KQo", "resto".
        /// "resto" son todas las manos que no estan en <paramref name="exclude"/>.
        /// </summary>
        public static HashSet<string> ParseRange(string text, ISet<string>? exclude, out List<string> errors)
        {
            errors = new List<string>();
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (var token in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (token.Equals("resto", StringComparison.OrdinalIgnoreCase))
                {
                    for (int row = 0; row < 13; row++)
                        for (int col = 0; col < 13; col++)
                            result.Add(HandAt(row, col));
                    continue;
                }

                var hands = ExpandToken(token);
                if (hands is null)
                    errors.Add(token);
                else
                    result.UnionWith(hands);
            }

            if (exclude is not null)
                result.ExceptWith(exclude);
            return result;
        }

        private static IEnumerable<string>? ExpandToken(string token)
        {
            var dash = token.IndexOf('-');
            if (dash > 0)
            {
                var from = Hand(token[..dash]);
                var to = Hand(token[(dash + 1)..]);
                if (from is null || to is null || from.Value.Suffix != to.Value.Suffix)
                    return null;

                var (h1, k1, suffix) = from.Value;
                var (h2, k2, _) = to.Value;
                // Parejas: TT-77
                if (h1 == k1 && h2 == k2)
                    return Enumerable.Range(Math.Min(h1, h2), Math.Abs(h2 - h1) + 1).Select(i => Ranks[i] + Ranks[i]);
                if (h1 == k1 || h2 == k2)
                    return null;
                // Misma carta alta: A5s-A2s
                if (h1 == h2)
                    return Enumerable.Range(Math.Min(k1, k2), Math.Abs(k2 - k1) + 1).SelectMany(k => Named(h1, k, suffix));
                // Conectores con el mismo hueco: T9s-54s
                if (k1 - h1 != k2 - h2)
                    return null;
                return Enumerable.Range(Math.Min(h1, h2), Math.Abs(h2 - h1) + 1).SelectMany(h => Named(h, h + (k1 - h1), suffix));
            }

            var plus = token.EndsWith('+');
            var single = Hand(plus ? token[..^1] : token);
            if (single is null)
                return null;

            var (high, kicker, sfx) = single.Value;
            if (high == kicker)
                return plus
                    ? Enumerable.Range(0, high + 1).Select(i => Ranks[i] + Ranks[i])
                    : new[] { Ranks[high] + Ranks[high] };

            // ATs+ = ATs, AJs, AQs, AKs (sube el acompanante hasta debajo de la carta alta)
            return plus
                ? Enumerable.Range(high + 1, kicker - high).SelectMany(k => Named(high, k, sfx))
                : Named(high, kicker, sfx);
        }

        /// <summary>"ATs" -> (indice de A, indice de T, "s"). Indices: 0 = A ... 12 = 2.</summary>
        private static (int High, int Kicker, string Suffix)? Hand(string text)
        {
            text = text.Trim();
            if (text.Length is < 2 or > 3)
                return null;
            var a = Array.IndexOf(Ranks, text[0].ToString().ToUpperInvariant());
            var b = Array.IndexOf(Ranks, text[1].ToString().ToUpperInvariant());
            if (a < 0 || b < 0)
                return null;
            var suffix = text.Length == 3 ? text[2].ToString().ToLowerInvariant() : "";
            if (suffix is not ("" or "s" or "o") || (a == b && suffix != ""))
                return null;
            return (Math.Min(a, b), Math.Max(a, b), suffix);
        }

        private static IEnumerable<string> Named(int high, int kicker, string suffix)
        {
            var name = Ranks[high] + Ranks[kicker];
            return suffix == "" ? new[] { name + "s", name + "o" } : new[] { name + suffix };
        }

        /// <summary>
        /// Lo que escribe el usuario ("AJo", "aj o", "77", "KTs", "A10s", "ja") -> clases de mano.
        /// Sin "s" ni "o" se devuelven las dos. Lista vacia = no se entiende.
        /// </summary>
        public static IReadOnlyList<string> ParseHandClasses(string? input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return Array.Empty<string>();

            var text = input.Trim().Replace(" ", "").Replace("10", "T").ToUpperInvariant();
            const string order = "AKQJT98765432";
            if (text.Length is < 2 or > 3 || order.IndexOf(text[0]) < 0 || order.IndexOf(text[1]) < 0)
                return Array.Empty<string>();

            var high = text[0];
            var low = text[1];
            if (order.IndexOf(high) > order.IndexOf(low)) (high, low) = (low, high);
            if (high == low)
                return text.Length == 2 ? new[] { $"{high}{low}" } : Array.Empty<string>();

            if (text.Length == 2)
                return new[] { $"{high}{low}s", $"{high}{low}o" };

            return text[2] switch
            {
                'S' => new[] { $"{high}{low}s" },
                'O' => new[] { $"{high}{low}o" },
                _ => Array.Empty<string>()
            };
        }

        private static string ChartKey(string scenario, string heroPos, string villainPos) =>
            $"{scenario}:{heroPos}:{villainPos}".ToUpper();

        private (int row, int col) GetHandIndices(string hand)
        {
            // Example hand: "AKs", "AQo", "77"
            if (string.IsNullOrEmpty(hand) || hand.Length < 2) return (-1, -1);

            string r1 = hand[0].ToString();
            string r2 = hand[1].ToString();
            bool suited = hand.EndsWith("s");
            bool pair = r1 == r2;

            int idx1 = Array.IndexOf(Ranks, r1);
            int idx2 = Array.IndexOf(Ranks, r2);

            if (idx1 == -1 || idx2 == -1) return (-1, -1);

            if (pair)
            {
                return (idx1, idx1);
            }
            
            if (suited)
            {
                // Suited hands are top-right (row < col)
                return (Math.Min(idx1, idx2), Math.Max(idx1, idx2));
            }
            else
            {
                // Offsuit hands are bottom-left (row > col)
                return (Math.Max(idx1, idx2), Math.Min(idx1, idx2));
            }
        }
    }
}

