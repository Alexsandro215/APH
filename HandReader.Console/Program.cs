using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using HandReader.Core.IO;
using HandReader.Core.Parsing;
using HandReader.Core.Stats;

namespace HandReader.ConsoleApp;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var path = AskFilePath();
        if (string.IsNullOrWhiteSpace(path))
        {
            System.Console.WriteLine("No se seleccionó archivo. Saliendo...");
            return;
        }
        if (!File.Exists(path))
        {
            System.Console.WriteLine($"No existe: {path}");
            return;
        }

        var agg = new StatsAggregator();
        var parser = new PokerStarsParser(agg);

        using var tail = new FileTailReader(path);
        tail.OnLines += (lines) =>
        {
            parser.FeedLines(lines, () => Render(agg, Path.GetFileName(path)));
        };
        tail.Start();

        System.Console.WriteLine("Leyendo y analizando en vivo. Presiona ESC para salir.");
        Render(agg, Path.GetFileName(path));

        while (true)
        {
            if (System.Console.KeyAvailable)
            {
                var k = System.Console.ReadKey(intercept: true);
                if (k.Key == ConsoleKey.Escape) break;
            }
            Thread.Sleep(150);
        }
    }

    private static string? AskFilePath()
    {
        using var ofd = new OpenFileDialog
        {
            Title = "Selecciona el archivo de Hand History (.txt)",
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            Multiselect = false,
            CheckFileExists = true,
            CheckPathExists = true
        };

        var dummy = new Form { TopMost = true, StartPosition = FormStartPosition.CenterScreen };
        dummy.Shown += (_, __) => { ofd.ShowDialog(dummy); dummy.Close(); };
        Application.Run(dummy);

        return ofd.FileName;
    }

    // ======================= RENDER =======================

    private static void Render(StatsAggregator agg, string fileName)
    {
        System.Console.Clear();
        System.Console.WriteLine($"HUD Reader — Fuente: {fileName}");
        System.Console.WriteLine("ESC para salir\n");

        // Cabeceras
        WriteHeader(
            "Nickname","Manos","VPIP%","PFR%","3B%",
            "AF","AFq%","CBetF%","FvCBetF%","WTSD%","W$SD%","WWSF%",
            "Últimas 9","Última Secuencia"
        );
        WriteSep();

        // Solo los 6 de la última mano (en orden por asiento)
        foreach (var name in agg.CurrentTableOrder)
        {
            if (!agg.Players.TryGetValue(name, out var p)) continue;

            WriteRow(
                p.Name,
                p.HandsReceived.ToString(),
                p.VPIPPct.ToString("0"),
                p.PFRPct.ToString("0"),
                p.ThreeBetPct.ToString("0"),
                p.AF.ToString("0.00"),
                p.AFqPct.ToString("0"),
                p.CBetFlopPct.ToString("0"),
                p.FoldVsCBetFlopPct.ToString("0"),
                p.WTSDPct.ToString("0"),
                p.WSDPct.ToString("0"),
                p.WWSFPct.ToString("0"),
                FormatLast9(p.LastHands),
                p.LastActionSeq
            );
        }

        System.Console.WriteLine();
        System.Console.WriteLine("Leyendo cambios… (tail)");
    }

    // ===== Helpers de impresión (ancho fijo por columna) =====
    // Ajusta los anchos si lo necesitas para tu consola

    private const int W_Name   = 16;
    private const int W_Manos  = 5;
    private const int W_Pct5   = 5;   // VPIP/PFR/3B
    private const int W_AF     = 6;   // AF 0.00
    private const int W_Pct6   = 6;   // AFq/WTSD/W$SD/WWSF
    private const int W_CBet   = 7;   // CBetF%
    private const int W_FvCBet = 9;   // FvCBetF%
    private const int W_Last9  = 36;
    private const int W_Seq    = 24;

    private static void WriteHeader(
        string n, string m, string vpip, string pfr, string t3b,
        string af, string afq, string cbetf, string fvcbetf, string wtsd, string wsd, string wwsf,
        string last9, string seq)
    {
        System.Console.WriteLine(
            $"{Pad(n,W_Name)} | {Pad(m,W_Manos)} | {Pad(vpip,W_Pct5)} | {Pad(pfr,W_Pct5)} | {Pad(t3b,W_Pct5)} | " +
            $"{Pad(af,W_AF)} | {Pad(afq,W_Pct6)} | {Pad(cbetf,W_CBet)} | {Pad(fvcbetf,W_FvCBet)} | " +
            $"{Pad(wtsd,W_Pct6)} | {Pad(wsd,W_Pct6)} | {Pad(wwsf,W_Pct6)} | {Pad(last9,W_Last9)} | {Pad(seq,W_Seq)}"
        );
    }

    private static void WriteRow(
        string n, string m, string vpip, string pfr, string t3b,
        string af, string afq, string cbetf, string fvcbetf, string wtsd, string wsd, string wwsf,
        string last9, string seq)
    {
        System.Console.WriteLine(
            $"{Pad(n,W_Name)} | {Pad(m,W_Manos)} | {Pad(vpip,W_Pct5)} | {Pad(pfr,W_Pct5)} | {Pad(t3b,W_Pct5)} | " +
            $"{Pad(af,W_AF)} | {Pad(afq,W_Pct6)} | {Pad(cbetf,W_CBet)} | {Pad(fvcbetf,W_FvCBet)} | " +
            $"{Pad(wtsd,W_Pct6)} | {Pad(wsd,W_Pct6)} | {Pad(wwsf,W_Pct6)} | {Trunc(last9,W_Last9)} | {Trunc(seq,W_Seq)}"
        );
    }

    private static void WriteSep()
    {
        System.Console.WriteLine(
            new string('-', W_Name)  + "-+-" +
            new string('-', W_Manos) + "-+-" +
            new string('-', W_Pct5)  + "-+-" +
            new string('-', W_Pct5)  + "-+-" +
            new string('-', W_Pct5)  + "-+-" +
            new string('-', W_AF)    + "-+-" +
            new string('-', W_Pct6)  + "-+-" +
            new string('-', W_CBet)  + "-+-" +
            new string('-', W_FvCBet)+ "-+-" +
            new string('-', W_Pct6)  + "-+-" +
            new string('-', W_Pct6)  + "-+-" +
            new string('-', W_Pct6)  + "-+-" +
            new string('-', W_Last9) + "-+-" +
            new string('-', W_Seq)
        );
    }

    private static string FormatLast9(IReadOnlyCollection<string> cells)
{
    // Queremos que las manos nuevas aparezcan a la DERECHA.
    // Las más antiguas a la izquierda.
    var list = cells.ToList();

    // Si tiene menos de 9, rellenar con "--" al inicio.
    while (list.Count < 9) list.Insert(0, "--");

    // Tomamos las 9 últimas (sin invertir el orden)
    // pero las imprimimos de IZQ→DER mostrando la más nueva a la derecha.
    var nine = list.TakeLast(9)
                   .Select(t => (t ?? "--").PadRight(3).Substring(0, 3))
                   .ToList();

    // Invertimos el orden visual: nuevas → derecha
    nine.Reverse();

    return "|" + string.Join("|", nine) + "|";
}


    private static string Pad(string s, int width)
    {
        if (s.Length >= width) return s;
        return s + new string(' ', width - s.Length);
    }

    private static string Trunc(string s, int width)
    {
        if (s.Length <= width) return s;
        return s[..(width - 1)] + "…";
    }
}
