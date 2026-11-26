using Crossfire.Input;
using Crossfire.Net;
using Crossfire.Sim;
using Crossfire.Sim.Bots;
using Crossfire.Sim.Replays;
using Crossfire.Sim.Rules;
using Crossfire.Sim.Stats;

namespace Crossfire.App;

/// <summary>
/// Drives an online game frame by frame: polls the session, steps the rollback match at 60 Hz (with gentle time
/// sync), feeds this machine's inputs (the local player, and on the host every bot), turns confirmed ticks into
/// stats and a replay, and keeps per-slot controllers and labels for the HUD.
/// </summary>
internal sealed class OnlineGame : ILocalInputSource, IConfirmedTickSink, IDisposable
{
    private const double TickSeconds = 1.0 / MatchRules.TicksPerSecond;

    private readonly KeyboardInput _keyboard;
    private readonly MouseInput _mouse;
    private readonly GamepadInput _pads;
    private BotBrain[] _brains = [];
    private int _matchVersion = -1, _ownerVersion = -1;
    private MatchPhase _phaseBefore;
    private double _accumulator;
    private double _stalledFor;

    public OnlineSession Session { get; }
    /// <summary>How this machine's player plays (mouse, keyboard or gamepad).</summary>
    public ControlScheme Controls { get; set; }
    public SlotController[] Controllers { get; private set; } = [];
    public MatchStats Stats { get; } = new();
    public ReplayRecorder Recorder { get; } = new();
    /// <summary>Replay of the last finished match (set when its end is confirmed).</summary>
    public Replay? LastReplay { get; private set; }
    /// <summary>Set once when the match end is confirmed (open the results screen).</summary>
    public bool MatchOverPending { get; set; }
    /// <summary>The match was (re)started since the last frame: rebuild per-match UI.</summary>
    public bool MatchStarted { get; private set; }
    public bool LabelsChanged { get; private set; }
    /// <summary>Seconds the game has been waiting for a laggy player (0 when running).</summary>
    public double StalledFor => _stalledFor;
    public int WaitingForSlot { get; private set; } = -1;
    public bool KeyboardBlocked { get; set; }
    public bool MouseBlocked { get; set; }
    /// <summary>Hold everyone's inputs (results screen up).</summary>
    public bool HoldInputs { get; set; }

    public NetMatch? Match => Session.Match;
    public SimState? State => Session.Match?.Session.State;

    public OnlineGame(OnlineSession session, KeyboardInput keyboard, MouseInput mouse, GamepadInput pads, ControlScheme controls)
    {
        Session = session;
        _keyboard = keyboard;
        _mouse = mouse;
        _pads = pads;
        Controls = controls;
    }

    /// <summary>Polls the network and, during a match, advances it by real time. Returns ticks simulated this frame.</summary>
    public int Update(double frameDt, Action<SimState, ReadOnlySpan<SimEvent>> onFreshEvents)
    {
        MatchStarted = false;
        LabelsChanged = false;
        Session.Poll();
        var match = Session.Match;
        if (match == null || Session.Phase != OnlinePhase.InMatch) return 0;
        if (Session.MatchVersion != _matchVersion) OnMatchStarted(match);
        if (match.OwnerVersion != _ownerVersion) OnOwnersChanged(match);

        // Time sync: when this machine runs ahead of the others, take a little time off the clock.
        double adv = match.FrameAdvantage;
        double slow = adv > 1.0 ? Math.Min(0.25, (adv - 1.0) * 0.05) : 0;
        _accumulator += frameDt * (1.0 - slow);

        int steps = 0;
        while (_accumulator >= TickSeconds && steps < 8)
        {
            if (!match.TryAdvance(this))
            {
                // Waiting for someone: hold the clock (do not bank time to fast-forward later).
                _accumulator = Math.Min(_accumulator, TickSeconds);
                break;
            }
            _accumulator -= TickSeconds;
            steps++;
            onFreshEvents(match.Session.State, match.Session.FreshEvents);
            match.SendFrames();
        }
        if (steps == 0)
        {
            match.SendFrames(); // keep acks and resends flowing while waiting
            _stalledFor += frameDt;
            WaitingForSlot = match.Session.Laggiest(out _);
        }
        else
        {
            _stalledFor = 0;
            WaitingForSlot = -1;
        }
        return steps;
    }

