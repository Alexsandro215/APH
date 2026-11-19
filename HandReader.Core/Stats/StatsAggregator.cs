using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using HandReader.Core.Models;

namespace HandReader.Core.Stats;

/// <summary>Un asiento de la ultima mano: numero, jugador y si estaba ausente ("is sitting out").</summary>
public readonly record struct TableSeat(int Seat, string Name, bool SittingOut);

public sealed class StatsAggregator
{
    /// <summary>Maximo de asientos de una mesa (PokerStars llega a 10, no a 6).</summary>
    public const int MaxSeats = 10;

    private readonly ConcurrentDictionary<string, PlayerStats> _players = new(StringComparer.Ordinal);
    private string[] _currentTableOrder = Array.Empty<string>();
    private TableSeat[] _currentSeats = Array.Empty<TableSeat>();

    public IReadOnlyDictionary<string, PlayerStats> Players => _players;
    public IReadOnlyList<string> CurrentTableOrder => _currentTableOrder;

    /// <summary>Asientos de la ultima mano (para colocar el HUD en el lugar de cada jugador).</summary>
    public IReadOnlyList<TableSeat> CurrentSeats => _currentSeats;

    /// <summary>Asientos de la mesa: 2, 6, 9 o 10 ("6-max" en la cabecera de la mano).</summary>
    public int TableSize { get; private set; } = 6;

    public void SetCurrentSeats(IEnumerable<TableSeat> seats, int tableSize)
    {
        _currentSeats = seats.Where(s => !string.IsNullOrWhiteSpace(s.Name)).Take(MaxSeats).ToArray();
        TableSize = Math.Clamp(tableSize, 2, MaxSeats);
    }

    public PlayerStats GetOrAdd(string name)
    {
        return _players.GetOrAdd(name, n => new PlayerStats(n));
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
    public void IncThreeBetOpp(string p) => GetOrAdd(p).IncThreeBetOpp();

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
        _currentTableOrder = orderedPlayersBySeat
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Take(MaxSeats)
            .ToArray();
    }
}
