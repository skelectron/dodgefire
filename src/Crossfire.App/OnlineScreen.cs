using System.Text;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Crossfire.Input;
using Crossfire.Net;
using Crossfire.Render;
using Crossfire.Sim.Bots;
using Crossfire.Sim.Patterns;

namespace Crossfire.App;

/// <summary>
/// Online UI drawn over the attract match: the connect page (host, or join by IP:port), the shared lobby
/// (mode, bots, roles, seats, Gunner patterns, ready / start), and messages when a connection fails or ends.
/// In-match overlays (waiting for a player, desync) are drawn by <see cref="DrawMatchOverlay"/>.
/// </summary>
internal sealed class OnlineScreen
{
    public enum Result
    {
        None,
        /// <summary>Close the online screen and go back to the title menu (session already disposed).</summary>
        Back,
        /// <summary>A session was created (host or join): the app should create its OnlineGame.</summary>
        SessionCreated,
    }

    private static readonly string[] ModeNames = ["1V1", "2V2", "4V4"];
    private static readonly string[] DifficultyNames = ["EASY", "NORMAL", "HARD"];
    private static readonly string[] ControlNames = ["MOUSE", "KEYBOARD", "GAMEPAD"];
    private static readonly string[] PresetNames = ["CLASSIC", "HUNTER", "TRAPPER", "CUSTOM"];
    private static readonly string[] PresetChoices = PresetNames.Select(n => $"<  {n}  >").ToArray();
    private static readonly string[] PlayerNames = Enumerable.Range(1, 8).Select(i => $"PLAYER {i}").ToArray();

    private readonly KeyboardInput _keyboard;
    private readonly MouseInput _mouse;
    private readonly HashSet<GlfwKey> _held = [];
    private readonly byte[] _address = new byte[64];
    private readonly string _settingsFile = Path.Combine(AppContext.BaseDirectory, "settings", "last_join.txt");
    private bool _leftWas = true, _rightWas = true;
    private string? _message;
    private string _hostInfo = "";
    private int _preset;
    private float _time;

    // Clickable rectangles from the last Draw.
    private readonly List<(float X, float Y, float W, float H, int Id)> _hits = [];

    public OnlineSession? Session { get; private set; }
    public ControlScheme Controls { get; set; } = ControlScheme.Mouse;
    /// <summary>The CUSTOM preset (the patterns picked in the title menu).</summary>
    public Loadout CustomLoadout { get; set; } = Loadout.Classic;
    public NetConditions SimulatedNetwork { get; set; }
    public ushort Port { get; set; } = RakNetTransport.DefaultPort;
    /// <summary>Host only on this address (e.g. 127.0.0.1 for local testing: no firewall prompt). Null = all interfaces.</summary>
    public string? BindAddress { get; set; }
    public bool IsOpen { get; private set; }
    /// <summary>Changes on every visible change (UI sounds).</summary>
    public int Signature => (Session?.Version ?? 0) * 31 + _preset * 7 + (int)Controls + (_message?.Length ?? 0);

    public OnlineScreen(KeyboardInput keyboard, MouseInput mouse)
    {
        _keyboard = keyboard;
        _mouse = mouse;
        string last = "127.0.0.1";
        try { if (File.Exists(_settingsFile)) last = File.ReadAllText(_settingsFile).Trim(); } catch (IOException) { }
        SetAddress(last);
    }

    private void SetAddress(string text)
    {
        Array.Clear(_address);
        Encoding.ASCII.GetBytes(text.AsSpan(0, Math.Min(text.Length, _address.Length - 1)), _address);
    }

    private string Address => Encoding.ASCII.GetString(_address, 0, Math.Max(0, Array.IndexOf(_address, (byte)0))).Trim();

    public void Open(string? message = null)
    {
        IsOpen = true;
        _message = message;
        _held.Clear();
        foreach (var k in Keys) if (_keyboard.IsDown(k)) _held.Add(k);
        _leftWas = _rightWas = true;
    }

    public void Close() => IsOpen = false;

    /// <summary>The online game ended (left, host gone, failure): show the reason on the connect page.</summary>
    public void SessionEnded(string? reason)
    {
        Session = null;
        Open(reason);
    }

