using System.Net.Sockets;
using System.Text;
using Brutal.RakNetApi;

namespace Crossfire.Net;

/// <summary>
/// UDP transport on RakNet (Brutal.RakNet). The host listens on one port (the only one that needs forwarding);
/// clients connect to it. RakNet reserves the first byte of a packet for its own message ids below
/// <see cref="DefaultMessageIDTypes.UserPacketEnum"/>, so every game packet is prefixed with that id.
/// </summary>
public sealed unsafe class RakNetTransport : ITransport
{
    public const ushort DefaultPort = 47820;
    private const int MaxClients = 7;
    private static readonly byte UserId = (byte)DefaultMessageIDTypes.UserPacketEnum;

    private readonly RakPeerInstance _peer;
    private readonly RakNetGUID[] _peers = new RakNetGUID[MaxClients + 1]; // index = peer id (host: 1..7; client: 0 = host)
    private readonly bool[] _used = new bool[MaxClients + 1];
    private readonly byte[] _out = new byte[2048];
    private bool _disposed;

    public bool IsHost { get; }
    /// <summary>Client: set when the connection attempt failed or the host refused (full, unreachable).</summary>
    public string? Failure { get; private set; }
    public string? FailureReason => Failure;
    public bool Connected => IsHost || _used[0];

    private RakNetTransport(bool host)
    {
        IsHost = host;
        _peer = RakNetLibrary.CreateInstance();
        if (_peer.IsNull()) throw new InvalidOperationException("RakNet: could not create a peer.");
    }

    /// <summary>Listens for up to 7 clients. <paramref name="bindAddress"/> limits it to one interface (e.g. 127.0.0.1 for tests).</summary>
    public static RakNetTransport Host(ushort port = DefaultPort, string? bindAddress = null)
    {
        var t = new RakNetTransport(host: true);
        var sd = bindAddress != null ? new SocketDescriptor(bindAddress, port, AddressFamily.InterNetwork) : new SocketDescriptor(port, AddressFamily.InterNetwork);
        Span<SocketDescriptor> sds = [sd];
        var result = RakPeerInterface.Startup(t._peer, MaxClients, sds, 0);
        if (result != StartupResult.RaknetStarted)
        {
            t.Dispose();
            throw new InvalidOperationException(result == StartupResult.SocketPortAlreadyInUse
                ? $"Port {port} is already in use (is another CROSSFIRE hosting?)."
                : $"Could not start hosting on port {port}: {result}.");
        }
        RakPeerInterface.SetMaximumIncomingConnections(t._peer, MaxClients);
        t.Setup();
        return t;
    }

    /// <summary>Starts connecting to a host; a Connected event (peer 0) follows when it accepts.</summary>
    public static RakNetTransport Join(string host, ushort port = DefaultPort)
    {
        var t = new RakNetTransport(host: false);
        Span<SocketDescriptor> sds = [new SocketDescriptor(0, AddressFamily.InterNetwork)];
        var result = RakPeerInterface.Startup(t._peer, 1, sds, 0);
        if (result != StartupResult.RaknetStarted)
        {
            t.Dispose();
            throw new InvalidOperationException($"Could not start networking: {result}.");
        }
        t.Setup();
        byte[] hostBytes = Encoding.ASCII.GetBytes(host + "\0");
        var attempt = RakPeerInterface.Connect(t._peer, hostBytes, port, ReadOnlySpan<byte>.Empty, null, 0, 6, 500, 0);
        if (attempt != ConnectionAttemptResult.ConnectionAttemptStarted)
        {
            t.Dispose();
            throw new InvalidOperationException(attempt == ConnectionAttemptResult.CannotResolveDomainName ? $"Cannot find host '{host}'." : $"Cannot connect: {attempt}.");
        }
        return t;
    }

    private void Setup()
    {
        RakPeerInterface.SetOccasionalPing(_peer, true);
        RakPeerInterface.SetTimeoutTime(_peer, 6000, SystemAddress.UNASSIGNED_SYSTEM_ADDRESS);
    }

    /// <summary>This machine's IPv4 addresses (to tell friends what to join).</summary>
    public string[] LocalAddresses()
    {
        uint n = RakPeerInterface.GetNumberOfAddresses(_peer);
        var list = new List<string>();
        for (uint i = 0; i < n; i++)
        {
            string ip = RakPeerInterface.GetLocalIP(_peer, i);
            if (!string.IsNullOrEmpty(ip) && !ip.Contains(':') && ip != "127.0.0.1") list.Add(ip);
        }
        return [.. list];
    }

