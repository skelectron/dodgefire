using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Crossfire.Input;
using Crossfire.Render;
using Crossfire.Sim.Bots;
using Crossfire.Sim.Patterns;
using Crossfire.Sim.Players;

namespace Crossfire.App;

/// <summary>
/// Title screen drawn over a live Bot vs Bot match (attract mode). Two pages:
/// 1. Mode list: hovering only highlights. Click (or Enter) opens that mode's options.
/// 2. Options for the chosen mode (role, controls, difficulty), then START or BACK.
/// Values only ever change on a click or a Left/Right key, never on hover.
/// Keyboard: Up/Down (W/S) move, Left/Right (A/D) change, Enter/Space confirm, Esc back/quit.
/// </summary>
internal sealed class TitleMenu
{
    public enum Result
    {
        None,
        Start,
        Quit,
        /// <summary>Open the online screen (host or join).</summary>
        Online,
    }

    private static readonly (GameMode Mode, string Title, string Detail)[] Modes =
    [
        (GameMode.Team, "TEAM 4V4", "You and 3 bot teammates against 4 bots. Each team: 2 Dodgers + 2 Gunners."),
        (GameMode.VsBot, "VS BOT", "You against one bot. Roles swap each round."),
        (GameMode.Local, "LOCAL 1V1", "Two players: P1 on mouse, a gamepad or WASD + QERF; P2 on a gamepad or Arrows + UIOP."),
        (GameMode.LocalTeam, "LOCAL 2V2", "Up to 4 players on mouse, both keyboard halves and gamepads. Bots fill empty seats."),
        (GameMode.Online, "ONLINE", "Host a game or join one by IP address. Up to 8 players, bots fill the rest."),
        (GameMode.Demo, "BOT VS BOT", "Sit back and watch two bots play."),
    ];

    private enum Row
    {
        Role,
        Controls,
        Loadout,
        Difficulty,
        Roles,
        Start,
        Back,
    }

    private static readonly string[] RoleNames = ["DODGER", "GUNNER"];
    private static readonly string[] ControlNames = ["MOUSE", "KEYBOARD", "GAMEPAD"];
    private static readonly string[] DifficultyNames = ["EASY", "NORMAL", "HARD"];
    private const int MaxRows = 8;
    private static readonly string[] SwapNames = ["FIXED", "SWAP EACH ROUND"];

    private static readonly (string Name, string Line1, string Line2)[] PatternInfo =
    [
        ("RING", "Expanding rings", "with shifting gaps."),
        ("SPIRAL", "Rotating arms.", "Area denial."),
        ("AIMED", "Fast fan at the", "nearest Dodger. Cheap."),
        ("WALL", "Slow wall of big", "bullets with a gap."),
        ("SWEEP", "A laser stream that", "sweeps across an arc."),
        ("HOMING", "Bullets that briefly", "steer at the Dodger."),
        ("MINES", "Drifting mines that", "burst into rings."),
    ];
    private const int CardCount = 7;
    private const int LoadoutRows = CardCount + 2; // cards, DONE, BACK

    private readonly KeyboardInput _keyboard;
    private readonly MouseInput _mouse;
    private readonly HashSet<GlfwKey> _held = [];
    private bool _leftWas = true, _rightWas = true;
    private float _lastMouseX = float.NaN, _lastMouseY = float.NaN;

    private bool _optionsPage;
    private bool _loadoutPage;
    private readonly PatternKind[] _picked = [PatternKind.Ring, PatternKind.Spiral, PatternKind.Aimed, PatternKind.Wall];
    private int _pickedCount = 4;
    private readonly PatternKind[] _pickedBackup = new PatternKind[4];
    private int _pickedBackupCount;
    private readonly string[] _cardCost = new string[CardCount];
    private string _loadoutSummary = "RING · SPIRAL · AIMED · WALL";
    private readonly float[] _cardX = new float[LoadoutRows], _cardY = new float[LoadoutRows], _cardW = new float[LoadoutRows], _cardH = new float[LoadoutRows];
    private int _mode;
    private int _focus;
    private int _role;
    private int _controls; // 0 = mouse (default)
    private int _difficulty = 1;
    private int _swapRoles; // team modes: 0 = fixed roles (default), 1 = swap every round
    private float _time;

    // Row rectangles from the last Draw (window units), for mouse hit-testing.
    private readonly float[] _rowY = new float[MaxRows];
    private readonly float[] _rowH = new float[MaxRows];
    private int _rowCount;
    private float _rowX0, _rowX1, _centerX;

    // Lobby page (Local 2v2): seats in MatchSetups.LocalTeam order.
    private readonly GamepadInput _pads;
    private readonly Lobby _lobby = new(4);
    private readonly Func<InputDevice, bool> _deviceStillThere;
    private bool _lobbyPage;
    private readonly float[] _seatX = new float[4], _seatY = new float[4];
    private float _seatW, _seatH;
    private float _lobbyBtnY, _lobbyStartX, _lobbyBackX, _lobbyBtnW, _lobbyBtnH;
    private static readonly string[] SeatNames = ["BLUE DODGER", "BLUE GUNNER", "RED DODGER", "RED GUNNER"];
    private static readonly string[] PresetNames = ["CLASSIC", "HUNTER", "TRAPPER", "CUSTOM"];
    private static readonly string[] PresetChoices = PresetNames.Select(n => $"<  {n}  >").ToArray();
    private static readonly string[] PadLabels = Enumerable.Range(1, GamepadInput.MaxPads).Select(i => $"PAD {i}").ToArray();
    private static readonly string[] PadCountText = Enumerable.Range(0, GamepadInput.MaxPads + 1)
        .Select(n => n == 0 ? "NO GAMEPADS CONNECTED" : n == 1 ? "1 GAMEPAD CONNECTED" : $"{n} GAMEPADS CONNECTED").ToArray();

