using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Crossfire.Input;
using Crossfire.Render;
using Crossfire.Sim;
using Crossfire.Sim.Stats;

namespace Crossfire.App;

/// <summary>
/// End-of-match results: per-player stats table, MVP, and REMATCH / WATCH REPLAY / MENU buttons.
/// Strings are built once in <see cref="Open"/>, so drawing does not allocate.
/// </summary>
internal sealed class ResultsScreen
{
    public enum Action
    {
        None,
        Rematch,
        Replay,
        Menu,
    }

    private static readonly string[] Columns = ["LIVES LOST", "LIVES TAKEN", "GRAZES", "FLIPS/BLOCKS", "BREAKS", "PATTERNS", "OVERDRIVES", "COMBOS"];
    private static readonly string[] ButtonNames = ["REMATCH", "WATCH REPLAY", "MENU"];

    private readonly KeyboardInput _keyboard;
    private readonly MouseInput _mouse;
    private string _title = "";
    private string _score = "";
    private int _winner = -1;
    private int _rows;
    private readonly string[] _names = new string[SimState.MaxPlayers];
    private readonly string[] _roles = new string[SimState.MaxPlayers];
    private readonly string[,] _cells = new string[SimState.MaxPlayers, Columns.Length];
    private readonly int[] _team = new int[SimState.MaxPlayers];
    private readonly int[] _order = new int[SimState.MaxPlayers];
    private int _mvp = -1;
    private bool _replayAvailable;
    private int _focus;
    private bool _leftWas = true;
    private readonly HashSet<GlfwKey> _held = [];
    private float _time;

    // Button rects from the last Draw (window units).
    private readonly float[] _bx = new float[3], _by = new float[3], _bw = new float[3], _bh = new float[3];

    public bool IsOpen { get; private set; }
    /// <summary>The highlighted button (for the UI move sound).</summary>
    public int Focus => _focus;

    private readonly GamepadInput _pads;

    public ResultsScreen(KeyboardInput keyboard, MouseInput mouse, GamepadInput pads)
    {
        _keyboard = keyboard;
        _mouse = mouse;
        _pads = pads;
    }

    public void Open(SimState s, MatchStats stats, SlotController[] controllers, int youSlot, bool replayAvailable)
    {
        IsOpen = true;
        _time = 0;
        _replayAvailable = replayAvailable;
        _focus = 0;
        _leftWas = true;
        _held.Clear();
        foreach (var k in Keys) if (_keyboard.IsDown(k)) _held.Add(k);

        _winner = s.MatchWinner;
        _title = _winner >= 0 ? $"{GameRenderer.TeamName[_winner]} WINS THE MATCH" : "MATCH OVER";
        _score = $"{s.Scores[0]} - {s.Scores[1]}  ·  {stats.Rounds} ROUND{(stats.Rounds == 1 ? "" : "S")}";
        _mvp = stats.Mvp();

        // Rows grouped by team: blue first.
        _rows = 0;
        for (int team = 0; team < SimState.TeamCount; team++)
            for (int i = 0; i < s.PlayerCount; i++)
                if (s.Players[i].Active && s.Players[i].Team == team) _order[_rows++] = i;

        for (int r = 0; r < _rows; r++)
        {
            int i = _order[r];
            ref readonly var p = ref stats.Players[i];
            bool bot = i < controllers.Length && controllers[i].IsBot;
            _team[r] = s.Players[i].Team;
            _names[r] = i == youSlot ? $"P{i + 1} YOU" : bot ? $"P{i + 1} BOT" : i < controllers.Length ? $"P{i + 1} {controllers[i].ShortLabel}" : $"P{i + 1}";
            _roles[r] = p.TicksDodging > 0 && p.TicksGunning > 0 ? "BOTH" : p.TicksGunning > 0 ? "GUNNER" : "DODGER";
            _cells[r, 0] = p.LivesLost.ToString();
            _cells[r, 1] = p.LivesTaken.ToString();
            _cells[r, 2] = p.Grazes.ToString();
            _cells[r, 3] = p.Flips > 0 ? $"{p.Flips} / {p.FlipBlocks}" : "-";
            _cells[r, 4] = p.Breaks.ToString();
            _cells[r, 5] = p.Placed.ToString();
            _cells[r, 6] = p.Overdrives.ToString();
            _cells[r, 7] = p.Combos.ToString();
        }
    }

