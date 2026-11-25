using System.Runtime.InteropServices;
using Crossfire.Sim.Patterns;
using Crossfire.Sim.Players;
using Crossfire.Sim.Rules;

namespace Crossfire.Sim.Replays;

/// <summary>
/// A recorded session: everything needed to re-simulate it exactly. Because the simulation is
/// deterministic, that is just the starting setup plus every tick's inputs (and any live rule edits).
/// </summary>
public sealed class Replay
{
    private const uint Magic = 0x50524643; // "CFRP"
    private const int Version = 1;

    public required uint Seed { get; init; }
    public required MatchRules Rules { get; init; }
    public required PlayerSetup[] Layout { get; init; }
    /// <summary>Tick-major inputs: tick t, player p at [t * Layout.Length + p].</summary>
    public required SimInput[] Inputs { get; init; }
    public required int Ticks { get; init; }
    /// <summary>Rule edits made during the session (F1 tuning), applied before the step at that tick.</summary>
    public required (int Tick, MatchRules Rules)[] RuleChanges { get; init; }
    /// <summary>Tick where the last match started (earlier matches in the session are fast-forwarded).</summary>
    public required int MatchStartTick { get; init; }
    /// <summary><see cref="SimState.ComputeHash"/> after the last recorded tick.</summary>
    public required ulong FinalHash { get; init; }

    public int Players => Layout.Length;

    public ReadOnlySpan<SimInput> InputsAt(int tick) => Inputs.AsSpan(tick * Players, Players);

    public void Save(Stream stream)
    {
        using var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        w.Write(Magic);
        w.Write(Version);
        w.Write(Marshal.SizeOf<MatchRules>());
        w.Write(Seed);
        WriteRules(w, Rules);
        w.Write(Layout.Length);
        foreach (var p in Layout)
        {
            w.Write((byte)p.Team);
            w.Write((byte)p.Role);
            var lo = p.Loadout ?? Loadout.Classic;
            for (int k = 0; k < MatchRules.PatternSlots; k++) w.Write((byte)lo[k]);
        }
        w.Write(Ticks);
        w.Write(MemoryMarshal.AsBytes(Inputs.AsSpan(0, Ticks * Layout.Length)));
        w.Write(RuleChanges.Length);
        foreach (var (tick, rules) in RuleChanges)
        {
            w.Write(tick);
            WriteRules(w, rules);
        }
        w.Write(MatchStartTick);
        w.Write(FinalHash);
    }

    public static Replay Load(Stream stream)
    {
        using var r = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        if (r.ReadUInt32() != Magic) throw new InvalidDataException("Not a CROSSFIRE replay.");
        int version = r.ReadInt32();
        if (version != Version) throw new InvalidDataException($"Replay version {version} is not supported (expected {Version}).");
        if (r.ReadInt32() != Marshal.SizeOf<MatchRules>()) throw new InvalidDataException("Replay was recorded by a different game build.");
        uint seed = r.ReadUInt32();
        var rules = ReadRules(r);
        int players = r.ReadInt32();
        if (players is < 1 or > SimState.MaxPlayers) throw new InvalidDataException("Bad player count.");
        var layout = new PlayerSetup[players];
        for (int i = 0; i < players; i++)
        {
            int team = r.ReadByte();
            var role = (PlayerRole)r.ReadByte();
            var lo = new Loadout();
            for (int k = 0; k < MatchRules.PatternSlots; k++) lo[k] = (PatternKind)r.ReadByte();
            layout[i] = new PlayerSetup(team, role, lo);
        }
        int ticks = r.ReadInt32();
        var inputs = new SimInput[ticks * players];
        r.BaseStream.ReadExactly(MemoryMarshal.AsBytes(inputs.AsSpan()));
        int changes = r.ReadInt32();
        var ruleChanges = new (int, MatchRules)[changes];
        for (int i = 0; i < changes; i++) ruleChanges[i] = (r.ReadInt32(), ReadRules(r));
        int matchStart = r.ReadInt32();
        ulong hash = r.ReadUInt64();
        return new Replay
        {
            Seed = seed, Rules = rules, Layout = layout, Inputs = inputs, Ticks = ticks,
            RuleChanges = ruleChanges, MatchStartTick = matchStart, FinalHash = hash,
        };
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var f = File.Create(path);
        Save(f);
    }

    public static Replay Load(string path)
    {
        using var f = File.OpenRead(path);
        return Load(f);
    }

    private static void WriteRules(BinaryWriter w, MatchRules rules) =>
        w.Write(MemoryMarshal.AsBytes(new ReadOnlySpan<MatchRules>(in rules)));

    private static MatchRules ReadRules(BinaryReader r)
    {
        MatchRules rules = default;
        r.BaseStream.ReadExactly(MemoryMarshal.AsBytes(new Span<MatchRules>(ref rules)));
        return rules;
    }
}

/// <summary>
/// Records a session as it is played. Call <see cref="Record"/> just before each <see cref="Simulation.Step"/>.
/// Inputs go into pre-sized chunks, so recording allocates only when a chunk fills (every ~4.5 minutes).
/// </summary>
public sealed class ReplayRecorder
{
    private const int ChunkTicks = 16_384;