    public TitleMenu(KeyboardInput keyboard, MouseInput mouse, GamepadInput pads)
    {
        _keyboard = keyboard;
        _mouse = mouse;
        _pads = pads;
        _deviceStillThere = d => d.Kind != DeviceKind.Gamepad || _pads.IsConnected(d.Pad);
    }

    /// <summary>Seats for the Local 2v2 match: who sits where and the loadout each Gunner seat brings.</summary>
    private LobbySeat[] LobbySeats()
    {
        var seats = new LobbySeat[_lobby.Seats];
        for (int i = 0; i < seats.Length; i++)
        {
            var loadout = _lobby.PresetAt(i) switch
            {
                1 => Loadout.Hunter,
                2 => Loadout.Trapper,
                3 => CurrentLoadout,
                _ => Loadout.Classic,
            };
            seats[i] = new LobbySeat(_lobby.DeviceAt(i), loadout);
        }
        return seats;
    }

    private static bool IsGunnerSeat(int seat) => seat % 2 == 1;
    /// <summary>Gunner seats bring patterns; with role swap on, every seat does (Dodgers gun in later rounds).</summary>
    private bool HasLoadoutChoice(int seat) => IsGunnerSeat(seat) || _swapRoles == 1;

    /// <summary>Card costs shown on the loadout page (from the current rules).</summary>
    public void SetPatternCosts(in Crossfire.Sim.Rules.MatchRules rules)
    {
        for (int k = 0; k < CardCount; k++) _cardCost[k] = $"{(int)(rules.Patterns[k].Cost.Raw >> 16)} ENERGY";
    }

    private Loadout CurrentLoadout => Loadout.Of(_picked[0], _picked[1], _picked[2], _picked[3]);

    /// <summary>Changes whenever the highlighted row or page changes (for the UI move sound).</summary>
    public int FocusSignature => (_lobbyPage ? 3000 + LobbyMoveSignature : _loadoutPage ? 2000 : _optionsPage ? 1000 : 0) + _focus;
    /// <summary>Changes whenever a setting or the loadout changes (for the UI confirm sound).</summary>
    public int ValueSignature => _role + _swapRoles * 4 + _controls * 2 + _difficulty * 8 + _pickedCount * 32 + (int)_picked[0] * 256 + (int)_picked[1] * 2048 + (int)_picked[2] * 16384 + (int)_picked[3] * 131072
        + (_lobbyPage ? LobbyValueSignature * 1048576 : 0);

    private int LobbyMoveSignature
    {
        get
        {
            int sig = 0;
            for (int i = 0; i < _lobby.Seats; i++) sig = sig * 7 + (int)_lobby.DeviceAt(i).Kind + _lobby.DeviceAt(i).Pad + _lobby.PresetAt(i) * 3;
            return sig & 0x3FF;
        }
    }

    private int LobbyValueSignature
    {
        get
        {
            int sig = 0;
            for (int i = 0; i < _lobby.Seats; i++) sig = sig * 2 + (_lobby.IsReady(i) ? 1 : 0);
            return sig + _lobby.Humans * 16;
        }
    }

    /// <summary>Controls and patterns chosen in the menu (online uses them too).</summary>
    public ControlScheme Controls => (ControlScheme)_controls;
    public Loadout PickedLoadout => CurrentLoadout;

    public MatchOptions Options => new(
        Modes[_mode].Mode,
        (BotDifficulty)_difficulty,
        _role == 0 ? PlayerRole.Dodger : PlayerRole.Gunner,
        (ControlScheme)_controls,
        CurrentLoadout,
        Modes[_mode].Mode == GameMode.LocalTeam ? LobbySeats() : null,
        _swapRoles == 1);

    /// <summary>Call when the menu opens. Swallows keys and buttons that are already held.</summary>
    public void Open()
    {
        _held.Clear();
        foreach (var k in WatchedKeys) if (_keyboard.IsDown(k)) _held.Add(k);
        _leftWas = _rightWas = true;
        _optionsPage = false;
        _loadoutPage = false;
        _lobbyPage = false;
        _creditsPage = false;
        _focus = _mode;
    }

    private static readonly GlfwKey[] WatchedKeys =
        [GlfwKey.Up, GlfwKey.Down, GlfwKey.Left, GlfwKey.Right, GlfwKey.W, GlfwKey.S, GlfwKey.A, GlfwKey.D, GlfwKey.Enter, GlfwKey.KpEnter, GlfwKey.Space, GlfwKey.Escape,
         GlfwKey.X, GlfwKey.RightControl, GlfwKey.Backspace];

    private bool Pressed(GlfwKey k)
    {
        bool down = _keyboard.IsDown(k);
        bool wasHeld = _held.Contains(k);
        if (down && !wasHeld) { _held.Add(k); return true; }
        if (!down) _held.Remove(k);
        return false;
    }

    /// <summary>Rows of the options page for the chosen mode.</summary>
    private int OptionRows(Span<Row> into)
    {
        int n = 0;
        var mode = Modes[_mode].Mode;
        if (mode is GameMode.Team or GameMode.VsBot) into[n++] = Row.Role;
        if (mode is not (GameMode.Demo or GameMode.LocalTeam)) into[n++] = Row.Controls;
        if (mode != GameMode.Demo) into[n++] = Row.Loadout;
        if (mode is GameMode.Team or GameMode.LocalTeam) into[n++] = Row.Roles;
        if (mode != GameMode.Local) into[n++] = Row.Difficulty;
        into[n++] = Row.Start;
        into[n++] = Row.Back;
        return n;
    }

