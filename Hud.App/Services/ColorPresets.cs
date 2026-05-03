// Hud.App/Services/ColorPresets.cs
using System;
using System.Collections.Generic;

namespace Hud.App.Services
{
    // Nota: StakeProfile está declarado en Hud.App/Services/StakeProfile.cs

    /// <summary>
    /// Rango semántico para colorear una métrica.
    /// </summary>
    public sealed record StatRange(
        double Min, double Low, double OptLow, double OptHigh, double High, double Max,
        bool Invert = false);

    /// <summary>
    /// Presets iniciales por stake. Claves esperadas:
    /// "VPIP","PFR","THREEBET","AF","AFQ","CBF","FVCBF","WTSD","WSD","WWSF"
    /// </summary>
    public static class ColorPresets
    {
        public static readonly Dictionary<StakeProfile, Dictionary<string, StatRange>> Sets =
            new()
            {
                // ----------------------------- LOW (NL2–NL25) -----------------------------
                [StakeProfile.Low] = new(StringComparer.OrdinalIgnoreCase)
                {
                    // Preflop
                    ["VPIP"]     = new(0, 14, 21, 28, 36, 80),
                    ["PFR"]      = new(0, 10, 16, 23, 30, 70),
                    ["THREEBET"] = new(0,  3,  6,  9, 14, 40),

                    // Agresión
                    ["AF"]       = new(0, 0.9, 1.5, 3.0, 5.0, 15),
                    ["AFQ"]      = new(0,   35, 45, 60, 75, 100),

                    // Postflop (Flop)
                    ["CBF"]      = new(0,   45, 55, 70, 85, 100),
                    ["FVCBF"]    = new(0,   35, 45, 60, 75, 100, Invert: true),

                    // Showdown
                    ["WTSD"]     = new(0,   20, 24, 28, 33, 60),
                    ["WSD"]      = new(0,   44, 49, 55, 61, 100, Invert: true),

                    // Won When Saw Flop
                    ["WWSF"]     = new(0,   40, 46, 52, 58, 100)
                },

                // ----------------------------- MID (NL50–NL200) -----------------------------
                [StakeProfile.Mid] = new(StringComparer.OrdinalIgnoreCase)
                {
                    // Preflop
                    ["VPIP"]     = new(0, 13, 20, 26, 35, 80),
                    ["PFR"]      = new(0, 11, 17, 24, 28, 70),
                    ["THREEBET"] = new(0,  3,  6, 10, 15, 40),

                    // Agresión
                    ["AF"]       = new(0, 1.0, 1.6, 3.2, 5.5, 15),
                    ["AFQ"]      = new(0,   38, 47, 62, 78, 100),

                    // Postflop (Flop)
                    ["CBF"]      = new(0,   48, 55, 68, 85, 100),
                    ["FVCBF"]    = new(0,   33, 43, 58, 78, 100, Invert: true),

                    // Showdown
                    ["WTSD"]     = new(0,   19, 23, 27, 33, 60),
                    ["WSD"]      = new(0,   45, 49, 56, 64, 100, Invert: true),

                    // Won When Saw Flop
                    ["WWSF"]     = new(0,   41, 46, 53, 61, 100)
                },

                // ----------------------------- HIGH (NL500+) -----------------------------
                [StakeProfile.High] = new(StringComparer.OrdinalIgnoreCase)
                {
                    // Preflop
                    ["VPIP"]     = new(0, 12, 19, 24, 32, 70),
                    ["PFR"]      = new(0, 12, 18, 23, 26, 60),
                    ["THREEBET"] = new(0,  4,  6, 11, 14, 35),

                    // Agresión
                    ["AF"]       = new(0, 1.1, 1.8, 3.3, 5.0, 12),
                    ["AFQ"]      = new(0,   40, 48, 64, 75, 100),

                    // Postflop (Flop)
                    ["CBF"]      = new(0,   50, 53, 66, 82, 100),
                    ["FVCBF"]    = new(0,   34, 42, 56, 75, 100, Invert: true),

                    // Showdown
                    ["WTSD"]     = new(0,   18, 22, 26, 32, 55),
                    ["WSD"]      = new(0,   46, 48, 56, 62, 95, Invert: true),

                    // Won When Saw Flop
                    ["WWSF"]     = new(0,   43, 45, 52, 60, 95)
                }
            };

        public static StatRange? TryGetRange(StakeProfile stake, string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            if (!Sets.TryGetValue(stake, out var dict)) return null;
            return dict.TryGetValue(key, out var range) ? range : null;
        }
    }
}
