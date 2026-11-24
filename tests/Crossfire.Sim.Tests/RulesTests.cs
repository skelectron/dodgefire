using Crossfire.Sim;
using Crossfire.Sim.Patterns;
using Crossfire.Sim.Players;
using static Crossfire.Sim.Tests.TestHelpers;

namespace Crossfire.Sim.Tests;

public class RulesTests
{
    private const int Dodger = 0, Gunner = 1;

    [Fact]
    public void Dodger_is_clamped_to_its_own_half()
    {
        using var sim = new Simulation();
        using var s = SimState.CreateMatch(OneVsOne, 1);
        SimInput right = new(127, 0, InputButtons.None);
        for (int i = 0; i < 1000; i++) sim.Step(s, [right, default]);
        Assert.Equal(s.Rules.HalfWidth - s.Rules.DodgerEdgeMargin, s.Players[Dodger].Pos.X);

        SimInput upLeft = new(-127, -127, InputButtons.None);
        for (int i = 0; i < 1000; i++) sim.Step(s, [upLeft, default]);
        Assert.Equal(s.Rules.DodgerEdgeMargin, s.Players[Dodger].Pos.X);
        Assert.Equal(s.Rules.DodgerEdgeMargin, s.Players[Dodger].Pos.Y);
    }

    [Fact]
    public void Focus_mode_moves_slower()
    {
        using var sim = new Simulation();
        using var s = SimState.CreateMatch(OneVsOne, 1);
        var start = s.Players[Dodger].Pos;
        sim.Step(s, [new SimInput(0, 127, InputButtons.Focus), default]);
        Assert.Equal(s.Rules.DodgerFocusSpeed, s.Players[Dodger].Pos.Y - start.Y);
        start = s.Players[Dodger].Pos;
        sim.Step(s, [new SimInput(0, 127, InputButtons.None), default]);
        Assert.Equal(s.Rules.DodgerSpeed, s.Players[Dodger].Pos.Y - start.Y);
    }

    [Fact]
    public void Gunner_cursor_is_clamped_to_enemy_half()
    {
        using var sim = new Simulation();
        using var s = SimState.CreateMatch(OneVsOne, 1);
        // Team 1 Gunner aims into team 0's (left) half.
        for (int i = 0; i < 500; i++) sim.Step(s, [default, new SimInput(127, 0, InputButtons.None)]);
        Assert.Equal(s.Rules.HalfWidth, s.Players[Gunner].Cursor.X);
    }

    [Fact]
    public void Cannot_place_during_countdown()
    {
        using var sim = new Simulation();
        using var s = SimState.CreateMatch(OneVsOne, 1);
        Assert.Equal(MatchPhase.Countdown, s.Phase);
        MoveCursorAwayFromDodger(sim, s);
        sim.Step(s, [default, new SimInput(0, 0, InputButtons.Pattern1)]);
        Assert.DoesNotContain(s.Emitters, e => e.Active);
    }

    [Fact]
    public void Anti_spawn_kill_rejects_placement_near_dodger()
    {
        using var sim = new Simulation();
        using var s = SimState.CreateMatch(OneVsOne, 1);
        SkipCountdown(sim, s);
        s.Players[Gunner].Cursor = s.Players[Dodger].Pos + FixVec2.FromInt(20, 0);
        s.Players[Gunner].Energy = s.Rules.EnergyMax;
        sim.Step(s, [default, new SimInput(0, 0, InputButtons.Pattern1)]);
        Assert.DoesNotContain(s.Emitters, e => e.Active);
        Assert.Contains(s.Events.AsSpan(0, s.EventCount).ToArray(), e => e.Type == SimEventType.Rejected);
    }

    [Fact]
    public void Placement_costs_energy_sets_cooldown_and_is_edge_triggered()
    {
        using var sim = new Simulation();
        using var s = SimState.CreateMatch(OneVsOne, 1);
        SkipCountdown(sim, s);
        MoveCursorAwayFromDodger(sim, s);
        s.Players[Gunner].Energy = s.Rules.EnergyMax;
        var hold = new SimInput(0, 0, InputButtons.Pattern3);
        sim.Step(s, [default, hold]);
        Assert.Single(s.Emitters, e => e.Active);
        Assert.Equal(s.Rules.Patterns[2].CooldownTicks, s.Players[Gunner].Cooldowns[2]);
        Assert.True(s.Players[Gunner].Energy < s.Rules.EnergyMax);

        // Holding the button does not place again; neither does re-pressing during cooldown.
        sim.Step(s, [default, hold]);
        sim.Step(s, [default, default]);
        sim.Step(s, [default, hold]);
        Assert.Single(s.Emitters, e => e.Active);
    }