    private static readonly GlfwKey[] Keys = [GlfwKey.Escape, GlfwKey.Enter, GlfwKey.KpEnter];

    private bool Pressed(GlfwKey k)
    {
        bool down = _keyboard.IsDown(k);
        bool was = _held.Contains(k);
        if (down && !was) { _held.Add(k); return true; }
        if (!down) _held.Remove(k);
        return false;
    }

    // --- Actions ---------------------------------------------------------------------------------------

    private ITransport Wrap(ITransport t) =>
        SimulatedNetwork is { LatencyMs: 0, JitterMs: 0, LossPercent: 0, DuplicatePercent: 0 } ? t : new ConditionedTransport(t, SimulatedNetwork, Environment.TickCount);

    public Result Host()
    {
        try
        {
            var raw = RakNetTransport.Host(Port, BindAddress);
            var ips = raw.LocalAddresses();
            _hostInfo = ips.Length > 0 ? $"YOUR ADDRESS: {string.Join("  /  ", ips)}  ·  PORT {Port} (UDP)" : $"PORT {Port} (UDP)";
            Session = new OnlineSession(Wrap(raw));
            _message = null;
            ApplyPreset();
            return Result.SessionCreated;
        }
        catch (InvalidOperationException e)
        {
            _message = e.Message;
            return Result.None;
        }
    }

