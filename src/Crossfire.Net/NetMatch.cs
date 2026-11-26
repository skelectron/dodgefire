using System.Buffers.Binary;
using Crossfire.Sim;

namespace Crossfire.Net;

/// <summary>Supplies inputs for the slots this machine owns (the local player, and on the host every bot).</summary>
public interface ILocalInputSource
{
    SimInput Read(int slot, SimState state);
}

/// <summary>Wire message ids (first byte of every packet).</summary>
public enum MessageType : byte
{
    Frame = 1,
    Hash = 2,
    Desync = 3,
    SlotOwner = 4,
    // Lobby and match setup messages live in NetLobby (10+).
}

/// <summary>
/// One online match on top of a <see cref="RollbackSession"/>: star topology (clients talk only to the host,
/// the host relays every slot's inputs to everyone). Inputs travel unreliably in every packet until acknowledged,
/// so losses are repaired by the next packet. Also measures round-trip time and frame advantage (for time sync),
/// hands a disconnected player's slot to a host bot, and compares state hashes every <see cref="HashInterval"/> ticks.
/// </summary>
public sealed class NetMatch : IConfirmedTickSink, IDisposable
{
    public const int HostPeer = 0;
    public const int HashInterval = 30;
    private const int MaxRun = 16;
    private const int MaxPeers = SimState.MaxPlayers; // host + up to 7 clients

    private readonly ITransport _transport;
    private readonly byte[] _send = new byte[1400];
    private readonly byte[] _recv = new byte[2048];
    private readonly int[] _owner;                       // per slot: owning peer id (0 = host, also bots)
    private readonly int[,] _peerAck;                    // [peer, slot]: newest contiguous tick that peer confirmed it has
    private readonly bool[] _peerLive = new bool[MaxPeers];
    private readonly int[] _peerTick = new int[MaxPeers];     // latest sender tick heard from that peer
    private readonly double[] _rttTicks = new double[MaxPeers];
    private readonly double[] _advantage = new double[MaxPeers];
    private readonly (int Tick, ulong Hash)[] _myHashes = new (int, ulong)[64];
    private readonly (int Tick, ulong Hash)[,] _peerHashes = new (int, ulong)[MaxPeers, 64];

    public RollbackSession Session { get; }
    public int LocalPeer { get; }
    public bool IsHost => LocalPeer == HostPeer;
    /// <summary>Forwarded confirmed ticks (stats, replay recorder).</summary>
    public IConfirmedTickSink? Sink { get; set; }

    public bool HostLost { get; private set; }
    public int DesyncTick { get; private set; } = -1;
    public bool Desynced => DesyncTick >= 0;
    public long BytesSent { get; private set; }
    public long BytesReceived { get; private set; }
    /// <summary>Changes whenever slot ownership changes (players dropping out become host bots).</summary>
    public int OwnerVersion { get; private set; }

    public NetMatch(ITransport transport, RollbackSession session, int localPeer, ReadOnlySpan<int> slotOwners)
    {
        _transport = transport;
        Session = session;
        LocalPeer = localPeer;
        _owner = slotOwners.ToArray();
        _peerAck = new int[MaxPeers, session.Slots];
        for (int p = 0; p < MaxPeers; p++)
        for (int s = 0; s < session.Slots; s++) _peerAck[p, s] = session.ContiguousTick(s);
        foreach (int o in _owner) if (o > 0 && o < MaxPeers) _peerLive[o] = true;
        _peerLive[HostPeer] = true;
        Session.Sink = this;
    }

    public int OwnerOf(int slot) => _owner[slot];
    public bool IsLocalSlot(int slot) => _owner[slot] == LocalPeer;
    /// <summary>Round trip to a peer in milliseconds (clients: only the host is known).</summary>
    public double PingMs(int peer) => _rttTicks[peer] * 1000.0 / Sim.Rules.MatchRules.TicksPerSecond;
    public double PingToSlotMs(int slot) => IsHost ? PingMs(_owner[slot]) : PingMs(HostPeer);

    /// <summary>
    /// How many ticks this machine runs ahead of the others (positive = ahead, should slow down a little).
    /// Host: worst case over clients. Client: against the host.
    /// </summary>
    public double FrameAdvantage
    {
        get
        {
            if (!IsHost) return _advantage[HostPeer];
            double worst = 0;
            for (int p = 1; p < MaxPeers; p++) if (_peerLive[p]) worst = Math.Max(worst, _advantage[p]);
            return worst;
        }
    }

    // --- Receiving ---------------------------------------------------------------------------------

    /// <summary>Drains the network. Call every frame (also while stalled).</summary>
    public void Poll()
    {
        while (true)
        {
            var type = _transport.Poll(out int peer, _recv, out int length);
            if (type == NetEventType.None) return;
            if ((uint)peer >= MaxPeers) continue;
            switch (type)
            {
                case NetEventType.Disconnected:
                    OnDisconnected(peer);
                    break;
                case NetEventType.Data:
                    Handle(peer, _recv.AsSpan(0, length));
                    break;
            }
        }
    }

