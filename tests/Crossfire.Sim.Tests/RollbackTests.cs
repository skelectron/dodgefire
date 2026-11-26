using Crossfire.Net;
using Crossfire.Sim.Bots;
using Crossfire.Sim.Players;
using static Crossfire.Sim.Tests.TestHelpers;

namespace Crossfire.Sim.Tests;

/// <summary>Phase 8: rollback session and the online match protocol over a simulated network.</summary>
public class RollbackTests
{
    private static readonly PlayerSetup[] FourVsFour =
    [
        new(0, PlayerRole.Dodger), new(1, PlayerRole.Dodger), new(0, PlayerRole.Gunner), new(1, PlayerRole.Gunner),
        new(0, PlayerRole.Dodger), new(1, PlayerRole.Dodger), new(0, PlayerRole.Gunner), new(1, PlayerRole.Gunner),
    ];

    /// <summary>A "human" that does not depend on the state: changes its mind every few ticks, fires and flips now and then.</summary>
    private static SimInput Scripted(int slot, int tick)
    {
        uint h = (uint)(slot * 73856093) ^ (uint)((tick / 7) * 19349663);
        h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
        sbyte x = (sbyte)((int)(h % 3) - 1), y = (sbyte)((int)(h / 3 % 3) - 1);
        var buttons = InputButtons.None;
        if ((h >> 8 & 7) == 0) buttons |= InputButtons.Focus;
        if ((tick % 23) == slot % 5) buttons |= (InputButtons)((int)InputButtons.Pattern1 << (int)(h >> 12 & 3));
        if ((tick % 97) == slot) buttons |= InputButtons.Flip;
        return new SimInput((sbyte)(x * SimInput.AxisMax), (sbyte)(y * SimInput.AxisMax), buttons);
    }

    [Fact]
    public void CopyFrom_snapshot_continues_exactly_like_the_original()
    {
        using var sim = new Simulation();
        using var a = SimState.CreateMatch(FourVsFour, 3);
        var inputs = new SimInput[8];
        for (int t = 0; t < 400; t++)
        {
            for (int s = 0; s < 8; s++) inputs[s] = Scripted(s, t);
            sim.Step(a, inputs);
        }
        using var b = new SimState();
        b.CopyFrom(a);
        Assert.Equal(a.ComputeHash(), b.ComputeHash());
        for (int t = 400; t < 900; t++)
        {
            for (int s = 0; s < 8; s++) inputs[s] = Scripted(s, t);
            sim.Step(a, inputs);
            sim.Step(b, inputs);
        }
        Assert.Equal(a.ComputeHash(), b.ComputeHash());
    }

    private sealed class Recorder : IConfirmedTickSink
    {
        public readonly List<SimInput[]> Inputs = [];
        public readonly Dictionary<int, ulong> Hashes = [];
        public int First = -1;

        public void OnConfirmedTick(int tick, ReadOnlySpan<SimInput> inputs, ReadOnlySpan<SimEvent> events, SimState stateAfter)
        {
            if (First < 0) First = tick;
            Assert.Equal(First + Inputs.Count, tick); // in order, no gaps
            Inputs.Add(inputs.ToArray());
            if ((tick + 1) % 30 == 0) Hashes[tick] = stateAfter.ComputeHash();
        }
    }

    [Fact]
    public void Late_inputs_roll_back_and_end_in_the_same_state_as_a_plain_run()
    {
        using var initial = SimState.CreateMatch(FourVsFour, 5);
        using var session = new RollbackSession(initial, new RollbackOptions(InputDelay: 2, Window: 8));
        var rec = new Recorder();
        session.Sink = rec;
        // Slot 0 is "local" (known 2 ticks ahead); every other slot arrives 6 ticks late, so most ticks are predicted then corrected.
        const int Ticks = 900;
        for (int t = 0; t < Ticks; t++)
        {
            session.SetInput(0, session.LocalInputTick, Scripted(0, session.LocalInputTick));
            int late = session.CurrentTick - 6;
            for (int s = 1; s < 8; s++)
                for (int k = session.ContiguousTick(s) + 1; k <= late; k++) session.SetInput(s, k, Scripted(s, k));
            Assert.True(session.CanAdvance);
            session.Advance();
        }
        Assert.True(session.Rollbacks > 50, $"expected rollbacks, got {session.Rollbacks}");

        // Reference: the same inputs, no prediction.
        using var sim = new Simulation();
        using var reference = SimState.CreateMatch(FourVsFour, 5);
        for (int i = 0; i < rec.Inputs.Count; i++)
        {
            sim.Step(reference, rec.Inputs[i]);
            if (rec.Hashes.TryGetValue(i, out ulong h)) Assert.Equal(reference.ComputeHash(), h);
        }
        for (int i = 0; i < rec.Inputs.Count; i++)
            for (int s = 0; s < 8; s++)
                Assert.Equal(i < 2 ? default : Scripted(s, i), rec.Inputs[i][s]);
    }

