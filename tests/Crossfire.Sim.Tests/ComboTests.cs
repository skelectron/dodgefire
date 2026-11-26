using Crossfire.Sim.Bots;
using Crossfire.Sim.Patterns;
using Crossfire.Sim.Players;
using Crossfire.Sim.Stats;
using static Crossfire.Sim.Tests.TestHelpers;

namespace Crossfire.Sim.Tests;

/// <summary>Phase 7: crossfire combos between teammate Gunners.</summary>
public unsafe class ComboTests
{
    // Two blue Gunners (0, 1) shooting into the red half at the red Dodger (2); a blue Dodger (3) keeps the round symmetric.
    private const int G0 = 0, G1 = 1, RedDodger = 2;
    private static readonly PlayerSetup[] Layout =
        [new(0, PlayerRole.Gunner), new(0, PlayerRole.Gunner), new(1, PlayerRole.Dodger), new(0, PlayerRole.Dodger), new(1, PlayerRole.Gunner)];

    private static SimState Playing(Simulation sim, bool combos = true)
    {
        var rules = Rules.MatchRules.Default;
        rules.CombosEnabled = combos;
        var s = SimState.CreateMatch(Layout, 11, rules);
        SkipCountdown(sim, s);
        for (int i = 0; i < s.PlayerCount; i++) s.Players[i].InvulnTicks = 100_000;
        return s;
    }

    /// <summary>Puts a Gunner's cursor at <paramref name="pos"/> and presses pattern 1 for one tick. Returns that tick's events.</summary>
    private static List<SimEvent> Place(Simulation sim, SimState s, int gunner, FixVec2 pos, InputButtons button = InputButtons.Pattern1)
    {
        s.Players[gunner].Cursor = pos;
        var inputs = new SimInput[s.PlayerCount];
        sim.Step(s, inputs); // release (placement is edge-triggered)
        s.Players[gunner].Cursor = pos;
        inputs[gunner] = new SimInput(0, 0, button);
        sim.Step(s, inputs);
        return Events(s);
    }

    private static List<SimEvent> Events(SimState s)
    {
        var list = new List<SimEvent>();
        for (int i = 0; i < s.EventCount; i++) list.Add(s.Events[i]);
        return list;
    }

    private static int EmitterOf(SimState s, int owner)
    {
        for (int i = 0; i < SimState.MaxEmitters; i++)
            if (s.Emitters[i].Active && s.Emitters[i].Owner == owner) return i;
        return -1;
    }

    private static (FixVec2 A, FixVec2 B) AcrossTheDodger(SimState s)
    {
        var d = s.Players[RedDodger].Pos;
        return (d + FixVec2.FromInt(0, -170), d + FixVec2.FromInt(0, 170));
    }

    [Fact]
    public void Teammates_patterns_link_restart_their_telegraphs_and_refund_energy()
    {
        using var sim = new Simulation();
        using var s = Playing(sim);
        var (a, b) = AcrossTheDodger(s);
        Place(sim, s, G0, a);
        int first = EmitterOf(s, G0);
        Assert.True(first >= 0);
        for (int t = 0; t < 10; t++) sim.Step(s, new SimInput[s.PlayerCount]);
        Assert.True(s.Emitters[first].Age > 5);

        var energyBefore = s.Players[G0].Energy;
        var events = Place(sim, s, G1, b);
        int second = EmitterOf(s, G1);
        Assert.Contains(events, e => e.Type == SimEventType.Combo && e.Player == G1 && e.Other == G0);
        Assert.Equal(second + 1, s.Emitters[first].ComboPartner);
        Assert.Equal(first + 1, s.Emitters[second].ComboPartner);
        Assert.True(s.Emitters[second].ComboLeader);
        Assert.False(s.Emitters[first].ComboLeader);
        Assert.Equal(s.Emitters[first].Age, s.Emitters[second].Age); // fire together
        Assert.True(s.Emitters[first].Age <= 1, "partner telegraph restarted");
        Assert.True(s.Players[G0].Energy > energyBefore, "partner got the combo refund");
    }

    [Fact]
    public void Combo_volley_spawns_the_telegraphed_line_with_a_gap_splitting_into_two_walls()
    {
        using var sim = new Simulation();
        using var s = Playing(sim);
        var (a, b) = AcrossTheDodger(s);
        Place(sim, s, G0, a);
        Place(sim, s, G1, b);
        int leader = EmitterOf(s, G1), partner = EmitterOf(s, G0);
        var leaderE = s.Emitters[leader];
        int count = Combo.Count(in s.Rules, a, b);

        bool fired = false;
        for (int t = 0; t < 120 && !fired; t++)
        {
            sim.Step(s, new SimInput[s.PlayerCount]);
            foreach (var e in Events(s)) fired |= e.Type == SimEventType.ComboFired;
        }
        Assert.True(fired);

        // The combo bullets move exactly perpendicular to the line (vertical line -> horizontal motion) at the combo speed.
        int combo = 0;
        var pool = s.Bullets;
        for (int i = 0; i < pool.Count; i++)
            if (pool.VelY[i] == 0 && Math.Abs(pool.VelX[i]) == s.Rules.ComboBulletSpeed.Raw) combo++;
        int gaps = s.Rules.ComboGapSlots;
        Assert.Equal(2 * (count - gaps), combo);

        // The gap is where the telegraph said it would be: no combo bullet near those points.
        for (int k = 0; k < count; k++)
        {
            if (!Combo.IsGap(in s.Rules, count, leaderE.ComboGap, k)) continue;
            var gp = Combo.Point(s.Emitters[partner].Pos, s.Emitters[leader].Pos, k, count);
            for (int i = 0; i < pool.Count; i++)
                if (pool.VelY[i] == 0 && Math.Abs(pool.VelX[i]) == s.Rules.ComboBulletSpeed.Raw)
                    Assert.False(Math.Abs(pool.PosY[i] - gp.Y.Raw) < Fix.FromInt(2).Raw, $"bullet in gap slot {k}");
        }
    }

