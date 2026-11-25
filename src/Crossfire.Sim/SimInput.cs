namespace Crossfire.Sim;

[Flags]
public enum InputButtons : ushort
{
    None = 0,
    Focus = 1 << 0,
    Pattern1 = 1 << 1,
    Pattern2 = 1 << 2,
    Pattern3 = 1 << 3,
    Pattern4 = 1 << 4,
    /// <summary>Dodger: spend a full Break bar to clear every bullet in your half.</summary>
    Super = 1 << 5,
    Start = 1 << 6,
    /// <summary>Dodger: barrel-roll dash that absorbs one bullet.</summary>
    Flip = 1 << 7,
    /// <summary>
    /// Pointer control: <see cref="SimInput.TargetX"/>/<see cref="SimInput.TargetY"/> hold a target point and the
    /// player moves straight toward it (capped per tick) instead of using the movement axes.
    /// </summary>
    Pointer = 1 << 8,
}

/// <summary>
/// One player's input for one tick. Movement axes are -127..127 so keyboard (digital) and
/// gamepad (analog) map onto the same deterministic representation.
/// Dodgers use the axes to move; Gunners use them to move their cursor.
/// With <see cref="InputButtons.Pointer"/> set, the target point (quarter arena units) is used instead (mouse).
/// </summary>
public readonly record struct SimInput(sbyte MoveX, sbyte MoveY, InputButtons Buttons, short TargetX = 0, short TargetY = 0)
{
    public const sbyte AxisMax = 127;
    /// <summary>Target coordinates are in 1/<see cref="TargetScale"/> arena units (sub-unit precision for smooth mouse motion).</summary>
    public const int TargetScale = 4;

    public FixVec2 Target => new(Fix.Ratio(TargetX, TargetScale), Fix.Ratio(TargetY, TargetScale));
    public bool Has(InputButtons b) => (Buttons & b) != 0;
}
