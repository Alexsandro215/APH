using System.Diagnostics;
using Hud.App.Services;
using Hud.App.Services.Solver;
using Hud.App.Services.Trainer;
using Xunit;
using Xunit.Abstractions;

namespace HandReader.Tests;

public class FlopGtoBenchTests
{
    private readonly ITestOutputHelper _out;
    public FlopGtoBenchTests(ITestOutputHelper output) => _out = output;

    [Theory(Skip = "medicion manual de velocidad")]
    [InlineData("BTN", "Ks 7d 2c")]
    [InlineData("SB", "9h 8h 6c")]
    public void Bench(string opener, string board)
    {
        var charts = PreflopLibrary.Load(Path.Combine(AppContext.BaseDirectory, "Data"));
        var ranges = new TrainerRangeEstimator(charts);
        var open = ranges.PreflopRangeFor(opener, TrainerActionKind.Raise, 0, "-", "-", null);
        var bb = ranges.PreflopRangeFor("BB", TrainerActionKind.Call, 1, opener, opener, null);
        var flop = TrainerCards.ParseMany(board);
        var watch = Stopwatch.StartNew();
        var oop = opener == "SB" ? open : bb;
        var ip = opener == "SB" ? bb : open;
        var solver = new FlopGtoSolver(flop, 5.5, 97.5, oop.Combos, ip.Combos, trackTurnAverages: true);
        _out.WriteLine($"{opener} {board}: build {watch.ElapsedMilliseconds} ms, nodes {solver.DecisionNodes}, oop {oop.Combos.Count} ip {ip.Combos.Count}");
        foreach (var target in new[] { 25, 50, 100, 200, 400 })
        {
            watch.Restart();
            var it = solver.Solve(target, TimeSpan.FromMinutes(5));
            var solveMs = watch.ElapsedMilliseconds;
            watch.Restart();
            var exp = solver.ExploitabilityPercentOfPot();
            _out.WriteLine($"  {it} it (+{solveMs} ms): explotabilidad {exp:0.00}% del bote (BR {watch.ElapsedMilliseconds} ms)");
        }
        foreach (var node in solver.FlopNodes.Where(n => n.Path is "" or "x" or "b1" or "x-b1"))
        {
            var strategy = solver.Strategy(node.Path);
            var totals = new double[node.Actions.Count];
            var count = 0;
            foreach (var f in strategy)
            {
                if (f is null) continue;
                count++;
                for (var a = 0; a < f.Length; a++) totals[a] += f[a];
            }
            _out.WriteLine($"  node '{node.Path}' p{node.Player}: " + string.Join(" ", node.Codes.Select((c, a) => $"{c}={totals[a] / count:P0}")));
        }
    }

    [Theory(Skip = "medicion manual de velocidad")]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    public void Paralelo(int workers)
    {
        var charts = PreflopLibrary.Load(Path.Combine(AppContext.BaseDirectory, "Data"));
        var ranges = new TrainerRangeEstimator(charts);
        var folder = Path.Combine(Path.GetTempPath(), "aph-flop-bench-" + Guid.NewGuid().ToString("N"));
        FlopLibrary.UseFolder(folder);
        try
        {
            var spots = FlopLibrary.Spots;
            var items = FlopLibrary.CanonicalFlops.Take(4 * workers).Select((key, i) => (Spot: spots[i % spots.Length], Key: key)).ToArray();
            var watch = Stopwatch.StartNew();
            var next = -1;
            var threads = Enumerable.Range(0, workers).Select(_ => new Thread(() =>
            {
                int i;
                while ((i = Interlocked.Increment(ref next)) < items.Length)
                {
                    var (oop, ip) = FlopLibrary.RangesFor(items[i].Spot, ranges);
                    FlopLibrary.BuildOne(items[i].Spot, items[i].Key, oop, ip, FlopLibrary.Iterations, CancellationToken.None);
                }
            })).ToList();
            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join());
            var perFlop = watch.Elapsed.TotalSeconds / items.Length;
            var bytes = new DirectoryInfo(folder).GetFiles().Sum(f => f.Length) / (double)items.Length;
            _out.WriteLine($"{workers} hilos: {perFlop:0.00} s por flop -> biblioteca completa {perFlop * FlopLibrary.TotalFlops / 3600:0.0} h; {bytes / 1024:0.0} KB por flop");
        }
        finally
        {
            FlopLibrary.UseFolder(null);
            try { Directory.Delete(folder, true); } catch { }
        }
    }
}
