using Crossfire.Sim;
using Crossfire.Sim.Bullets;
using Crossfire.Sim.Patterns;
using Crossfire.Sim.Players;
using static Crossfire.Sim.Tests.TestHelpers;

namespace Crossfire.Sim.Tests;

/// <summary>New patterns (Sweep, Homing, Mines), loadouts and the Gunner's Overdrive.</summary>
public unsafe class GunnerOptionsTests
{
    private const int Dodger = 0, Gunner = 1;

    private static SimState PlayingMatch(Simulation sim, Loadout? gunnerLoadout = null)
    {
        var s = SimState.CreateMatch([new(0, PlayerRole.Dodger), new(1, PlayerRole.Gunner, gunnerLoadout)], 3);
        SkipCountdown(sim, s);
        return s;
    }

    private static int AngleOf(BulletPool pool, int i) => FixMath.Atan2(new Fix(pool.VelY[i]), new Fix(pool.VelX[i]));

    [Fact]
    public void Homing_turns_toward_the_dodger_but_no_faster_than_the_cap()
    {
        using var sim = new Simulation();
        using var s = PlayingMatch(sim);
        s.Players[Dodger].Pos = FixVec2.FromInt(320, 500);
        s.Players[Dodger].InvulnTicks = 1000;
        // Bullet above the Dodger, flying right (perpendicular to the Dodger direction).
        var def = s.Rules.Patterns[(int)PatternKind.Homing];
        s.Bullets.TrySpawn(FixVec2.FromInt(200, 200), new FixVec2(def.BulletSpeed, Fix.Zero), 0, BulletType.Medium, Gunner, BulletFlags.Homing, def.TimerTicks);
        int before = AngleOf(s.Bullets, 0);
        for (int t = 0; t < PatternLibrary.HomingStepTicks; t++) sim.Step(s, default);
        int after = AngleOf(s.Bullets, 0);
        int turned = PatternLibrary.WrapAngle(after - before);
        Assert.True(turned > 0, $"should turn toward the Dodger below (turned {turned})");
        Assert.True(turned <= def.Extra + 2, $"turned {turned} > cap {def.Extra}");

        // After the timer it stops homing and flies straight.
        for (int t = 0; t < def.TimerTicks; t++) sim.Step(s, default);
        Assert.Equal(0, s.Bullets.Flags[0] & (byte)BulletFlags.Homing);
        Assert.Equal(0, s.Bullets.SpecialCount);
        int straight = AngleOf(s.Bullets, 0);
        sim.Step(s, default);
        Assert.Equal(straight, AngleOf(s.Bullets, 0));
    }

    [Fact]
    public void Mine_bursts_into_a_ring_when_its_fuse_runs_out()
    {
        using var sim = new Simulation();
        using var s = PlayingMatch(sim);
        s.Players[Dodger].Pos = FixVec2.FromInt(50, 50);
        var def = s.Rules.Patterns[(int)PatternKind.Mines];
        s.Bullets.TrySpawn(FixVec2.FromInt(300, 400), FixVec2.Zero, 0, BulletType.Large, Gunner, BulletFlags.Mine, 5);
        for (int t = 0; t < 4; t++) sim.Step(s, default);
        Assert.Equal(1, s.Bullets.Count);
        sim.Step(s, default);
        Assert.Equal(def.Extra, s.Bullets.Count);
        Assert.Equal(0, s.Bullets.SpecialCount);
        for (int i = 0; i < s.Bullets.Count; i++)
        {
            Assert.Equal((byte)BulletType.Small, s.Bullets.Type[i]);
            Assert.Equal(Gunner, s.Bullets.Owner[i]);
        }
        Assert.Contains(s.Events.AsSpan(0, s.EventCount).ToArray(), e => e.Type == SimEventType.MineBurst);
    }

