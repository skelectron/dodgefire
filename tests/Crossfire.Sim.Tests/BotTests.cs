using Crossfire.Sim;
using Crossfire.Sim.Bots;
using Crossfire.Sim.Players;
using Crossfire.Sim.Rules;
using static Crossfire.Sim.Tests.TestHelpers;

namespace Crossfire.Sim.Tests;

public class BotTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static MatchRules EndlessRules()
    {
        // Keep roles fixed and the match running so we measure one role over a long stretch.
        var r = MatchRules.Default;
        r.SwapRolesEachRound = false;
        r.RoundsToWin = 1000;
        r.SurvivalTicks = 1_000_000;
        return r;
    }

    private static (ulong Hash, int Placed) RunBotMatch(BotDifficulty a, BotDifficulty b, int ticks)
    {
        using var sim = new Simulation();
        using var s = SimState.CreateMatch(OneVsOne, 77);
        var brains = new[] { BotBrain.Create(a, 1), BotBrain.Create(b, 2) };
        var inputs = new SimInput[2];
        int placed = 0;
        for (int t = 0; t < ticks; t++)
        {
            for (int i = 0; i < 2; i++) inputs[i] = BotController.Think(s, i, ref brains[i]);
            sim.Step(s, inputs);
            for (int e = 0; e < s.EventCount; e++) if (s.Events[e].Type == SimEventType.Placed) placed++;
        }
        return (s.ComputeHash(), placed);
    }

    [Fact]
    public void Bot_vs_bot_is_deterministic()
    {
        var first = RunBotMatch(BotDifficulty.Hard, BotDifficulty.Normal, 10_000);
        var second = RunBotMatch(BotDifficulty.Hard, BotDifficulty.Normal, 10_000);
        Assert.Equal(first.Hash, second.Hash);
        Assert.True(first.Placed > 20, $"placed={first.Placed}");
    }

    /// <summary>Lives lost by the slot-0 Dodger against a scripted random gunner, for a given controller.</summary>
    private static int LivesLost(BotDifficulty? dodgerBot, int ticks = 6000) => LivesLost(dodgerBot, out _, out _, out _, ticks);

    private static int LivesLost(BotDifficulty? dodgerBot, out int flips, out int blocks, out int breaks, int ticks = 6000)
    {
        using var sim = new Simulation();
        using var s = SimState.CreateMatch(OneVsOne, 5, EndlessRules());
        var brain = dodgerBot is { } d ? BotBrain.Create(d, 9) : default;
        var gunnerBrain = BotBrain.Create(BotDifficulty.Hard, 10);
        var inputs = new SimInput[2];
        int lost = 0;
        flips = blocks = breaks = 0;
        for (int t = 0; t < ticks; t++)
        {
            inputs[0] = dodgerBot.HasValue ? BotController.Think(s, 0, ref brain) : default;
            inputs[1] = BotController.Think(s, 1, ref gunnerBrain);
            sim.Step(s, inputs);
            for (int e = 0; e < s.EventCount; e++)
            {
                if (s.Events[e].Player != 0) continue;
                switch (s.Events[e].Type)
                {
                    case SimEventType.Hit or SimEventType.Death: lost++; break;
                    case SimEventType.Flip: flips++; break;
                    case SimEventType.FlipBlock: blocks++; break;
                    case SimEventType.Break: breaks++; break;
                }
            }
        }
        return lost;
    }

    [Fact]
    public void Dodger_bots_dodge_and_harder_is_better()
    {
        int idle = LivesLost(null);
        int easy = LivesLost(BotDifficulty.Easy);
        int normal = LivesLost(BotDifficulty.Normal, out int nf, out int nb, out int nk);
        int hard = LivesLost(BotDifficulty.Hard, out int hf, out int hb, out int hk);
        output.WriteLine($"normal: flips={nf} blocks={nb} breaks={nk}; hard: flips={hf} blocks={hb} breaks={hk}");
        output.WriteLine($"lives lost in 100 s vs Hard gunner bot: idle={idle} easy={easy} normal={normal} hard={hard}");
        Assert.True(hard < idle, $"hard={hard} idle={idle}");
        Assert.True(hard < easy, $"hard={hard} easy={easy}");
        Assert.True(normal < easy, $"normal={normal} easy={easy}");
        Assert.True(easy < idle, $"easy={easy} idle={idle}");
    }

    [Fact]
    public void Gunner_bot_places_patterns_and_rarely_gets_rejected()
    {
        using var sim = new Simulation();
        using var s = SimState.CreateMatch(OneVsOne, 3, EndlessRules());
        var brain = BotBrain.Create(BotDifficulty.Normal, 4);
        var inputs = new SimInput[2];
        int placed = 0, rejected = 0, hits = 0;
        for (int t = 0; t < 3600; t++)
        {
            inputs[1] = BotController.Think(s, 1, ref brain);
            sim.Step(s, inputs);
            for (int e = 0; e < s.EventCount; e++)
            {
                if (s.Events[e].Type == SimEventType.Placed) placed++;
                if (s.Events[e].Type == SimEventType.Rejected) rejected++;
                if (s.Events[e].Type is SimEventType.Hit or SimEventType.Death) hits++;
            }
        }
        Assert.True(placed >= 20, $"placed={placed}");
        Assert.True(rejected * 5 < placed, $"rejected={rejected} placed={placed}");
        Assert.True(hits > 0, "a Gunner bot should land hits on an idle Dodger");
    }

    [Fact]
    public void Bots_handle_role_swaps_and_finish_a_match()
    {
        using var sim = new Simulation();
        using var s = SimState.CreateMatch(OneVsOne, 11);
        var brains = new[] { BotBrain.Create(BotDifficulty.Hard, 1), BotBrain.Create(BotDifficulty.Hard, 2) };
        var inputs = new SimInput[2];
        bool sawMatchOver = false, sawRematch = false;
        for (int t = 0; t < 60 * 60 * 6 && !sawRematch; t++)
        {
            for (int i = 0; i < 2; i++) inputs[i] = BotController.Think(s, i, ref brains[i]);
            sim.Step(s, inputs);
            if (s.Phase == MatchPhase.MatchOver) sawMatchOver = true;
            if (sawMatchOver && s.Phase == MatchPhase.Countdown) sawRematch = true;
        }
        Assert.True(sawMatchOver, "bots should finish a best-of-3");
        Assert.True(sawRematch, "bot-only matches restart by themselves");
    }

    /// <summary>The Team 4v4 layout: each team fields 2 Dodgers + 2 Gunners, roles fixed.</summary>
    private static readonly PlayerSetup[] FourVsFour =
    [
        new(0, PlayerRole.Dodger), new(0, PlayerRole.Dodger), new(0, PlayerRole.Gunner), new(0, PlayerRole.Gunner),
        new(1, PlayerRole.Dodger), new(1, PlayerRole.Dodger), new(1, PlayerRole.Gunner), new(1, PlayerRole.Gunner),
    ];

    private static (ulong Hash, int Rounds, int MatchWinner, double MaxThinkMs) RunTeamMatch(BotDifficulty d, int ticks)
    {
        var rules = MatchRules.Default;
        rules.SwapRolesEachRound = false;
        using var sim = new Simulation();
        using var s = SimState.CreateMatch(FourVsFour, 21, rules);
        var brains = new BotBrain[FourVsFour.Length];
        for (int i = 0; i < brains.Length; i++) brains[i] = BotBrain.Create(d, 100 + (uint)i);
        var inputs = new SimInput[FourVsFour.Length];
        int rounds = 0, winner = -1;
        double maxThink = 0;
        for (int t = 0; t < ticks && winner < 0; t++)
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            for (int i = 0; i < inputs.Length; i++) inputs[i] = BotController.Think(s, i, ref brains[i]);
            if (t > 600) maxThink = Math.Max(maxThink, System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            sim.Step(s, inputs);
            for (int e = 0; e < s.EventCount; e++)
            {
                if (s.Events[e].Type == SimEventType.RoundEnd) rounds++;
                if (s.Events[e].Type == SimEventType.MatchEnd) winner = s.MatchWinner;
            }
        }
        return (s.ComputeHash(), rounds, winner, maxThink);
    }

    [Fact]
    public void Team_4v4_bots_are_deterministic_and_finish_a_match()
    {
        var first = RunTeamMatch(BotDifficulty.Normal, 60 * 60 * 6);
        var second = RunTeamMatch(BotDifficulty.Normal, 60 * 60 * 6);
        output.WriteLine($"4v4 normal: rounds={first.Rounds} winner={first.MatchWinner} max think (8 bots) {first.MaxThinkMs:F2} ms");
        Assert.Equal(first.Hash, second.Hash);
        Assert.True(first.MatchWinner >= 0, "a 4v4 bot match should end within 6 minutes");
        Assert.True(first.Rounds >= 2);

        var hard = RunTeamMatch(BotDifficulty.Hard, 60 * 60 * 6);
        output.WriteLine($"4v4 hard: rounds={hard.Rounds} winner={hard.MatchWinner} max think (8 bots) {hard.MaxThinkMs:F2} ms");
    }

    [Fact]
    public void Think_does_not_allocate()
    {
        using var sim = new Simulation();
        using var s = SimState.CreateMatch(OneVsOne, 1);
        var brains = new[] { BotBrain.Create(BotDifficulty.Hard, 1), BotBrain.Create(BotDifficulty.Hard, 2) };
        var inputs = new SimInput[2];
        for (int t = 0; t < 600; t++)
        {
            for (int i = 0; i < 2; i++) inputs[i] = BotController.Think(s, i, ref brains[i]);
            sim.Step(s, inputs);
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int t = 0; t < 600; t++)
        {
            for (int i = 0; i < 2; i++) inputs[i] = BotController.Think(s, i, ref brains[i]);
            sim.Step(s, inputs);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}

