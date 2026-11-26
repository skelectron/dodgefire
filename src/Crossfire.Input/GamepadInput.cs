using Brutal.GlfwApi;
using Crossfire.Sim;
using Crossfire.Sim.Players;

namespace Crossfire.Input;

/// <summary>Buttons of a standard (Xbox-layout) gamepad as a bit mask, so one frame fits in a value type.</summary>
[Flags]
public enum PadButtons : ushort
{
    None = 0,
    A = 1 << 0,
    B = 1 << 1,
    X = 1 << 2,
    Y = 1 << 3,
    LeftBumper = 1 << 4,
    RightBumper = 1 << 5,
    Back = 1 << 6,
    Start = 1 << 7,
    Guide = 1 << 8,
    LeftThumb = 1 << 9,
    RightThumb = 1 << 10,
    DpadUp = 1 << 11,
    DpadRight = 1 << 12,
    DpadDown = 1 << 13,
    DpadLeft = 1 << 14,
}

/// <summary>One gamepad's state for one frame (sticks -1..1, triggers 0..1).</summary>
public struct PadSnapshot
{
    public float LeftX, LeftY, RightX, RightY, LeftTrigger, RightTrigger;
    public PadButtons Buttons;

    public readonly bool Has(PadButtons b) => (Buttons & b) != 0;
    public readonly bool LeftTriggerDown => LeftTrigger > GamepadMapping.TriggerThreshold;
    public readonly bool RightTriggerDown => RightTrigger > GamepadMapping.TriggerThreshold;
}

/// <summary>
/// Pure gamepad → <see cref="SimInput"/> mapping (no GLFW calls, so it is unit-testable).
/// Dodger: left stick / d-pad move · A flip · RB or RT break · LB or LT focus.
/// Gunner: either stick / d-pad aims · A B X Y fire patterns 1-4 · RB or RT overdrive · LB or LT slow aim.
/// Start (or A) also sends Start, for the rematch prompt.
/// </summary>
public static class GamepadMapping
{
    public const float DeadZone = 0.2f;
    public const float TriggerThreshold = 0.5f;

    /// <summary>HUD hints in the <see cref="KeyboardScheme.Hints"/> layout: move, focus, patterns, start, flip, break.</summary>
    public static readonly string[] Hints = ["Stick", "LB", "A B X Y", "Start", "A", "RB"];

    /// <summary>
    /// Radial dead zone with rescaling: inside <see cref="DeadZone"/> is zero, the edge maps to full speed,
    /// and the direction is kept (so diagonals are not squashed into the cardinal axes).
    /// </summary>
    public static (sbyte X, sbyte Y) Stick(float x, float y)
    {
        float len = MathF.Sqrt(x * x + y * y);
        if (len <= DeadZone || float.IsNaN(len)) return (0, 0);
        float scaled = Math.Min(1f, (len - DeadZone) / (1f - DeadZone));
        float k = scaled / len * SimInput.AxisMax;
        return ((sbyte)Math.Clamp((int)MathF.Round(x * k), -SimInput.AxisMax, SimInput.AxisMax),
                (sbyte)Math.Clamp((int)MathF.Round(y * k), -SimInput.AxisMax, SimInput.AxisMax));
    }

    public static SimInput Map(in PadSnapshot pad, PlayerRole role)
    {
        // Movement: left stick, or the d-pad (full speed), or for a Gunner the right stick as well.
        var (x, y) = Stick(pad.LeftX, pad.LeftY);
        if (role == PlayerRole.Gunner && x == 0 && y == 0) (x, y) = Stick(pad.RightX, pad.RightY);
        int dx = (pad.Has(PadButtons.DpadRight) ? 1 : 0) - (pad.Has(PadButtons.DpadLeft) ? 1 : 0);
        int dy = (pad.Has(PadButtons.DpadDown) ? 1 : 0) - (pad.Has(PadButtons.DpadUp) ? 1 : 0);
        if (dx != 0 || dy != 0) { x = (sbyte)(dx * SimInput.AxisMax); y = (sbyte)(dy * SimInput.AxisMax); }

        var buttons = InputButtons.None;
        if (pad.Has(PadButtons.Start) || pad.Has(PadButtons.A)) buttons |= InputButtons.Start;
        if (pad.Has(PadButtons.LeftBumper) || pad.LeftTriggerDown) buttons |= InputButtons.Focus;
        if (pad.Has(PadButtons.RightBumper) || pad.RightTriggerDown) buttons |= InputButtons.Super; // Break / Overdrive

        if (role == PlayerRole.Dodger)
        {
            if (pad.Has(PadButtons.A)) buttons |= InputButtons.Flip;
        }
        else
        {
            if (pad.Has(PadButtons.A)) buttons |= InputButtons.Pattern1;
            if (pad.Has(PadButtons.B)) buttons |= InputButtons.Pattern2;
            if (pad.Has(PadButtons.X)) buttons |= InputButtons.Pattern3;
            if (pad.Has(PadButtons.Y)) buttons |= InputButtons.Pattern4;
        }
        return new SimInput(x, y, buttons);
    }

    /// <summary>Menu direction from a pad: d-pad or a firmly pushed left stick (-1/0/1 per axis).</summary>
    public static (int X, int Y) MenuDirection(in PadSnapshot pad)
    {
        int x = (pad.Has(PadButtons.DpadRight) ? 1 : 0) - (pad.Has(PadButtons.DpadLeft) ? 1 : 0);
        int y = (pad.Has(PadButtons.DpadDown) ? 1 : 0) - (pad.Has(PadButtons.DpadUp) ? 1 : 0);
        if (x == 0 && Math.Abs(pad.LeftX) > 0.6f) x = Math.Sign(pad.LeftX);
        if (y == 0 && Math.Abs(pad.LeftY) > 0.6f) y = Math.Sign(pad.LeftY);
        return (x, y);
    }
}