    [Fact]
    public void Session_waits_instead_of_predicting_past_the_window()
    {
        using var initial = SimState.CreateMatch(OneVsOne, 5);
        using var session = new RollbackSession(initial, new RollbackOptions(InputDelay: 2, Window: 8));
        int advanced = 0;
        for (int t = 0; t < 40 && session.CanAdvance; t++)
        {
            session.SetInput(0, session.LocalInputTick, default); // slot 1 never sends anything
            session.Advance();
            advanced++;
        }
        Assert.False(session.CanAdvance);
        Assert.Equal(1, session.Laggiest(out int behind));
        Assert.True(behind > 8);
        Assert.InRange(advanced, 9, 11);
    }

    [Fact]
    public void Advancing_with_rollbacks_does_not_allocate()
    {
        using var initial = SimState.CreateMatch(FourVsFour, 5);
        using var session = new RollbackSession(initial, new RollbackOptions(InputDelay: 2, Window: 8));
        void Run(int ticks)
        {
            for (int t = 0; t < ticks; t++)
            {
                session.SetInput(0, session.LocalInputTick, Scripted(0, session.LocalInputTick));
                int late = session.CurrentTick - 5;
                for (int s = 1; s < 8; s++)
                    for (int k = session.ContiguousTick(s) + 1; k <= late; k++) session.SetInput(s, k, Scripted(s, k));
                session.Advance();
            }
        }
        Run(300); // warm up (JIT, first round)
        long before = GC.GetAllocatedBytesForCurrentThread();
        Run(600);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(session.Rollbacks > 100);
        Assert.Equal(0, allocated);
    }

    // --- The key test: real protocol, lossy network, several peers -------------------------------------

    private sealed class Peer : ILocalInputSource, IDisposable
    {
        public NetMatch Match = null!;
        public ITransport Transport = null!;
        public readonly Recorder Recorder = new();
        public readonly BotBrain[] Brains = new BotBrain[8];
        public bool Gone;

        public SimInput Read(int slot, SimState state)
        {
            int tick = Match.Session.LocalInputTick;
            // Host bots: every slot the host owns except its own player slot 0.
            if (Match.IsHost && slot != 0) return BotController.Think(state, slot, ref Brains[slot]);
            return Scripted(slot, tick);
        }

        public void Dispose() => Match.Dispose();
    }

    public static TheoryData<int, int, int, int> Networks => new()
    {
        { 2, 60, 20, 5 },   // 1v1-ish: host + 1 client, rest bots
        { 4, 90, 40, 10 },  // 2v2 humans in a 4v4, bad network
        { 8, 50, 30, 5 },   // full 4v4 of humans
    };

    [Theory]
    [MemberData(nameof(Networks))]
    public void Every_peer_agrees_with_a_plain_run_over_a_lossy_network(int peers, int lagMs, int jitterMs, int lossPercent)
    {
        var net = new LoopbackNetwork(new NetConditions(lagMs, jitterMs, lossPercent, DuplicatePercent: 3), seed: peers);
        var owners = new int[8];
        for (int s = 0; s < 8; s++) owners[s] = s < peers ? s : NetMatch.HostPeer; // client k owns slot k
        using var initial = SimState.CreateMatch(FourVsFour, 77);

        var all = new List<Peer>();
        for (int p = 0; p < peers; p++)
        {
            var peer = new Peer { Transport = p == 0 ? net.CreateHost() : net.Connect() };
            for (int s = 0; s < 8; s++) peer.Brains[s] = BotBrain.Create(BotDifficulty.Hard, 500u + (uint)s);
            peer.Match = new NetMatch(peer.Transport, new RollbackSession(initial, new RollbackOptions(2, 8)), p, owners) { Sink = peer.Recorder };
            all.Add(peer);
        }

        const int Ticks = 1500;
        int frames = 0;
        while (all.Exists(x => x.Recorder.Inputs.Count < Ticks) && frames < Ticks * 4)
        {
            foreach (var peer in all)
            {
                peer.Match.Poll();
                if (peer.Match.Session.CurrentTick < Ticks + 60) peer.Match.TryAdvance(peer);
                peer.Match.SendFrames();
            }
            net.Advance(1000.0 / 60);
            frames++;
        }

        try
        {
            var host = all[0];
            Assert.All(all, p => Assert.False(p.Match.Desynced));
            Assert.All(all, p => Assert.True(p.Recorder.Inputs.Count >= Ticks, $"peer stalled at {p.Recorder.Inputs.Count}"));
            Assert.True(all.Skip(1).Sum(p => p.Match.Session.Rollbacks) > 0, "the network should have forced rollbacks");

            using var sim = new Simulation();
            using var reference = SimState.CreateMatch(FourVsFour, 77);
            for (int i = 0; i < Ticks; i++)
            {
                sim.Step(reference, host.Recorder.Inputs[i]);
                if ((i + 1) % 30 != 0) continue;
                ulong expected = reference.ComputeHash();
                foreach (var p in all) Assert.Equal(expected, p.Recorder.Hashes[i]);
            }
            foreach (var p in all)
                for (int i = 0; i < Ticks; i++) Assert.Equal(host.Recorder.Inputs[i], p.Recorder.Inputs[i]);
        }
        finally
        {
            foreach (var p in all) p.Dispose();
        }
    }

