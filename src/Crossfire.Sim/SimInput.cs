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
    Super = 1 << 5,
    Start = 1 << 6,
}

/// <summary>
/// One player's input for one tick. Movement axes are -127..127 so keyboard (digital) and
/// gamepad (analog) map onto the same deterministic representation.
/// Dodgers use the axes to move; Gunners use them to move their cursor.
/// </summary>
public readonly record struct SimInput(sbyte MoveX, sbyte MoveY, InputButtons Buttons)
{
    public const sbyte AxisMax = 127;
    public bool Has(InputButtons b) => (Buttons & b) != 0;
}