    [Fact]
    public void Placement_without_energy_is_rejected()
    {
        using var sim = new Simulation();
        using var s = SimState.CreateMatch(OneVsOne, 1);
        SkipCountdown(sim, s);
        MoveCursorAwayFromDodger(sim, s);
        s.Players[Gunner].Energy = Fix.Zero;
        sim.Step(s, [default, new SimInput(0, 0, InputButtons.Pattern1)]);
        Assert.DoesNotContain(s.Emitters, e => e.Active);
    }

    [Fact]
    public void Telegraph_delays_first_bullet()
    {
        using var sim = new Simulation();
        using var s = SimState.CreateMatch(OneVsOne, 1);
        SkipCountdown(sim, s);
        MoveCursorAwayFromDodger(sim, s);
        s.Players[Gunner].Energy = s.Rules.EnergyMax;
        sim.Step(s, [default, new SimInput(0, 0, InputButtons.Pattern1)]);
        // The placement tick counts as the first telegraph tick.
        for (int i = 0; i < s.Rules.TelegraphTicks - 1; i++)
        {
            sim.Step(s, default);
            Assert.Equal(0, s.Bullets.Count);
        }
        sim.Step(s, default);
        Assert.Equal(s.Rules.Patterns[0].Count, s.Bullets.Count);
    }

    [Fact]
    public void Bullets_never_cross_the_midline()
    {
        using var sim = new Simulation();
        using var s = SimState.CreateMatch(OneVsOne, 1);
        SkipCountdown(sim, s);
        MoveCursorAwayFromDodger(sim, s);
        s.Players[Gunner].Energy = s.Rules.EnergyMax;
        sim.Step(s, [default, new SimInput(0, 0, InputButtons.Pattern2)]);
        for (int t = 0; t < 400; t++)
        {
            sim.Step(s, default);
            unsafe
            {
                for (int i = 0; i < s.Bullets.Count; i++)
                    Assert.True(s.Bullets.PosX[i] <= s.Rules.HalfWidth.Raw);
            }
        }
    }

    [Fact]
    public void Hit_costs_a_life_grants_invulnerability_and_clears_nearby_bullets()
    {
        using var sim = new Simulation();
        using var s = SimState.CreateMatch(OneVsOne, 1);
        SkipCountdown(sim, s);
        var pos = s.Players[Dodger].Pos;
        // One bullet on top of the Dodger, one nearby (cleared), one far away (kept).
        s.Bullets.TrySpawn(pos, FixVec2.Zero, 0, BulletType.Small);
        s.Bullets.TrySpawn(pos + FixVec2.FromInt(60, 0), FixVec2.Zero, 0, BulletType.Small);
        s.Bullets.TrySpawn(new FixVec2(Fix.FromInt(20), Fix.FromInt(20)), FixVec2.Zero, 0, BulletType.Small);
        sim.Step(s, default);

        Assert.Equal(s.Rules.DodgerLives - 1, s.Players[Dodger].Lives);
        Assert.Equal(s.Rules.InvulnTicks, s.Players[Dodger].InvulnTicks);
        Assert.Equal(1, s.Bullets.Count);

        // While invulnerable, more bullets do nothing.
        s.Bullets.TrySpawn(s.Players[Dodger].Pos, FixVec2.Zero, 0, BulletType.Small);
        sim.Step(s, default);
        Assert.Equal(s.Rules.DodgerLives - 1, s.Players[Dodger].Lives);
    }

    [Fact]
    public void Bullets_in_the_other_half_cannot_hit()
    {
        using var sim = new Simulation();
        using var s = SimState.CreateMatch(OneVsOne, 1);
        SkipCountdown(sim, s);
        s.Bullets.TrySpawn(s.Players[Dodger].Pos, FixVec2.Zero, 1, BulletType.Small);
        sim.Step(s, default);
        Assert.Equal(s.Rules.DodgerLives, s.Players[Dodger].Lives);
    }

    [Fact]
    public void Graze_counts_once_per_bullet()
    {
        using var sim = new Simulation();
        using var s = SimState.CreateMatch(OneVsOne, 1);
        SkipCountdown(sim, s);
        s.Bullets.TrySpawn(s.Players[Dodger].Pos + FixVec2.FromInt(14, 0), FixVec2.Zero, 0, BulletType.Small);
        sim.Step(s, default);
        sim.Step(s, default);
        Assert.Equal(1, s.Players[Dodger].Grazes);
        Assert.Equal(s.Rules.DodgerLives, s.Players[Dodger].Lives);
    }

