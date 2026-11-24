using Crossfire.Sim.Bullets;
using Crossfire.Sim.Patterns;
using Crossfire.Sim.Players;
using Crossfire.Sim.Rules;

namespace Crossfire.Sim;

public enum MatchPhase : byte
{
    /// <summary>Players can move; Gunners cannot place yet.</summary>
    Countdown,
    Playing,
    /// <summary>Round result banner; everything frozen.</summary>
    RoundOver,
    /// <summary>Match finished; any player pressing Start begins a rematch.</summary>
    MatchOver,
}

public enum SimEventType : byte
{
    Hit,
    Death,
    Graze,
    Placed,
    Rejected,
    RoundStart,
    RoundEnd,
    MatchEnd,
}

/// <summary>Things that happened this tick, for render/audio feedback only (not hashed).</summary>
public struct SimEvent
{
    public SimEventType Type;
    public byte Player;
    public byte Team;
    public FixVec2 Pos;
}

/// <summary>One entry per player slot in a match.</summary>
public readonly record struct PlayerSetup(int Team, PlayerRole Role);

/// <summary>
/// The entire game state. Everything the simulation reads or writes lives here, so a state can be
/// copied (<see cref="CopyFrom"/>) for rollback and hashed (<see cref="ComputeHash"/>) for desync
/// detection. Allocated once per match; stepping never allocates.
/// </summary>
public sealed class SimState : IDisposable
{
    public const int MaxPlayers = 8;
    public const int MaxEmitters = 128;
    public const int MaxEvents = 64;
    public const int TeamCount = 2;

    public int Tick;
    public SimRng Rng;
    public MatchRules Rules;

    public MatchPhase Phase;
    /// <summary>Ticks remaining in Countdown / RoundOver.</summary>
    public int PhaseTicks;
    /// <summary>Ticks elapsed in the Playing phase of the current round.</summary>
    public int RoundTick;
    public int RoundNumber;
    public readonly int[] Scores = new int[TeamCount];
    /// <summary>Winner of the last finished round: team index, or -1 for a draw.</summary>
    public int LastRoundWinner = -1;
    public int MatchWinner = -1;

    public int PlayerCount;
    public readonly Player[] Players = new Player[MaxPlayers];
    public readonly Emitter[] Emitters = new Emitter[MaxEmitters];
    public readonly BulletPool Bullets;

    // Not part of the deterministic state: per-tick feedback for the renderer.
    public int EventCount;
    public readonly SimEvent[] Events = new SimEvent[MaxEvents];

    public SimState(int bulletCapacity = BulletPool.DefaultCapacity)
    {
        Bullets = new BulletPool(bulletCapacity);
    }

    /// <summary>Creates a fresh match: players placed, round 1 countdown running.</summary>
    public static SimState CreateMatch(ReadOnlySpan<PlayerSetup> players, uint seed, MatchRules? rules = null, int bulletCapacity = BulletPool.DefaultCapacity)
    {
        if (players.Length is 0 or > MaxPlayers) throw new ArgumentOutOfRangeException(nameof(players), players.Length, $"Need 1..{MaxPlayers} players.");
        var s = new SimState(bulletCapacity)
        {
            Rng = new SimRng(seed),
            Rules = rules ?? MatchRules.Default,
            PlayerCount = players.Length,
        };
        for (int i = 0; i < players.Length; i++)
        {
            if ((uint)players[i].Team >= TeamCount) throw new ArgumentOutOfRangeException(nameof(players), players[i].Team, "Team must be 0 or 1.");
            s.Players[i] = new Player
            {
                Active = true,
                Team = (byte)players[i].Team,
                Role = players[i].Role,
                StartRole = players[i].Role,
                LastRejectTick = -1,
            };
        }
        MatchFlow.StartMatch(s);
        return s;
    }

    public void PushEvent(SimEventType type, int player, int team, FixVec2 pos)
    {
        if (EventCount >= MaxEvents) return;
        Events[EventCount++] = new SimEvent { Type = type, Player = (byte)player, Team = (byte)team, Pos = pos };
    }

    /// <summary>Overwrites this state with <paramref name="other"/> (no allocation).</summary>
    public void CopyFrom(SimState other)
    {
        Tick = other.Tick;
        Rng = other.Rng;
        Rules = other.Rules;
        Phase = other.Phase;
        PhaseTicks = other.PhaseTicks;
        RoundTick = other.RoundTick;
        RoundNumber = other.RoundNumber;
        other.Scores.CopyTo(Scores, 0);
        LastRoundWinner = other.LastRoundWinner;
        MatchWinner = other.MatchWinner;
        PlayerCount = other.PlayerCount;
        other.Players.CopyTo(Players, 0);
        other.Emitters.CopyTo(Emitters, 0);
        Bullets.CopyFrom(other.Bullets);
        EventCount = other.EventCount;
        other.Events.CopyTo(Events, 0);
    }