    public float Alpha => (float)Math.Clamp(_accumulator / TickSeconds, 0, 1);

    private void OnMatchStarted(NetMatch match)
    {
        _matchVersion = Session.MatchVersion;
        _ownerVersion = -1;
        _accumulator = 0;
        _stalledFor = 0;
        var start = Session.CurrentStart!;
        int n = start.Layout.Length;
        _brains = new BotBrain[n];
        for (int i = 0; i < n; i++) _brains[i] = BotBrain.Create(start.Difficulty, OnlineSession.BotSeed(i));
        Controllers = BuildControllers(match, start);
        Stats.Reset();
        var state = match.Session.State;
        Recorder.Begin(state, start.Seed, start.Layout);
        _phaseBefore = state.Phase;
        match.Sink = this;
        LastReplay = null;
        MatchOverPending = false;
        MatchStarted = true;
        LabelsChanged = true;
        // The mouse player may share the keyboard only with nobody here: online, one person per machine.
        _mouse.KeysShared = false;
    }

    private SlotController[] BuildControllers(NetMatch match, MatchStart start)
    {
        var c = new SlotController[start.Layout.Length];
        var local = Controls switch
        {
            ControlScheme.Mouse => ControllerKind.Mouse,
            ControlScheme.Gamepad => ControllerKind.Gamepad,
            _ => ControllerKind.KeyboardAny,
        };
        for (int i = 0; i < c.Length; i++)
        {
            uint seed = OnlineSession.BotSeed(i);
            c[i] = !start.Human[i] || (match.OwnerOf(i) == NetMatch.HostPeer && match.OwnerOf(i) != start.Owners[i])
                ? new SlotController(ControllerKind.Bot, start.Difficulty, seed)
                : match.OwnerOf(i) == Session.LocalPeer ? new SlotController(local, start.Difficulty, seed)
                : new SlotController(ControllerKind.Remote, start.Difficulty, seed);
        }
        return c;
    }

    private void OnOwnersChanged(NetMatch match)
    {
        _ownerVersion = match.OwnerVersion;
        var start = Session.CurrentStart!;
        // A player who left is now a host bot.
        for (int i = 0; i < Controllers.Length; i++)
            if (start.Human[i] && match.OwnerOf(i) == NetMatch.HostPeer && start.Owners[i] != NetMatch.HostPeer && !Controllers[i].IsBot)
                Controllers[i].SetBot(start.Difficulty);
        LabelsChanged = true;
    }

    /// <summary>This machine's slot, or -1 (spectating).</summary>
    public int LocalSlot
    {
        get
        {
            var m = Session.Match;
            if (m == null || Session.CurrentStart == null) return -1;
            for (int i = 0; i < Controllers.Length; i++)
                if (Session.CurrentStart.Human[i] && m.OwnerOf(i) == Session.LocalPeer && !Controllers[i].IsBot) return i;
            return -1;
        }
    }

    SimInput ILocalInputSource.Read(int slot, SimState state)
    {
        if (slot >= Controllers.Length) return default;
        var c = Controllers[slot];
        // Results screen up: everyone holds still (bots too, or they would press Start and rematch on their own).
        if (HoldInputs || state.Phase == MatchPhase.MatchOver) return default;
        if (c.IsBot) return BotController.Think(state, slot, ref _brains[slot]); // host only (bots are host-owned)
        return c.Read(state, slot, _keyboard, _mouse, _pads, KeyboardBlocked, MouseBlocked);
    }

    void IConfirmedTickSink.OnConfirmedTick(int tick, ReadOnlySpan<SimInput> inputs, ReadOnlySpan<SimEvent> events, SimState stateAfter)
    {
        Recorder.Record(in stateAfter.Rules, _phaseBefore, inputs);
        _phaseBefore = stateAfter.Phase;
        Stats.Consume(stateAfter, events);
        // Every 10 s, log the confirmed state hash: identical lines on every machine mean the match is in sync.
        if ((tick + 1) % 600 == 0) Console.WriteLine($"[net] confirmed tick {tick} hash {stateAfter.ComputeHash():X16}");
        foreach (var e in events)
        {
            if (e.Type != SimEventType.MatchEnd) continue;
            LastReplay = Recorder.Finish(stateAfter);
            MatchOverPending = true;
        }
    }

    public void Dispose() => Session.Dispose();
}