    [Fact]
    public void Losing_all_lives_ends_round_and_swaps_roles()
    {
        using var sim = new Simulation();
        using var s = SimState.CreateMatch(OneVsOne, 1);
        SkipCountdown(sim, s);
        KillDodger(sim, s, Dodger);
        Assert.Equal(MatchPhase.RoundOver, s.Phase);
        Assert.Equal(1, s.LastRoundWinner);
        Assert.Equal(1, s.Scores[1]);

        StepIdle(sim, s, s.Rules.RoundOverTicks);
        Assert.Equal(MatchPhase.Countdown, s.Phase);
        Assert.Equal(2, s.RoundNumber);
        Assert.Equal(PlayerRole.Gunner, s.Players[0].Role);
        Assert.Equal(PlayerRole.Dodger, s.Players[1].Role);
        Assert.Equal(0, s.Bullets.Count);
    }

    [Fact]
    public void Surviving_an_asymmetric_round_wins_it()
    {
        using var sim = new Simulation();
        using var s = SimState.CreateMatch(OneVsOne, 1);
        SkipCountdown(sim, s);
        StepIdle(sim, s, s.Rules.SurvivalTicks);
        Assert.Equal(MatchPhase.RoundOver, s.Phase);
        Assert.Equal(0, s.LastRoundWinner);
    }

    [Fact]
    public void Symmetric_round_has_no_timeout_and_best_of_three_ends_match()
    {
        using var sim = new Simulation();
        using var s = SimState.CreateMatch(TwoVsTwo, 1);
        SkipCountdown(sim, s);
        StepIdle(sim, s, s.Rules.SurvivalTicks + 10);
        Assert.Equal(MatchPhase.Playing, s.Phase);

        // Team 0 wins two rounds -> match over.
        for (int round = 0; round < 2; round++)
        {
            int victim = FindDodger(s, team: 1);
            KillDodger(sim, s, victim);
            Assert.Equal(0, s.LastRoundWinner);
            StepIdle(sim, s, s.Rules.RoundOverTicks);
            if (round == 0) SkipCountdown(sim, s);
        }
        Assert.Equal(MatchPhase.MatchOver, s.Phase);
        Assert.Equal(0, s.MatchWinner);

        // Start from any player restarts with original roles.
        sim.Step(s, [default, default, default, new SimInput(0, 0, InputButtons.Start)]);
        Assert.Equal(MatchPhase.Countdown, s.Phase);
        Assert.Equal(1, s.RoundNumber);
        Assert.Equal(0, s.Scores[0]);
        Assert.Equal(PlayerRole.Dodger, s.Players[0].Role);
    }

    [Fact]
    public void Step_does_not_allocate_with_many_bullets()
    {
        using var sim = new Simulation();
        using var s = SimState.CreateMatch(TwoVsTwo, 1);
        SkipCountdown(sim, s);
        // 20k bullets spread around both halves, slow so they stay on screen.
        var rng = new SimRng(3);
        for (int i = 0; i < 20_000; i++)
        {
            int half = i & 1;
            var pos = new FixVec2(Fix.FromInt(40 + rng.NextInt(560) + half * 640), Fix.FromInt(40 + rng.NextInt(640)));
            s.Bullets.TrySpawn(pos, FixMath.FromAngle(rng.NextInt(4096), Fix.Ratio(1, 8)), half, (BulletType)(i % 3));
        }
        // Make Dodgers invulnerable-free targets far from bullets doesn't matter; just measure allocation.
        var script = new InputScript(9);
        Span<SimInput> inputs = stackalloc SimInput[4];
        for (int i = 0; i < 100; i++) { script.Fill(inputs); sim.Step(s, inputs); } // warm up JIT

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 300; i++) { script.Fill(inputs); sim.Step(s, inputs); }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
    }

    private static void MoveCursorAwayFromDodger(Simulation sim, SimState s)
    {
        // Dodger starts at the centre of the left half; move the cursor to the top-left corner.
        s.Players[Gunner].Cursor = FixVec2.FromInt(60, 60);
    }

    private static int FindDodger(SimState s, int team)
    {
        for (int i = 0; i < s.PlayerCount; i++)
            if (s.Players[i].IsDodger && s.Players[i].Team == team) return i;
        throw new InvalidOperationException("no dodger");
    }

    private static void KillDodger(Simulation sim, SimState s, int index)
    {
        while (s.Players[index].Lives > 0)
        {
            s.Players[index].InvulnTicks = 0;
            s.Bullets.TrySpawn(s.Players[index].Pos, FixVec2.Zero, s.Players[index].Team, BulletType.Small);
            sim.Step(s, default);
        }
    }
}