    [Fact]
    public void No_link_when_too_far_already_linked_enemy_or_disabled()
    {
        using var sim = new Simulation();
        // Too far apart.
        using (var s = Playing(sim))
        {
            var d = s.Players[RedDodger].Pos;
            Place(sim, s, G0, new FixVec2(d.X, Fix.FromInt(20)));
            Place(sim, s, G1, new FixVec2(d.X, Fix.FromInt(700)));
            Assert.False(s.Emitters[EmitterOf(s, G0)].Linked);
        }
        // Disabled by the rules.
        using (var s = Playing(sim, combos: false))
        {
            var (a, b) = AcrossTheDodger(s);
            Place(sim, s, G0, a);
            Place(sim, s, G1, b);
            Assert.False(s.Emitters[EmitterOf(s, G1)].Linked);
        }
        // Your own pattern never links with itself, and an already linked pattern takes no third partner.
        using (var s = Playing(sim))
        {
            var (a, b) = AcrossTheDodger(s);
            Place(sim, s, G0, a);
            Place(sim, s, G0, b, InputButtons.Pattern3); // same Gunner, second pattern
            for (int i = 0; i < SimState.MaxEmitters; i++) Assert.False(s.Emitters[i].Active && s.Emitters[i].Linked);
            Place(sim, s, G1, b + FixVec2.FromInt(150, 0));
            int linked = 0;
            for (int i = 0; i < SimState.MaxEmitters; i++) if (s.Emitters[i].Active && s.Emitters[i].Linked) linked++;
            Assert.Equal(2, linked);
        }
    }

    [Fact]
    public void Linking_puts_both_gunners_on_combo_cooldown()
    {
        using var sim = new Simulation();
        using var s = Playing(sim);
        var (a, b) = AcrossTheDodger(s);
        Place(sim, s, G0, a);
        Place(sim, s, G1, b);
        Assert.Equal(s.Rules.ComboCooldownTicks, s.Players[G0].ComboCooldown); // set after this tick's countdown
        Assert.True(s.Players[G1].ComboCooldown > 0);

        // A second pair right away does not link...
        var c = a + FixVec2.FromInt(-60, 0);
        Place(sim, s, G0, c, InputButtons.Pattern2);
        var events = Place(sim, s, G1, b + FixVec2.FromInt(-60, 0), InputButtons.Pattern2);
        Assert.DoesNotContain(events, e => e.Type == SimEventType.Combo);

        // ...but after the cooldown it does.
        for (int t = 0; t < s.Rules.ComboCooldownTicks; t++) sim.Step(s, new SimInput[s.PlayerCount]);
        Assert.Equal(0, s.Players[G0].ComboCooldown);
        s.Players[G0].Energy = s.Players[G1].Energy = s.Rules.EnergyMax;
        Place(sim, s, G0, a, InputButtons.Pattern3);
        events = Place(sim, s, G1, b, InputButtons.Pattern3);
        Assert.Contains(events, e => e.Type == SimEventType.Combo);
    }

    [Fact]
    public void Hard_bots_make_combos_in_4v4()
    {
        PlayerSetup[] fourVsFour =
        [
            new(0, PlayerRole.Dodger), new(0, PlayerRole.Dodger), new(0, PlayerRole.Gunner), new(0, PlayerRole.Gunner),
            new(1, PlayerRole.Dodger), new(1, PlayerRole.Dodger), new(1, PlayerRole.Gunner), new(1, PlayerRole.Gunner),
        ];
        using var sim = new Simulation();
        var rules = Rules.MatchRules.Default;
        rules.SwapRolesEachRound = false;
        using var s = SimState.CreateMatch(fourVsFour, 5, rules);
        var brains = new BotBrain[8];
        for (int i = 0; i < 8; i++) brains[i] = BotBrain.Create(BotDifficulty.Hard, 100u + (uint)i);
        var inputs = new SimInput[8];
        var stats = new MatchStats();
        for (int t = 0; t < 60 * 60; t++)
        {
            for (int i = 0; i < 8; i++) inputs[i] = BotController.Think(s, i, ref brains[i]);
            sim.Step(s, inputs);
            stats.Consume(s);
        }
        int combos = 0;
        for (int i = 0; i < 8; i++) combos += stats.Players[i].Combos;
        Assert.True(combos >= 4, $"expected bots to make combos in a minute of 4v4, got {combos / 2}");
    }
}
