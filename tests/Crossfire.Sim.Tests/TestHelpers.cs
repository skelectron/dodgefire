using Crossfire.Sim;
using Crossfire.Sim.Players;

namespace Crossfire.Sim.Tests;

internal static class TestHelpers
{
    /// <summary>1v1: player 0 = team 0 Dodger, player 1 = team 1 Gunner.</summary>
    public static readonly PlayerSetup[] OneVsOne =
    [
        new(0, PlayerRole.Dodger),
        new(1, PlayerRole.Gunner),
    ];

    /// <summary>2v2: Dodger + Gunner on each team.</summary>
    public static readonly PlayerSetup[] TwoVsTwo =
    [
        new(0, PlayerRole.Dodger),
        new(0, PlayerRole.Gunner),
        new(1, PlayerRole.Dodger),
        new(1, PlayerRole.Gunner),
    ];

    /// <summary>
    /// Deterministic scripted "player" inputs: wandering movement, frequent pattern presses.
    /// Uses its own RNG so the script never touches simulation state.
    /// </summary>
    public sealed class InputScript(uint seed)
    {
        private SimRng _rng = new(seed);

        public void Fill(Span<SimInput> inputs)
        {
            for (int i = 0; i < inputs.Length; i++)
            {
                sbyte mx = (sbyte)((_rng.NextInt(3) - 1) * 127);
                sbyte my = (sbyte)((_rng.NextInt(3) - 1) * 127);
                var b = InputButtons.None;
                if (_rng.NextInt(4) == 0) b |= InputButtons.Focus;
                int r = _rng.NextInt(24);
                if (r < 4) b |= (InputButtons)((int)InputButtons.Pattern1 << r);
                if (_rng.NextInt(200) == 0) b |= InputButtons.Start;
                if (_rng.NextInt(40) == 0) b |= InputButtons.Flip;
                if (_rng.NextInt(60) == 0) b |= InputButtons.Super;
                inputs[i] = new SimInput(mx, my, b);
            }
        }
    }

    public static void Run(Simulation sim, SimState s, InputScript script, int ticks, int players)
    {
        Span<SimInput> inputs = stackalloc SimInput[players];
        for (int t = 0; t < ticks; t++)
        {
            script.Fill(inputs);
            sim.Step(s, inputs);
        }
    }

    public static void StepIdle(Simulation sim, SimState s, int ticks)
    {
        for (int t = 0; t < ticks; t++) sim.Step(s, default);
    }

    /// <summary>Skips the countdown so the round is in the Playing phase.</summary>
    public static void SkipCountdown(Simulation sim, SimState s)
    {
        while (s.Phase == MatchPhase.Countdown) sim.Step(s, default);
    }
}
