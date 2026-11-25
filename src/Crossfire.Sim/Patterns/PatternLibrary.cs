using Crossfire.Sim.Bullets;
using Crossfire.Sim.Rules;

namespace Crossfire.Sim.Patterns;

/// <summary>Default pattern definitions and the code that turns a running emitter into bullets.</summary>
public static class PatternLibrary
{
    /// <summary>Expanding rings, each staggered by half a step so the gaps shift.</summary>
    public static PatternDef Ring => new()
    {
        Kind = PatternKind.Ring,
        Bullet = BulletType.Medium,
        Cost = Fix.FromInt(30),
        CooldownTicks = 90,
        DurationTicks = 61,
        FireInterval = 20,
        BulletSpeed = Fix.Ratio(13, 5),
        Count = 20,
        SpinPerVolley = FixMath.AngleUnitsPerTurn / 40,
    };

    /// <summary>Rotating multi-arm spiral — area denial.</summary>
    public static PatternDef Spiral => new()
    {
        Kind = PatternKind.Spiral,
        Bullet = BulletType.Small,
        Cost = Fix.FromInt(40),
        CooldownTicks = 180,
        DurationTicks = 150,
        FireInterval = 5,
        BulletSpeed = Fix.Ratio(12, 5),
        Count = 3,
        SpinPerVolley = 70,
    };

    /// <summary>Fast fan aimed at the nearest enemy Dodger at each volley's spawn tick.</summary>
    public static PatternDef Aimed => new()
    {
        Kind = PatternKind.Aimed,
        Bullet = BulletType.Small,
        Cost = Fix.FromInt(20),
        CooldownTicks = 60,
        DurationTicks = 37,
        FireInterval = 12,
        BulletSpeed = Fix.FromInt(4),
        Count = 5,
        Spread = 90, // ~8 degrees between bullets
    };

    /// <summary>A slow wall of big bullets with a random gap, pushed toward the nearest Dodger.</summary>
    public static PatternDef Wall => new()
    {
        Kind = PatternKind.Wall,
        Bullet = BulletType.Large,
        Cost = Fix.FromInt(35),
        CooldownTicks = 150,
        DurationTicks = 1,
        FireInterval = 1,
        BulletSpeed = Fix.Ratio(11, 5),
        Count = 15,
        Spread = 30, // spacing in arena units
    };

    /// <summary>A laser-like beam: a tight stream of fast bullets that sweeps across an arc through the nearest Dodger.</summary>
    public static PatternDef Sweep => new()
    {
        Kind = PatternKind.Sweep,
        Bullet = BulletType.Small,
        Cost = Fix.FromInt(35),
        CooldownTicks = 150,
        DurationTicks = 90,
        FireInterval = 2,
        BulletSpeed = Fix.Ratio(11, 2),
        Count = 2,
        Spread = 1150,          // ~100 degrees of arc
        SpinPerVolley = 26,     // 45 volleys x 26 ~ the whole arc
    };

    /// <summary>A fan of bullets that steer toward the nearest Dodger for a short time, then fly straight.</summary>
    public static PatternDef Homing => new()
    {
        Kind = PatternKind.Homing,
        Bullet = BulletType.Medium,
        Cost = Fix.FromInt(30),
        CooldownTicks = 120,
        DurationTicks = 1,
        FireInterval = 1,
        BulletSpeed = Fix.Ratio(5, 2),
        Count = 6,
        Spread = 180,           // ~16 degrees between bullets
        TimerTicks = 90,        // steer for 1.5 s
        Extra = 40,             // max ~3.5 degrees per steering step
    };

    /// <summary>Slow drifting mines that burst into a ring of small bullets after a fuse.</summary>
    public static PatternDef Mines => new()
    {
        Kind = PatternKind.Mines,
        Bullet = BulletType.Large,
        Cost = Fix.FromInt(30),
        CooldownTicks = 150,
        DurationTicks = 1,
        FireInterval = 1,
        BulletSpeed = Fix.Ratio(4, 5),
        Count = 3,
        Spread = 520,           // ~46 degrees between mines
        TimerTicks = 72,        // 1.2 s fuse
        Extra = 10,             // burst ring size
    };

    /// <summary>Default definition for each kind (index = <see cref="PatternKind"/>).</summary>
    public static PatternDef Default(PatternKind kind) => kind switch
    {
        PatternKind.Ring => Ring,
        PatternKind.Spiral => Spiral,
        PatternKind.Aimed => Aimed,
        PatternKind.Wall => Wall,
        PatternKind.Sweep => Sweep,
        PatternKind.Homing => Homing,
        _ => Mines,
    };