    private void Change(Row row, int dir)
    {
        switch (row)
        {
            case Row.Role: _role = (_role + 1) % 2; break;
            case Row.Controls: _controls = (_controls + (dir < 0 ? 2 : 1)) % 3; break;
            case Row.Difficulty: _difficulty = Math.Clamp(_difficulty + dir, 0, DifficultyNames.Length - 1); break;
            case Row.Roles: _swapRoles = 1 - _swapRoles; break;
        }
    }

    private void OpenOptions(int mode)
    {
        _mode = mode;
        _optionsPage = true;
        Span<Row> rows = stackalloc Row[MaxRows];
        int n = OptionRows(rows);
        _focus = n - 2; // START
        _rowCount = 0; // stale hit-test rects until the next Draw
    }

    private void CloseOptions()
    {
        _optionsPage = false;
        _focus = _mode;
        _rowCount = 0;
    }

    public Result Update(float dt, bool mouseBlocked)
    {
        _time += dt;
        if (_lobbyPage) return UpdateLobby(mouseBlocked);
        var pad = _pads.AnyMenu;
        bool up = Pressed(GlfwKey.Up) | Pressed(GlfwKey.W);
        bool down = Pressed(GlfwKey.Down) | Pressed(GlfwKey.S);
        bool left = Pressed(GlfwKey.Left) | Pressed(GlfwKey.A);
        bool right = Pressed(GlfwKey.Right) | Pressed(GlfwKey.D);
        bool confirm = Pressed(GlfwKey.Enter) | Pressed(GlfwKey.KpEnter) | Pressed(GlfwKey.Space);
        bool esc = Pressed(GlfwKey.Escape);
        // Any gamepad can drive the menu too (B = back, but never quits the game from the first page).
        up |= (pad & MenuActions.Up) != 0;
        down |= (pad & MenuActions.Down) != 0;
        left |= (pad & MenuActions.Left) != 0;
        right |= (pad & MenuActions.Right) != 0;
        confirm |= (pad & MenuActions.Confirm) != 0;
        bool padBack = (pad & MenuActions.Back) != 0;
        if (padBack && (_optionsPage || _loadoutPage || _creditsPage)) esc = true;

        var (mx, my) = _mouse.WindowPos;
        bool moved = mx != _lastMouseX || my != _lastMouseY;
        _lastMouseX = mx;
        _lastMouseY = my;
        bool lDown = _mouse.LeftDown, rDown = _mouse.RightDown;
        bool lClick = lDown && !_leftWas, rClick = rDown && !_rightWas;
        _leftWas = lDown;
        _rightWas = rDown;
        int hit = mouseBlocked ? -1 : HitRow(mx, my);

        if (_loadoutPage) return UpdateLoadout(up, down, left, right, confirm, esc, lClick, rClick, mx, my, moved, mouseBlocked);

        if (_creditsPage)
        {
            if (esc || confirm || lClick || rClick) { _creditsPage = false; _focus = CreditsRow; }
            return Result.None;
        }

        if (!_optionsPage)
        {
            // Page 1: modes, then CREDITS. Hover highlights only; click/Enter opens the options (or credits) page.
            int count = CreditsRow + 1;
            if (esc) return Result.Quit;
            if (up) _focus = (_focus + count - 1) % count;
            if (down) _focus = (_focus + 1) % count;
            if (hit >= 0 && hit < count && moved) _focus = hit;
            int pick = confirm ? _focus : lClick && hit >= 0 && hit < count ? hit : -1;
            if (pick == CreditsRow) { _creditsPage = true; _rowCount = 0; }
            else if (pick >= 0 && Modes[pick].Mode == GameMode.Online) { _mode = pick; return Result.Online; }
            else if (pick >= 0) OpenOptions(pick);
            return Result.None;
        }

        // Page 2: options for the chosen mode.
        Span<Row> rows = stackalloc Row[MaxRows];
        int n = OptionRows(rows);
        if (esc || rClick && hit < 0) { CloseOptions(); return Result.None; }
        if (up) _focus = (_focus + n - 1) % n;
        if (down) _focus = (_focus + 1) % n;
        if (hit >= 0 && hit < n && moved) _focus = hit;
        if (left || right) Change(rows[_focus], right ? 1 : -1);

        if (confirm)
        {
            if (rows[_focus] == Row.Back) { CloseOptions(); return Result.None; }
            if (rows[_focus] == Row.Loadout) { OpenLoadout(); return Result.None; }
            return StartOrLobby();
        }
        if ((lClick || rClick) && hit >= 0 && hit < n)
        {
            _focus = hit;
            switch (rows[hit])
            {
                case Row.Loadout: OpenLoadout(); return Result.None;
                case Row.Start: return lClick ? StartOrLobby() : Result.None;
                case Row.Back: CloseOptions(); return Result.None;
                default:
                    // Left half (or right click) = previous, right half = next.
                    Change(rows[hit], rClick || mx < _centerX ? -1 : 1);
                    break;
            }
        }
        return Result.None;
    }

    private int HitRow(float x, float y)
    {
        if (x < _rowX0 || x > _rowX1) return -1;
        for (int i = 0; i < _rowCount; i++)
            if (y >= _rowY[i] && y <= _rowY[i] + _rowH[i]) return i;
        return -1;
    }

