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
                _ => actionCode ?? "Fold"
            };
        }

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