    /// <summary>Speed of the small bullets a mine bursts into.</summary>
    public static readonly Fix MineBurstSpeed = Fix.Ratio(5, 2);
    /// <summary>Homing bullets re-aim every this many ticks.</summary>
    public const int HomingStepTicks = 3;

    /// <summary>Collision radius per bullet type (renderer draws slightly larger: forgiving hitboxes).</summary>
    public static Fix BulletRadius(BulletType type) => type switch
    {
        BulletType.Small => Fix.FromInt(3),
        BulletType.Medium => Fix.FromInt(5),
        _ => Fix.FromInt(7),
    };

    /// <summary>Emits one volley for the emitter. Volley index counts from 0.</summary>
    public static void Fire(SimState s, ref Emitter e, in PatternDef def, int volley)
    {
        var bullets = s.Bullets;
        int half = e.Half;
        int owner = e.Owner;
        switch (def.Kind)
        {
            case PatternKind.Ring:
            {
                int step = FixMath.AngleUnitsPerTurn / Math.Max(1, def.Count);
                int start = e.Angle + volley * def.SpinPerVolley;
                for (int i = 0; i < def.Count; i++)
                    bullets.TrySpawn(e.Pos, FixMath.FromAngle(start + i * step, def.BulletSpeed), half, def.Bullet, owner);
                break;
            }
            case PatternKind.Spiral:
            {
                int step = FixMath.AngleUnitsPerTurn / Math.Max(1, def.Count);
                for (int i = 0; i < def.Count; i++)
                    bullets.TrySpawn(e.Pos, FixMath.FromAngle(e.Angle + i * step, def.BulletSpeed), half, def.Bullet, owner);
                e.Angle += def.SpinPerVolley;
                break;
            }
            case PatternKind.Aimed:
            {
                int aim = AimAngle(s, half, e.Pos);
                int first = aim - (def.Count - 1) * def.Spread / 2;
                for (int i = 0; i < def.Count; i++)
                    bullets.TrySpawn(e.Pos, FixMath.FromAngle(first + i * def.Spread, def.BulletSpeed), half, def.Bullet, owner);
                break;
            }
            case PatternKind.Wall:
            {
                int aim = AimAngle(s, half, e.Pos);
                FixVec2 vel = FixMath.FromAngle(aim, def.BulletSpeed);
                FixVec2 along = FixMath.FromAngle(aim + FixMath.AngleUnitsPerTurn / 4, Fix.FromInt(def.Spread));
                int gapAt = 1 + s.Rng.NextInt(Math.Max(1, def.Count - 3));
                int mid = def.Count / 2;
                for (int i = 0; i < def.Count; i++)
                {
                    if (i == gapAt || i == gapAt + 1) continue; // gap two bullets wide
                    var pos = e.Pos + along * Fix.FromInt(i - mid);
                    bullets.TrySpawn(pos, vel, half, def.Bullet, owner);
                }
                break;
            }
            case PatternKind.Sweep:
            {
                // First volley: start at one edge of an arc centred on the nearest Dodger.
                if (volley == 0) e.Angle = AimAngle(s, half, e.Pos) - def.Spread / 2;
                for (int i = 0; i < def.Count; i++)
                    bullets.TrySpawn(e.Pos, FixMath.FromAngle(e.Angle + (i - def.Count / 2) * 12, def.BulletSpeed), half, def.Bullet, owner);
                e.Angle += def.SpinPerVolley;
                break;
            }
            case PatternKind.Homing:
            {
                int aim = AimAngle(s, half, e.Pos);
                int first = aim - (def.Count - 1) * def.Spread / 2;
                for (int i = 0; i < def.Count; i++)
                    bullets.TrySpawn(e.Pos, FixMath.FromAngle(first + i * def.Spread, def.BulletSpeed), half, def.Bullet, owner, BulletFlags.Homing, def.TimerTicks);
                break;
            }
            case PatternKind.Mines:
            {
                int aim = AimAngle(s, half, e.Pos);
                int first = aim - (def.Count - 1) * def.Spread / 2;
                for (int i = 0; i < def.Count; i++)
                    bullets.TrySpawn(e.Pos, FixMath.FromAngle(first + i * def.Spread, def.BulletSpeed), half, def.Bullet, owner, BulletFlags.Mine, def.TimerTicks);
                break;
            }
        }
    }

