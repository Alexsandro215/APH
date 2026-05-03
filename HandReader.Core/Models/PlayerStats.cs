using System.Collections.Generic;

namespace HandReader.Core.Models;

public sealed class PlayerStats
{
    public string Name { get; }
    public int HandsReceived { get; private set; }

    // PRE-FLOP
    public int VpipCount { get; private set; }
    public int PfrCount { get; private set; }
    public int ThreeBetCount { get; private set; }

    // POST-FLOP AGGRESSION
    public int PostflopBetsAndRaises { get; private set; }
    public int PostflopCalls { get; private set; }

    // CBET FLOP
    public int CBetFlopOpportunities { get; private set; }
    public int CBetFlopMade { get; private set; }

    // FOLD vs CBET FLOP
    public int FacedCBetFlop { get; private set; }
    public int FoldToCBetFlop { get; private set; }

    // SHOWDOWN / FLOP
    public int SawFlop { get; private set; }
    public int WentToShowdown { get; private set; }
    public int WonAtShowdown { get; private set; }     // (WSD) Won at ShowDown
    public int WonWhenSawFlop { get; private set; }    // (WWSF) aprox

    // Últimas 9 celdas fijas: token de cartas o xx / --.
    private readonly Queue<string> _last9 = new(capacity: 9);
    public IReadOnlyCollection<string> LastHands => _last9;

    public string LastActionSeq { get; private set; } = "-";

    public PlayerStats(string name) => Name = name;

    public void BumpHand() => HandsReceived++;

    public void PushCell(string token)
    {
        if (_last9.Count == 9) _last9.Dequeue();
        _last9.Enqueue(token);
    }

    public void SetLastActionSeq(string seq) =>
        LastActionSeq = string.IsNullOrWhiteSpace(seq) ? "-" : seq;

    // PRE-FLOP
    public void IncVPIP() => VpipCount++;
    public void IncPFR() => PfrCount++;
    public void IncThreeBet() => ThreeBetCount++;

    // POST-FLOP
    public void IncPostflopBR() => PostflopBetsAndRaises++;
    public void IncPostflopCall() => PostflopCalls++;

    // CBet
    public void IncCBetOpp() => CBetFlopOpportunities++;
    public void IncCBetMade() => CBetFlopMade++;

    // Fold vs CBet
    public void IncFacedCBet() => FacedCBetFlop++;
    public void IncFoldToCBet() => FoldToCBetFlop++;

    // Flop / Showdown
    public void IncSawFlop() => SawFlop++;
    public void IncWTSD() => WentToShowdown++;
    public void IncWSD() => WonAtShowdown++;        // <- antes IncW$SD
    public void IncWWSF() => WonWhenSawFlop++;

    // % básicos
    public double VPIPPct => HandsReceived > 0 ? 100.0 * VpipCount / HandsReceived : 0.0;
    public double PFRPct  => HandsReceived > 0 ? 100.0 * PfrCount  / HandsReceived : 0.0;
    public double ThreeBetPct => HandsReceived > 0 ? 100.0 * ThreeBetCount / HandsReceived : 0.0;

    // Aggression
    public double AF => PostflopCalls == 0 ? PostflopBetsAndRaises : (double)PostflopBetsAndRaises / PostflopCalls;
    public double AFqPct =>
        (PostflopBetsAndRaises + PostflopCalls) > 0
            ? 100.0 * PostflopBetsAndRaises / (PostflopBetsAndRaises + PostflopCalls)
            : 0.0;

    // CBet / Fold v CBet
    public double CBetFlopPct => CBetFlopOpportunities > 0 ? 100.0 * CBetFlopMade / CBetFlopOpportunities : 0.0;
    public double FoldVsCBetFlopPct => FacedCBetFlop > 0 ? 100.0 * FoldToCBetFlop / FacedCBetFlop : 0.0;

    // Showdown / WWSF
    public double WTSDPct => SawFlop > 0 ? 100.0 * WentToShowdown / SawFlop : 0.0;
    public double WSDPct  => WentToShowdown > 0 ? 100.0 * WonAtShowdown / WentToShowdown : 0.0;  // <- antes W$SDPct
    public double WWSFPct => SawFlop > 0 ? 100.0 * WonWhenSawFlop / SawFlop : 0.0;
}
