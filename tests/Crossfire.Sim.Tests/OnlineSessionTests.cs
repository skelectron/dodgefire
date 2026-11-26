using Crossfire.Net;
using Crossfire.Sim.Bots;
using Crossfire.Sim.Patterns;

namespace Crossfire.Sim.Tests;

/// <summary>Phase 8: join handshake, lobby and match start over the simulated network.</summary>
public class OnlineSessionTests
{
    private sealed class Inputs(OnlineSession session) : ILocalInputSource
    {
        private readonly BotBrain[] _brains = Enumerable.Range(0, 8).Select(i => BotBrain.Create(BotDifficulty.Normal, OnlineSession.BotSeed(i))).ToArray();

        public SimInput Read(int slot, SimState state)
        {
            var start = session.CurrentStart!;
            if (!start.Human[slot]) return BotController.Think(state, slot, ref _brains[slot]); // host bots
            int t = session.Match!.Session.LocalInputTick;
            return new SimInput((sbyte)((t / 17 + slot) % 3 * 90 - 90), (sbyte)((t / 29) % 3 * 90 - 90), t % 50 == slot * 3 ? InputButtons.Pattern2 : InputButtons.None);
        }
    }

    private static void Run(LoopbackNetwork net, IEnumerable<OnlineSession> sessions, int frames, Dictionary<OnlineSession, Inputs>? inputs = null)
    {
        for (int f = 0; f < frames; f++)
        {
            foreach (var s in sessions)
            {
                s.Poll();
                if (s.Match is { } m && inputs != null)
                {
                    m.TryAdvance(inputs[s]);
                    m.SendFrames();
                }
            }
            net.Advance(1000.0 / 60);
        }
    }

    [Fact]
    public void Clients_join_take_seats_ready_up_and_play_the_same_match()
    {
        var net = new LoopbackNetwork(new NetConditions(50, 15, 3), seed: 4);
        using var host = new OnlineSession(net.CreateHost());
        using var a = new OnlineSession(net.Connect());
        using var b = new OnlineSession(net.Connect());
        var all = new[] { host, a, b };
        Run(net, all, 30);

        Assert.All(all, s => Assert.Equal(OnlinePhase.Lobby, s.Phase));
        Assert.Equal(1, a.LocalPeer);
        Assert.Equal(2, b.LocalPeer);
        Assert.Equal(3, host.PeersPresent);
        // 1v1 has two seats: host and the first client got them, the second client waits unseated.
        Assert.Equal(0, host.MySeat);
        Assert.Equal(1, a.MySeat);
        Assert.Equal(-1, b.MySeat);

        host.Configure(OnlineMode.TwoVsTwo, BotDifficulty.Hard, swapRoles: true);
        Run(net, all, 20);
        Assert.Equal(OnlineMode.TwoVsTwo, b.Mode);
        b.RequestSeat(3);
        a.SetLoadout(Loadout.Hunter);
        Run(net, all, 20);
        Assert.Equal(3, b.MySeat);
        Assert.Equal(Loadout.Hunter[1], host.SeatLoadout[a.MySeat][1]);

        Assert.False(host.StartMatch(1234)); // nobody is ready yet
        a.SetReady(true);
        b.SetReady(true);
        Run(net, all, 20);
        Assert.True(host.AllReady);
        Assert.True(host.StartMatch(1234));
        Run(net, all, 20);
        Assert.All(all, s => Assert.Equal(OnlinePhase.InMatch, s.Phase));
        Assert.All(all, s => Assert.Equal(1234u, s.CurrentStart!.Seed));
        Assert.True(a.CurrentStart!.Rules.SwapRolesEachRound);
        Assert.False(a.CurrentStart.Human[2]); // empty seat -> host bot
        Assert.Equal(PatternKind.Homing, a.CurrentStart.Layout[a.MySeat].Loadout!.Value[1]);

        var inputs = all.ToDictionary(s => s, s => new Inputs(s));
        Run(net, all, 900, inputs);
        Assert.All(all, s => Assert.True(s.Match!.Session.ConfirmedTick > 600, $"stuck at {s.Match!.Session.ConfirmedTick}"));
        Assert.All(all, s => Assert.False(s.Match!.Desynced));
        for (int t = NetMatch.HashInterval - 1; t < 600; t += NetMatch.HashInterval)
        {
            ulong expected = host.Match!.HashAt(t) ?? 0;
            Assert.NotEqual(0UL, expected);
            Assert.Equal(expected, a.Match!.HashAt(t));
            Assert.Equal(expected, b.Match!.HashAt(t));
        }
    }

    [Fact]
    public void Joining_during_a_match_is_refused_with_a_reason_and_leaving_frees_the_seat()
    {
        var net = new LoopbackNetwork(new NetConditions(30), seed: 8);
        using var host = new OnlineSession(net.CreateHost());
        var a = new OnlineSession(net.Connect());
        Run(net, [host, a], 20);
        a.SetReady(true);
        Run(net, [host, a], 10);
        Assert.True(host.StartMatch(7));
        Run(net, [host, a], 10);

        using var late = new OnlineSession(net.Connect());
        Run(net, [host, a, late], 20);
        Assert.Equal(OnlinePhase.Closed, late.Phase);
        Assert.Contains("in progress", late.Failure);

        host.ReturnToLobby();
        Run(net, [host, a], 10);
        Assert.Equal(OnlinePhase.Lobby, a.Phase);
        Assert.False(host.SeatReady[1]);

        a.Dispose(); // leaves
        Run(net, [host], 10);
        Assert.Equal(-1, host.SeatOwner[1]);
        Assert.Equal(1, host.PeersPresent);
    }

    [Fact]
    public void Switching_to_a_bigger_mode_keeps_people_seated_in_order()
    {
        var net = new LoopbackNetwork(new NetConditions(10), seed: 2);
        using var host = new OnlineSession(net.CreateHost());
        using var a = new OnlineSession(net.Connect());
        Run(net, [host, a], 10);
        a.RequestSeat(1);
        host.Configure(OnlineMode.FourVsFour, BotDifficulty.Easy, swapRoles: false);
        Run(net, [host, a], 10);
        Assert.Equal(8, a.SeatCount);
        Assert.Equal(0, host.MySeat);
        Assert.Equal(1, a.MySeat);
        host.Configure(OnlineMode.OneVsOne, BotDifficulty.Easy, false);
        Run(net, [host, a], 10);
        Assert.Equal(1, a.MySeat);
    }
}