    /// <summary>
    /// Per-tick behaviour of homing bullets and mines (after movement). Skipped entirely when the pool has none.
    /// Mine bursts are spawned after the scan so swap-removal stays well-defined.
    /// </summary>
    public static unsafe void UpdateSpecialBullets(SimState s)
    {
        var pool = s.Bullets;
        if (pool.SpecialCount == 0) return;
        ref readonly var homing = ref s.Rules.Patterns[(int)PatternKind.Homing];
        ref readonly var mines = ref s.Rules.Patterns[(int)PatternKind.Mines];
        int maxTurn = Math.Max(1, homing.Extra);

        // Bursts to spawn after the scan: position, half, owner (at most 64 per tick; the rest burst next tick).
        Span<FixVec2> burstPos = stackalloc FixVec2[64];
        Span<byte> burstHalf = stackalloc byte[64];
        Span<byte> burstOwner = stackalloc byte[64];
        int bursts = 0;

        int i = 0;
        while (i < pool.Count)
        {
            byte f = pool.Flags[i];
            if ((f & (byte)BulletFlags.Special) == 0) { i++; continue; }

            if ((f & (byte)BulletFlags.Mine) != 0)
            {
                if (pool.Timer[i] > 0) pool.Timer[i]--;
                if (pool.Timer[i] == 0 && bursts < burstPos.Length)
                {
                    burstPos[bursts] = new FixVec2(new Fix(pool.PosX[i]), new Fix(pool.PosY[i]));
                    burstHalf[bursts] = pool.Half[i];
                    burstOwner[bursts] = pool.Owner[i];
                    bursts++;
                    pool.RemoveAt(i); // re-test the bullet swapped into slot i
                    continue;
                }
                i++;
                continue;
            }

            // Homing: re-aim every few ticks, turning at most maxTurn toward the nearest Dodger in its half.
            if (pool.Timer[i] > 0)
            {
                pool.Timer[i]--;
                if (pool.Timer[i] % HomingStepTicks == 0)
                {
                    var pos = new FixVec2(new Fix(pool.PosX[i]), new Fix(pool.PosY[i]));
                    var vel = new FixVec2(new Fix(pool.VelX[i]), new Fix(pool.VelY[i]));
                    if (TryNearestDodger(s, pool.Half[i], pos, out var target, out _))
                    {
                        int cur = FixMath.Atan2(vel.Y, vel.X);
                        var d = target - pos;
                        int want = FixMath.Atan2(d.Y, d.X);
                        int diff = WrapAngle(want - cur);
                        int turn = Math.Clamp(diff, -maxTurn, maxTurn);
                        var nv = FixMath.FromAngle(cur + turn, homing.BulletSpeed);
                        pool.VelX[i] = nv.X.Raw;
                        pool.VelY[i] = nv.Y.Raw;
                    }
                }
            }
            if (pool.Timer[i] == 0)
            {
                pool.Flags[i] = (byte)(f & ~(byte)BulletFlags.Homing); // done steering: fly straight
                pool.SpecialCount--;
            }
            i++;
        }

        int ring = Math.Max(1, mines.Extra);
        int step = FixMath.AngleUnitsPerTurn / ring;
        for (int b = 0; b < bursts; b++)
        {
            int start = s.Rng.NextInt(step);
            for (int k = 0; k < ring; k++)
                pool.TrySpawn(burstPos[b], FixMath.FromAngle(start + k * step, MineBurstSpeed), burstHalf[b], BulletType.Small, burstOwner[b]);
            s.PushEvent(SimEventType.MineBurst, burstOwner[b], 1 - burstHalf[b], burstPos[b]);
        }
    }

    /// <summary>Wraps an angle difference into (-half turn, half turn].</summary>
    public static int WrapAngle(int a)
    {
        int turn = FixMath.AngleUnitsPerTurn;
        a %= turn;
        if (a > turn / 2) a -= turn;
        else if (a <= -turn / 2) a += turn;
        return a;
    }

    /// <summary>
    /// Angle from <paramref name="from"/> to the nearest living Dodger in <paramref name="half"/>.
    /// Falls back to aiming at the half's centre when nobody is alive there.
    /// </summary>
    public static int AimAngle(SimState s, int half, FixVec2 from)
    {
        FixVec2 target;
        if (!TryNearestDodger(s, half, from, out target, out _))
        {
            s.Rules.HalfBounds(half, out Fix minX, out Fix maxX);
            target = new FixVec2((minX + maxX) / 2, Fix.FromInt(s.Rules.ArenaHeight / 2));
        }
        FixVec2 d = target - from;
        return FixMath.Atan2(d.Y, d.X);
    }

    public static bool TryNearestDodger(SimState s, int team, FixVec2 from, out FixVec2 pos, out long distSqRaw)
    {
        pos = default;
        distSqRaw = long.MaxValue;
        bool found = false;
        for (int i = 0; i < s.PlayerCount; i++)
        {
            ref readonly var p = ref s.Players[i];
            if (!p.IsAliveDodger || p.Team != team) continue;
            long d2 = (p.Pos - from).LengthSqRaw();
            if (d2 < distSqRaw)
            {
                distSqRaw = d2;
                pos = p.Pos;
                found = true;
            }
        }
        return found;
    }
}
