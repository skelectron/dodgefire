using Crossfire.Sim;
using Crossfire.Sim.Bullets;

namespace Crossfire.Net;

/// <param name="InputDelay">Local inputs are scheduled this many ticks ahead (hides that much latency without rollback).</param>
/// <param name="Window">Maximum ticks the session may run ahead of the oldest unconfirmed input (rollback depth).</param>
public readonly record struct RollbackOptions(int InputDelay = 2, int Window = 8);

/// <summary>Receives every tick once all of its inputs are known and it was simulated with them, in order.</summary>
public interface IConfirmedTickSink
{
    /// <param name="stateAfter">The simulation state right after this tick (valid only during the call).</param>
    void OnConfirmedTick(int tick, ReadOnlySpan<SimInput> inputs, ReadOnlySpan<SimEvent> events, SimState stateAfter);
}

/// <summary>
/// Rollback (GGPO-style) on top of the deterministic simulation. Knows nothing about the network:
/// callers feed confirmed inputs per (slot, tick) with <see cref="SetInput"/> (local ones at
/// <see cref="LocalInputTick"/>, remote ones whenever they arrive) and call <see cref="Advance"/> once per tick.
/// Missing inputs are predicted (the slot's latest known input repeated). When a confirmed input differs from
/// what was used, the state is restored from a snapshot and re-simulated. Fully allocated up front: advancing,
/// rolling back and confirming do not allocate.
/// </summary>
public sealed class RollbackSession : IDisposable
{
    /// <summary>Ticks of input history per slot (future inputs can be buffered this far ahead).</summary>
    public const int InputRing = 256;

    private readonly Simulation _sim;
    private readonly SimState _live;
    private readonly SimState[] _snaps;
    private readonly int _ring;
    private readonly int _slots;

    private readonly SimInput[] _input;       // [slot * InputRing + tick % InputRing]
    private readonly int[] _inputTick;        // tick confirmed at that position, or int.MinValue
    private readonly int[] _contig;           // per slot: every tick <= contig is confirmed
    private readonly int[] _latest;           // per slot: newest confirmed tick (may be beyond contig)
    private readonly SimInput[] _used;        // [(tick % ring) * slots + slot]: inputs the latest simulation of that tick used
    private readonly SimInput[] _frame;
    private readonly SimEvent[] _events;      // [(tick % ring) * MaxEvents + i]
    private readonly int[] _eventCount;
    private readonly ushort[] _shown;         // [(tick % ring) * MaxEvents + i]: event keys already handed out as fresh
    private readonly int[] _shownCount;
    private readonly SimEvent[] _fresh;
    private int _freshCount;
    private int _rollbackFrom = int.MaxValue;
    private int _confirmed;                    // last tick delivered to the sink

    public RollbackOptions Options { get; }
    public IConfirmedTickSink? Sink { get; set; }

    /// <summary>The live (possibly predicted) state; what to draw.</summary>
    public SimState State => _live;
    /// <summary>Next tick to simulate.</summary>
    public int CurrentTick => _live.Tick;
    /// <summary>The tick local inputs are scheduled for right now.</summary>
    public int LocalInputTick => _live.Tick + Options.InputDelay;
    public int ConfirmedTick => _confirmed;
    public int Slots => _slots;

    // Statistics (for the Net panel).
    public long Rollbacks { get; private set; }
    public long ResimulatedTicks { get; private set; }
    public int LastRollbackDepth { get; private set; }
    public int MaxRollbackDepth { get; private set; }

