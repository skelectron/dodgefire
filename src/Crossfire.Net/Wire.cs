using System.Buffers.Binary;
using Crossfire.Sim;

namespace Crossfire.Net;

/// <summary>Fixed little-endian encodings shared by every message.</summary>
public static class Wire
{
    /// <summary>One <see cref="SimInput"/>: move x, move y, buttons, target x, target y.</summary>
    public const int InputSize = 8;

    public static void WriteInput(Span<byte> b, SimInput i)
    {
        b[0] = (byte)i.MoveX;
        b[1] = (byte)i.MoveY;
        BinaryPrimitives.WriteUInt16LittleEndian(b[2..], (ushort)i.Buttons);
        BinaryPrimitives.WriteInt16LittleEndian(b[4..], i.TargetX);
        BinaryPrimitives.WriteInt16LittleEndian(b[6..], i.TargetY);
    }

    public static SimInput ReadInput(ReadOnlySpan<byte> b) => new(
        (sbyte)b[0],
        (sbyte)b[1],
        (InputButtons)BinaryPrimitives.ReadUInt16LittleEndian(b[2..]),
        BinaryPrimitives.ReadInt16LittleEndian(b[4..]),
        BinaryPrimitives.ReadInt16LittleEndian(b[6..]));
}
