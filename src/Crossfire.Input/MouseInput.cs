using Brutal.GlfwApi;
using Crossfire.Sim;
using Crossfire.Sim.Players;
using Crossfire.Sim.Rules;

namespace Crossfire.Input;

/// <summary>
/// Mouse control for one human player.
/// Dodger: the ship steers toward the pointer; left click = Flip, right click = Break, Shift or middle button = focus.
/// Gunner: the cursor steers toward the pointer; the wheel (or keys 1-4) picks a pattern, left click places it.
///
/// The pointer is only a target: it becomes ordinary movement axes, so the simulation still moves at
/// the normal speed and stays deterministic (no teleporting to the mouse).
/// </summary>
public sealed class MouseInput(GlfwWindow window)
{
    /// <summary>HUD hints in the same layout as <see cref="KeyboardScheme.Hints"/>: move, focus, patterns, start, flip, break.</summary>
    public static readonly string[] Hints = ["Mouse", "Shift", "Wheel + L-Click", "Click", "L-Click", "R-Click"];

    /// <summary>Pointer in arena units, set by the app each frame (null when unknown).</summary>
    public float ArenaX { get; private set; }
    public float ArenaY { get; private set; }
    public bool HasTarget { get; private set; }

    /// <summary>Pattern slot the Gunner fires with a left click (0..3).</summary>
    public int SelectedPattern { get; private set; }

    /// <summary>Pointer in window units (same space as ImGui and the title menu).</summary>
    public (float X, float Y) WindowPos => ((float)window.CursorX, (float)window.CursorY);

    public bool LeftDown => window.GetMouseButton(GlfwMouseButton.Left) == GlfwButtonAction.Press;
    public bool RightDown => window.GetMouseButton(GlfwMouseButton.Right) == GlfwButtonAction.Press;
    public bool MiddleDown => window.GetMouseButton(GlfwMouseButton.Middle) == GlfwButtonAction.Press;

    /// <summary>True when another player uses the keyboard: Shift and the number keys then belong to them.</summary>
    public bool KeysShared { get; set; }

    private float _wheel;

    public void SetArenaTarget(float x, float y)
    {
        ArenaX = x;
        ArenaY = y;
        HasTarget = true;
    }

    /// <summary>Feeds one frame of wheel movement; each notch steps the selected pattern (wrapping).</summary>
    public void AddWheel(float delta)
    {
        _wheel += delta;
        while (_wheel >= 1f) { _wheel -= 1f; SelectedPattern = (SelectedPattern + MatchRules.PatternSlots - 1) % MatchRules.PatternSlots; }
        while (_wheel <= -1f) { _wheel += 1f; SelectedPattern = (SelectedPattern + 1) % MatchRules.PatternSlots; }
    }

    public void SelectPattern(int slot) => SelectedPattern = Math.Clamp(slot, 0, MatchRules.PatternSlots - 1);

    public SimInput Read(SimState s, int slot, bool mouseBlocked)
    {
        // Number keys still pick a pattern directly (handy while aiming).
        for (int k = 0; k < MatchRules.PatternSlots && !KeysShared; k++)
            if (window.GetKey(GlfwKey.Number1 + k) == GlfwKeyAction.Press) SelectedPattern = k;

        ref readonly var p = ref s.Players[slot];
        bool left = !mouseBlocked && LeftDown, right = !mouseBlocked && RightDown;
        bool shift = !KeysShared && (window.GetKey(GlfwKey.LeftShift) != GlfwKeyAction.Release || window.GetKey(GlfwKey.RightShift) != GlfwKeyAction.Release);
        var buttons = left ? InputButtons.Start : InputButtons.None; // click = rematch on the match-over screen

        if (p.Role == PlayerRole.Dodger)
        {
            if (shift || (!mouseBlocked && MiddleDown)) buttons |= InputButtons.Focus;
            if (left) buttons |= InputButtons.Flip;
            if (right) buttons |= InputButtons.Super;
        }
        else
        {
            if (left) buttons |= (InputButtons)((int)InputButtons.Pattern1 << SelectedPattern);
            if (right) buttons |= InputButtons.Super; // Overdrive
        }

        if (!HasTarget || mouseBlocked) return new SimInput(0, 0, buttons);
        // Pointer control: the simulation moves straight to this point (capped per tick) and stops exactly on it,
        // so the ship/reticle follows the mouse without the lag of axis steering.
        return new SimInput(0, 0, buttons | InputButtons.Pointer, Quantize(ArenaX), Quantize(ArenaY));
    }

    private static short Quantize(float arenaUnits) =>
        (short)Math.Clamp((int)MathF.Round(arenaUnits * SimInput.TargetScale), -8000, 8000);
}