    /// <param name="initial">Match state at its first tick; copied, not kept.</param>
    public RollbackSession(SimState initial, RollbackOptions options, int bulletWorkers = 0)
    {
        if (options.InputDelay < 0 || options.Window < 1) throw new ArgumentOutOfRangeException(nameof(options));
        Options = options;
        _slots = initial.PlayerCount;
        _ring = options.Window + 2;
        int capacity = initial.Bullets.Capacity;
        _sim = new Simulation(bulletWorkers);
        _live = new SimState(capacity);
        _live.CopyFrom(initial);
        _snaps = new SimState[_ring];
        for (int i = 0; i < _ring; i++) _snaps[i] = new SimState(capacity);

        _input = new SimInput[_slots * InputRing];
        _inputTick = new int[_slots * InputRing];
        Array.Fill(_inputTick, int.MinValue);
        _contig = new int[_slots];
        _latest = new int[_slots];
        _used = new SimInput[_ring * _slots];
        _frame = new SimInput[_slots];
        _events = new SimEvent[_ring * SimState.MaxEvents];
        _eventCount = new int[_ring];
        _shown = new ushort[_ring * SimState.MaxEvents];
        _shownCount = new int[_ring];
        _fresh = new SimEvent[(_ring + 1) * SimState.MaxEvents];

        // Everyone agrees the first InputDelay ticks have no input (nobody could have sent any yet).
        int start = initial.Tick;
        for (int s = 0; s < _slots; s++)
        {
            for (int t = start; t < start + options.InputDelay; t++) Store(s, t, default);
            _contig[s] = start + options.InputDelay - 1;
            _latest[s] = _contig[s];
        }
        _confirmed = start - 1;
    }

    private int Index(int slot, int tick) => slot * InputRing + (int)((uint)tick % InputRing);

    private void Store(int slot, int tick, SimInput input)
    {
        int i = Index(slot, tick);
        _input[i] = input;
        _inputTick[i] = tick;
    }

    public bool IsConfirmed(int slot, int tick) => _inputTick[Index(slot, tick)] == tick;
    /// <summary>Every input of this slot up to and including this tick is known.</summary>
    public int ContiguousTick(int slot) => _contig[slot];
    public int LatestTick(int slot) => _latest[slot];
    public SimInput ConfirmedInput(int slot, int tick) => _input[Index(slot, tick)];

    /// <summary>Adds a confirmed input. Duplicates and inputs that are too old or too far ahead are ignored.</summary>
    /// <returns>False when the input could not be accepted (out of range).</returns>
    public bool SetInput(int slot, int tick, SimInput input)
    {
        if ((uint)slot >= (uint)_slots) return false;
        if (tick <= _contig[slot]) return true; // already known
        if (tick >= CurrentTick + InputRing - _ring - 2) return false; // too far ahead to buffer
        if (IsConfirmed(slot, tick)) return true;
        // Inputs for ticks already simulated must still be inside the snapshot window (guaranteed by CanAdvance).
        if (tick < CurrentTick - _ring + 1) throw new InvalidOperationException($"Input for tick {tick} arrived after its rollback window (now {CurrentTick}).");

        Store(slot, tick, input);
        if (tick > _latest[slot]) _latest[slot] = tick;
        if (tick < CurrentTick && _used[UsedIndex(tick, slot)] != input && tick < _rollbackFrom) _rollbackFrom = tick;
        while (IsConfirmed(slot, _contig[slot] + 1)) _contig[slot]++;
        return true;
    }

    private int UsedIndex(int tick, int slot) => (int)((uint)tick % (uint)_ring) * _slots + slot;

    /// <summary>The input a tick is simulated with: confirmed if known, otherwise the slot's newest known input before it.</summary>
    public SimInput InputFor(int slot, int tick)
    {
        if (IsConfirmed(slot, tick)) return _input[Index(slot, tick)];
        int floor = Math.Max(_contig[slot], tick - InputRing + 1);
        for (int t = tick - 1; t > floor; t--)
            if (IsConfirmed(slot, t)) return _input[Index(slot, t)];
        return _input[Index(slot, _contig[slot])];
    }

    /// <summary>Slot whose inputs are furthest behind (the one we would be waiting for), and how far.</summary>
    public int Laggiest(out int ticksBehind)
    {
        int worst = 0;
        ticksBehind = int.MinValue;
        for (int s = 0; s < _slots; s++)
        {
            int behind = CurrentTick - _contig[s];
            if (behind > ticksBehind) { ticksBehind = behind; worst = s; }
        }
        return worst;
    }