/// <summary>Menu actions a device performed this frame (edge-triggered).</summary>
[Flags]
public enum MenuActions : byte
{
    None = 0,
    Up = 1 << 0,
    Down = 1 << 1,
    Left = 1 << 2,
    Right = 1 << 3,
    Confirm = 1 << 4,
    Back = 1 << 5,
    /// <summary>Pad Back/Select: leave the match or replay (like Esc).</summary>
    Escape = 1 << 6,
}

/// <summary>
/// Polls every GLFW joystick slot that has a gamepad mapping, once per frame. Pads can be plugged in or
/// pulled out at any time. Reading a pad copies a value type, so polling does not allocate
/// (names are fetched only when a pad connects).
/// </summary>
public sealed unsafe class GamepadInput
{
    public const int MaxPads = 16; // GLFW_JOYSTICK_LAST + 1

    private readonly PadSnapshot[] _pads = new PadSnapshot[MaxPads];
    private readonly PadSnapshot[] _previous = new PadSnapshot[MaxPads];
    private readonly bool[] _connected = new bool[MaxPads];
    private readonly string?[] _names = new string?[MaxPads];
    private readonly MenuActions[] _menu = new MenuActions[MaxPads];
    private readonly (int X, int Y)[] _menuDir = new (int, int)[MaxPads];

    /// <summary>Incremented whenever a pad connects or disconnects.</summary>
    public int Generation { get; private set; }
    public int ConnectedCount { get; private set; }

    public bool IsConnected(int pad) => (uint)pad < MaxPads && _connected[pad];
    public string Name(int pad) => (uint)pad < MaxPads ? _names[pad] ?? $"Pad {pad + 1}" : "?";
    public ref readonly PadSnapshot State(int pad) => ref _pads[pad];
    public MenuActions Menu(int pad) => (uint)pad < MaxPads ? _menu[pad] : MenuActions.None;

    /// <summary>Menu actions from any connected pad this frame.</summary>
    public MenuActions AnyMenu { get; private set; }

    /// <summary>First connected pad, or -1.</summary>
    public int FirstConnected
    {
        get
        {
            for (int i = 0; i < MaxPads; i++) if (_connected[i]) return i;
            return -1;
        }
    }

    public void Poll()
    {
        AnyMenu = MenuActions.None;
        int count = 0;
        for (int id = 0; id < MaxPads; id++)
        {
            _previous[id] = _pads[id];
            bool present = Glfw.JoystickPresent(id) && Glfw.JoystickIsGamepad(id);
            if (present != _connected[id])
            {
                _connected[id] = present;
                _names[id] = present ? Glfw.GetGamepadName(id) : null;
                _previous[id] = default;
                Generation++;
                Console.WriteLine(present ? $"Gamepad {id + 1} connected: {_names[id]}" : $"Gamepad {id + 1} disconnected");
            }
            if (!present)
            {
                _pads[id] = default;
                _menu[id] = MenuActions.None;
                continue;
            }
            count++;
            GlfwGamepadState gs;
            if (!Glfw.GetGamepadState(id, out gs)) { _pads[id] = default; continue; }
            _pads[id] = Convert(&gs);
            _menu[id] = MenuEdges(id);
            AnyMenu |= _menu[id];
        }
        ConnectedCount = count;
    }

    private MenuActions MenuEdges(int id)
    {
        ref readonly var now = ref _pads[id];
        ref readonly var was = ref _previous[id];
        var a = MenuActions.None;
        var dir = GamepadMapping.MenuDirection(now);
        var old = _menuDir[id];
        _menuDir[id] = dir;
        if (dir.X < 0 && old.X >= 0) a |= MenuActions.Left;
        if (dir.X > 0 && old.X <= 0) a |= MenuActions.Right;
        if (dir.Y < 0 && old.Y >= 0) a |= MenuActions.Up;
        if (dir.Y > 0 && old.Y <= 0) a |= MenuActions.Down;
        var pressed = now.Buttons & ~was.Buttons;
        if ((pressed & (PadButtons.A | PadButtons.Start)) != 0) a |= MenuActions.Confirm;
        if ((pressed & PadButtons.B) != 0) a |= MenuActions.Back;
        if ((pressed & PadButtons.Back) != 0) a |= MenuActions.Escape;
        return a;
    }

    private static PadSnapshot Convert(GlfwGamepadState* gs)
    {
        byte* b = (byte*)&gs->Buttons;
        float* ax = (float*)&gs->Axes;
        var buttons = PadButtons.None;
        for (int i = 0; i <= (int)GlfwGamepadButton.DpadLeft; i++)
            if (b[i] != 0) buttons |= (PadButtons)(1 << i);
        return new PadSnapshot
        {
            LeftX = ax[(int)GlfwGamepadAxis.LeftX],
            LeftY = ax[(int)GlfwGamepadAxis.LeftY],
            RightX = ax[(int)GlfwGamepadAxis.RightX],
            RightY = ax[(int)GlfwGamepadAxis.RightY],
            // GLFW triggers rest at -1 and go to +1 when fully pressed.
            LeftTrigger = (ax[(int)GlfwGamepadAxis.LeftTrigger] + 1f) * 0.5f,
            RightTrigger = (ax[(int)GlfwGamepadAxis.RightTrigger] + 1f) * 0.5f,
            Buttons = buttons,
        };
    }

    /// <summary>Gameplay input for one pad in a role (neutral if the pad is gone).</summary>
    public SimInput Read(int pad, PlayerRole role) =>
        IsConnected(pad) ? GamepadMapping.Map(_pads[pad], role) : default;
}