    public Result Join(string address)
    {
        string host = address;
        ushort port = Port;
        int colon = address.LastIndexOf(':');
        if (colon > 0 && ushort.TryParse(address[(colon + 1)..], out ushort p)) { host = address[..colon]; port = p; }
        if (string.IsNullOrWhiteSpace(host)) { _message = "Type the host's address first."; return Result.None; }
        try
        {
            Session = new OnlineSession(Wrap(RakNetTransport.Join(host, port)));
            _hostInfo = $"HOST {host}:{port}";
            _message = null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_settingsFile)!);
                File.WriteAllText(_settingsFile, address);
            }
            catch (IOException) { }
            return Result.SessionCreated;
        }
        catch (InvalidOperationException e)
        {
            _message = e.Message;
            return Result.None;
        }
    }

    private Loadout PresetLoadout => _preset switch { 1 => Loadout.Hunter, 2 => Loadout.Trapper, 3 => CustomLoadout, _ => Loadout.Classic };

    private void ApplyPreset() => Session?.SetLoadout(PresetLoadout);

    // --- Update ----------------------------------------------------------------------------------------

    /// <summary>Handles clicks and keys. Returns Back when the player left the online screen.</summary>
    public Result Update(float dt, bool mouseBlocked, bool keyboardBlocked)
    {
        _time += dt;
        bool esc = !keyboardBlocked && Pressed(GlfwKey.Escape);
        bool enter = !keyboardBlocked && (Pressed(GlfwKey.Enter) | Pressed(GlfwKey.KpEnter));
        bool lDown = _mouse.LeftDown, rDown = _mouse.RightDown;
        bool click = lDown && !_leftWas && !mouseBlocked, rclick = rDown && !_rightWas && !mouseBlocked;
        _leftWas = lDown;
        _rightWas = rDown;
        int hit = -1;
        if (click || rclick)
        {
            var (mx, my) = _mouse.WindowPos;
            foreach (var h in _hits)
                if (mx >= h.X && mx <= h.X + h.W && my >= h.Y && my <= h.Y + h.H) { hit = h.Id; break; }
        }
        int dir = rclick ? -1 : 1;

        var s = Session;
        if (s == null)
        {
            // Connect page.
            if (esc || hit == IdBack) return Result.Back;
            if (hit == IdHost) return Host();
            if (hit == IdJoin || (enter && keyboardBlocked == false && _addressFocused)) return Join(Address);
            if (hit == IdControls) Controls = (ControlScheme)(((int)Controls + (dir < 0 ? 2 : 1)) % 3);
            return Result.None;
        }

        if (s.Phase == OnlinePhase.Closed) return Result.None; // the app shows the reason and disposes
        if (esc || hit == IdLeave) return Leave();
        if (s.Phase != OnlinePhase.Lobby) return Result.None;

        if (hit == IdControls) Controls = (ControlScheme)(((int)Controls + (dir < 0 ? 2 : 1)) % 3);
        if (s.IsHost)
        {
            if (hit == IdMode) s.Configure((OnlineMode)(((int)s.Mode + (dir < 0 ? 2 : 1)) % 3), s.Difficulty, s.SwapRoles);
            if (hit == IdBots) s.Configure(s.Mode, (BotDifficulty)(((int)s.Difficulty + (dir < 0 ? 2 : 1)) % 3), s.SwapRoles);
            if (hit == IdRoles) s.Configure(s.Mode, s.Difficulty, !s.SwapRoles);
            if ((hit == IdReady || enter) && s.AllReady) s.StartMatch((uint)Environment.TickCount);
        }
        else if (hit == IdReady || enter)
        {
            int seat = s.MySeat;
            if (seat >= 0) s.SetReady(!s.SeatReady[seat]);
        }
        if (hit >= IdSeat && hit < IdSeat + OnlineSession.MaxSeats)
        {
            int seat = hit - IdSeat;
            if (seat == s.MySeat && !s.SeatReady[seat]) { _preset = (_preset + (dir < 0 ? 3 : 1)) % 4; ApplyPreset(); }
            else if (s.SeatOwner[seat] < 0) { s.RequestSeat(seat); ApplyPreset(); }
        }
        return Result.None;
    }

    private Result Leave()
    {
        Session?.Dispose();
        Session = null;
        _message = null;
        return Result.Back;
    }

    private const int IdBack = 1, IdHost = 2, IdJoin = 3, IdControls = 4, IdLeave = 5, IdMode = 6, IdBots = 7, IdRoles = 8, IdReady = 9, IdSeat = 20;
    private bool _addressFocused;

    // --- Drawing ---------------------------------------------------------------------------------------

    public void Draw(GameRenderer r, float w, float h)
    {
        var dl = ImGui.GetForegroundDrawList(default);
        Fill(dl, 0, 0, w, h, new Color4(0.01f, 0.01f, 0.03f, 0.66f));
        _hits.Clear();
        float cx = w * 0.5f;
        r.Label(dl, "CROSSFIRE", cx, h * 0.07f, 56, new Color4(1f, 0.85f, 0.35f), center: true);
        r.Label(dl, "ONLINE", cx, h * 0.07f + 64, 22, new Color4(0.45f, 0.85f, 1f), center: true);

        var s = Session;
        if (s == null) DrawConnect(r, dl, w, h);
        else if (s.Phase == OnlinePhase.Connecting) DrawStatus(r, dl, w, h, $"CONNECTING TO {_hostInfo[5..]} ...", "ESC TO CANCEL");
        else if (s.Phase == OnlinePhase.Lobby) DrawLobby(r, dl, s, w, h);
        else if (s.Phase == OnlinePhase.InMatch) DrawStatus(r, dl, w, h, "STARTING ...", "");
    }

    private void DrawStatus(GameRenderer r, ImDrawListPtr dl, float w, float h, string text, string hint)
    {
        float cx = w * 0.5f;
        r.Label(dl, text, cx, h * 0.45f, 24, new Color4(0.9f, 0.92f, 0.97f), center: true);
        r.Label(dl, hint, cx, h * 0.45f + 40, 14, new Color4(0.6f, 0.62f, 0.7f), center: true);
        Button(r, dl, cx - 100, h * 0.45f + 80, 200, 40, "LEAVE", IdLeave, false);
    }

    private void DrawConnect(GameRenderer r, ImDrawListPtr dl, float w, float h)
    {
        float cx = w * 0.5f, y = h * 0.30f;
        Button(r, dl, cx - 170, y, 340, 50, "HOST A GAME", IdHost, true);
        r.Label(dl, $"Others join you on port {Port} (UDP). For internet play, forward that port on your router.", cx, y + 60, 13, new Color4(0.62f, 0.66f, 0.76f), center: true);

        y += 120;
        r.Label(dl, "JOIN A GAME", cx, y, 20, new Color4(0.85f, 0.87f, 0.93f), center: true);
        y += 34;
        // Address field: an ImGui text box placed over our layout.
        float fw = 340, fh = 40;
        Fill(dl, cx - fw / 2 - 4, y - 4, fw + 8, fh + 8, new Color4(1f, 1f, 1f, 0.06f));
        var pos = new float2(cx - fw / 2, y);
        ImGui.SetNextWindowPos(in pos, ImGuiCond.Always, default);
        ImGui.SetNextWindowBgAlpha(0f);
        if (ImGui.Begin("##joinaddress", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoMove))
        {
            ImGui.PushFont(r.Art?.Font ?? ImGui.GetFont(), 24f);
            ImGui.SetNextItemWidth(fw);
            ImGui.InputText("##addr", _address, ImGuiInputTextFlags.CharsNoBlank, (ImGuiInputTextCallback?)null, default);
            _addressFocused = ImGui.IsItemActive();
            ImGui.PopFont();
        }
        ImGui.End();
        r.Label(dl, "host address, e.g. 203.0.113.7 or 192.168.1.20:47820", cx, y + fh + 10, 12, new Color4(0.55f, 0.57f, 0.65f), center: true);
        Button(r, dl, cx - 110, y + fh + 36, 220, 46, "JOIN", IdJoin, true);

        y += fh + 120;
        Row(r, dl, cx, y, "YOUR CONTROLS", ControlNames[(int)Controls], IdControls);
        Button(r, dl, cx - 100, y + 60, 200, 40, "BACK", IdBack, false);
        if (_message != null) r.Label(dl, _message, cx, h - 96, 15, new Color4(1f, 0.55f, 0.45f), center: true);
        r.Label(dl, "CLICK TO CHOOSE  ·  ENTER JOINS  ·  ESC BACK", cx, h - 64, 13, new Color4(0.55f, 0.57f, 0.65f), center: true);
    }

    private void DrawLobby(GameRenderer r, ImDrawListPtr dl, OnlineSession s, float w, float h)
    {
        float cx = w * 0.5f, y = h * 0.22f;
        r.Label(dl, s.IsHost ? "YOU ARE HOSTING" : "IN THE LOBBY", cx, y, 18, new Color4(0.85f, 0.87f, 0.93f), center: true);
        r.Label(dl, _hostInfo, cx, y + 26, 13, new Color4(0.62f, 0.66f, 0.76f), center: true);
        y += 62;

        // Settings: host can change them; everyone sees them.
        float colW = 230, sx = cx - colW * 1.5f;
        Setting(r, dl, sx, y, colW, "MODE", ModeNames[(int)s.Mode], s.IsHost ? IdMode : -1);
        Setting(r, dl, sx + colW, y, colW, "BOTS", DifficultyNames[(int)s.Difficulty], s.IsHost ? IdBots : -1);
        if (s.Mode != OnlineMode.OneVsOne) Setting(r, dl, sx + 2 * colW, y, colW, "ROLES", s.SwapRoles ? "SWAP EACH ROUND" : "FIXED", s.IsHost ? IdRoles : -1);
        else Setting(r, dl, sx + 2 * colW, y, colW, "ROLES", "SWAP EACH ROUND", -1);
        y += 64;

        // Seats: blue on the top row, red below (4v4: four per row).
        int n = s.SeatCount, perTeam = n / 2;
        float sw = perTeam == 4 ? 200 : 230, sh = 118, gap = 12;
        for (int seat = 0; seat < n; seat++)
        {
            int team = seat / perTeam, col = seat % perTeam;
            float rowW = perTeam * sw + (perTeam - 1) * gap;
            float x = cx - rowW / 2 + col * (sw + gap), sy = y + team * (sh + gap);
            DrawSeat(r, dl, s, seat, x, sy, sw, sh);
        }
        y += 2 * (sh + gap) + 10;

        Row(r, dl, cx, y, "YOUR CONTROLS", ControlNames[(int)Controls], IdControls);
        y += 56;
        if (s.IsHost)
        {
            bool ready = s.AllReady;
            Button(r, dl, cx - 210, y, 200, 44, ready ? "START" : "WAITING FOR READY", IdReady, ready);
        }
        else
        {
            int mine = s.MySeat;
            bool ready = mine >= 0 && s.SeatReady[mine];
            Button(r, dl, cx - 210, y, 200, 44, mine < 0 ? "PICK A SEAT" : ready ? "READY ✓" : "READY", IdReady, mine >= 0 && !ready);
        }
        Button(r, dl, cx + 10, y, 200, 44, "LEAVE", IdLeave, false);
        r.Label(dl, s.IsHost ? "CLICK A SETTING TO CHANGE IT · CLICK A FREE SEAT TO MOVE · EMPTY SEATS ARE YOUR BOTS · ENTER STARTS"
                             : "CLICK A FREE SEAT TO MOVE · CLICK YOUR GUNNER SEAT TO CHANGE PATTERNS · ENTER READY",
            cx, h - 64, 13, new Color4(0.55f, 0.57f, 0.65f), center: true);
    }

    private void DrawSeat(GameRenderer r, ImDrawListPtr dl, OnlineSession s, int seat, float x, float y, float w, float h)
    {
        var (team, role) = OnlineLayouts.Seat(s.Mode, seat);
        var tc = GameRenderer.TeamColor[team];
        int owner = s.SeatOwner[seat];
        bool mine = owner == s.LocalPeer, ready = s.SeatReady[seat] || (owner == 0 && owner >= 0);
        Fill(dl, x, y, w, h, owner >= 0 ? new Color4(tc.R, tc.G, tc.B, mine ? 0.32f : 0.18f) : new Color4(1f, 1f, 1f, 0.04f));
        Fill(dl, x, y, w, 4, tc);
        _hits.Add((x, y, w, h, IdSeat + seat));
        r.Label(dl, $"{(team == 0 ? "BLUE" : "RED")} {(role == Crossfire.Sim.Players.PlayerRole.Dodger ? "DODGER" : "GUNNER")}", x + w / 2, y + 10, 14, tc, center: true);
        string who = owner < 0 ? "BOT" : mine ? "YOU" : owner == 0 ? "HOST" : PlayerNames[owner];
        r.Label(dl, who, x + w / 2, y + 32, 24, owner < 0 ? new Color4(0.5f, 0.52f, 0.6f) : new Color4(0.95f, 0.96f, 1f), center: true);
        if (owner >= 0)
            r.Label(dl, owner == 0 ? "HOST" : s.SeatReady[seat] ? "READY" : "NOT READY", x + w / 2, y + 62, 13,
                owner == 0 || s.SeatReady[seat] ? new Color4(0.45f, 1f, 0.55f) : new Color4(1f, 0.8f, 0.4f), center: true);
        bool patterns = role == Crossfire.Sim.Players.PlayerRole.Gunner || s.SwapRoles || s.Mode == OnlineMode.OneVsOne;
        if (mine && patterns)
        {
            Fill(dl, x + 8, y + h - 34, w - 16, 26, new Color4(1f, 0.78f, 0.3f, 0.16f));
            r.Label(dl, s.SeatReady[seat] ? PresetNames[_preset] : PresetChoices[_preset], x + w / 2, y + h - 30, 14, new Color4(1f, 0.85f, 0.5f), center: true);
        }
        else if (owner < 0)
            r.Label(dl, "CLICK TO SIT", x + w / 2, y + h - 28, 11, new Color4(0.5f, 0.52f, 0.6f), center: true);
        _ = ready;
    }

    private void Setting(GameRenderer r, ImDrawListPtr dl, float x, float y, float w, string label, string value, int id)
    {
        r.Label(dl, label, x + w / 2, y, 12, new Color4(0.6f, 0.62f, 0.7f), center: true);
        r.Label(dl, id >= 0 ? $"<  {value}  >" : value, x + w / 2, y + 18, 19, id >= 0 ? new Color4(1f, 0.85f, 0.4f) : new Color4(0.85f, 0.87f, 0.93f), center: true);
        if (id >= 0) _hits.Add((x + 10, y - 4, w - 20, 46, id));
    }

    private void Row(GameRenderer r, ImDrawListPtr dl, float cx, float y, string label, string value, int id)
    {
        r.Label(dl, label, cx - 30, y + 6, 14, new Color4(0.6f, 0.62f, 0.7f), rightAlign: true);
        r.Label(dl, $"<  {value}  >", cx + 100, y, 22, new Color4(0.85f, 0.85f, 0.95f), center: true);
        _hits.Add((cx - 240, y - 6, 480, 40, id));
    }

    private void Button(GameRenderer r, ImDrawListPtr dl, float x, float y, float w, float h, string text, int id, bool primary)
    {
        var (mx, my) = _mouse.WindowPos;
        bool hover = mx >= x && mx <= x + w && my >= y && my <= y + h;
        var bg = primary ? new Color4(0.25f, 0.7f, 0.35f, hover ? 0.6f : 0.35f) : new Color4(1f, 1f, 1f, hover ? 0.14f : 0.06f);
        Fill(dl, x, y, w, h, bg);
        r.Label(dl, text, x + w / 2, y + h / 2 - 10, 19, primary ? new Color4(0.92f, 1f, 0.92f) : new Color4(0.85f, 0.87f, 0.93f), center: true);
        _hits.Add((x, y, w, h, id));
    }

    // --- In-match overlays ----------------------------------------------------------------------------

    public static void DrawMatchOverlay(GameRenderer r, OnlineGame game, float w, float h)
    {
        var dl = ImGui.GetForegroundDrawList(default);
        var m = game.Match;
        if (m == null) return;
        float cx = w * 0.5f;

        // Ping (client: to the host; host: the slowest client), from cached strings so drawing does not allocate.
        double ping = 0;
        if (m.IsHost) { for (int p = 1; p < 8; p++) ping = Math.Max(ping, m.PingMs(p)); }
        else ping = m.PingMs(NetMatch.HostPeer);
        if (ping > 0)
        {
            int bucket = Math.Clamp((int)(ping / 5), 0, PingText.Length - 1);
            var pc = ping < 80 ? new Color4(0.5f, 0.9f, 0.6f) : ping < 160 ? new Color4(1f, 0.85f, 0.4f) : new Color4(1f, 0.5f, 0.4f);
            r.Label(dl, PingText[bucket], cx, 100, 12, pc, center: true);
        }
        if (m.Desynced)
        {
            Fill(dl, cx - 330, h * 0.4f, 660, 96, new Color4(0.15f, 0.02f, 0.02f, 0.9f));
            r.Label(dl, $"DESYNC AT TICK {m.DesyncTick}", cx, h * 0.4f + 14, 28, new Color4(1f, 0.45f, 0.4f), center: true);
            r.Label(dl, "The games disagreed and stopped. Replays were saved to the replays folder. ESC TO LEAVE", cx, h * 0.4f + 58, 13, new Color4(0.9f, 0.85f, 0.85f), center: true);
        }
        else if (game.StalledFor > 0.5 && game.WaitingForSlot >= 0)
        {
            Fill(dl, cx - 190, h * 0.18f, 380, 44, new Color4(0.02f, 0.02f, 0.05f, 0.85f));
            r.Label(dl, WaitingText[game.WaitingForSlot], cx, h * 0.18f + 12, 18, new Color4(1f, 0.85f, 0.4f), center: true);
        }
    }

    private static readonly string[] PingText = Enumerable.Range(0, 200).Select(i => $"PING {i * 5} MS").ToArray();
    private static readonly string[] WaitingText =Enumerable.Range(1, 8).Select(i => $"WAITING FOR P{i} ...").ToArray();

    private static void Fill(ImDrawListPtr dl, float x, float y, float w, float h, Color4 c)
    {
        var a = new float2(x, y);
        var b = new float2(x + w, y + h);
        var col = new ImColor8((byte)(Math.Clamp(c.R, 0, 1) * 255), (byte)(Math.Clamp(c.G, 0, 1) * 255), (byte)(Math.Clamp(c.B, 0, 1) * 255), (byte)(Math.Clamp(c.A, 0, 1) * 255));
        ImDrawListExtensions.AddRectFilled(dl, in a, in b, col, 6f, default);
    }
}