    public void Draw(GameRenderer r, float w, float h)
    {
        var dl = ImGui.GetForegroundDrawList(default);
        // Dim the attract-mode match behind the menu.
        Fill(dl, 0, 0, w, h, new Color4(0.01f, 0.01f, 0.03f, 0.62f));

        float cx = w * 0.5f;
        _centerX = cx;
        _rowX0 = cx - 260;
        _rowX1 = cx + 260;
        float y = h * 0.11f;
        float pulse = 0.85f + 0.15f * MathF.Sin(_time * 2.2f);
        r.Label(dl, "CROSSFIRE", cx + 3, y + 3, 72, new Color4(0, 0, 0, 0.6f), center: true);
        r.Label(dl, "CROSSFIRE", cx, y, 72, new Color4(1f, 0.85f * pulse, 0.35f), center: true);
        r.Label(dl, "TEAM BULLET-HELL PVP", cx, y + 82, 18, new Color4(0.75f, 0.78f, 0.9f), center: true);
        _rowCount = 0;

        if (_creditsPage) DrawCredits(r, dl, cx, h);
        else if (_lobbyPage) DrawLobby(r, dl, w, h);
        else if (_loadoutPage) DrawLoadout(r, dl, w, h);
        else if (!_optionsPage) DrawModes(r, dl, cx, h);
        else DrawOptions(r, dl, cx, h);

        r.Label(dl, "ART AND FONT BY KENNEY.NL (CC0)  ·  BUILT WITH BRUTAL", cx, h - 36, 12, new Color4(0.42f, 0.44f, 0.5f), center: true);
    }

    private void DrawModes(GameRenderer r, ImDrawListPtr dl, float cx, float h)
    {
        float y = h * 0.34f;
        const float rowH = 54;
        r.Label(dl, "CHOOSE A MODE", cx, y - 40, 14, new Color4(0.6f, 0.62f, 0.7f), center: true);
        for (int i = 0; i < Modes.Length; i++)
        {
            bool focused = i == _focus;
            float rowY = y + i * rowH;
            AddRow(rowY - 8, rowH - 6);
            if (focused) Fill(dl, _rowX0, rowY - 8, _rowX1 - _rowX0, rowH - 6, new Color4(1f, 1f, 1f, 0.09f));
            var c = focused ? new Color4(1f, 0.9f, 0.4f) : new Color4(0.7f, 0.72f, 0.82f);
            r.Label(dl, focused ? ModeTitlesFocused[i] : Modes[i].Title, cx, rowY, 30, c, center: true);
        }
        {
            // CREDITS: a smaller row under the modes.
            bool focused = _focus == CreditsRow;
            float rowY = y + Modes.Length * rowH + 4;
            AddRow(rowY - 6, 34);
            if (focused) Fill(dl, _rowX0, rowY - 6, _rowX1 - _rowX0, 34, new Color4(1f, 1f, 1f, 0.07f));
            r.Label(dl, focused ? "> CREDITS <" : "CREDITS", cx, rowY, 20, focused ? new Color4(1f, 0.9f, 0.4f) : new Color4(0.55f, 0.57f, 0.66f), center: true);
        }
        string detail = _focus == CreditsRow ? "Who made this, and the licences it ships under." : Modes[_focus].Detail;
        r.Label(dl, detail, cx, y + Modes.Length * rowH + 50, 15, new Color4(0.8f, 0.82f, 0.9f), center: true);
        r.Label(dl, "CLICK OR ENTER TO CHOOSE  ·  ESC QUIT", cx, h - 64, 13, new Color4(0.55f, 0.57f, 0.65f), center: true);
    }

    // --- Credits page (BRUTAL License v1.0, 2.1: the copyright notice in the credits) ----------------

    private static readonly int CreditsRow = Modes.Length;
    private static readonly string[] ModeTitlesFocused = Modes.Select(m => $"> {m.Title} <").ToArray();

    private static readonly (string Text, float Size, int Style)[] CreditLines =
    [
        ("CREDITS", 34, 0),
        ("", 10, 2),
        ("BUILT WITH BRUTAL", 20, 1),
        (SplashScreen.BrutalCopyright, 15, 2),
        ("This product is built with BRUTAL, a game development framework by Ahwoo Limited, used under the BRUTAL License v1.0.", 13, 3),
        (SplashScreen.BrutalUrl, 13, 3),
        ("", 14, 2),
        ("ART, FONT, SOUND EFFECTS AND MUSIC", 20, 1),
        ("Kenney  ·  www.kenney.nl  ·  Creative Commons Zero (CC0)", 15, 2),
        ("", 14, 2),
        ("THIRD-PARTY SOFTWARE", 20, 1),
        ("GLFW  ·  SDL_GameControllerDB  ·  Dear ImGui  ·  ImPlot  ·  SDL3  ·  SDL_mixer", 14, 2),
        ("stb  ·  stb_vorbis  ·  dr_flac  ·  dr_mp3  ·  shaderc  ·  glslang  ·  SPIRV-Tools", 14, 2),
        ("Khronos Vulkan headers  ·  glad  ·  ResX.NET  ·  RakNet  ·  SLikeNet  ·  .NET  ·  .NET Community Toolkit", 14, 2),
        ("Full licence texts: NOTICE.txt and the LICENSES folder next to the game.", 13, 3),
    ];

    private bool _creditsPage;

    private void DrawCredits(GameRenderer r, ImDrawListPtr dl, float cx, float h)
    {
        float y = h * 0.27f;
        foreach (var (text, size, style) in CreditLines)
        {
            if (text.Length > 0)
            {
                var c = style switch
                {
                    0 => new Color4(1f, 0.9f, 0.4f),
                    1 => new Color4(0.45f, 0.85f, 1f),
                    2 => new Color4(0.88f, 0.9f, 0.95f),
                    _ => new Color4(0.6f, 0.63f, 0.72f),
                };
                r.Label(dl, text, cx, y, size, c, center: true);
            }
            y += size + 12;
        }
        r.Label(dl, "CLICK, ENTER OR ESC TO GO BACK", cx, h - 64, 13, new Color4(0.55f, 0.57f, 0.65f), center: true);
    }