    /// <summary>A peer's connection ended: host turns their slots into bots; a client has lost the host.</summary>
    public void OnDisconnected(int peer)
    {
        if (IsHost) DropPeer(peer);
        else HostLost = true;
    }

    /// <summary>Handles one match packet (when something else polls the transport and routes packets here).</summary>
    public void Handle(int peer, ReadOnlySpan<byte> m)
    {
        if (m.Length < 1 || (uint)peer >= MaxPeers) return;
        BytesReceived += m.Length;
        switch ((MessageType)m[0])
        {
            case MessageType.Frame: ReadFrame(peer, m); break;
            case MessageType.Hash when IsHost && m.Length >= 13:
                RecordPeerHash(peer, BinaryPrimitives.ReadInt32LittleEndian(m[1..]), BinaryPrimitives.ReadUInt64LittleEndian(m[5..]));
                break;
            case MessageType.Desync when !IsHost && m.Length >= 5:
                if (DesyncTick < 0) DesyncTick = BinaryPrimitives.ReadInt32LittleEndian(m[1..]);
                break;
            case MessageType.SlotOwner when !IsHost && m.Length >= 3:
                if (m[1] < _owner.Length) { _owner[m[1]] = m[2]; OwnerVersion++; }
                break;
        }
    }

    private void ReadFrame(int peer, ReadOnlySpan<byte> m)
    {
        // [type][senderTick:i32][echoTick:i32][slots:u8][ack:i32 x slots][runs:u8]{slot:u8,start:i32,count:u8,input x count}
        if (m.Length < 10) return;
        int senderTick = BinaryPrimitives.ReadInt32LittleEndian(m[1..]);
        int echoTick = BinaryPrimitives.ReadInt32LittleEndian(m[5..]);
        int slots = m[9];
        int o = 10;
        if (slots != Session.Slots || m.Length < o + slots * 4 + 1) return;

        if (senderTick > _peerTick[peer])
        {
            _peerTick[peer] = senderTick;
            // RTT from the echo of our own tick; advantage = how far we are ahead of the peer's estimated "now".
            int rtt = Session.CurrentTick - echoTick;
            if (echoTick > 0 && rtt >= 0 && rtt < 600) _rttTicks[peer] = _rttTicks[peer] == 0 ? rtt : _rttTicks[peer] * 0.9 + rtt * 0.1;
            double peerNow = senderTick + _rttTicks[peer] * 0.5;
            _advantage[peer] = _advantage[peer] * 0.9 + (Session.CurrentTick - peerNow) * 0.1;
        }

        for (int s = 0; s < slots; s++, o += 4)
        {
            int ack = BinaryPrimitives.ReadInt32LittleEndian(m[o..]);
            if (ack > _peerAck[peer, s]) _peerAck[peer, s] = ack;
        }
        int runs = m[o++];
        for (int r = 0; r < runs; r++)
        {
            if (m.Length < o + 6) return;
            int slot = m[o];
            int start = BinaryPrimitives.ReadInt32LittleEndian(m[(o + 1)..]);
            int count = m[o + 5];
            o += 6;
            if (m.Length < o + count * Wire.InputSize) return;
            // Host accepts a client's runs only for that client's own slots; clients accept everything from the host.
            bool allowed = (uint)slot < (uint)Session.Slots && (!IsHost || _owner[slot] == peer) && !IsLocalSlot(slot);
            for (int k = 0; k < count; k++, o += Wire.InputSize)
                if (allowed) Session.SetInput(slot, start + k, Wire.ReadInput(m[o..]));
        }
    }

    // --- Ticking -----------------------------------------------------------------------------------

    /// <summary>
    /// If every needed input is close enough, schedules this machine's inputs, advances the session by one tick
    /// (rolling back if needed) and returns true. Returns false while waiting for a laggy peer.
    /// </summary>
    public bool TryAdvance(ILocalInputSource local)
    {
        if (Desynced || HostLost) return false;
        if (!Session.CanAdvance) return false;
        int at = Session.LocalInputTick;
        for (int s = 0; s < Session.Slots; s++)
            if (IsLocalSlot(s)) Session.SetInput(s, at, local.Read(s, Session.State));
        Session.Advance();
        return true;
    }

    /// <summary>Sends this tick's frame to every peer (inputs not yet acknowledged + acks). Call once per tick, also while stalled.</summary>
    public void SendFrames()
    {
        if (IsHost)
        {
            for (int p = 1; p < MaxPeers; p++)
                if (_peerLive[p]) SendFrame(p);
        }
        else if (!HostLost) SendFrame(HostPeer);
    }

