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
        switch (def.Kind)
        {
            case PatternKind.Ring:
            {
                int step = FixMath.AngleUnitsPerTurn / Math.Max(1, def.Count);
                int start = e.Angle + volley * def.SpinPerVolley;
                for (int i = 0; i < def.Count; i++)
                    bullets.TrySpawn(e.Pos, FixMath.FromAngle(start + i * step, def.BulletSpeed), half, def.Bullet);
                break;
            }
            case PatternKind.Spiral:
            {
                int step = FixMath.AngleUnitsPerTurn / Math.Max(1, def.Count);
                for (int i = 0; i < def.Count; i++)
                    bullets.TrySpawn(e.Pos, FixMath.FromAngle(e.Angle + i * step, def.BulletSpeed), half, def.Bullet);
                e.Angle += def.SpinPerVolley;
                break;
            }
            case PatternKind.Aimed:
            {
                int aim = AimAngle(s, half, e.Pos);
                int first = aim - (def.Count - 1) * def.Spread / 2;
                for (int i = 0; i < def.Count; i++)
                    bullets.TrySpawn(e.Pos, FixMath.FromAngle(first + i * def.Spread, def.BulletSpeed), half, def.Bullet);
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
                    bullets.TrySpawn(pos, vel, half, def.Bullet);
                }
                break;
            }
        }
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