    [Fact]
    public void Sweep_starts_at_one_edge_of_the_arc_and_rotates_across_it()
    {
        using var sim = new Simulation();
        using var s = PlayingMatch(sim);
        var def = s.Rules.Patterns[(int)PatternKind.Sweep];
        var pos = FixVec2.FromInt(320, 360);
        s.Emitters[0] = new Emitter { Active = true, Kind = PatternKind.Sweep, Owner = Gunner, Half = 0, Pos = pos, Age = s.Rules.TelegraphTicks };
        int aim = PatternLibrary.AimAngle(s, 0, pos);
        sim.Step(s, default); // first volley
        Assert.Equal(aim - def.Spread / 2 + def.SpinPerVolley, s.Emitters[0].Angle);
        int volleys = 1;
        while (s.Emitters[0].Active) { sim.Step(s, default); if (s.Emitters[0].Active) volleys = 1 + (s.Emitters[0].Age - s.Rules.TelegraphTicks - 1) / def.FireInterval; }
        int expected = def.DurationTicks / def.FireInterval;
        Assert.InRange(volleys, expected - 1, expected + 1);
        Assert.InRange(expected * def.SpinPerVolley, def.Spread - 100, def.Spread + 100);
    }

    [Fact]
    public void Loadout_slots_map_to_their_patterns()
    {
        using var sim = new Simulation();
        using var s = PlayingMatch(sim, Loadout.Hunter);
        s.Players[Gunner].Cursor = FixVec2.FromInt(500, 100);
        s.Players[Dodger].Pos = FixVec2.FromInt(100, 600);
        s.Players[Gunner].Energy = s.Rules.EnergyMax;
        sim.Step(s, [default, new SimInput(0, 0, InputButtons.Pattern2)]); // slot 2 of Hunter = Homing
        Assert.Contains(s.Emitters, e => e.Active && e.Kind == PatternKind.Homing);
        Assert.Equal(s.Rules.Patterns[(int)PatternKind.Homing].CooldownTicks, s.Players[Gunner].Cooldowns[1]);
    }

    [Fact]
    public void Overdrive_charges_from_spending_and_kills_and_needs_a_full_meter()
    {
        using var sim = new Simulation();
        using var s = PlayingMatch(sim);
        ref var g = ref s.Players[Gunner];
        s.Players[Gunner].Cursor = FixVec2.FromInt(500, 100);
        s.Players[Dodger].Pos = FixVec2.FromInt(100, 600);
        s.Players[Gunner].Energy = s.Rules.EnergyMax;
        sim.Step(s, [default, new SimInput(0, 0, InputButtons.Pattern1)]); // Ring costs 30 -> +10
        Assert.Equal(s.Rules.Patterns[0].Cost * s.Rules.OverdrivePerEnergy, s.Players[Gunner].Overdrive);

        // Not full: Super does nothing.
        sim.Step(s, [default, new SimInput(0, 0, InputButtons.Super)]);
        Assert.Equal(0, s.Players[Gunner].OverdriveTicks);

        // A bullet owned by the Gunner takes a life: +OverdrivePerKill.
        Fix beforeKill = s.Players[Gunner].Overdrive;
        s.Bullets.Clear();
        for (int i = 0; i < SimState.MaxEmitters; i++) s.Emitters[i].Active = false;
        s.Bullets.TrySpawn(s.Players[Dodger].Pos, FixVec2.Zero, 0, BulletType.Small, Gunner);
        sim.Step(s, [default, default]);
        Assert.Equal(beforeKill + s.Rules.OverdrivePerKill, s.Players[Gunner].Overdrive);

        // Full: activates, empties, and cooldowns run faster.
        s.Players[Gunner].Overdrive = s.Rules.OverdriveMax;
        s.Players[Gunner].Cooldowns[2] = 100;
        sim.Step(s, [default, new SimInput(0, 0, InputButtons.Super)]);
        Assert.Equal(s.Rules.OverdriveTicks - 1, s.Players[Gunner].OverdriveTicks);
        Assert.Equal(Fix.Zero, s.Players[Gunner].Overdrive);
        Assert.Equal(100 - s.Rules.OverdriveCooldownRate, s.Players[Gunner].Cooldowns[2]);
        Assert.Contains(s.Events.AsSpan(0, s.EventCount).ToArray(), e => e.Type == SimEventType.Overdrive);
    }

    [Fact]
    public void Hits_are_credited_to_the_bullet_owner()
    {
        using var sim = new Simulation();
        using var s = PlayingMatch(sim);
        s.Bullets.TrySpawn(s.Players[Dodger].Pos, FixVec2.Zero, 0, BulletType.Small, Gunner);
        sim.Step(s, default);
        var hit = Assert.Single(s.Events.AsSpan(0, s.EventCount).ToArray(), e => e.Type == SimEventType.Hit);
        Assert.Equal(Gunner, hit.Other);
    }
}
