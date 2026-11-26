using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Crossfire.Sim;
using Crossfire.Sim.Bots;
using Crossfire.Sim.Patterns;
using Crossfire.Sim.Players;
using Crossfire.Sim.Rules;

namespace Crossfire.Net;

public enum OnlineMode : byte
{
    OneVsOne,
    TwoVsTwo,
    FourVsFour,
}

public enum OnlinePhase : byte
{
    Connecting,
    Lobby,
    InMatch,
    /// <summary>Connection failed, refused or lost; see <see cref="OnlineSession.Failure"/>.</summary>
    Closed,
}

/// <summary>Seat layouts for online play (seat index = player slot).</summary>
public static class OnlineLayouts
{
    public static int Seats(OnlineMode mode) => mode switch { OnlineMode.OneVsOne => 2, OnlineMode.TwoVsTwo => 4, _ => 8 };

    public static (int Team, PlayerRole Role) Seat(OnlineMode mode, int seat) => mode switch
    {
        OnlineMode.OneVsOne => seat == 0 ? (0, PlayerRole.Dodger) : (1, PlayerRole.Gunner),
        OnlineMode.TwoVsTwo => (seat / 2, seat % 2 == 0 ? PlayerRole.Dodger : PlayerRole.Gunner),
        _ => (seat / 4, seat % 4 < 2 ? PlayerRole.Dodger : PlayerRole.Gunner),
    };
}

/// <summary>Everything every peer needs to create the same match.</summary>
public sealed class MatchStart
{
    public uint Seed;
    public OnlineMode Mode;
    public BotDifficulty Difficulty;
    public MatchRules Rules;
    public PlayerSetup[] Layout = [];
    /// <summary>Per slot: owning peer (0 = host, which also runs every bot).</summary>
    public int[] Owners = [];
    /// <summary>Per slot: true when a person plays it (false = host bot).</summary>
    public bool[] Human = [];
}

/// <summary>
/// An online game from connect to leave: join handshake (build check), the shared lobby (mode, bot difficulty,
/// role swap, seats, Gunner loadouts, ready flags; the host decides), match start, and routing of match packets
/// to <see cref="NetMatch"/>. Star topology: clients only talk to the host. Holds no UI, so it can be tested
/// over <see cref="LoopbackNetwork"/>.
/// </summary>
public sealed class OnlineSession : IDisposable
{
    public const ushort ProtocolVersion = 1;
    public const int MaxSeats = 8;

    private enum Msg : byte
    {
        Hello = 10,
        Welcome = 11,
        Refuse = 12,
        Lobby = 13,
        Seat = 14,
        Ready = 15,
        Loadout = 16,
        Start = 17,
        ToLobby = 18,
    }

    private enum RefuseReason : byte
    {
        BuildMismatch = 1,
        Full = 2,
        InProgress = 3,
    }

    private readonly byte[] _buf = new byte[4096];
    private readonly byte[] _send = new byte[4096];
    private readonly bool[] _joined = new bool[MaxSeats]; // host: peer has said Hello and was welcomed
    private int _matchesStarted;

    public ITransport Transport { get; }
    public bool IsHost => Transport.IsHost;
    public int LocalPeer { get; private set; }
    public OnlinePhase Phase { get; private set; }
    public string? Failure { get; private set; }

    // Lobby state (the host's copy is authoritative; clients receive it).
    public OnlineMode Mode { get; private set; } = OnlineMode.OneVsOne;
    public BotDifficulty Difficulty { get; private set; } = BotDifficulty.Normal;
    public bool SwapRoles { get; private set; }
    /// <summary>Per seat: owning peer, or -1 for a bot.</summary>
    public readonly int[] SeatOwner = new int[MaxSeats];
    public readonly bool[] SeatReady = new bool[MaxSeats];
    public readonly Loadout[] SeatLoadout = new Loadout[MaxSeats];
    /// <summary>Peers in the lobby (host: 0 plus welcomed clients; clients: as last told).</summary>
    public readonly bool[] PeerPresent = new bool[MaxSeats];
    /// <summary>Increments whenever the lobby changes (for UI sounds and redraws).</summary>
    public int Version { get; private set; }

