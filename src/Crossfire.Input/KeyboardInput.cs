using Brutal.GlfwApi;
using Crossfire.Sim;

namespace Crossfire.Input;

/// <summary>A keyboard layout for one player sharing the keyboard.</summary>
public sealed class KeyboardScheme
{
    public required string Name { get; init; }
    public required GlfwKey[] Up { get; init; }
    public required GlfwKey[] Down { get; init; }
    public required GlfwKey[] Left { get; init; }
    public required GlfwKey[] Right { get; init; }
    public required GlfwKey[] Focus { get; init; }
    /// <summary>Pattern slots 1..4; each slot accepts any of its keys.</summary>
    public required GlfwKey[][] Patterns { get; init; }
    public required GlfwKey[] Start { get; init; }
    /// <summary>Human-readable key hints for the HUD: move, focus, patterns, start.</summary>
    public required string[] Hints { get; init; }

    /// <summary>Left side: WASD, Left Shift, Q/E/R/F (or 1-4), Space.</summary>
    public static KeyboardScheme LeftSide { get; } = new()
    {
        Name = "Keyboard L",
        Up = [GlfwKey.W],
        Down = [GlfwKey.S],
        Left = [GlfwKey.A],
        Right = [GlfwKey.D],
        Focus = [GlfwKey.LeftShift],
        Patterns = [[GlfwKey.Q, GlfwKey.Number1], [GlfwKey.E, GlfwKey.Number2], [GlfwKey.R, GlfwKey.Number3], [GlfwKey.F, GlfwKey.Number4]],
        Start = [GlfwKey.Space],
        Hints = ["WASD", "L-Shift", "Q E R F", "Space"],
    };

    /// <summary>Right side: arrows, Right Shift / Right Ctrl, Numpad 1-4 (or U/I/O/P), Enter.</summary>
    public static KeyboardScheme RightSide { get; } = new()
    {
        Name = "Keyboard R",
        Up = [GlfwKey.Up],
        Down = [GlfwKey.Down],
        Left = [GlfwKey.Left],
        Right = [GlfwKey.Right],
        Focus = [GlfwKey.RightShift, GlfwKey.RightControl],
        Patterns = [[GlfwKey.Kp1, GlfwKey.U], [GlfwKey.Kp2, GlfwKey.I], [GlfwKey.Kp3, GlfwKey.O], [GlfwKey.Kp4, GlfwKey.P]],
        Start = [GlfwKey.Enter, GlfwKey.KpEnter],
        Hints = ["Arrows", "R-Shift", "U I O P", "Enter"],
    };
}

/// <summary>Polls GLFW key state and produces one <see cref="SimInput"/> per player slot.</summary>
public sealed class KeyboardInput(GlfwWindow window)
{
    private bool Down(GlfwKey[] keys)
    {
        foreach (var k in keys)
            if (window.GetKey(k) != GlfwKeyAction.Release) return true;
        return false;
    }

    public SimInput Read(KeyboardScheme scheme)
    {
        int x = (Down(scheme.Right) ? 1 : 0) - (Down(scheme.Left) ? 1 : 0);
        int y = (Down(scheme.Down) ? 1 : 0) - (Down(scheme.Up) ? 1 : 0);
        var buttons = InputButtons.None;
        if (Down(scheme.Focus)) buttons |= InputButtons.Focus;
        for (int i = 0; i < scheme.Patterns.Length && i < 4; i++)
            if (Down(scheme.Patterns[i])) buttons |= (InputButtons)((int)InputButtons.Pattern1 << i);
        if (Down(scheme.Start)) buttons |= InputButtons.Start;
        return new SimInput((sbyte)(x * SimInput.AxisMax), (sbyte)(y * SimInput.AxisMax), buttons);
    }

    /// <summary>One player using either scheme (e.g. solo vs a bot): axes add up, buttons combine.</summary>
    public SimInput ReadAny(KeyboardScheme a, KeyboardScheme b)
    {
        var ia = Read(a);
        var ib = Read(b);
        int x = Math.Clamp(ia.MoveX + ib.MoveX, -SimInput.AxisMax, SimInput.AxisMax);
        int y = Math.Clamp(ia.MoveY + ib.MoveY, -SimInput.AxisMax, SimInput.AxisMax);
        return new SimInput((sbyte)x, (sbyte)y, ia.Buttons | ib.Buttons);
    }

    public bool IsDown(GlfwKey key) => window.GetKey(key) != GlfwKeyAction.Release;
}
