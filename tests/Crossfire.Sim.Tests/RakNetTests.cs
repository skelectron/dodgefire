using System.Diagnostics;
using Crossfire.Net;
using Crossfire.Sim.Players;

namespace Crossfire.Sim.Tests;

/// <summary>Phase 8: the real RakNet transport over UDP on localhost (bound to 127.0.0.1, so no firewall prompt).</summary>
[Collection("RakNet")] // one at a time: they use real ports
public class RakNetTests
{
    private static ushort Port(int offset) => (ushort)(47900 + offset + Environment.ProcessId % 50 * 4);

    private static void Pump(Func<bool> until, params ITransport[] _)
    {
        var sw = Stopwatch.StartNew();
        while (!until())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("network did not respond in time");
            Thread.Sleep(2);
        }
    }

    [Fact]
    public void Client_connects_and_packets_flow_both_ways()
    {
        ushort port = Port(0);
        using var host = RakNetTransport.Host(port, "127.0.0.1");
        using var client = RakNetTransport.Join("127.0.0.1", port);
        var buf = new byte[256];

        int hostSawClient = -1;
        bool clientConnected = false;
        Pump(() =>
        {
            if (host.Poll(out int p, buf, out _) == NetEventType.Connected) hostSawClient = p;
            if (client.Poll(out _, buf, out _) == NetEventType.Connected) clientConnected = true;
            return hostSawClient > 0 && clientConnected;
        });
        Assert.Equal(1, hostSawClient);

        client.Send(0, [42, 1, 2, 3], Delivery.Reliable);
        host.Send(1, [7, 9], Delivery.Unreliable);
        byte[]? atHost = null, atClient = null;
        Pump(() =>
        {
            if (host.Poll(out int p, buf, out int n) == NetEventType.Data && p == 1) atHost = buf[..n];
            var b2 = new byte[256];
            if (client.Poll(out _, b2, out int n2) == NetEventType.Data) atClient = b2[..n2];
            return atHost != null && atClient != null;
        });
        Assert.Equal(new byte[] { 42, 1, 2, 3 }, atHost);
        Assert.Equal(new byte[] { 7, 9 }, atClient);

        client.Dispose();
        bool hostSawLeave = false;
        Pump(() => hostSawLeave |= host.Poll(out _, buf, out _) == NetEventType.Disconnected);
    }

    [Fact]
    public void Joining_a_port_nobody_hosts_fails_with_a_reason()
    {
        using var client = RakNetTransport.Join("127.0.0.1", Port(2));
        var buf = new byte[64];
        Pump(() => client.Poll(out _, buf, out _) == NetEventType.Disconnected);
        Assert.NotNull(client.Failure);
    }

    private sealed class Scripted : ILocalInputSource
    {
        public SimInput Read(int slot, SimState state) => new((sbyte)((state.Tick / 20 % 3 - 1) * 90), (sbyte)((state.Tick / 31 % 3 - 1) * 90),
            state.Tick % 40 == slot ? InputButtons.Pattern1 : InputButtons.None);
    }

    [Fact]
    public void A_1v1_over_real_udp_stays_in_sync()
    {
        ushort port = Port(1);
        using var hostT = RakNetTransport.Host(port, "127.0.0.1");
        using var clientT = RakNetTransport.Join("127.0.0.1", port);
        var buf = new byte[64];
        bool h = false, c = false;
        Pump(() =>
        {
            h |= hostT.Poll(out _, buf, out _) == NetEventType.Connected;
            c |= clientT.Poll(out _, buf, out _) == NetEventType.Connected;
            return h && c;
        });

        PlayerSetup[] layout = [new(0, PlayerRole.Dodger), new(1, PlayerRole.Gunner)];
        using var initial = SimState.CreateMatch(layout, 4);
        int[] owners = [0, 1];
        using var host = new NetMatch(hostT, new RollbackSession(initial, new RollbackOptions(2, 8)), 0, owners);
        using var client = new NetMatch(clientT, new RollbackSession(initial, new RollbackOptions(2, 8)), 1, owners);
        var src = new Scripted();
        const int Ticks = 600;
        var sw = Stopwatch.StartNew();
        while ((host.Session.ConfirmedTick < Ticks || client.Session.ConfirmedTick < Ticks) && sw.Elapsed < TimeSpan.FromSeconds(30))
        {
            foreach (var m in new[] { host, client })
            {
                m.Poll();
                if (m.Session.CurrentTick < Ticks + 40) m.TryAdvance(src);
                m.SendFrames();
            }
            Thread.Sleep(1);
        }
        Assert.True(host.Session.ConfirmedTick >= Ticks && client.Session.ConfirmedTick >= Ticks, $"host {host.Session.ConfirmedTick}, client {client.Session.ConfirmedTick}");
        Assert.False(host.Desynced || client.Desynced);
        for (int t = NetMatch.HashInterval - 1; t < Ticks; t += NetMatch.HashInterval)
            Assert.Equal(host.HashAt(t) ?? 1, client.HashAt(t) ?? 2);
    }
}