    public NetMatch? Match { get; private set; }
    public MatchStart? CurrentStart { get; private set; }
    /// <summary>Increments when a match (or rematch) starts; the app creates its per-match objects then.</summary>
    public int MatchVersion => _matchesStarted;
    public RollbackOptions RollbackOptions { get; set; } = new(InputDelay: 2, Window: 8);
    /// <summary>Host: rules used for matches it starts (defaults; F1 tuning on the host carries over).</summary>
    public MatchRules HostRules { get; set; } = MatchRules.Default;

    public OnlineSession(ITransport transport)
    {
        Transport = transport;
        Array.Fill(SeatOwner, -1);
        for (int i = 0; i < MaxSeats; i++) SeatLoadout[i] = Loadout.Classic;
        if (transport.IsHost)
        {
            LocalPeer = NetMatch.HostPeer;
            Phase = OnlinePhase.Lobby;
            PeerPresent[0] = true;
            _joined[0] = true;
            SeatOwner[0] = 0; // the host sits down first
        }
        else
        {
            Phase = OnlinePhase.Connecting;
        }
    }

    /// <summary>Identifies a compatible build: protocol, rules layout and default rules must all match.</summary>
    public static ulong BuildId
    {
        get
        {
            var rules = MatchRules.Default;
            ulong h = 1469598103934665603UL;
            foreach (byte b in MemoryMarshal.AsBytes(new ReadOnlySpan<MatchRules>(in rules))) h = (h ^ b) * 1099511628211UL;
            return h ^ ((ulong)ProtocolVersion << 48) ^ (ulong)Unsafe.SizeOf<MatchRules>();
        }
    }

    public int SeatCount => OnlineLayouts.Seats(Mode);
    public int MySeat => SeatOf(LocalPeer);

    public int SeatOf(int peer)
    {
        for (int s = 0; s < SeatCount; s++) if (SeatOwner[s] == peer) return s;
        return -1;
    }

    public int PeersPresent
    {
        get
        {
            int n = 0;
            foreach (bool b in PeerPresent) if (b) n++;
            return n;
        }
    }

    /// <summary>Every seated person is ready (and there is at least one).</summary>
    public bool AllReady
    {
        get
        {
            int humans = 0;
            for (int s = 0; s < SeatCount; s++)
            {
                if (SeatOwner[s] < 0) continue;
                humans++;
                if (!SeatReady[s] && SeatOwner[s] != 0) return false; // the host starts the match, so it counts as ready
            }
            return humans > 0;
        }
    }

    // --- Polling and routing -----------------------------------------------------------------------

    public void Poll()
    {
        if (Phase == OnlinePhase.Closed) return;
        while (true)
        {
            var type = Transport.Poll(out int peer, _buf, out int n);
            if (type == NetEventType.None) break;
            switch (type)
            {
                case NetEventType.Connected:
                    if (!IsHost) Send(0, Hello());
                    break;
                case NetEventType.Disconnected:
                    OnDisconnected(peer);
                    break;
                case NetEventType.Data:
                    var m = _buf.AsSpan(0, n);
                    if (n == 0) break;
                    if (m[0] < 10) { if (Match != null && Phase == OnlinePhase.InMatch) Match.Handle(peer, m); }
                    else HandleLobby(peer, m);
                    break;
            }
            if (Phase == OnlinePhase.Closed) break;
        }
        if (!IsHost && Match is { HostLost: true }) Close(Failure ?? "Lost connection to the host.");
    }

    private void OnDisconnected(int peer)
    {
        if (!IsHost)
        {
            Close(Transport.FailureReason ?? (Phase == OnlinePhase.Connecting ? "Could not connect." : "Lost connection to the host."));
            return;
        }
        if ((uint)peer >= MaxSeats) return;
        _joined[peer] = false;
        PeerPresent[peer] = false;
        for (int s = 0; s < MaxSeats; s++)
            if (SeatOwner[s] == peer) { SeatOwner[s] = -1; SeatReady[s] = false; }
        Match?.OnDisconnected(peer); // their slots become host bots, in sync for everyone
        Changed();
    }

    private void Close(string reason)
    {
        if (Phase == OnlinePhase.Closed) return;
        Failure = reason;
        Phase = OnlinePhase.Closed;
        Version++;
    }