    private void DrawOptions(GameRenderer r, ImDrawListPtr dl, float cx, float h)
    {
        float y = h * 0.32f;
        r.Label(dl, Modes[_mode].Title, cx, y - 30, 40, new Color4(1f, 0.9f, 0.4f), center: true);
        r.Label(dl, Modes[_mode].Detail, cx, y + 20, 15, new Color4(0.8f, 0.82f, 0.9f), center: true);

        Span<Row> rows = stackalloc Row[MaxRows];
        int n = OptionRows(rows);
        float sy = y + 66;
        const float rowH = 44;
        for (int k = 0; k < n; k++)
        {
            bool focused = _focus == k;
            float rowY = sy + k * rowH + (rows[k] >= Row.Start ? 14 : 0); // gap before the buttons
            AddRow(rowY - 6, rowH - 6);
            var row = rows[k];
            if (row is Row.Start or Row.Back)
            {
                var bg = row == Row.Start ? new Color4(0.25f, 0.7f, 0.35f, focused ? 0.55f : 0.3f) : new Color4(1f, 1f, 1f, focused ? 0.12f : 0.05f);
                Fill(dl, cx - 130, rowY - 6, 260, rowH - 6, bg);
                r.Label(dl, row == Row.Start ? "START" : "BACK", cx, rowY + 2, row == Row.Start ? 26 : 20,
                    row == Row.Start ? new Color4(0.9f, 1f, 0.9f) : new Color4(0.75f, 0.77f, 0.85f), center: true);
                continue;
            }
            if (focused) Fill(dl, _rowX0, rowY - 6, _rowX1 - _rowX0, rowH - 6, new Color4(1f, 1f, 1f, 0.08f));
            var (label, value, color) = row switch
            {
                Row.Role => (Modes[_mode].Mode == GameMode.VsBot || _swapRoles == 1 ? "START AS" : "YOUR ROLE", RoleNames[_role], new Color4(0.45f, 0.85f, 1f)),
                Row.Roles => ("ROLES", SwapNames[_swapRoles], new Color4(0.75f, 0.9f, 0.75f)),
                Row.Controls => ("CONTROLS", ControlNames[_controls], new Color4(0.85f, 0.85f, 0.95f)),
                Row.Loadout => ("GUNNER PATTERNS", _loadoutSummary, new Color4(1f, 0.78f, 0.45f)),
                _ => ("BOT DIFFICULTY", DifficultyNames[_difficulty],
                    _difficulty switch { 0 => new Color4(0.5f, 1f, 0.6f), 1 => new Color4(1f, 0.85f, 0.35f), _ => new Color4(1f, 0.4f, 0.4f) }),
            };
            r.Label(dl, label, cx - 40, rowY + 6, 14, new Color4(0.6f, 0.62f, 0.7f), rightAlign: true);
            if (row == Row.Loadout) r.Label(dl, focused ? $"{value}  >" : value, cx + 110, rowY + 4, 16, color, center: true);
            else r.Label(dl, $"<  {value}  >", cx + 100, rowY, 24, color, center: true);
        }

        // What the chosen role and controls mean.
        float hintY = sy + n * rowH + 26;
        if (Modes[_mode].Mode == GameMode.LocalTeam)
        {
            r.Label(dl, "START opens the lobby: everyone picks a seat there. Gunner seats can use these patterns (CUSTOM).", cx, hintY, 13, new Color4(0.62f, 0.66f, 0.76f), center: true);
        }
        else if (Modes[_mode].Mode != GameMode.Demo)
        {
            bool gunner = _role == 1 && Modes[_mode].Mode is GameMode.Team or GameMode.VsBot;
            string hint = (_controls, gunner) switch
            {
                (0, false) => "Dodger on mouse: ship follows the pointer · L-click flip · R-click break · Shift focus",
                (0, true) => "Gunner on mouse: reticle follows the pointer · wheel picks a pattern · L-click fires · R-click overdrive",
                (1, false) => "Dodger on keyboard: WASD move · Shift focus · Space flip · X break",
                (1, true) => "Gunner on keyboard: WASD aim · Q E R F fire patterns · X overdrive",
                (_, false) => _pads.ConnectedCount > 0 ? "Dodger on gamepad: stick move · LB focus · A flip · RB break" : "No gamepad connected yet: plug one in (any XInput / SDL-mapped pad)",
                _ => _pads.ConnectedCount > 0 ? "Gunner on gamepad: stick aims · A B X Y fire patterns · RB overdrive · LB slow aim" : "No gamepad connected yet: plug one in (any XInput / SDL-mapped pad)",
            };
            r.Label(dl, hint, cx, hintY, 13, new Color4(0.62f, 0.66f, 0.76f), center: true);
        }
        r.Label(dl, "CLICK A SETTING TO CHANGE IT  ·  ENTER START  ·  ESC BACK", cx, h - 64, 13, new Color4(0.55f, 0.57f, 0.65f), center: true);
    }

    // --- Lobby page (Local 2v2): devices join seats, pick a Gunner loadout, ready up ---------------

    private Result StartOrLobby()
    {
        if (Modes[_mode].Mode != GameMode.LocalTeam) return Result.Start;
        _lobbyPage = true;
        _lobby.Clear();
        return Result.None;
    }

    private static readonly InputDevice MouseDevice = new(DeviceKind.Mouse);
    private static readonly InputDevice KeysLeft = new(DeviceKind.KeyboardLeft);
    private static readonly InputDevice KeysRight = new(DeviceKind.KeyboardRight);

