using System;
using System.Collections.Generic;
using System.Linq;
using HandReader.Core.Models;

namespace HandReader.Core.Stats;

public sealed class StatsAggregator
{
    private readonly Dictionary<string, PlayerStats> _players = new(StringComparer.Ordinal);
    private readonly List<string> _currentTableOrder = new(6);

    public IReadOnlyDictionary<string, PlayerStats> Players => _players;
    public IReadOnlyList<string> CurrentTableOrder => _currentTableOrder;

    public PlayerStats GetOrAdd(string name)
    {
        if (!_players.TryGetValue(name, out var ps))
        {
            ps = new PlayerStats(name);
            _players[name] = ps;
        }
        return ps;
    }

    public void RegisterHandParticipation(IEnumerable<string> playersInHand)
    {
        foreach (var p in playersInHand) GetOrAdd(p).BumpHand();
    }

    // Last-9 cell tokens
    public void PushCell(string player, string token) => GetOrAdd(player).PushCell(token);

    public void SetActionSeq(string player, string seq) => GetOrAdd(player).SetLastActionSeq(seq);

    // Preflop
    public void IncVPIP(string p)     => GetOrAdd(p).IncVPIP();
    public void IncPFR(string p)      => GetOrAdd(p).IncPFR();
    public void IncThreeBet(string p) => GetOrAdd(p).IncThreeBet();

    // Postflop aggro
    public void IncBR(string p)   => GetOrAdd(p).IncPostflopBR();
    public void IncCall(string p) => GetOrAdd(p).IncPostflopCall();

    // CBet
    public void IncCBetOpp(string p)  => GetOrAdd(p).IncCBetOpp();
    public void IncCBetMade(string p) => GetOrAdd(p).IncCBetMade();

    // Fold vs CBet
    public void IncFacedCBet(string p)  => GetOrAdd(p).IncFacedCBet();
    public void IncFoldToCBet(string p) => GetOrAdd(p).IncFoldToCBet();

    // Flop/Showdown
    public void IncSawFlop(string p) => GetOrAdd(p).IncSawFlop();
    public void IncWTSD(string p)    => GetOrAdd(p).IncWTSD();
    public void IncWSD(string p)     => GetOrAdd(p).IncWSD();     // <- antes IncW$SD
    public void IncWWSF(string p)    => GetOrAdd(p).IncWWSF();

    public void SetCurrentTableOrder(IEnumerable<string> orderedPlayersBySeat)
    {
        _currentTableOrder.Clear();
        _currentTableOrder.AddRange(orderedPlayersBySeat.Where(p => !string.IsNullOrWhiteSpace(p)).Take(6));
    }
}
