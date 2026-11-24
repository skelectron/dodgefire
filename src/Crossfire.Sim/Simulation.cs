using Crossfire.Sim.Bullets;
using Crossfire.Sim.Collision;
using Crossfire.Sim.Players;
using Crossfire.Sim.Rules;

namespace Crossfire.Sim;

/// <summary>
/// Advances a <see cref="SimState"/> by one fixed tick. The result depends only on the state and
/// the inputs — never on time, threads or floats — so identical inputs give identical states.
/// The optional worker threads only change how fast bullets move, never the result.
/// </summary>
public sealed class Simulation : IDisposable
{
    private readonly ParallelBulletMover? _mover;

    /// <param name="bulletWorkers">0 = single-threaded bullet movement.</param>
    public Simulation(int bulletWorkers = 0)
    {
        if (bulletWorkers > 1) _mover = new ParallelBulletMover(bulletWorkers);
    }

    public ParallelBulletMover? Mover => _mover;

    /// <param name="inputs">One input per player slot (index = slot). Missing entries count as no input.</param>
    public void Step(SimState s, ReadOnlySpan<SimInput> inputs)
    {
        s.EventCount = 0;

        for (int i = 0; i < s.PlayerCount; i++)
        {
            s.Players[i].PrevPos = s.Players[i].Pos;
            s.Players[i].PrevCursor = s.Players[i].Cursor;
        }

        switch (s.Phase)
        {
            case MatchPhase.Countdown:
                MovePlayers(s, inputs);
                if (--s.PhaseTicks <= 0)
                {
                    s.Phase = MatchPhase.Playing;
                    s.PushEvent(SimEventType.RoundStart, 0, 0, FixVec2.Zero);
                }
                break;

            case MatchPhase.Playing:
                s.RoundTick++;
                // 1. Inputs -> movement (Dodgers clamped to their half, cursors to the enemy half).
                MovePlayers(s, inputs);
                // 2. Gunner actions -> validated, telegraphed emitters.
                for (int i = 0; i < s.PlayerCount; i++)
                    if (s.Players[i].IsGunner) GunnerSystem.Act(s, i, Get(inputs, i));
                // 3. Emitters spawn bullets.
                EmitterSystem.Update(s);
                // 4. Bullets move and despawn outside their half.
                if (_mover != null) _mover.Move(s.Bullets);
                else BulletSystem.MoveRange(s.Bullets, 0, s.Bullets.Count);
                BulletSystem.Despawn(s.Bullets, in s.Rules);
                // 5. Collision + graze.
                BulletCollision.Run(s);
                // 6. Rules.
                MatchFlow.CheckRoundEnd(s);
                break;

            case MatchPhase.RoundOver:
                if (--s.PhaseTicks <= 0) MatchFlow.AfterRoundOver(s);
                break;

            case MatchPhase.MatchOver:
                for (int i = 0; i < s.PlayerCount; i++)
                {
                    var pressed = Get(inputs, i).Buttons & ~s.Players[i].PrevButtons;
                    if ((pressed & InputButtons.Start) != 0)
                    {
                        MatchFlow.StartMatch(s);
                        break;
                    }
                }
                break;
        }

        for (int i = 0; i < s.PlayerCount; i++) s.Players[i].PrevButtons = Get(inputs, i).Buttons;

        // 7. Tick counter.
        s.Tick++;
    }

    private static void MovePlayers(SimState s, ReadOnlySpan<SimInput> inputs)
    {
        for (int i = 0; i < s.PlayerCount; i++)
        {
            ref readonly var p = ref s.Players[i];
            if (!p.Active) continue;
            if (p.Role == PlayerRole.Dodger) DodgerSystem.Move(s, i, Get(inputs, i));
            else GunnerSystem.MoveCursor(s, i, Get(inputs, i));
        }
    }

    private static SimInput Get(ReadOnlySpan<SimInput> inputs, int i) => i < inputs.Length ? inputs[i] : default;

    public void Dispose() => _mover?.Dispose();
}