    private Result UpdateLobby(bool mouseBlocked)
    {
        _lobby.Prune(_deviceStillThere);
        bool start = false;

        // Keyboard halves: WASD + Space (join/ready) + X (leave); Arrows + Enter (join/ready) + Right Ctrl / Backspace (leave).
        var kl = MenuActions.None;
        if (Pressed(GlfwKey.W)) kl |= MenuActions.Up;
        if (Pressed(GlfwKey.S)) kl |= MenuActions.Down;
        if (Pressed(GlfwKey.A)) kl |= MenuActions.Left;
        if (Pressed(GlfwKey.D)) kl |= MenuActions.Right;
        if (Pressed(GlfwKey.Space)) kl |= MenuActions.Confirm;
        if (Pressed(GlfwKey.X)) kl |= MenuActions.Back;
        var kr = MenuActions.None;
        if (Pressed(GlfwKey.Up)) kr |= MenuActions.Up;
        if (Pressed(GlfwKey.Down)) kr |= MenuActions.Down;
        if (Pressed(GlfwKey.Left)) kr |= MenuActions.Left;
        if (Pressed(GlfwKey.Right)) kr |= MenuActions.Right;
        if (Pressed(GlfwKey.Enter) | Pressed(GlfwKey.KpEnter)) kr |= MenuActions.Confirm;
        if (Pressed(GlfwKey.RightControl) | Pressed(GlfwKey.Backspace)) kr |= MenuActions.Back;
        if (Pressed(GlfwKey.Escape)) { _lobbyPage = false; return Result.None; }
        start |= _lobby.Apply(KeysLeft, kl);
        start |= _lobby.Apply(KeysRight, kr);

        for (int p = 0; p < GamepadInput.MaxPads; p++)
        {
            if (!_pads.IsConnected(p)) continue;
            var a = _pads.Menu(p);
            // B on a pad that has no seat leaves the lobby (so a pad-only group can get back out).
            if ((a & MenuActions.Back) != 0 && _lobby.SeatOf(new InputDevice(DeviceKind.Gamepad, p)) < 0 && _lobby.Humans == 0) { _lobbyPage = false; return Result.None; }
            start |= _lobby.Apply(new InputDevice(DeviceKind.Gamepad, p), a);
        }

        // Mouse: click a free seat to take it (or move there), click your seat to ready / un-ready,
        // click your Gunner seat's pattern line to change it, right click to leave. Buttons: START, BACK.
        var (mx, my) = _mouse.WindowPos;
        bool lDown = _mouse.LeftDown, rDown = _mouse.RightDown;
        bool lClick = lDown && !_leftWas, rClick = rDown && !_rightWas;
        _leftWas = lDown;
        _rightWas = rDown;
        if (!mouseBlocked && (lClick || rClick))
        {
            int seat = HitSeat(mx, my);
            int mine = _lobby.SeatOf(MouseDevice);
            if (rClick)
            {
                if (mine >= 0 && _lobby.IsReady(mine)) _lobby.SetReady(MouseDevice, false);
                else _lobby.Leave(MouseDevice);
            }
            else if (seat >= 0)
            {
                if (mine < 0) _lobby.Join(MouseDevice, seat);
                else if (seat != mine) _lobby.MoveTo(MouseDevice, seat);
                else if (HasLoadoutChoice(seat) && !_lobby.IsReady(seat) && my > _seatY[seat] + _seatH - 46) _lobby.CyclePreset(MouseDevice, 1);
                else _lobby.SetReady(MouseDevice, !_lobby.IsReady(seat));
            }
            else if (Inside(mx, my, _lobbyStartX, _lobbyBtnY, _lobbyBtnW, _lobbyBtnH)) start |= _lobby.AllReady;
            else if (Inside(mx, my, _lobbyBackX, _lobbyBtnY, _lobbyBtnW, _lobbyBtnH)) { _lobbyPage = false; return Result.None; }
        }
        return start && _lobby.AllReady ? Result.Start : Result.None;
    }

    private static bool Inside(float x, float y, float rx, float ry, float rw, float rh) => x >= rx && x <= rx + rw && y >= ry && y <= ry + rh;

    private int HitSeat(float x, float y)
    {
        for (int i = 0; i < _lobby.Seats; i++)
            if (Inside(x, y, _seatX[i], _seatY[i], _seatW, _seatH)) return i;
        return -1;
    }

    private static string DeviceLabel(InputDevice d) => d.Kind switch
    {
        DeviceKind.Mouse => "MOUSE",
        DeviceKind.KeyboardLeft => "WASD KEYS",
        DeviceKind.KeyboardRight => "ARROW KEYS",
        DeviceKind.Gamepad => PadLabels[Math.Clamp(d.Pad, 0, PadLabels.Length - 1)],
        _ => "BOT",
    };