    /// <summary>RakNet's round trip estimate to a peer in ms, or -1.</summary>
    public int PingMs(int peer) => (uint)peer < _used.Length && _used[peer] ? RakPeerInterface.GetAveragePing(_peer, AddressOrGUID.From(_peers[peer])) : -1;

    public void Send(int peer, ReadOnlySpan<byte> data, Delivery delivery)
    {
        if (_disposed || (uint)peer >= _used.Length || !_used[peer] || data.Length + 1 > _out.Length) return;
        _out[0] = UserId;
        data.CopyTo(_out.AsSpan(1));
        var reliability = delivery == Delivery.Reliable ? PacketReliability.ReliableOrdered : PacketReliability.Unreliable;
        var priority = delivery == Delivery.Reliable ? PacketPriority.HighPriority : PacketPriority.ImmediatePriority;
        RakPeerInterface.Send(_peer, _out.AsSpan(0, data.Length + 1), priority, reliability, 0, AddressOrGUID.From(_peers[peer]), false, 0);
    }

    public NetEventType Poll(out int peer, Span<byte> buffer, out int length)
    {
        peer = 0;
        length = 0;
        while (!_disposed)
        {
            Packet* p = RakPeerInterface.Receive(_peer);
            if (p == null) return NetEventType.None;
            try
            {
                if (p->Length == 0) continue;
                byte id = p->Data[0];
                var guid = p->Guid;
                switch ((DefaultMessageIDTypes)id)
                {
                    case DefaultMessageIDTypes.NewIncomingConnection when IsHost:
                    {
                        int slot = FreePeerId();
                        if (slot < 0)
                        {
                            RakPeerInterface.CloseConnection(_peer, AddressOrGUID.From(guid), true, 0, PacketPriority.HighPriority);
                            continue;
                        }
                        _peers[slot] = guid;
                        _used[slot] = true;
                        peer = slot;
                        return NetEventType.Connected;
                    }
                    case DefaultMessageIDTypes.ConnectionRequestAccepted when !IsHost:
                        _peers[0] = guid;
                        _used[0] = true;
                        peer = 0;
                        return NetEventType.Connected;
                    case DefaultMessageIDTypes.ConnectionAttemptFailed:
                        Failure = "Could not reach the host (wrong address, host not running, or its port is not forwarded).";
                        return NetEventType.Disconnected;
                    case DefaultMessageIDTypes.NoFreeIncomingConnections:
                        Failure = "The game is full.";
                        return NetEventType.Disconnected;
                    case DefaultMessageIDTypes.DisconnectionNotification:
                    case DefaultMessageIDTypes.ConnectionLost:
                    {
                        int id2 = PeerOf(guid);
                        if (id2 < 0) continue;
                        _used[id2] = false;
                        if (!IsHost) Failure ??= (DefaultMessageIDTypes)id == DefaultMessageIDTypes.ConnectionLost ? "Lost connection to the host." : "The host closed the game.";
                        peer = id2;
                        return NetEventType.Disconnected;
                    }
                    default:
                    {
                        if (id != UserId) continue; // RakNet internals we do not use
                        int from = PeerOf(guid);
                        if (from < 0) continue;
                        int n = (int)Math.Min(p->Length - 1, (uint)buffer.Length);
                        new ReadOnlySpan<byte>(p->Data + 1, n).CopyTo(buffer);
                        peer = from;
                        length = n;
                        return NetEventType.Data;
                    }
                }
            }
            finally
            {
                RakPeerInterface.DeallocatePacket(_peer, p);
            }
        }
        return NetEventType.None;
    }

    private int FreePeerId()
    {
        for (int i = 1; i < _used.Length; i++) if (!_used[i]) return i;
        return -1;
    }

    private int PeerOf(RakNetGUID guid)
    {
        for (int i = 0; i < _used.Length; i++) if (_used[i] && _peers[i].G == guid.G) return i;
        return -1;
    }

    public void Disconnect(int peer)
    {
        if ((uint)peer >= _used.Length || !_used[peer]) return;
        RakPeerInterface.CloseConnection(_peer, AddressOrGUID.From(_peers[peer]), true, 0, PacketPriority.HighPriority);
        _used[peer] = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RakPeerInterface.Shutdown(_peer, 300, 0, PacketPriority.HighPriority);
        RakPeerInterface.DestroyInstance(_peer);
    }
}
