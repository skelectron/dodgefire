using Crossfire.Sim.Bullets;
using Crossfire.Sim.Patterns;
using Crossfire.Sim.Players;

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
        // Index of the bullet that touched each target's hitbox (absorbed if the target is flipping).
        Span<int> hitBullet = stackalloc int[SimState.MaxPlayers];
        // Owner of that bullet, captured now: flip blocks below swap-remove bullets and may move it.
        Span<int> hitOwner = stackalloc int[SimState.MaxPlayers];
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
                    hitBullet[t] = i;
                    hitOwner[t] = pool.Owner[i];
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
                ref var gp = ref s.Players[pi];
                gp.Grazes += grazeCount;
                gp.Break = Fix.Min(rules.BreakMax, gp.Break + rules.BreakPerGraze * grazeCount);
                s.PushEvent(SimEventType.Graze, pi, h, gp.Pos, grazeCount);
            }
        }

        // Flip blocks first: each flipping target absorbs the bullet that touched it. Removing by
        // descending index keeps the other recorded indices valid (swap-remove only moves the tail).
        for (int pass = 0; pass < targetCount; pass++)
        {
            int best = -1;
            for (int t = 0; t < targetCount; t++)
                if (hit[t] && s.Players[targets[t]].FlipGuard && (best < 0 || hitBullet[t] > hitBullet[best])) best = t;
            if (best < 0) break;
            ref var p = ref s.Players[targets[best]];
            p.FlipGuard = false;
            hit[best] = false;
            pool.RemoveAt(hitBullet[best]);
            // A teammate touching the very same bullet is spared too: the bullet is gone.
            for (int t = 0; t < targetCount; t++)
                if (hit[t] && hitBullet[t] == hitBullet[best]) hit[t] = false;
            s.PushEvent(SimEventType.FlipBlock, targets[best], p.Team, p.Pos);
        }

        for (int t = 0; t < targetCount; t++)
        {
            if (hit[t]) ApplyHit(s, targets[t], hitOwner[t]);
        }
    }

    /// <summary>Removes every bullet in one half.</summary>
    public static void ClearHalf(SimState s, int half)
    {
        var pool = s.Bullets;
        int i = 0;
        while (i < pool.Count)
        {
            if (pool.Half[i] == half) pool.RemoveAt(i);
            else i++;
        }
    }

    private static void ApplyHit(SimState s, int playerIndex, int owner)
    {
        ref var p = ref s.Players[playerIndex];
        p.Break = Fix.Max(Fix.Zero, p.Break - s.Rules.BreakHitPenalty);
        p.Lives--;
        p.InvulnTicks = s.Rules.InvulnTicks;
        s.PushEvent(p.Lives > 0 ? SimEventType.Hit : SimEventType.Death, playerIndex, p.Team, p.Pos, owner);
        GunnerSystem.CreditKill(s, owner);
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