    private void DrawLobby(GameRenderer r, ImDrawListPtr dl, float w, float h)
    {
        float cx = w * 0.5f;
        float y = h * 0.29f;
        r.Label(dl, "LOCAL 2V2 LOBBY", cx, y - 34, 34, new Color4(1f, 0.9f, 0.4f), center: true);
        r.Label(dl, PadCountText[Math.Min(_pads.ConnectedCount, GamepadInput.MaxPads)], cx, y + 8, 13, new Color4(0.6f, 0.64f, 0.74f), center: true);

        const float sw = 214, sh = 178, gap = 14, teamGap = 44;
        _seatW = sw;
        _seatH = sh;
        float total = 4 * sw + 2 * gap + teamGap;
        float sy = y + 40;
        string botLevel = DifficultyNames[_difficulty];
        for (int i = 0; i < _lobby.Seats; i++)
        {
            float x = cx - total * 0.5f + i * (sw + gap) + (i >= 2 ? teamGap - gap : 0);
            _seatX[i] = x;
            _seatY[i] = sy;
            var team = GameRenderer.TeamColor[i / 2];
            var d = _lobby.DeviceAt(i);
            bool taken = !d.IsNone, ready = _lobby.IsReady(i);
            Fill(dl, x, sy, sw, sh, taken ? new Color4(team.R, team.G, team.B, ready ? 0.3f : 0.16f) : new Color4(1f, 1f, 1f, 0.04f));
            Fill(dl, x, sy, sw, 4, team);
            r.Label(dl, SeatNames[i], x + sw * 0.5f, sy + 14, 16, team, center: true);
            if (taken)
            {
                r.Label(dl, DeviceLabel(d), x + sw * 0.5f, sy + 46, 26, new Color4(0.95f, 0.96f, 1f), center: true);
                r.Label(dl, ready ? "READY" : "CHOOSING", x + sw * 0.5f, sy + 86, 18, ready ? new Color4(0.45f, 1f, 0.55f) : new Color4(1f, 0.85f, 0.4f), center: true);
            }
            else
            {
                r.Label(dl, "BOT", x + sw * 0.5f, sy + 46, 26, new Color4(0.5f, 0.52f, 0.6f), center: true);
                r.Label(dl, botLevel, x + sw * 0.5f, sy + 86, 14, new Color4(0.5f, 0.52f, 0.6f), center: true);
            }
            if (HasLoadoutChoice(i) && taken)
            {
                Fill(dl, x + 10, sy + sh - 42, sw - 20, 32, new Color4(1f, 0.78f, 0.3f, ready ? 0.08f : 0.16f));
                r.Label(dl, ready ? PresetNames[_lobby.PresetAt(i)] : PresetChoices[_lobby.PresetAt(i)], x + sw * 0.5f, sy + sh - 35, 16, new Color4(1f, 0.85f, 0.5f), center: true);
            }
            else if (!IsGunnerSeat(i) && _swapRoles == 0)
            {
                r.Label(dl, "dodges, flips, breaks", x + sw * 0.5f, sy + sh - 32, 12, new Color4(0.55f, 0.58f, 0.68f), center: true);
            }
        }

        // START / BACK.
        _lobbyBtnW = 200;
        _lobbyBtnH = 42;
        _lobbyBtnY = sy + sh + 24;
        _lobbyStartX = cx - _lobbyBtnW - 10;
        _lobbyBackX = cx + 10;
        bool allReady = _lobby.AllReady;
        Fill(dl, _lobbyStartX, _lobbyBtnY, _lobbyBtnW, _lobbyBtnH, new Color4(0.25f, 0.7f, 0.35f, allReady ? 0.55f : 0.1f));
        r.Label(dl, allReady ? "START" : _lobby.Humans == 0 ? "JOIN FIRST" : "WAITING", _lobbyStartX + _lobbyBtnW * 0.5f, _lobbyBtnY + 11, 18,
            allReady ? new Color4(0.9f, 1f, 0.9f) : new Color4(0.45f, 0.47f, 0.5f), center: true);
        Fill(dl, _lobbyBackX, _lobbyBtnY, _lobbyBtnW, _lobbyBtnH, new Color4(1f, 1f, 1f, 0.06f));
        r.Label(dl, "BACK", _lobbyBackX + _lobbyBtnW * 0.5f, _lobbyBtnY + 11, 18, new Color4(0.8f, 0.82f, 0.9f), center: true);

        var help = new Color4(0.62f, 0.66f, 0.76f);
        float hy = _lobbyBtnY + _lobbyBtnH + 22;
        r.Label(dl, "JOIN / READY:  CLICK A SEAT  ·  SPACE (WASD KEYS)  ·  ENTER (ARROW KEYS)  ·  A (GAMEPAD)", cx, hy, 13, help, center: true);
        r.Label(dl, "CHANGE SEAT: LEFT / RIGHT  ·  GUNNER PATTERNS: UP / DOWN  ·  LEAVE: RIGHT-CLICK, X, R-CTRL, B", cx, hy + 20, 13, help, center: true);
        r.Label(dl, allReady ? "EVERYONE IS READY: PRESS JOIN AGAIN OR CLICK START" : "EMPTY SEATS ARE PLAYED BY BOTS  ·  ESC BACK", cx, h - 64, 13,
            allReady ? new Color4(0.5f, 1f, 0.6f) : new Color4(0.55f, 0.57f, 0.65f), center: true);
    }

    // --- Loadout page: pick 4 of 7 patterns; selection order = button order ------------------------

    private void OpenLoadout()
    {
        _loadoutPage = true;
        Array.Copy(_picked, _pickedBackup, 4);
        _pickedBackupCount = _pickedCount;
        _focus = 0;
    }

    private void CloseLoadout(bool keep)
    {
        if (!keep)
        {
            Array.Copy(_pickedBackup, _picked, 4);
            _pickedCount = _pickedBackupCount;
        }
        else
        {
            var names = new string[4];
            for (int k = 0; k < 4; k++) names[k] = PatternInfo[(int)_picked[k]].Name;
            _loadoutSummary = string.Join(" · ", names);
        }
        _loadoutPage = false;
        Span<Row> rows = stackalloc Row[MaxRows];
        int n = OptionRows(rows);
        _focus = Math.Max(0, rows[..n].IndexOf(Row.Loadout));
    }

    private int PickedSlot(PatternKind kind)
    {
        for (int k = 0; k < _pickedCount; k++) if (_picked[k] == kind) return k;
        return -1;
    }

    private void Toggle(int card)
    {
        var kind = (PatternKind)card;
        int slot = PickedSlot(kind);
        if (slot >= 0)
        {
            for (int k = slot; k < _pickedCount - 1; k++) _picked[k] = _picked[k + 1];
            _pickedCount--;
        }
        else if (_pickedCount < 4)
        {
            _picked[_pickedCount++] = kind;
        }
    }