    private readonly List<SimInput[]> _chunks = [];
    private readonly List<(int, MatchRules)> _ruleChanges = [];
    private uint _seed;
    private MatchRules _startRules;
    private MatchRules _lastRules;
    private PlayerSetup[] _layout = [];
    private int _players;
    private int _ticks;
    private int _matchStartTick;
    private MatchPhase _lastPhase;

    /// <summary>
    /// False once something edited the state outside of inputs (god mode, stress spawning): such a
    /// session can no longer be re-simulated, so the replay is withheld.
    /// </summary>
    public bool Replayable { get; private set; }
    public int Ticks => _ticks;
    public bool Active => _players > 0;

    /// <summary>Starts recording a fresh state (call right after <see cref="SimState.CreateMatch"/>).</summary>
    public void Begin(SimState s, uint seed, PlayerSetup[] layout)
    {
        _seed = seed;
        _startRules = s.Rules;
        _lastRules = s.Rules;
        _players = layout.Length;
        _layout = new PlayerSetup[_players];
        for (int i = 0; i < _players; i++) _layout[i] = layout[i] with { Loadout = s.Players[i].Loadout };
        _ticks = 0;
        _matchStartTick = 0;
        _lastPhase = s.Phase;
        _ruleChanges.Clear();
        _chunks.Clear(); // keep nothing from the previous session; chunks are reallocated lazily
        Replayable = true;
    }

    public void MarkNotReplayable() => Replayable = false;

    public void Record(SimState s, ReadOnlySpan<SimInput> inputs)
    {
        if (!Active) return;
        if (!RulesEqual(in s.Rules, in _lastRules))
        {
            _lastRules = s.Rules;
            _ruleChanges.Add((_ticks, s.Rules));
        }
        // A rematch started from the match-over screen: the replay will begin here.
        if (_lastPhase == MatchPhase.MatchOver && s.Phase != MatchPhase.MatchOver) _matchStartTick = _ticks;
        _lastPhase = s.Phase;

        int chunk = _ticks / ChunkTicks, at = _ticks % ChunkTicks;
        if (chunk == _chunks.Count) _chunks.Add(new SimInput[ChunkTicks * _players]);
        var dst = _chunks[chunk].AsSpan(at * _players, _players);
        for (int p = 0; p < _players; p++) dst[p] = p < inputs.Length ? inputs[p] : default;
        _ticks++;
    }

    /// <summary>Snapshot of everything recorded so far (null when not replayable).</summary>
    public Replay? Finish(SimState s)
    {
        if (!Active || !Replayable) return null;
        var inputs = new SimInput[_ticks * _players];
        for (int t = 0; t < _ticks; t += ChunkTicks)
        {
            int n = Math.Min(ChunkTicks, _ticks - t) * _players;
            _chunks[t / ChunkTicks].AsSpan(0, n).CopyTo(inputs.AsSpan(t * _players));
        }
        return new Replay
        {
            Seed = _seed, Rules = _startRules, Layout = (PlayerSetup[])_layout.Clone(), Inputs = inputs, Ticks = _ticks,
            RuleChanges = [.. _ruleChanges], MatchStartTick = _matchStartTick, FinalHash = s.ComputeHash(),
        };
    }

    private static bool RulesEqual(in MatchRules a, in MatchRules b) =>
        MemoryMarshal.AsBytes(new ReadOnlySpan<MatchRules>(in a)).SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<MatchRules>(in b)));
}

/// <summary>Re-simulates a <see cref="Replay"/> tick by tick.</summary>
public sealed class ReplayPlayer : IDisposable
{
    private readonly Simulation _sim = new();
    private int _nextRuleChange;

    public Replay Replay { get; }
    public SimState State { get; private set; }
    public int Tick { get; private set; }
    public bool Finished => Tick >= Replay.Ticks;

    public ReplayPlayer(Replay replay)
    {
        Replay = replay;
        State = SimState.CreateMatch(replay.Layout, replay.Seed, replay.Rules);
    }

    /// <summary>Back to the start of the recording (new state).</summary>
    public void Restart()
    {
        State.Dispose();
        State = SimState.CreateMatch(Replay.Layout, Replay.Seed, Replay.Rules);
        Tick = 0;
        _nextRuleChange = 0;
    }

    /// <summary>Advances one tick. Returns false when the recording has ended.</summary>
    public bool Step()
    {
        if (Finished) return false;
        while (_nextRuleChange < Replay.RuleChanges.Length && Replay.RuleChanges[_nextRuleChange].Tick == Tick)
            State.Rules = Replay.RuleChanges[_nextRuleChange++].Rules;
        _sim.Step(State, Replay.InputsAt(Tick));
        Tick++;
        return true;
    }

    /// <summary>Silently simulates up to <paramref name="tick"/> (used to skip earlier matches of a session).</summary>
    public void FastForwardTo(int tick)
    {
        while (Tick < tick && Step()) { }
        State.EventCount = 0;
    }

    /// <summary>True when the re-simulated state matches the recording (checked at the end).</summary>
    public bool HashMatches => Finished && State.ComputeHash() == Replay.FinalHash;

    public void Dispose()
    {
        State.Dispose();
        _sim.Dispose();
    }
}
