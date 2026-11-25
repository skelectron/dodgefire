using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Crossfire.Input;
using Crossfire.Render;
using Crossfire.Sim.Replays;

namespace Crossfire.App;

/// <summary>
/// Plays a <see cref="Replay"/> back with simple transport controls:
/// Space / click = pause, Left/Right or wheel = speed (0.5x..4x), R = restart, Esc = leave (handled by the caller).
/// </summary>
internal sealed class ReplayView : IDisposable
{
    private static readonly float[] Speeds = [0.5f, 1f, 2f, 4f];
    private static readonly string[] SpeedLabels = ["0.5x", "1x", "2x", "4x"];

    private readonly KeyboardInput _keyboard;
    private readonly MouseInput _mouse;
    private readonly HashSet<GlfwKey> _held = [];
    private bool _leftWas = true;
    private int _speed = 1;
    private bool _paused;
    private double _accumulator;
    private bool _checked;
    private bool _hashOk;

    public ReplayPlayer Player { get; }
    public bool FromCommandLine { get; init; }

    public ReplayView(Replay replay, KeyboardInput keyboard, MouseInput mouse)
    {
        _keyboard = keyboard;
        _mouse = mouse;
        Player = new ReplayPlayer(replay);
        Player.FastForwardTo(replay.MatchStartTick);
        foreach (var k in Keys) if (_keyboard.IsDown(k)) _held.Add(k);
    }

    private static readonly GlfwKey[] Keys = [GlfwKey.Space, GlfwKey.Left, GlfwKey.Right, GlfwKey.R];

    private bool Pressed(GlfwKey k)
    {
        bool down = _keyboard.IsDown(k);
        bool was = _held.Contains(k);
        if (down && !was) { _held.Add(k); return true; }
        if (!down) _held.Remove(k);
        return false;
    }

    public void AddWheel(float wheel)
    {
        if (wheel > 0.5f) _speed = Math.Min(Speeds.Length - 1, _speed + 1);
        else if (wheel < -0.5f) _speed = Math.Max(0, _speed - 1);
    }

    /// <summary>Advances the replay by real time. Returns the number of ticks stepped (events are on <see cref="ReplayPlayer.State"/>).</summary>
    public int Update(double dt, double tickSeconds, bool mouseBlocked, Action<Crossfire.Sim.SimState> onStep)
    {
        if (Pressed(GlfwKey.Space)) _paused = !_paused;
        if (Pressed(GlfwKey.Left)) _speed = Math.Max(0, _speed - 1);
        if (Pressed(GlfwKey.Right)) _speed = Math.Min(Speeds.Length - 1, _speed + 1);
        bool lDown = _mouse.LeftDown;
        if (!mouseBlocked && lDown && !_leftWas) _paused = !_paused;
        _leftWas = lDown;
        if (Pressed(GlfwKey.R))
        {
            Player.Restart();
            Player.FastForwardTo(Player.Replay.MatchStartTick);
            _checked = false;
            _paused = false;
        }

        if (_paused || Player.Finished) { _accumulator = 0; return 0; }
        _accumulator += dt * Speeds[_speed];
        int steps = 0;
        while (_accumulator >= tickSeconds && steps < 16 && !Player.Finished)
        {
            _accumulator -= tickSeconds;
            Player.Step();
            onStep(Player.State);
            steps++;
        }
        if (Player.Finished && !_checked)
        {
            _checked = true;
            _hashOk = Player.HashMatches;
            if (!_hashOk) Console.WriteLine("Replay desync: re-simulated state does not match the recording.");
        }
        return steps;
    }

    public float Alpha(double tickSeconds) => _paused || Player.Finished ? 0f : (float)Math.Clamp(_accumulator / tickSeconds, 0, 1);

    public void Draw(GameRenderer r, float w, float h)
    {
        var dl = ImGui.GetForegroundDrawList(default);
        var replay = Player.Replay;
        float cx = w * 0.5f;
        var gold = new Color4(1f, 0.85f, 0.35f);

        // Everything lives in the bottom bar, under the arena: tag + speed on the left, controls in the middle,
        // and a thin progress line along the very bottom edge.
        float by = h - 23;
        string tag = Player.Finished ? (_hashOk ? "REPLAY · END" : "REPLAY · DESYNC") : _paused ? "REPLAY · PAUSED" : "REPLAY";
        r.Label(dl, tag, 14, by, 15, Player.Finished && !_hashOk ? new Color4(1f, 0.4f, 0.35f) : gold);
        r.Label(dl, SpeedLabels[_speed], 190, by + 1, 14, new Color4(0.85f, 0.87f, 0.93f));
        r.Label(dl, "SPACE / CLICK PAUSE  ·  LEFT/RIGHT OR WHEEL SPEED  ·  R RESTART  ·  ESC BACK", cx, by + 1, 12, new Color4(0.6f, 0.62f, 0.7f), center: true);

        int start = replay.MatchStartTick, total = Math.Max(1, replay.Ticks - start);
        float progress = Math.Clamp((Player.Tick - start) / (float)total, 0f, 1f);
        Fill(dl, 0, h - 4, w, 4, new Color4(1f, 1f, 1f, 0.1f));
        Fill(dl, 0, h - 4, w * progress, 4, gold);
    }

    private static void Fill(ImDrawListPtr dl, float x, float y, float w, float h, Color4 c)
    {
        var a = new float2(x, y);
        var b = new float2(x + w, y + h);
        var col = new ImColor8((byte)(c.R * 255), (byte)(c.G * 255), (byte)(c.B * 255), (byte)(c.A * 255));
        ImDrawListExtensions.AddRectFilled(dl, in a, in b, col, 2f, default);
    }

    public void Dispose() => Player.Dispose();
}