    /// <summary>FNV-1a hash over every deterministic field (events excluded).</summary>
    public unsafe ulong ComputeHash()
    {
        var h = new StateHasher();
        h.Add(Tick);
        h.Add((int)Rng.State);
        HashRules(ref h, in Rules);
        h.Add((int)Phase);
        h.Add(PhaseTicks);
        h.Add(RoundTick);
        h.Add(RoundNumber);
        h.Add(Scores[0]);
        h.Add(Scores[1]);
        h.Add(LastRoundWinner);
        h.Add(MatchWinner);
        h.Add(PlayerCount);
        for (int i = 0; i < PlayerCount; i++)
        {
            ref readonly Player p = ref Players[i];
            h.Add(p.Active ? 1 : 0);
            h.Add(p.Team);
            h.Add((int)p.Role);
            h.Add((int)p.StartRole);
            h.Add((int)p.PrevButtons);
            h.Add(p.Pos);
            h.Add(p.Lives);
            h.Add(p.InvulnTicks);
            h.Add(p.Focus ? 1 : 0);
            h.Add(p.Grazes);
            h.Add(p.Cursor);
            h.Add(p.Energy.Raw);
            for (int k = 0; k < MatchRules.PatternSlots; k++) h.Add(p.Cooldowns[k]);
            h.Add(p.LastRejectTick);
        }
        for (int i = 0; i < MaxEmitters; i++)
        {
            ref readonly Emitter e = ref Emitters[i];
            if (!e.Active) continue;
            h.Add(i);
            h.Add(e.PatternSlot);
            h.Add(e.Owner);
            h.Add(e.Half);
            h.Add(e.Pos);
            h.Add(e.Age);
            h.Add(e.Angle);
        }
        BulletPool b = Bullets;
        h.Add(b.Count);
        h.AddBytes(b.PosX, b.Count * sizeof(int));
        h.AddBytes(b.PosY, b.Count * sizeof(int));
        h.AddBytes(b.VelX, b.Count * sizeof(int));
        h.AddBytes(b.VelY, b.Count * sizeof(int));
        h.AddBytes(b.Half, b.Count);
        h.AddBytes(b.Type, b.Count);
        h.AddBytes(b.Flags, b.Count);
        return h.Value;
    }

    private static void HashRules(ref StateHasher h, in MatchRules r)
    {
        h.Add(r.ArenaWidth); h.Add(r.ArenaHeight);
        h.Add(r.DodgerLives); h.Add(r.DodgerSpeed.Raw); h.Add(r.DodgerFocusSpeed.Raw);
        h.Add(r.HitboxRadius.Raw); h.Add(r.GrazeRadius.Raw); h.Add(r.DodgerEdgeMargin.Raw);
        h.Add(r.InvulnTicks); h.Add(r.HitClearRadius.Raw);
        h.Add(r.CursorSpeed.Raw); h.Add(r.EnergyMax.Raw); h.Add(r.EnergyStart.Raw); h.Add(r.EnergyRegenPerTick.Raw);
        h.Add(r.AntiSpawnRadius.Raw); h.Add(r.TelegraphTicks);
        for (int k = 0; k < MatchRules.PatternSlots; k++)
        {
            PatternDef d = r.Patterns[k];
            h.Add((int)d.Kind); h.Add((int)d.Bullet); h.Add(d.Cost.Raw); h.Add(d.CooldownTicks); h.Add(d.DurationTicks);
            h.Add(d.FireInterval); h.Add(d.BulletSpeed.Raw); h.Add(d.Count); h.Add(d.Spread); h.Add(d.SpinPerVolley);
        }
        h.Add(r.RoundsToWin); h.Add(r.CountdownTicks); h.Add(r.RoundOverTicks); h.Add(r.SurvivalTicks);
        h.Add(r.SuddenDeathTicks); h.Add(r.SuddenDeathRegenMultiplier); h.Add(r.SwapRolesEachRound ? 1 : 0);
    }

    public void Dispose() => Bullets.Dispose();
}

internal struct StateHasher
{
    private const ulong Offset = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;
    public ulong Value = Offset;

    public StateHasher() { }

    public void Add(int v)
    {
        unchecked
        {
            for (int i = 0; i < 4; i++)
            {
                Value ^= (byte)(v >> (i * 8));
                Value *= Prime;
            }
        }
    }

    public void Add(FixVec2 v)
    {
        Add(v.X.Raw);
        Add(v.Y.Raw);
    }

    public unsafe void AddBytes(void* data, int length)
    {
        byte* p = (byte*)data;
        unchecked
        {
            for (int i = 0; i < length; i++)
            {
                Value ^= p[i];
                Value *= Prime;
            }
        }
    }
}
