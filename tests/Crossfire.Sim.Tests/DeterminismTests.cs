using Crossfire.Sim;
using static Crossfire.Sim.Tests.TestHelpers;

namespace Crossfire.Sim.Tests;

public class DeterminismTests
{
    private const int Ticks = 10_000;

    private static ulong RunMatch(PlayerSetup[] setup, uint seed, uint inputSeed, int workers = 0, int threshold = int.MaxValue)
    {
        using var sim = new Simulation(workers);
        if (sim.Mover != null) sim.Mover.Threshold = threshold;
        using var s = SimState.CreateMatch(setup, seed);
        Run(sim, s, new InputScript(inputSeed), Ticks, setup.Length);
        return s.ComputeHash();
    }

    [Fact]
    public void Same_inputs_give_identical_hash_after_10k_ticks()
    {
        Assert.Equal(RunMatch(OneVsOne, 7, 99), RunMatch(OneVsOne, 7, 99));
        Assert.Equal(RunMatch(TwoVsTwo, 7, 99), RunMatch(TwoVsTwo, 7, 99));
    }

    [Fact]
    public void Different_inputs_or_seed_give_different_hash()
    {
        Assert.NotEqual(RunMatch(TwoVsTwo, 7, 99), RunMatch(TwoVsTwo, 7, 100));
        Assert.NotEqual(RunMatch(TwoVsTwo, 7, 99), RunMatch(TwoVsTwo, 8, 99));
    }

    /// <summary>
    /// Golden value: if this changes, simulation behaviour changed. That is fine when intended
    /// (tuning, new rules) 鈥?update the constant 鈥?but it must never change by accident
    /// (platform, build config, threading).
    /// </summary>
    [Fact]
    public void Golden_hash_is_stable()
    {
        const ulong Golden = 0x706D79BF28D74410; // Phase 7: combo rules, emitter links and combo cooldown in the hash
        ulong actual = RunMatch(TwoVsTwo, 1234, 5678);
        Assert.True(Golden == actual, $"Golden hash mismatch: expected 0x{Golden:X16}, actual 0x{actual:X16}");
    }

    [Fact]
    public void Parallel_bullet_movement_matches_single_threaded()
    {
        ulong serial = RunMatch(TwoVsTwo, 3, 4);
        ulong parallel = RunMatch(TwoVsTwo, 3, 4, workers: 4, threshold: 1);
        Assert.Equal(serial, parallel);
    }

    [Fact]
    public void Snapshot_and_resimulate_reproduces_state()
    {
        using var sim = new Simulation();
        using var a = SimState.CreateMatch(TwoVsTwo, 11);
        var script = new InputScript(22);
        Run(sim, a, script, 3000, 4);

        using var snapshot = new SimState();
        snapshot.CopyFrom(a);
        Assert.Equal(a.ComputeHash(), snapshot.ComputeHash());

        // Record the next 2000 ticks of input, apply to both.
        var recorded = new SimInput[2000 * 4];
        script.Fill(recorded);
        for (int t = 0; t < 2000; t++)
        {
            sim.Step(a, recorded.AsSpan(t * 4, 4));
            sim.Step(snapshot, recorded.AsSpan(t * 4, 4));
        }
        Assert.Equal(a.ComputeHash(), snapshot.ComputeHash());
    }

    [Fact]
    public void Scripted_match_actually_exercises_bullets_and_hits()
    {
        using var sim = new Simulation();
        using var s = SimState.CreateMatch(TwoVsTwo, 5);
        var script = new InputScript(6);
        Span<SimInput> inputs = stackalloc SimInput[4];
        int maxBullets = 0, hits = 0, placed = 0;
        for (int t = 0; t < Ticks; t++)
        {
            script.Fill(inputs);
            sim.Step(s, inputs);
            maxBullets = Math.Max(maxBullets, s.Bullets.Count);
            for (int e = 0; e < s.EventCount; e++)
            {
                if (s.Events[e].Type is SimEventType.Hit or SimEventType.Death) hits++;
                if (s.Events[e].Type == SimEventType.Placed) placed++;
            }
        }
        Assert.True(placed > 10, $"placed={placed}");
        Assert.True(maxBullets > 50, $"maxBullets={maxBullets}");
        Assert.True(hits > 0, $"hits={hits}");
    }
}