    public void Close() => IsOpen = false;

    private static readonly GlfwKey[] Keys = [GlfwKey.Left, GlfwKey.Right, GlfwKey.A, GlfwKey.D, GlfwKey.Enter, GlfwKey.KpEnter, GlfwKey.Space];

    private bool Pressed(GlfwKey k)
    {
        bool down = _keyboard.IsDown(k);
        bool was = _held.Contains(k);
        if (down && !was) { _held.Add(k); return true; }
        if (!down) _held.Remove(k);
        return false;
    }

    public Action Update(float dt, bool mouseBlocked)
    {
        if (!IsOpen) return Action.None;
        _time += dt;
        bool left = Pressed(GlfwKey.Left) | Pressed(GlfwKey.A);
        bool right = Pressed(GlfwKey.Right) | Pressed(GlfwKey.D);
        bool confirm = Pressed(GlfwKey.Enter) | Pressed(GlfwKey.KpEnter) | Pressed(GlfwKey.Space);
        var pad = _time > 0.4f ? _pads.AnyMenu : MenuActions.None; // ignore the press that ended the match
        left |= (pad & MenuActions.Left) != 0;
        right |= (pad & MenuActions.Right) != 0;
        confirm |= (pad & MenuActions.Confirm) != 0;
        if (left) _focus = Next(_focus, -1);
        if (right) _focus = Next(_focus, 1);

        bool lDown = _mouse.LeftDown;
        bool click = lDown && !_leftWas;
        _leftWas = lDown;
        if (!mouseBlocked && _time > 0.4f) // ignore the click that ended the match
        {
            var (mx, my) = _mouse.WindowPos;
            for (int b = 0; b < 3; b++)
            {
                if (mx < _bx[b] || mx > _bx[b] + _bw[b] || my < _by[b] || my > _by[b] + _bh[b] || !Enabled(b)) continue;
                _focus = b;
                if (click) confirm = true;
            }
        }
        if (!confirm || !Enabled(_focus)) return Action.None;
        return _focus switch { 0 => Action.Rematch, 1 => Action.Replay, _ => Action.Menu };
    }

    private bool Enabled(int b) => b != 1 || _replayAvailable;

    private int Next(int from, int dir)
    {
        int f = from;
        for (int k = 0; k < 3; k++)
        {
            f = (f + dir + 3) % 3;
            if (Enabled(f)) return f;
        }
        return from;
    }

