using Crossfire.Sim;
using static Crossfire.Sim.Tests.TestHelpers;

namespace Crossfire.Sim.Tests;

/// <summary>Mouse (pointer) control: straight to the target, capped per tick, landing exactly on it.</summary>
public class PointerTests
{
    private const int Dodger = 0, Gunner = 1;

    private static SimInput Point(float x, float y, InputButtons extra = InputButtons.None) =>
        new(0, 0, InputButtons.Pointer | extra, (short)(x * SimInput.TargetScale), (short)(y * SimInput.TargetScale));

    [Fact]
    public void Dodger_lands_exactly_on_a_near_target()
    {
        using var sim = new Simulation();
        using var s = SimState.CreateMatch(OneVsOne, 1);
        var start = s.Players[Dodger].Pos;
        var target = start + new FixVec2(Fix.Ratio(13, 4), Fix.Ratio(-7, 4)); // within one tick's reach
        sim.Step(s, [Point(target.X.Raw / 65536f, target.Y.Raw / 65536f), default]);
        Assert.Equal(target, s.Players[Dodger].Pos);
    }

    [Fact]
    public void Dodger_speed_is_capped_so_it_cannot_skip_over_bullets()
    {
        using var sim = new Simulation();
        using var s = SimState.CreateMatch(OneVsOne, 1);
        var start = s.Players[Dodger].Pos;
        sim.Step(s, [Point(20, 20), default]); // far away
        long moved = (s.Players[Dodger].Pos - start).LengthSqRaw();
        long cap = s.Rules.DodgerPointerSpeed.Raw + 2; // rounding
        Assert.True(moved <= cap * cap);
        Assert.True(moved >= (cap - 4) * (cap - 4), "should move at full pointer speed when far");
        // The no-tunnelling bound: smallest gap a bullet can slip through is 2 x (hitbox + small bullet radius).
        var minGap = (s.Rules.HitboxRadius + Patterns.PatternLibrary.BulletRadius(Patterns.BulletType.Small)) * 2;
        Assert.True(s.Rules.DodgerPointerSpeed < minGap);
    }

    [Fact]
    public void Focus_slows_pointer_movement()
    {
        using var sim = new Simulation();
        using var s = SimState.CreateMatch(OneVsOne, 1);
        var start = s.Players[Dodger].Pos;
        sim.Step(s, [Point(20, start.Y.Raw / 65536f, InputButtons.Focus), default]);
        Assert.Equal(s.Rules.DodgerFocusSpeed, start.X - s.Players[Dodger].Pos.X);
    }

    [Fact]
    public void Pointer_in_the_enemy_half_is_clamped_to_own_half()
    {
        using var sim = new Simulation();
        using var s = SimState.CreateMatch(OneVsOne, 1);
        for (int i = 0; i < 200; i++) sim.Step(s, [Point(1200, 300), default]);
        Assert.Equal(s.Rules.HalfWidth - s.Rules.DodgerEdgeMargin, s.Players[Dodger].Pos.X);
    }

    [Fact]
    public void Gunner_reticle_sticks_to_the_pointer()
    {
        using var sim = new Simulation();
        using var s = SimState.CreateMatch(OneVsOne, 1);
        // Team 1 Gunner aims into the left half.
        // A big jump takes a few ticks (capped per tick), then it sits exactly on the pointer.
        for (int i = 0; i < 6; i++) sim.Step(s, [default, Point(300.25f, 200.5f)]);
        Assert.Equal(new FixVec2(Fix.Ratio(1201, 4), Fix.Ratio(802, 4)), s.Players[Gunner].Cursor);
    }
}
