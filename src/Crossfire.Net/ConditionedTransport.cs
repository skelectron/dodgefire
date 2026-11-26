using System.Diagnostics;

namespace Crossfire.Net;

/// <summary>
/// Wraps a real transport and adds simulated network trouble to outgoing packets (--net-sim): extra latency,
/// jitter, and loss/duplication of unreliable packets. Reliable packets are only delayed, in order.
/// </summary>
public sealed class ConditionedTransport(ITransport inner, NetConditions conditions, int seed = 1) : ITransport
{
    private readonly struct Pending(long due, int peer, byte[] data, Delivery delivery)
    {
        public readonly long Due = due;
        public readonly int Peer = peer;
        public readonly byte[] Data = data;
        public readonly Delivery Delivery = delivery;
    }

    private readonly List<Pending> _queue = [];
    private readonly Random _rng = new(seed);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _lastReliableDue;

    public NetConditions Conditions { get; set; } = conditions;
    public ITransport Inner => inner;
    public bool IsHost => inner.IsHost;

    public void Send(int peer, ReadOnlySpan<byte> data, Delivery delivery)
    {
        var c = Conditions;
        if (c.LatencyMs == 0 && c.JitterMs == 0 && c.LossPercent == 0 && c.DuplicatePercent == 0) { inner.Send(peer, data, delivery); return; }
        if (delivery == Delivery.Unreliable && c.LossPercent > 0 && _rng.Next(100) < c.LossPercent) return;
        long due = _clock.ElapsedMilliseconds + c.LatencyMs + (c.JitterMs > 0 ? _rng.Next(c.JitterMs + 1) : 0);
        if (delivery == Delivery.Reliable)
        {
            due = Math.Max(due, _lastReliableDue);
            _lastReliableDue = due;
        }
        _queue.Add(new Pending(due, peer, data.ToArray(), delivery));
        if (delivery == Delivery.Unreliable && c.DuplicatePercent > 0 && _rng.Next(100) < c.DuplicatePercent)
            _queue.Add(new Pending(due + _rng.Next(1, 20), peer, data.ToArray(), delivery));
        Flush();
    }

    private void Flush()
    {
        long now = _clock.ElapsedMilliseconds;
        // Stable: due order, then insertion order (keeps reliable packets in order).
        for (int i = 0; i < _queue.Count;)
        {
            int best = -1;
            for (int k = 0; k < _queue.Count; k++)
                if (_queue[k].Due <= now && (best < 0 || _queue[k].Due < _queue[best].Due)) best = k;
            if (best < 0) break;
            var p = _queue[best];
            _queue.RemoveAt(best);
            inner.Send(p.Peer, p.Data, p.Delivery);
        }
    }

    public NetEventType Poll(out int peer, Span<byte> buffer, out int length)
    {
        Flush();
        return inner.Poll(out peer, buffer, out length);
    }

    public void Disconnect(int peer) => inner.Disconnect(peer);
    public string? FailureReason => inner.FailureReason;

    public void Dispose() => inner.Dispose();
}
