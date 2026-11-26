namespace Crossfire.Net;

public enum Delivery : byte
{
    /// <summary>May be lost, duplicated or reordered (inputs: every packet repeats the unacknowledged ones).</summary>
    Unreliable,
    /// <summary>Arrives once, in order (lobby, match start, hashes).</summary>
    Reliable,
}

public enum NetEventType : byte
{
    None,
    Connected,
    Disconnected,
    Data,
}

/// <summary>
/// Moves packets between peers. Peer ids: on a host, connected clients are 1..N; on a client, the host is 0.
/// Implementations: <see cref="LoopbackNetwork"/> (tests, in-process), RakNet (real play).
/// </summary>
public interface ITransport : IDisposable
{
    bool IsHost { get; }
    void Send(int peer, ReadOnlySpan<byte> data, Delivery delivery);
    /// <summary>Next network event; packet bytes are copied into <paramref name="buffer"/>. Returns <see cref="NetEventType.None"/> when empty.</summary>
    NetEventType Poll(out int peer, Span<byte> buffer, out int length);
    /// <summary>Disconnects one peer (host) or from the host (client).</summary>
    void Disconnect(int peer);
    /// <summary>Why the connection failed or ended, when known (e.g. host unreachable, game full).</summary>
    string? FailureReason => null;
}

/// <summary>Settings for simulated network trouble (loopback tests and the --net-sim wrapper).</summary>
public record struct NetConditions(int LatencyMs = 0, int JitterMs = 0, int LossPercent = 0, int DuplicatePercent = 0)
{
    public static NetConditions Parse(string text)
    {
        var c = new NetConditions();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = part.Split('=');
            if (kv.Length != 2 || !int.TryParse(kv[1], out int v)) continue;
            switch (kv[0].ToLowerInvariant())
            {
                case "lag" or "latency": c.LatencyMs = Math.Max(0, v); break;
                case "jitter": c.JitterMs = Math.Max(0, v); break;
                case "loss": c.LossPercent = Math.Clamp(v, 0, 100); break;
                case "dup" or "duplicate": c.DuplicatePercent = Math.Clamp(v, 0, 100); break;
            }
        }
        return c;
    }

    public override readonly string ToString() => $"lag {LatencyMs} ms, jitter {JitterMs} ms, loss {LossPercent}%, dup {DuplicatePercent}%";
}

/// <summary>
/// In-process network for tests: one host endpoint and N client endpoints. Time is driven by
/// <see cref="Advance"/> (milliseconds), so tests are deterministic. Unreliable packets can be delayed,
/// reordered, lost or duplicated; reliable packets are delayed but arrive once and in order.
/// </summary>
public sealed class LoopbackNetwork
{
    private sealed class Packet
    {
        public int From, To; // endpoint ids: 0 = host, 1..N clients
        public NetEventType Type;
        public byte[] Data = [];
        public double DueMs;
        public long Seq;
    }

    private readonly List<Packet> _inFlight = [];
    private readonly Dictionary<int, Queue<Packet>> _inbox = [];
    private readonly Dictionary<(int, int), double> _lastReliableDue = [];
    private readonly Random _rng;
    private double _now;
    private long _seq;
    private readonly HashSet<int> _connected = [];

    public NetConditions Conditions { get; set; }

    public LoopbackNetwork(NetConditions conditions, int seed = 1)
    {
        Conditions = conditions;
        _rng = new Random(seed);
        _inbox[0] = new Queue<Packet>();
    }

    public ITransport CreateHost() => new Endpoint(this, 0);

    /// <summary>Creates a client endpoint and connects it to the host (both sides get a Connected event after the latency).</summary>
    public ITransport Connect()
    {
        int id = _inbox.Count;
        _inbox[id] = new Queue<Packet>();
        _connected.Add(id);
        Enqueue(new Packet { From = id, To = 0, Type = NetEventType.Connected }, reliable: true);
        Enqueue(new Packet { From = 0, To = id, Type = NetEventType.Connected }, reliable: true);
        return new Endpoint(this, id);
    }

    /// <summary>Advances simulated time and delivers due packets to inboxes.</summary>
    public void Advance(double ms)
    {
        _now += ms;
        _inFlight.Sort((a, b) => a.DueMs != b.DueMs ? a.DueMs.CompareTo(b.DueMs) : a.Seq.CompareTo(b.Seq));
        int n = 0;
        while (n < _inFlight.Count && _inFlight[n].DueMs <= _now)
        {
            var p = _inFlight[n++];
            _inbox[p.To].Enqueue(p);
        }
        _inFlight.RemoveRange(0, n);
    }

    private double Delay()
    {
        var c = Conditions;
        return c.LatencyMs + (c.JitterMs > 0 ? _rng.NextDouble() * c.JitterMs : 0);
    }

    private void Enqueue(Packet p, bool reliable)
    {
        p.Seq = _seq++;
        double due = _now + Delay();
        if (reliable)
        {
            // In order per direction: never earlier than the previous reliable packet on this link.
            var key = (p.From, p.To);
            if (_lastReliableDue.TryGetValue(key, out double last) && due < last) due = last;
            _lastReliableDue[key] = due;
        }
        p.DueMs = due;
        _inFlight.Add(p);
    }

    private void Send(int from, int to, ReadOnlySpan<byte> data, Delivery delivery)
    {
        if (!_inbox.ContainsKey(to)) return;
        if (to != 0 && !_connected.Contains(to)) return;
        if (from != 0 && !_connected.Contains(from)) return;
        bool reliable = delivery == Delivery.Reliable;
        var c = Conditions;
        if (!reliable && c.LossPercent > 0 && _rng.Next(100) < c.LossPercent) return;
        Enqueue(new Packet { From = from, To = to, Type = NetEventType.Data, Data = data.ToArray() }, reliable);
        if (!reliable && c.DuplicatePercent > 0 && _rng.Next(100) < c.DuplicatePercent)
            Enqueue(new Packet { From = from, To = to, Type = NetEventType.Data, Data = data.ToArray() }, false);
    }

    private void Drop(int client)
    {
        if (!_connected.Remove(client)) return;
        Enqueue(new Packet { From = client, To = 0, Type = NetEventType.Disconnected }, reliable: true);
        Enqueue(new Packet { From = 0, To = client, Type = NetEventType.Disconnected }, reliable: true);
    }

    private sealed class Endpoint(LoopbackNetwork net, int id) : ITransport
    {
        public bool IsHost => id == 0;

        public void Send(int peer, ReadOnlySpan<byte> data, Delivery delivery) => net.Send(id, IsHost ? peer : 0, data, delivery);

        public NetEventType Poll(out int peer, Span<byte> buffer, out int length)
        {
            peer = 0;
            length = 0;
            if (!net._inbox[id].TryDequeue(out var p)) return NetEventType.None;
            peer = IsHost ? p.From : 0;
            if (p.Type == NetEventType.Data)
            {
                length = Math.Min(p.Data.Length, buffer.Length);
                p.Data.AsSpan(0, length).CopyTo(buffer);
            }
            return p.Type;
        }

        public void Disconnect(int peer) => net.Drop(IsHost ? peer : id);

        public void Dispose()
        {
            if (!IsHost) net.Drop(id);
        }
    }
}