    public void Draw(GameRenderer r, float w, float h)
    {
        if (!IsOpen) return;
        var dl = ImGui.GetForegroundDrawList(default);
        float appear = Math.Clamp(_time / 0.35f, 0f, 1f);
        Fill(dl, 0, 0, w, h, new Color4(0.01f, 0.01f, 0.03f, 0.55f * appear));

        const float colW = 96, nameW = 140, roleW = 80;
        float tableW = nameW + roleW + Columns.Length * colW;
        float panelW = tableW + 40, rowH = 26;
        float panelH = 150 + 30 + _rows * rowH + 90;
        float x0 = (w - panelW) * 0.5f, y0 = Math.Max(10, (h - panelH) * 0.5f);
        Fill(dl, x0, y0, panelW, panelH, new Color4(0.03f, 0.035f, 0.06f, 0.92f * appear));
        var winColor = _winner >= 0 ? GameRenderer.TeamColor[_winner] : new Color4(0.9f, 0.9f, 0.95f);
        Fill(dl, x0, y0, panelW, 4, winColor);

        float cx = w * 0.5f, y = y0 + 22;
        r.Label(dl, _title, cx, y, 40, winColor, center: true);
        r.Label(dl, _score, cx, y + 52, 18, new Color4(0.8f, 0.82f, 0.9f), center: true);

        // Table header.
        float tx = x0 + 20;
        y = y0 + 120;
        var head = new Color4(0.55f, 0.58f, 0.68f);
        r.Label(dl, "PLAYER", tx, y, 11.5f, head);
        r.Label(dl, "ROLE", tx + nameW, y, 11.5f, head);
        for (int c = 0; c < Columns.Length; c++)
            r.Label(dl, Columns[c], tx + nameW + roleW + c * colW + colW * 0.5f, y, 11.5f, head, center: true);
        y += 24;

        for (int row = 0; row < _rows; row++)
        {
            var tc = GameRenderer.TeamColor[_team[row]];
            if (row % 2 == 0) Fill(dl, tx - 8, y - 4, tableW + 16, rowH, new Color4(1f, 1f, 1f, 0.035f));
            bool mvp = _order[row] == _mvp;
            if (mvp) Fill(dl, tx - 8, y - 4, 4, rowH, new Color4(1f, 0.85f, 0.3f));
            r.Label(dl, _names[row], tx, y, 15, tc);
            if (mvp) r.Label(dl, "MVP", tx + nameW - 38, y + 2, 11.5f, new Color4(1f, 0.85f, 0.3f));
            r.Label(dl, _roles[row], tx + nameW, y + 2, 12.5f, new Color4(0.75f, 0.77f, 0.85f));
            for (int c = 0; c < Columns.Length; c++)
                r.Label(dl, _cells[row, c], tx + nameW + roleW + c * colW + colW * 0.5f, y, 15, new Color4(0.92f, 0.93f, 0.97f), center: true);
            y += rowH;
        }

        // Buttons.
        float bw = 200, bh = 42, gap = 18;
        float bxStart = cx - (3 * bw + 2 * gap) * 0.5f, by = y0 + panelH - bh - 22;
        for (int b = 0; b < 3; b++)
        {
            float bx = bxStart + b * (bw + gap);
            _bx[b] = bx; _by[b] = by; _bw[b] = bw; _bh[b] = bh;
            bool enabled = Enabled(b), focused = enabled && _focus == b;
            var bg = b == 0 ? new Color4(0.25f, 0.7f, 0.35f, focused ? 0.6f : 0.28f) : new Color4(1f, 1f, 1f, focused ? 0.16f : 0.06f);
            if (!enabled) bg = new Color4(1f, 1f, 1f, 0.03f);
            Fill(dl, bx, by, bw, bh, bg);
            var tc = !enabled ? new Color4(0.4f, 0.4f, 0.45f) : focused ? new Color4(1f, 0.95f, 0.75f) : new Color4(0.85f, 0.87f, 0.93f);
            r.Label(dl, ButtonNames[b], bx + bw * 0.5f, by + 11, 18, tc, center: true);
        }
        if (!_replayAvailable)
            r.Label(dl, "replay unavailable: debug tools changed the match", cx, by + bh + 4, 11, new Color4(0.5f, 0.5f, 0.55f), center: true);
    }

    private static void Fill(ImDrawListPtr dl, float x, float y, float w, float h, Color4 c)
    {
        var a = new float2(x, y);
        var b = new float2(x + w, y + h);
        var col = new ImColor8((byte)(Math.Clamp(c.R, 0, 1) * 255), (byte)(Math.Clamp(c.G, 0, 1) * 255), (byte)(Math.Clamp(c.B, 0, 1) * 255), (byte)(Math.Clamp(c.A, 0, 1) * 255));
        ImDrawListExtensions.AddRectFilled(dl, in a, in b, col, 6f, default);
    }
}
