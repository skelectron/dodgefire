using Crossfire.Sim.Players;

namespace Crossfire.Sim.Stats;

/// <summary>Per-player numbers for the end-of-match results screen.</summary>
public struct PlayerStats
{
    public int LivesLost;
    public int LivesTaken;
    public int Grazes;
    public int Flips;
    public int FlipBlocks;
    public int Breaks;
    public int Placed;
    public int Rejected;
    public int Overdrives;
    /// <summary>Crossfire combos this Gunner was part of (as placer or partner).</summary>
    public int Combos;
    /// <summary>Ticks spent alive as a Dodger while a round was being played.</summary>
    public int TicksSurvived;
    /// <summary>Ticks spent as a Dodger / as a Gunner while playing (role mix, for 1v1 role swaps).</summary>
    public int TicksDodging, TicksGunning;
}

/// <summary>
/// Collects match statistics from simulation events. Call <see cref="Consume"/> after every step.
/// Lives outside the deterministic state (it only reads it) and never allocates after construction.
/// </summary>
public sealed class MatchStats
{
    public readonly PlayerStats[] Players = new PlayerStats[SimState.MaxPlayers];
    public int Rounds;
    public int Winner = -1;
    public int PlayerCount;
    /// <summary>Ticks of Playing phase in this match.</summary>
    public int PlayTicks;

    public void Reset()
    {
        Array.Clear(Players);
        Rounds = 0;
        Winner = -1;
        PlayerCount = 0;
        PlayTicks = 0;
    }

    public void Consume(SimState s)
    {
        PlayerCount = s.PlayerCount;
        for (int i = 0; i < s.EventCount; i++)
        {
            ref readonly var e = ref s.Events[i];
            switch (e.Type)
            {
                case SimEventType.Hit or SimEventType.Death:
                    Players[e.Player].LivesLost++;
                    if (e.Other < s.PlayerCount) Players[e.Other].LivesTaken++;
                    break;
                case SimEventType.Graze: Players[e.Player].Grazes += e.Other == 0xFF ? 1 : e.Other; break;
                case SimEventType.Flip: Players[e.Player].Flips++; break;
                case SimEventType.FlipBlock: Players[e.Player].FlipBlocks++; break;
                case SimEventType.Break: Players[e.Player].Breaks++; break;
                case SimEventType.Placed: Players[e.Player].Placed++; break;
                case SimEventType.Rejected: Players[e.Player].Rejected++; break;
                case SimEventType.Overdrive: Players[e.Player].Overdrives++; break;
                case SimEventType.Combo:
                    Players[e.Player].Combos++;
                    if (e.Other < Players.Length) Players[e.Other].Combos++;
                    break;
                case SimEventType.RoundEnd: Rounds++; break;
                case SimEventType.MatchEnd: Winner = s.MatchWinner; break;
            }
        }

        if (s.Phase != MatchPhase.Playing) return;
        PlayTicks++;
        for (int i = 0; i < s.PlayerCount; i++)
        {
            ref readonly var p = ref s.Players[i];
            if (!p.Active) continue;
            if (p.Role == PlayerRole.Dodger)
            {
                Players[i].TicksDodging++;
                if (p.Lives > 0) Players[i].TicksSurvived++;
            }
            else
            {
                Players[i].TicksGunning++;
            }
        }
    }

    /// <summary>
    /// Most valuable player: the most lives taken, then the most flip blocks + breaks, then the fewest lives lost.
    /// -1 if nobody did anything.
    /// </summary>
    public int Mvp()
    {
        int best = -1;
        long bestScore = long.MinValue;
        for (int i = 0; i < PlayerCount; i++)
        {
            ref readonly var p = ref Players[i];
            long score = p.LivesTaken * 1000L + (p.FlipBlocks + p.Breaks) * 100L + p.Grazes - p.LivesLost * 300L;
            if (score > bestScore) { bestScore = score; best = i; }
        }
        return best;
    }
}
