using Crossfire.Sim;
using Crossfire.Sim.Bots;
using Crossfire.Sim.Patterns;
using Crossfire.Sim.Players;
using Crossfire.Sim.Replays;
using Crossfire.Sim.Rules;
using Crossfire.Sim.Stats;

namespace Crossfire.Sim.Tests;

public class ReplayAndStatsTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static readonly PlayerSetup[] FourVsFour =
    [
        new(0, PlayerRole.Dodger), new(0, PlayerRole.Dodger), new(0, PlayerRole.Gunner, Loadout.Hunter), new(0, PlayerRole.Gunner, Loadout.Trapper),
        new(1, PlayerRole.Dodger), new(1, PlayerRole.Dodger), new(1, PlayerRole.Gunner), new(1, PlayerRole.Gunner, Loadout.Hunter),
    ];

    /// <summary>Plays bots for <paramref name="ticks"/>, recording a replay and stats. Optionally edits a rule mid-way.</summary>
    private static (Replay Replay, MatchStats Stats, ulong Hash) PlayAndRecord(PlayerSetup[] layout, int ticks, bool editRules)
    {
        var rules = MatchRules.Default;
        rules.SwapRolesEachRound = false;
        using var sim = new Simulation();
        using var s = SimState.CreateMatch(layout, 42, rules);
        var brains = new BotBrain[layout.Length];
        for (int i = 0; i < brains.Length; i++) brains[i] = BotBrain.Create(BotDifficulty.Hard, 7 + (uint)i);
        var recorder = new ReplayRecorder();
        recorder.Begin(s, 42, layout);
        var stats = new MatchStats();
        var inputs = new SimInput[layout.Length];
        for (int t = 0; t < ticks; t++)
        {
            if (editRules && t == ticks / 2) s.Rules.DodgerSpeed = Fix.FromInt(6);
            for (int i = 0; i < inputs.Length; i++) inputs[i] = BotController.Think(s, i, ref brains[i]);
            recorder.Record(s, inputs);
            sim.Step(s, inputs);
            stats.Consume(s);
        }
        return (recorder.Finish(s)!, stats, s.ComputeHash());
    }

    [Fact]
    public void Replay_reproduces_the_session_exactly_including_rule_edits()
    {
        var (replay, _, hash) = PlayAndRecord(FourVsFour, 60 * 60 * 3, editRules: true);
        Assert.Single(replay.RuleChanges);
        Assert.Equal(hash, replay.FinalHash);

        using var player = new ReplayPlayer(replay);
        while (player.Step()) { }
        Assert.True(player.HashMatches);
    }

    [Fact]
    public void Replay_survives_a_save_load_round_trip()
    {
        var (replay, _, _) = PlayAndRecord([new(0, PlayerRole.Dodger), new(1, PlayerRole.Gunner, Loadout.Trapper)], 3000, editRules: false);
        var bytes = new MemoryStream();
        replay.Save(bytes);
        output.WriteLine($"3000 ticks, 2 players: {bytes.Length} bytes");
        bytes.Position = 0;
        var loaded = Replay.Load(bytes);
        Assert.Equal(replay.Ticks, loaded.Ticks);
        Assert.Equal(replay.FinalHash, loaded.FinalHash);
        Assert.Equal(PatternKind.Mines, loaded.Layout[1].Loadout!.Value[1]);

        var again = new MemoryStream();
        loaded.Save(again);
        Assert.Equal(bytes.ToArray(), again.ToArray());

        using var player = new ReplayPlayer(loaded);
        while (player.Step()) { }
        Assert.True(player.HashMatches);
    }

    [Fact]
    public void Stats_add_up()
    {
        var (_, stats, _) = PlayAndRecord(FourVsFour, 60 * 60 * 3, editRules: false);
        int lost = 0, taken = 0, placed = 0;
        for (int i = 0; i < FourVsFour.Length; i++)
        {
            var p = stats.Players[i];
            lost += p.LivesLost;
            taken += p.LivesTaken;
            placed += p.Placed;
            output.WriteLine($"P{i + 1} {FourVsFour[i].Role}: lost {p.LivesLost} taken {p.LivesTaken} grazes {p.Grazes} flips {p.Flips}/{p.FlipBlocks} breaks {p.Breaks} placed {p.Placed} overdrives {p.Overdrives}");
        }
        Assert.True(lost > 0);
        Assert.Equal(lost, taken); // every bullet has an owner, so every life lost is credited
        Assert.True(placed > 20);
        Assert.True(stats.Mvp() >= 0);
    }

    [Fact]
    public void Stats_consume_does_not_allocate()
    {
        var rules = MatchRules.Default;
        using var sim = new Simulation();
        using var s = SimState.CreateMatch(FourVsFour, 1, rules);
        var stats = new MatchStats();
        var brains = new BotBrain[8];
        for (int i = 0; i < 8; i++) brains[i] = BotBrain.Create(BotDifficulty.Normal, (uint)i);
        var inputs = new SimInput[8];
        void Tick()
        {
            for (int i = 0; i < 8; i++) inputs[i] = BotController.Think(s, i, ref brains[i]);
            sim.Step(s, inputs);
            stats.Consume(s);
        }
        for (int t = 0; t < 600; t++) Tick();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int t = 0; t < 600; t++) Tick();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
