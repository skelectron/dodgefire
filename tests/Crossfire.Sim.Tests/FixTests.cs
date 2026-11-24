using Crossfire.Sim;

namespace Crossfire.Sim.Tests;

public class FixTests
{
    private static double D(Fix f) => f.Raw / 65536.0;

    [Fact]
    public void Arithmetic_is_exact_for_simple_values()
    {
        Assert.Equal(Fix.FromInt(5), Fix.FromInt(2) + Fix.FromInt(3));
        Assert.Equal(Fix.FromInt(-1), Fix.FromInt(2) - Fix.FromInt(3));
        Assert.Equal(Fix.FromInt(6), Fix.FromInt(2) * Fix.FromInt(3));
        Assert.Equal(Fix.Ratio(3, 2), Fix.FromInt(3) / Fix.FromInt(2));
        Assert.Equal(Fix.Ratio(9, 4), Fix.Ratio(3, 2) * Fix.Ratio(3, 2));
        Assert.Equal(-Fix.Half, Fix.Half - Fix.One);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(100)]
    [InlineData(1280 * 1280 / 1000)]
    [InlineData(30000)]
    public void Sqrt_matches_double_within_one_ulp(int v)
    {
        var r = Fix.Sqrt(Fix.FromInt(v));
        Assert.InRange(D(r), Math.Sqrt(v) - 1.0 / 65536, Math.Sqrt(v) + 1.0 / 65536);
    }

    [Fact]
    public void ISqrt_is_exact_floor()
    {
        for (ulong n = 0; n < 5000; n++)
        {
            ulong r = FixMath.ISqrt(n);
            Assert.True(r * r <= n && (r + 1) * (r + 1) > n, $"n={n} r={r}");
        }
        Assert.Equal(4294967295UL, FixMath.ISqrt(ulong.MaxValue));
    }

    [Fact]
    public void Sin_and_cos_match_double_closely_over_full_turn()
    {
        for (int a = -4096; a < 8192; a += 7)
        {
            double rad = a * Math.PI * 2 / 4096;
            Assert.InRange(D(FixMath.Sin(a)), Math.Sin(rad) - 2e-5, Math.Sin(rad) + 2e-5);
            Assert.InRange(D(FixMath.Cos(a)), Math.Cos(rad) - 2e-5, Math.Cos(rad) + 2e-5);
        }
        Assert.Equal(Fix.One, FixMath.Sin(1024));
        Assert.Equal(-Fix.One, FixMath.Cos(2048));
    }

    [Fact]
    public void Atan2_is_within_one_angle_unit_in_every_quadrant()
    {
        for (int a = 0; a < 4096; a += 13)
        {
            var v = FixMath.FromAngle(a, Fix.FromInt(200));
            int got = FixMath.Atan2(v.Y, v.X);
            int diff = Math.Abs(((got - a + 2048) & 4095) - 2048);
            Assert.True(diff <= 1, $"angle {a} -> {got}");
        }
        Assert.Equal(0, FixMath.Atan2(Fix.Zero, Fix.Zero));
    }

    [Fact]
    public void WithLength_normalises()
    {
        var v = new FixVec2(Fix.FromInt(30), Fix.FromInt(40)).WithLength(Fix.FromInt(5));
        Assert.Equal(Fix.FromInt(3), v.X);
        Assert.Equal(Fix.FromInt(4), v.Y);
        Assert.Equal(FixVec2.Zero, FixVec2.Zero.WithLength(Fix.One));
    }

    [Fact]
    public void Rng_is_seeded_and_bounded()
    {
        var a = new SimRng(42);
        var b = new SimRng(42);
        for (int i = 0; i < 1000; i++)
        {
            int x = a.NextInt(17);
            Assert.Equal(x, b.NextInt(17));
            Assert.InRange(x, 0, 16);
        }
    }
}