    private void HandleLobby(int peer, ReadOnlySpan<byte> m)
    {
        switch ((Msg)m[0])
        {
            // Host side.
            case Msg.Hello when IsHost && m.Length >= 11:
            {
                if ((uint)peer >= MaxSeats) return;
                ushort protocol = BinaryPrimitives.ReadUInt16LittleEndian(m[1..]);
                ulong build = BinaryPrimitives.ReadUInt64LittleEndian(m[3..]);
                if (protocol != ProtocolVersion || build != BuildId) { Refuse(peer, RefuseReason.BuildMismatch); return; }
                if (Phase == OnlinePhase.InMatch) { Refuse(peer, RefuseReason.InProgress); return; }
                _joined[peer] = true;
                PeerPresent[peer] = true;
                Send(peer, [(byte)Msg.Welcome, (byte)peer]);
                // Sit them in the first free seat, if any.
                for (int s = 0; s < SeatCount; s++) if (SeatOwner[s] < 0) { SeatOwner[s] = peer; break; }
                Changed();
                break;
            }
            case Msg.Seat when IsHost && _joined[peer] && m.Length >= 2:
                TakeSeat(peer, m[1] == 255 ? -1 : m[1]);
                break;
            case Msg.Ready when IsHost && _joined[peer] && m.Length >= 2:
            {
                int seat = SeatOf(peer);
                if (seat >= 0) { SeatReady[seat] = m[1] != 0; Changed(); }
                break;
            }
            case Msg.Loadout when IsHost && _joined[peer] && m.Length >= 5:
            {
                int seat = SeatOf(peer);
                if (seat >= 0 && !SeatReady[seat]) { SeatLoadout[seat] = ReadLoadout(m[1..]); Changed(); }
                break;
            }

            // Client side.
            case Msg.Welcome when !IsHost && m.Length >= 2:
                LocalPeer = m[1];
                Phase = OnlinePhase.Lobby;
                Version++;
                break;
            case Msg.Refuse when !IsHost:
                Close((RefuseReason)(m.Length > 1 ? m[1] : 0) switch
                {
                    RefuseReason.BuildMismatch => "The host runs a different version of CROSSFIRE.",
                    RefuseReason.Full => "The game is full.",
                    RefuseReason.InProgress => "A match is in progress. Try again when it ends.",
                    _ => "The host refused the connection.",
                });
                Transport.Disconnect(0);
                break;
            case Msg.Lobby when !IsHost:
                ReadLobby(m);
                break;
            case Msg.Start when !IsHost:
                if (ReadStart(m) is { } start) BeginMatch(start);
                break;
            case Msg.ToLobby when !IsHost:
                EndMatch();
                break;
        }
    }

    private void Refuse(int peer, RefuseReason reason) => Send(peer, [(byte)Msg.Refuse, (byte)reason]);