    /// <summary>False while some slot's inputs are so far behind that advancing would leave the rollback window.</summary>
    public bool CanAdvance
    {
        get
        {
            Laggiest(out int behind);
            return behind <= Options.Window;
        }
    }

    /// <summary>
    /// Rolls back if a confirmed input changed the past, then simulates one new tick and delivers newly confirmed ticks.
    /// Returns the number of ticks re-simulated (0 = no rollback). Fresh events are in <see cref="FreshEvents"/>.
    /// </summary>
    public int Advance()
    {
        if (!CanAdvance) throw new InvalidOperationException("Cannot advance: waiting for inputs.");
        _freshCount = 0;
        int resim = 0;
        int now = CurrentTick;
        if (_rollbackFrom < now)
        {
            _live.CopyFrom(_snaps[SnapIndex(_rollbackFrom)]);
            for (int t = _rollbackFrom; t < now; t++)
            {
                StepTick(t, saveSnapshot: t != _rollbackFrom, firstTime: false);
                resim++;
            }
            Rollbacks++;
            ResimulatedTicks += resim;
            LastRollbackDepth = resim;
            MaxRollbackDepth = Math.Max(MaxRollbackDepth, resim);
        }
        _rollbackFrom = int.MaxValue;
        StepTick(now, saveSnapshot: true, firstTime: true);
        DeliverConfirmed();
        return resim;
    }

    private int SnapIndex(int tick) => (int)((uint)tick % (uint)_ring);

    private void StepTick(int tick, bool saveSnapshot, bool firstTime)
    {
        int slot = SnapIndex(tick);
        if (saveSnapshot) _snaps[slot].CopyFrom(_live);
        for (int s = 0; s < _slots; s++)
        {
            var input = InputFor(s, tick);
            _frame[s] = input;
            _used[slot * _slots + s] = input;
        }
        _sim.Step(_live, _frame);

        // Keep this tick's events (for confirmation) and hand out the ones not shown before.
        int n = _live.EventCount;
        _eventCount[slot] = n;
        Array.Copy(_live.Events, 0, _events, slot * SimState.MaxEvents, n);
        if (firstTime) _shownCount[slot] = 0;
        for (int i = 0; i < n; i++)
        {
            ref readonly var e = ref _live.Events[i];
            ushort key = (ushort)((int)e.Type << 8 | e.Player);
            if (WasShown(slot, key)) continue;
            if (_shownCount[slot] < SimState.MaxEvents) _shown[slot * SimState.MaxEvents + _shownCount[slot]++] = key;
            if (_freshCount < _fresh.Length) _fresh[_freshCount++] = e;
        }
    }

    private bool WasShown(int slot, ushort key)
    {
        int b = slot * SimState.MaxEvents;
        for (int i = 0; i < _shownCount[slot]; i++) if (_shown[b + i] == key) return true;
        return false;
    }

    /// <summary>Events from the last <see cref="Advance"/> that were not handed out before (rollback re-simulation skips repeats).</summary>
    public ReadOnlySpan<SimEvent> FreshEvents => _fresh.AsSpan(0, _freshCount);

    private void DeliverConfirmed()
    {
        int limit = CurrentTick - 1;
        for (int s = 0; s < _slots; s++) limit = Math.Min(limit, _contig[s]);
        while (_confirmed < limit)
        {
            int t = ++_confirmed;
            if (Sink == null) continue;
            int slot = SnapIndex(t);
            // State after tick t = snapshot taken before tick t+1, or the live state if t+1 is the next tick.
            var after = t + 1 >= CurrentTick ? _live : _snaps[SnapIndex(t + 1)];
            Sink.OnConfirmedTick(t, _used.AsSpan(slot * _slots, _slots), _events.AsSpan(slot * SimState.MaxEvents, _eventCount[slot]), after);
        }
    }

    public void Dispose()
    {
        _sim.Dispose();
        _live.Dispose();
        foreach (var s in _snaps) s.Dispose();
    }
}