    private Result UpdateLoadout(bool up, bool down, bool left, bool right, bool confirm, bool esc, bool lClick, bool rClick, float mx, float my, bool moved, bool mouseBlocked)
    {
        if (esc || (rClick && !mouseBlocked && HitCard(mx, my) < 0)) { CloseLoadout(keep: false); return Result.None; }
        // Grid: cards 0-3 on the first row, 4-6 on the second, then DONE (7) and BACK (8).
        if (left) _focus = (_focus + LoadoutRows - 1) % LoadoutRows;
        if (right) _focus = (_focus + 1) % LoadoutRows;
        if (down) _focus = _focus < 4 ? Math.Min(_focus + 4, 6) : _focus < 7 ? 7 : 8;
        if (up) _focus = _focus >= 7 ? 4 : _focus >= 4 ? _focus - 4 : _focus;
        int hit = mouseBlocked ? -1 : HitCard(mx, my);
        if (hit >= 0 && moved) _focus = hit;
        bool act = confirm || (lClick && hit >= 0);
        if (lClick && hit >= 0) _focus = hit;
        if (!act) return Result.None;
        if (_focus < CardCount) Toggle(_focus);
        else if (_focus == CardCount) { if (_pickedCount == 4) CloseLoadout(keep: true); }
        else CloseLoadout(keep: false);
        return Result.None;
    }

    private int HitCard(float x, float y)
    {
        for (int i = 0; i < LoadoutRows; i++)
            if (x >= _cardX[i] && x <= _cardX[i] + _cardW[i] && y >= _cardY[i] && y <= _cardY[i] + _cardH[i]) return i;
        return -1;
    }

    private void DrawLoadout(GameRenderer r, ImDrawListPtr dl, float w, float h)
    {
        float cx = w * 0.5f;
        float y = h * 0.30f;
        r.Label(dl, "GUNNER PATTERNS", cx, y - 34, 34, new Color4(1f, 0.9f, 0.4f), center: true);
        r.Label(dl, "Pick 4. The order you pick them is the button order (1-4, or Q E R F / wheel).", cx, y + 10, 14, new Color4(0.8f, 0.82f, 0.9f), center: true);

        const float cw = 230, ch = 92, gap = 14;
        float rowY = y + 46;
        for (int i = 0; i < CardCount; i++)
        {
            int perRow = i < 4 ? 4 : 3;
            int col = i < 4 ? i : i - 4;
            float rowW = perRow * cw + (perRow - 1) * gap;
            float x = cx - rowW * 0.5f + col * (cw + gap);
            float cy = rowY + (i < 4 ? 0 : ch + gap);
            _cardX[i] = x; _cardY[i] = cy; _cardW[i] = cw; _cardH[i] = ch;
            int slot = PickedSlot((PatternKind)i);
            bool focused = _focus == i;
            var bg = slot >= 0 ? new Color4(1f, 0.78f, 0.3f, focused ? 0.32f : 0.2f) : new Color4(1f, 1f, 1f, focused ? 0.12f : 0.05f);
            Fill(dl, x, cy, cw, ch, bg);
            if (focused) Fill(dl, x, cy + ch - 3, cw, 3, new Color4(1f, 0.9f, 0.5f));
            r.Label(dl, PatternInfo[i].Name, x + 14, cy + 10, 22, slot >= 0 ? new Color4(1f, 0.92f, 0.65f) : new Color4(0.85f, 0.87f, 0.93f));
            r.Label(dl, PatternInfo[i].Line1, x + 14, cy + 38, 11, new Color4(0.72f, 0.75f, 0.84f));
            r.Label(dl, PatternInfo[i].Line2, x + 14, cy + 52, 11, new Color4(0.72f, 0.75f, 0.84f));
            if (_cardCost[i] != null) r.Label(dl, _cardCost[i], x + 14, cy + 71, 10.5f, new Color4(0.55f, 0.6f, 0.75f));
            if (slot >= 0)
            {
                Fill(dl, x + cw - 38, cy + 10, 26, 26, new Color4(1f, 0.85f, 0.35f));
                r.Label(dl, SlotDigits[slot], x + cw - 25, cy + 13, 18, new Color4(0.08f, 0.06f, 0.02f), center: true);
            }
        }

        // DONE / BACK.
        float by = rowY + 2 * (ch + gap) + 16, bw = 200, bh = 42;
        bool ready = _pickedCount == 4;
        for (int b = 0; b < 2; b++)
        {
            int idx = CardCount + b;
            float bx = cx - bw - 10 + b * (bw + 20);
            _cardX[idx] = bx; _cardY[idx] = by; _cardW[idx] = bw; _cardH[idx] = bh;
            bool focused = _focus == idx;
            var bg = b == 0
                ? new Color4(0.25f, 0.7f, 0.35f, !ready ? 0.1f : focused ? 0.6f : 0.3f)
                : new Color4(1f, 1f, 1f, focused ? 0.14f : 0.05f);
            Fill(dl, bx, by, bw, bh, bg);
            var tc = b == 0 && !ready ? new Color4(0.45f, 0.47f, 0.5f) : new Color4(0.9f, 0.92f, 0.95f);
            r.Label(dl, b == 0 ? (ready ? "DONE" : PickMore[_pickedCount]) : "BACK", bx + bw * 0.5f, by + 11, 18, tc, center: true);
        }
        r.Label(dl, "CLICK OR SPACE TO PICK / UNPICK  ·  ESC BACK", cx, h - 64, 13, new Color4(0.55f, 0.57f, 0.65f), center: true);
    }

    private static readonly string[] SlotDigits = ["1", "2", "3", "4"];
    private static readonly string[] PickMore = ["PICK 4 MORE", "PICK 3 MORE", "PICK 2 MORE", "PICK 1 MORE", "DONE"];

    private void AddRow(float y, float height)
    {
        if (_rowCount >= MaxRows) return;
        _rowY[_rowCount] = y;
        _rowH[_rowCount] = height;
        _rowCount++;
    }

    private static void Fill(ImDrawListPtr dl, float x, float y, float w, float h, Color4 c)
    {
        var a = new float2(x, y);
        var b = new float2(x + w, y + h);
        var col = new ImColor8((byte)(c.R * 255), (byte)(c.G * 255), (byte)(c.B * 255), (byte)(c.A * 255));
        ImDrawListExtensions.AddRectFilled(dl, in a, in b, col, 6f, default);
    }
}