    private void SendFrame(int peer)
    {
        var b = _send.AsSpan();
        b[0] = (byte)MessageType.Frame;
        BinaryPrimitives.WriteInt32LittleEndian(b[1..], Session.CurrentTick);
        BinaryPrimitives.WriteInt32LittleEndian(b[5..], _peerTick[peer]);
        b[9] = (byte)Session.Slots;
        int o = 10;
        for (int s = 0; s < Session.Slots; s++, o += 4) BinaryPrimitives.WriteInt32LittleEndian(b[o..], Session.ContiguousTick(s));
        int runsAt = o++;
        int runs = 0;
        for (int s = 0; s < Session.Slots; s++)
        {
            // Client: only its own slots. Host: every slot the receiving peer does not own.
            bool send = IsHost ? _owner[s] != peer : IsLocalSlot(s);
            if (!send) continue;
            int from = _peerAck[peer, s] + 1;
            int to = IsLocalSlot(s) ? Session.LatestTick(s) : Session.ContiguousTick(s);
            if (to < from) continue;
            int count = Math.Min(MaxRun, to - from + 1);
            if (o + 6 + count * Wire.InputSize > b.Length) break;
            b[o] = (byte)s;
            BinaryPrimitives.WriteInt32LittleEndian(b[(o + 1)..], from);
            b[o + 5] = (byte)count;
            o += 6;
            for (int k = 0; k < count; k++, o += Wire.InputSize) Wire.WriteInput(b[o..], Session.ConfirmedInput(s, from + k));
            runs++;
        }
        b[runsAt] = (byte)runs;
        _transport.Send(peer, b[..o], Delivery.Unreliable);
        BytesSent += o;
    }

    // --- Confirmed ticks: forward, hash ---------------------------------------------------------------

    void IConfirmedTickSink.OnConfirmedTick(int tick, ReadOnlySpan<SimInput> inputs, ReadOnlySpan<SimEvent> events, SimState stateAfter)
    {
        Sink?.OnConfirmedTick(tick, inputs, events, stateAfter);
        if ((tick + 1) % HashInterval != 0) return;
        ulong hash = stateAfter.ComputeHash();
        _myHashes[(tick / HashInterval) & 63] = (tick, hash);
        if (IsHost)
        {
            for (int p = 1; p < MaxPeers; p++) Compare(p, tick);
        }
        else
        {
            var b = _send.AsSpan();
            b[0] = (byte)MessageType.Hash;
            BinaryPrimitives.WriteInt32LittleEndian(b[1..], tick);
            BinaryPrimitives.WriteUInt64LittleEndian(b[5..], hash);
            _transport.Send(HostPeer, b[..13], Delivery.Reliable);
        }
    }

    private void RecordPeerHash(int peer, int tick, ulong hash)
    {
        _peerHashes[peer, (tick / HashInterval) & 63] = (tick, hash);
        Compare(peer, tick);
    }

    private void Compare(int peer, int tick)
    {
        var mine = _myHashes[(tick / HashInterval) & 63];
        var theirs = _peerHashes[peer, (tick / HashInterval) & 63];
        if (mine.Tick != tick || theirs.Tick != tick || mine.Hash == theirs.Hash || Desynced) return;
        DesyncTick = tick;
        Span<byte> b = stackalloc byte[5];
        b[0] = (byte)MessageType.Desync;
        BinaryPrimitives.WriteInt32LittleEndian(b[1..], tick);
        for (int p = 1; p < MaxPeers; p++) if (_peerLive[p]) _transport.Send(p, b, Delivery.Reliable);
    }

    /// <summary>Hashes this machine computed, oldest first is not guaranteed (ring of the last 64 checks).</summary>
    public ulong? HashAt(int tick) => _myHashes[(tick / HashInterval) & 63].Tick == tick ? _myHashes[(tick / HashInterval) & 63].Hash : null;

    // --- Host: players leaving --------------------------------------------------------------------

    /// <summary>Host: a client left. Its slots become host bots from the next unknown tick (everyone gets the same inputs).</summary>
    public void DropPeer(int peer)
    {
        if (!IsHost || peer == HostPeer || !_peerLive[peer]) return;
        _peerLive[peer] = false;
        for (int s = 0; s < Session.Slots; s++)
        {
            if (_owner[s] != peer) continue;
            // Fill the gap up to our own schedule with the last known input, then the host bot takes over.
            var last = Session.ConfirmedInput(s, Session.ContiguousTick(s));
            for (int t = Session.ContiguousTick(s) + 1; t < Session.LocalInputTick; t++) Session.SetInput(s, t, last with { Buttons = last.Buttons & ~InputButtons.Pointer });
            _owner[s] = HostPeer;
            Span<byte> b = stackalloc byte[3];
            b[0] = (byte)MessageType.SlotOwner;
            b[1] = (byte)s;
            b[2] = HostPeer;
            for (int p = 1; p < MaxPeers; p++) if (_peerLive[p]) _transport.Send(p, b, Delivery.Reliable);
        }
        OwnerVersion++;
    }

    public void Dispose() => Session.Dispose();
}