    [Fact]
    public void A_diverging_peer_is_caught_by_the_hash_check()
    {
        var net = new LoopbackNetwork(new NetConditions(40, 10, 0), seed: 3);
        var owners = new[] { 0, 1, 0, 0, 0, 0, 0, 0 };
        using var initial = SimState.CreateMatch(FourVsFour, 21);
        using var broken = SimState.CreateMatch(FourVsFour, 21);
        broken.Players[5].Pos += FixVec2.FromInt(1, 0); // one unit off: a "determinism bug" on the client
        var all = new List<Peer>();
        for (int p = 0; p < 2; p++)
        {
            var peer = new Peer { Transport = p == 0 ? net.CreateHost() : net.Connect() };
            for (int s = 0; s < 8; s++) peer.Brains[s] = BotBrain.Create(BotDifficulty.Hard, 40u + (uint)s);
            peer.Match = new NetMatch(peer.Transport, new RollbackSession(p == 0 ? initial : broken, new RollbackOptions(2, 8)), p, owners) { Sink = peer.Recorder };
            all.Add(peer);
        }
        for (int frame = 0; frame < 600 && !all[1].Match.Desynced; frame++)
        {
            foreach (var peer in all)
            {
                peer.Match.Poll();
                peer.Match.TryAdvance(peer);
                peer.Match.SendFrames();
            }
            net.Advance(1000.0 / 60);
        }
        try
        {
            Assert.True(all[0].Match.Desynced, "host should detect the mismatch");
            Assert.True(all[1].Match.Desynced, "client should be told");
            Assert.Equal(all[0].Match.DesyncTick, all[1].Match.DesyncTick);
            Assert.Equal(NetMatch.HashInterval - 1, all[0].Match.DesyncTick); // first check already differs
            Assert.False(all[1].Match.TryAdvance(all[1])); // the match stops
        }
        finally
        {
            foreach (var p in all) p.Dispose();
        }
    }

    [Fact]
    public void A_player_who_drops_out_becomes_a_host_bot_and_everyone_stays_in_sync()
    {
        var net = new LoopbackNetwork(new NetConditions(70, 20, 5), seed: 9);
        var owners = new[] { 0, 1, 2, 3, 0, 0, 0, 0 };
        using var initial = SimState.CreateMatch(FourVsFour, 12);
        var all = new List<Peer>();
        for (int p = 0; p < 4; p++)
        {
            var peer = new Peer { Transport = p == 0 ? net.CreateHost() : net.Connect() };
            for (int s = 0; s < 8; s++) peer.Brains[s] = BotBrain.Create(BotDifficulty.Normal, 900u + (uint)s);
            peer.Match = new NetMatch(peer.Transport, new RollbackSession(initial, new RollbackOptions(2, 8)), p, owners) { Sink = peer.Recorder };
            all.Add(peer);
        }

        const int Ticks = 1200;
        for (int frame = 0; frame < Ticks * 4 && all.Exists(x => !x.Gone && x.Recorder.Inputs.Count < Ticks); frame++)
        {
            if (frame == 500) { all[2].Gone = true; all[2].Transport.Dispose(); } // client 2 quits
            foreach (var peer in all)
            {
                if (peer.Gone) continue;
                peer.Match.Poll();
                if (peer.Match.Session.CurrentTick < Ticks + 60) peer.Match.TryAdvance(peer);
                peer.Match.SendFrames();
            }
            net.Advance(1000.0 / 60);
        }
        try
        {
            var live = all.Where(p => !p.Gone).ToList();
            Assert.Equal(NetMatch.HostPeer, all[0].Match.OwnerOf(2));
            Assert.Equal(NetMatch.HostPeer, all[1].Match.OwnerOf(2)); // told by the host
            Assert.All(live, p => Assert.True(p.Recorder.Inputs.Count >= Ticks));
            Assert.All(live, p => Assert.False(p.Match.Desynced));
            foreach (int tick in all[0].Recorder.Hashes.Keys.Where(k => k < Ticks))
                foreach (var p in live) Assert.Equal(all[0].Recorder.Hashes[tick], p.Recorder.Hashes[tick]);
        }
        finally
        {
            foreach (var p in all) p.Dispose();
        }
    }
}
