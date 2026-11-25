using Crossfire.Sim;
using Crossfire.Sim.Bullets;
using Crossfire.Sim.Patterns;
using static Crossfire.Sim.Tests.TestHelpers;

namespace Crossfire.Sim.Tests;

/// <summary>Flip (one-bullet immunity roll) and Break (spend a full bar to clear your half).</summary>
public class DodgerAbilityTests
{
    private const int Dodger = 0, Gunner = 1;
    private static readonly SimInput FlipPress = new(0, 0, InputButtons.Flip);
    private static readonly SimInput BreakPress = new(0, 0, InputButtons.Super);

    /// <summary>Puts a stationary bullet exactly on the Dodger (it will touch the hitbox next collision pass).</summary>
    private static void BulletOnDodger(SimState s) =>
        Assert.True(s.Bullets.TrySpawn(s.Players[Dodger].Pos, FixVec2.Zero, s.Players[Dodger].Team, BulletType.Small));

    [Fact]
    public void Flip_absorbs_exactly_one_bullet()
    {
        using var sim = new Simulation();
        using var s = SimState.CreateMatch(OneVsOne, 1);
        SkipCountdown(sim, s);
        int lives = s.Players[Dodger].Lives;

        sim.Step(s, [FlipPress, default]);
        Assert.True(s.Players[Dodger].FlipGuard);
        Assert.Equal(s.Rules.FlipTicks, s.Players[Dodger].FlipTicks);

        // First bullet: absorbed, removed, no life lost.
        BulletOnDodger(s);
        sim.Step(s, [default, default]);
        Assert.Equal(lives, s.Players[Dodger].Lives);
        Assert.False(s.Players[Dodger].FlipGuard);
        Assert.Equal(0, s.Bullets.Count);
        Assert.Contains(s.Events.AsSpan(0, s.EventCount).ToArray(), e => e.Type == SimEventType.FlipBlock);

        // Second bullet in the same flip: a normal hit.
        BulletOnDodger(s);
        sim.Step(s, [default, default]);
        Assert.Equal(lives - 1, s.Players[Dodger].Lives);
    }

    [Fact]
    public void Flip_guard_ends_with_the_flip_and_respects_cooldown()
    {
        using var sim = new Simulation();
        using var s = SimState.CreateMatch(OneVsOne, 1);
        SkipCountdown(sim, s);

        sim.Step(s, [FlipPress, default]);
        for (int i = 0; i < s.Rules.FlipTicks; i++) sim.Step(s, [default, default]);
        Assert.False(s.Players[Dodger].FlipGuard);
        Assert.Equal(0, s.Players[Dodger].FlipTicks);

        // Still cooling down: pressing again does nothing.
        Assert.True(s.Players[Dodger].FlipCooldown > 0);
        sim.Step(s, [FlipPress, default]);
        Assert.Equal(0, s.Players[Dodger].FlipTicks);

        // After the cooldown it works again.
        while (s.Players[Dodger].FlipCooldown > 0) sim.Step(s, [default, default]);
        sim.Step(s, [FlipPress, default]);
        Assert.True(s.Players[Dodger].FlipGuard);
    }

    [Fact]
    public void Flip_is_a_faster_dash()
    {
        using var sim = new Simulation();
        using var s = SimState.CreateMatch(OneVsOne, 1);
        SkipCountdown(sim, s);
        var start = s.Players[Dodger].Pos;
        sim.Step(s, [new SimInput(0, 127, InputButtons.Flip | InputButtons.Focus), default]);
        Assert.Equal(s.Rules.DodgerSpeed * s.Rules.FlipSpeedMultiplier, s.Players[Dodger].Pos.Y - start.Y);
    }

    [Fact]
    public void Grazing_fills_break_and_a_hit_costs_some()
    {
        using var sim = new Simulation();
        using var s = SimState.CreateMatch(OneVsOne, 1);
        SkipCountdown(sim, s);
        var pos = s.Players[Dodger].Pos;
        // Inside the graze radius, outside the hitbox.
        s.Bullets.TrySpawn(pos + FixVec2.FromInt(12, 0), FixVec2.Zero, 0, BulletType.Small);
        s.Bullets.TrySpawn(pos + FixVec2.FromInt(-12, 0), FixVec2.Zero, 0, BulletType.Small);
        sim.Step(s, [default, default]);
        Assert.Equal(s.Rules.BreakPerGraze * 2, s.Players[Dodger].Break);

        // A bullet is only grazed once.
        sim.Step(s, [default, default]);
        Assert.Equal(s.Rules.BreakPerGraze * 2, s.Players[Dodger].Break);

        s.Players[Dodger].Break = Fix.FromInt(60);
        s.Bullets.Clear();
        BulletOnDodger(s);
        sim.Step(s, [default, default]);
        Assert.Equal(Fix.FromInt(60) - s.Rules.BreakHitPenalty, s.Players[Dodger].Break);
    }

    [Fact]
    public void Break_needs_a_full_bar()
    {
        using var sim = new Simulation();
        using var s = SimState.CreateMatch(OneVsOne, 1);
        SkipCountdown(sim, s);
        s.Bullets.TrySpawn(FixVec2.FromInt(100, 100), FixVec2.Zero, 0, BulletType.Small);
        s.Players[Dodger].Break = s.Rules.BreakMax - Fix.One;
        sim.Step(s, [BreakPress, default]);
        Assert.Equal(1, s.Bullets.Count);
    }

    [Fact]
    public unsafe void Break_clears_own_half_only_and_cancels_emitters_there()
    {
        using var sim = new Simulation();
        using var s = SimState.CreateMatch(OneVsOne, 1);
        SkipCountdown(sim, s);
        for (int i = 0; i < 50; i++)
        {
            s.Bullets.TrySpawn(FixVec2.FromInt(20 + i * 10, 50), FixVec2.Zero, 0, BulletType.Small);
            s.Bullets.TrySpawn(FixVec2.FromInt(700 + i * 10, 50), FixVec2.Zero, 1, BulletType.Small);
        }
        s.Emitters[0] = new Emitter { Active = true, Half = 0, Kind = PatternKind.Ring, Owner = Gunner, Pos = FixVec2.FromInt(100, 600) };
        s.Emitters[1] = new Emitter { Active = true, Half = 1, Kind = PatternKind.Ring, Owner = Gunner, Pos = FixVec2.FromInt(1000, 600) };
        s.Players[Dodger].Break = s.Rules.BreakMax;

        sim.Step(s, [BreakPress, default]);

        Assert.Equal(Fix.Zero, s.Players[Dodger].Break);
        Assert.True(s.Players[Dodger].InvulnTicks > 0);
        Assert.False(s.Emitters[0].Active);
        Assert.True(s.Emitters[1].Active);
        for (int i = 0; i < s.Bullets.Count; i++) Assert.Equal(1, s.Bullets.Half[i]);
        Assert.Contains(s.Events.AsSpan(0, s.EventCount).ToArray(), e => e.Type == SimEventType.Break);
    }

    [Fact]
    public void Abilities_do_nothing_during_countdown()
    {
        using var sim = new Simulation();
        using var s = SimState.CreateMatch(OneVsOne, 1);
        Assert.Equal(MatchPhase.Countdown, s.Phase);
        s.Players[Dodger].Break = s.Rules.BreakMax;
        sim.Step(s, [new SimInput(0, 0, InputButtons.Flip | InputButtons.Super), default]);
        Assert.Equal(0, s.Players[Dodger].FlipTicks);
        Assert.Equal(s.Rules.BreakMax, s.Players[Dodger].Break);
    }
}