    private byte[] Hello()
    {
        var b = new byte[11];
        b[0] = (byte)Msg.Hello;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(1), ProtocolVersion);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(3), BuildId);
        return b;
    }

    private void Send(int peer, ReadOnlySpan<byte> data) => Transport.Send(peer, data, Delivery.Reliable);

    // --- Lobby actions (host applies directly; clients ask the host) -----------------------------------

    public void RequestSeat(int seat)
    {
        if (Phase != OnlinePhase.Lobby) return;
        if (IsHost) TakeSeat(0, seat);
        else Send(0, [(byte)Msg.Seat, (byte)(seat < 0 ? 255 : seat)]);
    }

    public void SetReady(bool ready)
    {
        if (Phase != OnlinePhase.Lobby) return;
        if (IsHost)
        {
            int s = SeatOf(0);
            if (s >= 0) { SeatReady[s] = ready; Changed(); }
        }
        else Send(0, [(byte)Msg.Ready, (byte)(ready ? 1 : 0)]);
    }

    public void SetLoadout(Loadout loadout)
    {
        if (Phase != OnlinePhase.Lobby) return;
        if (IsHost)
        {
            int s = SeatOf(0);
            if (s >= 0) { SeatLoadout[s] = loadout; Changed(); }
            return;
        }
        Span<byte> b = stackalloc byte[5];
        b[0] = (byte)Msg.Loadout;
        WriteLoadout(b[1..], loadout);
        Send(0, b);
    }

    /// <summary>Host: game settings. Changing the mode frees seats that no longer exist and clears every ready flag.</summary>
    public void Configure(OnlineMode mode, BotDifficulty difficulty, bool swapRoles)
    {
        if (!IsHost || Phase != OnlinePhase.Lobby) return;
        if (mode != Mode)
        {
            Mode = mode;
            // Re-seat everyone in the new layout, in seat order.
            Span<int> people = stackalloc int[MaxSeats];
            int n = 0;
            for (int s = 0; s < MaxSeats; s++) if (SeatOwner[s] >= 0) people[n++] = SeatOwner[s];
            Array.Fill(SeatOwner, -1);
            for (int i = 0; i < n && i < SeatCount; i++) SeatOwner[i] = people[i];
            Array.Clear(SeatReady);
        }
        Difficulty = difficulty;
        SwapRoles = swapRoles;
        Changed();
    }

    private void TakeSeat(int peer, int seat)
    {
        if (seat >= SeatCount || (seat >= 0 && SeatOwner[seat] >= 0 && SeatOwner[seat] != peer)) return;
        int old = SeatOf(peer);
        if (old >= 0)
        {
            SeatOwner[old] = -1;
            SeatReady[old] = false;
        }
        if (seat >= 0)
        {
            SeatOwner[seat] = peer;
            SeatReady[seat] = false;
            if (old >= 0) SeatLoadout[seat] = SeatLoadout[old];
        }
        Changed();
    }

    private void Changed()
    {
        Version++;
        if (!IsHost) return;
        var b = _send.AsSpan();
        int n = WriteLobby(b);
        for (int p = 1; p < MaxSeats; p++) if (_joined[p]) Send(p, b[..n]);
    }

    private int WriteLobby(Span<byte> b)
    {
        b[0] = (byte)Msg.Lobby;
        b[1] = (byte)Mode;
        b[2] = (byte)Difficulty;
        b[3] = (byte)(SwapRoles ? 1 : 0);
        int o = 4;
        for (int s = 0; s < MaxSeats; s++)
        {
            b[o++] = (byte)(sbyte)SeatOwner[s];
            b[o++] = (byte)(SeatReady[s] ? 1 : 0);
            WriteLoadout(b[o..], SeatLoadout[s]);
            o += 4;
        }
        for (int p = 0; p < MaxSeats; p++) b[o++] = (byte)(_joined[p] ? 1 : 0);
        return o;
    }

    private void ReadLobby(ReadOnlySpan<byte> m)
    {
        if (m.Length < 4 + MaxSeats * 6 + MaxSeats) return;
        Mode = (OnlineMode)m[1];
        Difficulty = (BotDifficulty)m[2];
        SwapRoles = m[3] != 0;
        int o = 4;
        for (int s = 0; s < MaxSeats; s++)
        {
            SeatOwner[s] = (sbyte)m[o++];
            SeatReady[s] = m[o++] != 0;
            SeatLoadout[s] = ReadLoadout(m[o..]);
            o += 4;
        }
        for (int p = 0; p < MaxSeats; p++) PeerPresent[p] = m[o++] != 0;
        Version++;
    }

    private static void WriteLoadout(Span<byte> b, in Loadout l)
    {
        for (int k = 0; k < MatchRules.PatternSlots; k++) b[k] = (byte)l[k];
    }

    private static Loadout ReadLoadout(ReadOnlySpan<byte> b)
    {
        var l = new Loadout();
        for (int k = 0; k < MatchRules.PatternSlots; k++) l[k] = (PatternKind)Math.Min(b[k], (byte)(MatchRules.PatternKinds - 1));
        return l;
    }

    // --- Match start / end -------------------------------------------------------------------------

    /// <summary>Host: starts (or restarts) a match with the current lobby. Returns false if not everyone is ready.</summary>
    public bool StartMatch(uint seed)
    {
        if (!IsHost || Phase == OnlinePhase.Closed) return false;
        if (Phase == OnlinePhase.Lobby && !AllReady) return false;
        int n = SeatCount;
        var rules = HostRules;
        // 1v1 swaps roles every round (like Vs Bot); team modes follow the lobby setting.
        rules.SwapRolesEachRound = Mode == OnlineMode.OneVsOne || SwapRoles;
        var start = new MatchStart
        {
            Seed = seed, Mode = Mode, Difficulty = Difficulty, Rules = rules,
            Layout = new PlayerSetup[n], Owners = new int[n], Human = new bool[n],
        };
        for (int s = 0; s < n; s++)
        {
            var (team, role) = OnlineLayouts.Seat(Mode, s);
            bool human = SeatOwner[s] >= 0;
            var loadout = human ? SeatLoadout[s] : BotController.LoadoutFor(Difficulty, BotSeed(s));
            start.Layout[s] = new PlayerSetup(team, role, loadout);
            start.Owners[s] = human ? SeatOwner[s] : NetMatch.HostPeer;
            start.Human[s] = human;
        }
        var b = _send.AsSpan();
        int len = WriteStart(b, start);
        for (int p = 1; p < MaxSeats; p++) if (_joined[p]) Send(p, b[..len]);
        BeginMatch(start);
        return true;
    }

    public static uint BotSeed(int slot) => 0xB07u + (uint)slot * 7919u;

    /// <summary>Host: everyone back to the lobby (ready flags cleared).</summary>
    public void ReturnToLobby()
    {
        if (!IsHost || Phase != OnlinePhase.InMatch) return;
        for (int p = 1; p < MaxSeats; p++) if (_joined[p]) Send(p, [(byte)Msg.ToLobby]);
        EndMatch();
        Changed();
    }

    private void BeginMatch(MatchStart start)
    {
        Match?.Dispose();
        using var initial = SimState.CreateMatch(start.Layout, start.Seed, start.Rules);
        Match = new NetMatch(Transport, new RollbackSession(initial, RollbackOptions), LocalPeer, start.Owners);
        CurrentStart = start;
        Phase = OnlinePhase.InMatch;
        _matchesStarted++;
        Version++;
    }

    private void EndMatch()
    {
        Match?.Dispose();
        Match = null;
        Array.Clear(SeatReady);
        Phase = OnlinePhase.Lobby;
        Version++;
    }

    private static int WriteStart(Span<byte> b, MatchStart s)
    {
        b[0] = (byte)Msg.Start;
        BinaryPrimitives.WriteUInt32LittleEndian(b[1..], s.Seed);
        b[5] = (byte)s.Mode;
        b[6] = (byte)s.Difficulty;
        b[7] = (byte)s.Layout.Length;
        int o = 8;
        for (int i = 0; i < s.Layout.Length; i++)
        {
            b[o++] = (byte)s.Layout[i].Team;
            b[o++] = (byte)s.Layout[i].Role;
            WriteLoadout(b[o..], s.Layout[i].Loadout ?? Loadout.Classic);
            o += 4;
            b[o++] = (byte)s.Owners[i];
            b[o++] = (byte)(s.Human[i] ? 1 : 0);
        }
        var rules = s.Rules;
        var bytes = MemoryMarshal.AsBytes(new ReadOnlySpan<MatchRules>(in rules));
        BinaryPrimitives.WriteInt32LittleEndian(b[o..], bytes.Length);
        o += 4;
        bytes.CopyTo(b[o..]);
        return o + bytes.Length;
    }

    private static MatchStart? ReadStart(ReadOnlySpan<byte> m)
    {
        if (m.Length < 8) return null;
        int n = m[7];
        if (n is < 1 or > MaxSeats) return null;
        var s = new MatchStart
        {
            Seed = BinaryPrimitives.ReadUInt32LittleEndian(m[1..]), Mode = (OnlineMode)m[5], Difficulty = (BotDifficulty)m[6],
            Layout = new PlayerSetup[n], Owners = new int[n], Human = new bool[n],
        };
        int o = 8;
        if (m.Length < o + n * 8 + 4) return null;
        for (int i = 0; i < n; i++)
        {
            int team = m[o++];
            var role = (PlayerRole)m[o++];
            var loadout = ReadLoadout(m[o..]);
            o += 4;
            s.Layout[i] = new PlayerSetup(team, role, loadout);
            s.Owners[i] = m[o++];
            s.Human[i] = m[o++] != 0;
        }
        int size = BinaryPrimitives.ReadInt32LittleEndian(m[o..]);
        o += 4;
        if (size != Unsafe.SizeOf<MatchRules>() || m.Length < o + size) return null;
        s.Rules = MemoryMarshal.Read<MatchRules>(m.Slice(o, size));
        return s;
    }

    /// <summary>Leave: clients disconnect from the host; the host closes the game for everyone.</summary>
    public void Dispose()
    {
        Match?.Dispose();
        Match = null;
        if (IsHost)
            for (int p = 1; p < MaxSeats; p++) { if (_joined[p]) Transport.Disconnect(p); }
        else Transport.Disconnect(0);
        Transport.Dispose();
        Phase = OnlinePhase.Closed;
    }
}
