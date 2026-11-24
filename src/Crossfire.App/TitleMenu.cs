using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Crossfire.Input;
using Crossfire.Render;
using Crossfire.Sim.Bots;

namespace Crossfire.App;

/// <summary>
/// Title screen drawn over a live Bot vs Bot match (attract mode).
/// Up/Down (W/S) pick a mode, Left/Right (A/D) pick bot difficulty, Enter/Space start, Esc quits.
/// </summary>
internal sealed class TitleMenu
{
    public enum Result
    {
        None,
        Start,
        Quit,
    }

    private static readonly (GameMode Mode, string Title, string Detail)[] Items =
    [
        (GameMode.VsBot, "VS BOT", "You (WASD or Arrows) against a bot. Roles swap each round."),
        (GameMode.Local, "LOCAL 1V1", "Two players, one keyboard: WASD + QERF  vs  Arrows + UIOP."),
        (GameMode.Demo, "BOT VS BOT", "Sit back and watch two bots play."),
    ];
    private static readonly BotDifficulty[] Difficulties = [BotDifficulty.Easy, BotDifficulty.Normal, BotDifficulty.Hard];
    private static readonly string[] DifficultyNames = ["< EASY >", "< NORMAL >", "< HARD >"];

    private readonly KeyboardInput _keyboard;
    private readonly HashSet<GlfwKey> _held = [];
    private int _selected;
    private int _difficulty = 1;
    private float _time;

    public TitleMenu(KeyboardInput keyboard) => _keyboard = keyboard;

    public GameMode Mode => Items[_selected].Mode;
    public BotDifficulty Difficulty => Difficulties[_difficulty];

    /// <summary>Call once per frame. Swallows the keys that were already down when the menu opened.</summary>
    public void Open()
    {
        _held.Clear();
        foreach (var k in WatchedKeys) if (_keyboard.IsDown(k)) _held.Add(k);
    }

    private static readonly GlfwKey[] WatchedKeys =
        [GlfwKey.Up, GlfwKey.Down, GlfwKey.Left, GlfwKey.Right, GlfwKey.W, GlfwKey.S, GlfwKey.A, GlfwKey.D, GlfwKey.Enter, GlfwKey.KpEnter, GlfwKey.Space, GlfwKey.Escape];

    private bool Pressed(GlfwKey k)
    {
        bool down = _keyboard.IsDown(k);
        bool wasHeld = _held.Contains(k);
        if (down && !wasHeld) { _held.Add(k); return true; }
        if (!down) _held.Remove(k);
        return false;
    }

    public Result Update(float dt)
    {
        _time += dt;
        bool up = Pressed(GlfwKey.Up) | Pressed(GlfwKey.W);
        bool down = Pressed(GlfwKey.Down) | Pressed(GlfwKey.S);
        bool left = Pressed(GlfwKey.Left) | Pressed(GlfwKey.A);
        bool right = Pressed(GlfwKey.Right) | Pressed(GlfwKey.D);
        bool start = Pressed(GlfwKey.Enter) | Pressed(GlfwKey.KpEnter) | Pressed(GlfwKey.Space);
        bool quit = Pressed(GlfwKey.Escape);

        if (up) _selected = (_selected + Items.Length - 1) % Items.Length;
        if (down) _selected = (_selected + 1) % Items.Length;
        if (left) _difficulty = Math.Max(0, _difficulty - 1);
        if (right) _difficulty = Math.Min(Difficulties.Length - 1, _difficulty + 1);
        if (quit) return Result.Quit;
        return start ? Result.Start : Result.None;
    }

    public void Draw(GameRenderer r, float w, float h)
    {
        var dl = ImGui.GetForegroundDrawList(default);
        // Dim the attract-mode match behind the menu.
        Fill(dl, 0, 0, w, h, new Color4(0.01f, 0.01f, 0.03f, 0.62f));

        float cx = w * 0.5f;
        float y = h * 0.16f;
        float pulse = 0.85f + 0.15f * MathF.Sin(_time * 2.2f);
        r.Label(dl, "CROSSFIRE", cx + 3, y + 3, 72, new Color4(0, 0, 0, 0.6f), center: true);
        r.Label(dl, "CROSSFIRE", cx, y, 72, new Color4(1f, 0.85f * pulse, 0.35f), center: true);
        r.Label(dl, "TEAM BULLET-HELL PVP", cx, y + 82, 18, new Color4(0.75f, 0.78f, 0.9f), center: true);

        y = h * 0.42f;
        for (int i = 0; i < Items.Length; i++)
        {
            bool sel = i == _selected;
            float rowY = y + i * 62;
            if (sel) Fill(dl, cx - 240, rowY - 8, 480, 52, new Color4(1f, 1f, 1f, 0.07f));
            var c = sel ? new Color4(1f, 0.9f, 0.4f) : new Color4(0.65f, 0.68f, 0.78f);
            r.Label(dl, sel ? $"> {Items[i].Title} <" : Items[i].Title, cx, rowY, 30, c, center: true);
        }

        float detailY = y + Items.Length * 62 + 10;
        r.Label(dl, Items[_selected].Detail, cx, detailY, 15, new Color4(0.8f, 0.82f, 0.9f), center: true);
        if (Mode != GameMode.Local)
        {
            r.Label(dl, "BOT DIFFICULTY", cx, detailY + 36, 14, new Color4(0.6f, 0.62f, 0.7f), center: true);
            var dc = _difficulty switch { 0 => new Color4(0.5f, 1f, 0.6f), 1 => new Color4(1f, 0.85f, 0.35f), _ => new Color4(1f, 0.4f, 0.4f) };
            r.Label(dl, DifficultyNames[_difficulty], cx, detailY + 56, 24, dc, center: true);
        }

        r.Label(dl, "UP/DOWN  MODE     LEFT/RIGHT  DIFFICULTY     ENTER/SPACE  START     ESC  QUIT",
            cx, h - 64, 13, new Color4(0.55f, 0.57f, 0.65f), center: true);
        r.Label(dl, "ART AND FONT BY KENNEY.NL (CC0)  ·  BUILT WITH BRUTAL", cx, h - 36, 12, new Color4(0.42f, 0.44f, 0.5f), center: true);
    }

    private static void Fill(ImDrawListPtr dl, float x, float y, float w, float h, Color4 c)
    {
        var a = new float2(x, y);
        var b = new float2(x + w, y + h);
        var col = new ImColor8((byte)(c.R * 255), (byte)(c.G * 255), (byte)(c.B * 255), (byte)(c.A * 255));
        ImDrawListExtensions.AddRectFilled(dl, in a, in b, col, 6f, default);
    }
}
