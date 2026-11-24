using System.Runtime.CompilerServices;

namespace Crossfire.Sim;

/// <summary>
/// Q16.16 signed fixed-point number. The simulation uses only this type (never float/double)
/// so that every machine produces bit-identical results — the foundation for rollback netcode.
/// Range is roughly ±32767 with a resolution of 1/65536.
/// </summary>
public readonly record struct Fix(int Raw) : IComparable<Fix>
{
    public const int FractionBits = 16;
    public const int OneRaw = 1 << FractionBits;

    public static Fix Zero => default;
    public static Fix One => new(OneRaw);
    public static Fix Half => new(OneRaw >> 1);
    public static Fix MaxValue => new(int.MaxValue);
    public static Fix MinValue => new(int.MinValue);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Fix FromInt(int value) => new(value << FractionBits);

    /// <summary>Exact rational constructor: numerator / denominator, rounded toward zero.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Fix Ratio(int numerator, int denominator) => new((int)(((long)numerator << FractionBits) / denominator));

    public int ToIntFloor() => Raw >> FractionBits;
    public int ToIntRound() => (Raw + (OneRaw >> 1)) >> FractionBits;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Fix operator +(Fix a, Fix b) => new(a.Raw + b.Raw);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Fix operator -(Fix a, Fix b) => new(a.Raw - b.Raw);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Fix operator -(Fix a) => new(-a.Raw);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Fix operator *(Fix a, Fix b) => new((int)(((long)a.Raw * b.Raw) >> FractionBits));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Fix operator /(Fix a, Fix b) => new((int)(((long)a.Raw << FractionBits) / b.Raw));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Fix operator *(Fix a, int b) => new(a.Raw * b);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Fix operator /(Fix a, int b) => new(a.Raw / b);

    public static bool operator <(Fix a, Fix b) => a.Raw < b.Raw;
    public static bool operator >(Fix a, Fix b) => a.Raw > b.Raw;
    public static bool operator <=(Fix a, Fix b) => a.Raw <= b.Raw;
    public static bool operator >=(Fix a, Fix b) => a.Raw >= b.Raw;

    public int CompareTo(Fix other) => Raw.CompareTo(other.Raw);

    public static Fix Min(Fix a, Fix b) => a.Raw < b.Raw ? a : b;
    public static Fix Max(Fix a, Fix b) => a.Raw > b.Raw ? a : b;
    public static Fix Clamp(Fix v, Fix lo, Fix hi) => v.Raw < lo.Raw ? lo : v.Raw > hi.Raw ? hi : v;
    public static Fix Abs(Fix v) => new(v.Raw < 0 ? -v.Raw : v.Raw);

    /// <summary>Square root using a pure-integer method (no floating point).</summary>
    public static Fix Sqrt(Fix v)
    {
        if (v.Raw <= 0) return Zero;
        // sqrt(raw / 2^16) * 2^16 == sqrt(raw * 2^16)
        return new((int)FixMath.ISqrt((ulong)v.Raw << FractionBits));
    }

    public override string ToString() => (Raw / (double)OneRaw).ToString("0.#####");
}

/// <summary>2D vector of <see cref="Fix"/>.</summary>
public readonly record struct FixVec2(Fix X, Fix Y)
{
    public static FixVec2 Zero => default;

    public static FixVec2 FromInt(int x, int y) => new(Fix.FromInt(x), Fix.FromInt(y));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FixVec2 operator +(FixVec2 a, FixVec2 b) => new(a.X + b.X, a.Y + b.Y);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FixVec2 operator -(FixVec2 a, FixVec2 b) => new(a.X - b.X, a.Y - b.Y);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FixVec2 operator *(FixVec2 a, Fix s) => new(a.X * s, a.Y * s);

    /// <summary>Squared length as a raw 64-bit Q32.32 value — cannot overflow for in-arena distances.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long LengthSqRaw() => (long)X.Raw * X.Raw + (long)Y.Raw * Y.Raw;

    public Fix Length() => new((int)FixMath.ISqrt((ulong)LengthSqRaw()));

    /// <summary>Returns this vector scaled to <paramref name="length"/>; zero vector stays zero.</summary>
    public FixVec2 WithLength(Fix length)
    {
        Fix len = Length();
        if (len.Raw == 0) return Zero;
        return new(new Fix((int)((long)X.Raw * length.Raw / len.Raw)), new Fix((int)((long)Y.Raw * length.Raw / len.Raw)));
    }

    public override string ToString() => $"({X}, {Y})";
}

/// <summary>Deterministic integer math helpers.</summary>
public static class FixMath
{
    /// <summary>Angles are binary angle units: 4096 per full turn (1024 = 90°).</summary>
    public const int AngleUnitsPerTurn = 4096;
    public const int AngleMask = AngleUnitsPerTurn - 1;

    public static ulong ISqrt(ulong n)
    {
        if (n == 0) return 0;
        // Bit-by-bit integer sqrt; exact floor(sqrt(n)).
        ulong result = 0;
        ulong bit = 1UL << 62;
        while (bit > n) bit >>= 2;
        while (bit != 0)
        {
            if (n >= result + bit)
            {
                n -= result + bit;
                result = (result >> 1) + bit;
            }
            else
            {
                result >>= 1;
            }
            bit >>= 2;
        }
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Fix Sin(int angle)
    {
        angle &= AngleMask;
        ReadOnlySpan<int> t = SinTable.QuarterSine;
        return angle switch
        {
            < 1024 => new Fix(t[angle]),
            < 2048 => new Fix(t[2048 - angle]),
            < 3072 => new Fix(-t[angle - 2048]),
            _ => new Fix(-t[4096 - angle]),
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Fix Cos(int angle) => Sin(angle + 1024);

    /// <summary>Unit vector at the given binary angle, scaled by <paramref name="length"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FixVec2 FromAngle(int angle, Fix length) => new(Cos(angle) * length, Sin(angle) * length);

    /// <summary>
    /// Integer atan2 returning binary angle units (0..4095). Uses binary search on the sine table,
    /// so it is exact to within one angle unit and fully deterministic.
    /// </summary>
    public static int Atan2(Fix y, Fix x)
    {
        if (x.Raw == 0 && y.Raw == 0) return 0;
        long ax = Math.Abs((long)x.Raw), ay = Math.Abs((long)y.Raw);
        // Find angle a in [0,1024] in the first octant-free quadrant where tan(a) ~= ay/ax:
        // compare ay*cos(a) with ax*sin(a) via binary search.
        int lo = 0, hi = 1024;
        ReadOnlySpan<int> t = SinTable.QuarterSine;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            long s = t[mid], c = t[1024 - mid];
            if (ax * s < ay * c) lo = mid + 1; else hi = mid;
        }
        int a = lo;
        if (x.Raw >= 0 && y.Raw >= 0) return a & AngleMask;
        if (x.Raw < 0 && y.Raw >= 0) return (2048 - a) & AngleMask;
        if (x.Raw < 0) return (2048 + a) & AngleMask;
        return (4096 - a) & AngleMask;
    }
}
