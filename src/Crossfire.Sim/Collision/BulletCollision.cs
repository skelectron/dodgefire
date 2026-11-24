using Crossfire.Sim.Bullets;
using Crossfire.Sim.Patterns;

namespace Crossfire.Sim.Collision;

/// <summary>
/// Enemy bullets vs Dodgers, plus graze detection.
///
/// Note on broad-phase: every bullet already belongs to exactly one half, and only that half's
/// Dodgers (at most 4) can be hit, so the cost is one cheap axis-reject per bullet per Dodger.
/// A uniform grid would add a rebuild pass over all bullets every tick for no gain at these
/// target counts; benchmarks (tests/Crossfire.Benchmarks) keep this honest.
/// </summary>
public static unsafe class BulletCollision
{
    public static void Run(SimState s)
    {
        ref readonly var rules = ref s.Rules;
        var pool = s.Bullets;

        // Vulnerable (alive, not invulnerable) Dodgers, grouped by half.
        Span<int> targets = stackalloc int[SimState.MaxPlayers];
        Span<int> targetHalf = stackalloc int[SimState.MaxPlayers];
        Span<bool> hit = stackalloc bool[SimState.MaxPlayers];
        int targetCount = 0;
        for (int i = 0; i < s.PlayerCount; i++)
        {
            ref readonly var p = ref s.Players[i];
            if (p.IsAliveDodger && p.InvulnTicks == 0)
            {
                targets[targetCount] = i;
                targetHalf[targetCount] = p.Team;
                targetCount++;
            }
        }
        if (targetCount == 0 || pool.Count == 0) return;

        Span<int> radiusRaw = [PatternLibrary.BulletRadius(BulletType.Small).Raw, PatternLibrary.BulletRadius(BulletType.Medium).Raw, PatternLibrary.BulletRadius(BulletType.Large).Raw];
        int hitR = rules.HitboxRadius.Raw;
        int grazeR = rules.GrazeRadius.Raw;

        int* px = pool.PosX, py = pool.PosY;
        byte* half = pool.Half, type = pool.Type, flags = pool.Flags;
        int count = pool.Count;

        for (int t = 0; t < targetCount; t++)
        {
            int pi = targets[t];
            int h = targetHalf[t];
            int dxRaw0 = s.Players[pi].Pos.X.Raw, dyRaw0 = s.Players[pi].Pos.Y.Raw;
            int grazeCount = 0;

            for (int i = 0; i < count; i++)
            {
                if (half[i] != h) continue;
                int br = radiusRaw[type[i]];
                int reach = grazeR + br;
                int dx = px[i] - dxRaw0;
                if (dx > reach || dx < -reach) continue;
                int dy = py[i] - dyRaw0;
                if (dy > reach || dy < -reach) continue;

                long d2 = (long)dx * dx + (long)dy * dy;
                long hitDist = hitR + br;
                if (d2 <= hitDist * hitDist)
                {
                    hit[t] = true;
                    break;
                }
                if ((flags[i] & (byte)BulletFlags.Grazed) == 0 && d2 <= (long)reach * reach)
                {
                    flags[i] |= (byte)BulletFlags.Grazed;
                    grazeCount++;
                }
            }

            if (grazeCount > 0)
            {
                s.Players[pi].Grazes += grazeCount;
                s.PushEvent(SimEventType.Graze, pi, h, s.Players[pi].Pos);
            }
        }

        for (int t = 0; t < targetCount; t++)
        {
            if (hit[t]) ApplyHit(s, targets[t]);
        }
    }

    private static void ApplyHit(SimState s, int playerIndex)
    {
        ref var p = ref s.Players[playerIndex];
        p.Lives--;
        p.InvulnTicks = s.Rules.InvulnTicks;
        s.PushEvent(p.Lives > 0 ? SimEventType.Hit : SimEventType.Death, playerIndex, p.Team, p.Pos);
        ClearBulletsNear(s, p.Team, p.Pos, s.Rules.HitClearRadius);
    }

    /// <summary>Removes bullets of one half within <paramref name="radius"/> of <paramref name="center"/>.</summary>
    public static void ClearBulletsNear(SimState s, int half, FixVec2 center, Fix radius)
    {
        var pool = s.Bullets;
        long r2 = (long)radius.Raw * radius.Raw;
        int i = 0;
        while (i < pool.Count)
        {
            long dx = pool.PosX[i] - center.X.Raw;
            long dy = pool.PosY[i] - center.Y.Raw;
            if (pool.Half[i] == half && dx * dx + dy * dy <= r2) pool.RemoveAt(i);
            else i++;
        }
    }
}
