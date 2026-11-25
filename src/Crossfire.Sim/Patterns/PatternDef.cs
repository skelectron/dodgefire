using System.Runtime.CompilerServices;
using Crossfire.Sim.Rules;

namespace Crossfire.Sim.Patterns;

/// <summary>Every placeable pattern. The value is also the index into <see cref="MatchRules.Patterns"/>.</summary>
public enum PatternKind : byte
{
    Ring,
    Spiral,
    Aimed,
    Wall,
    /// <summary>A laser-like stream that sweeps across an arc.</summary>
    Sweep,
    /// <summary>A fan of bullets that briefly steer toward the nearest Dodger.</summary>
    Homing,
    /// <summary>Slow mines that burst into a ring after a fuse.</summary>
    Mines,
}

/// <summary>Bullet size classes. Radius lookup lives in <see cref="PatternLibrary.BulletRadius"/>.</summary>
public enum BulletType : byte
{
    Small,
    Medium,
    Large,
}

/// <summary>Data describing a placeable bullet pattern. Shape behaviour lives in <see cref="PatternLibrary"/>.</summary>
public struct PatternDef
{
    public PatternKind Kind;
    public BulletType Bullet;
    public Fix Cost;
    public int CooldownTicks;
    /// <summary>Firing duration after the telegraph ends.</summary>
    public int DurationTicks;
    public int FireInterval;
    public Fix BulletSpeed;
    /// <summary>Bullets per volley (ring size, spiral arms, fan size, wall length, beam width, mines).</summary>
    public int Count;
    /// <summary>Angle units between fan bullets (Aimed, Homing), spacing in arena units (Wall), total arc (Sweep).</summary>
    public int Spread;
    /// <summary>Angle units added per volley (Spiral rotation, Ring stagger, Sweep speed).</summary>
    public int SpinPerVolley;
    /// <summary>Ticks of timed bullet behaviour: homing time (Homing), fuse (Mines).</summary>
    public int TimerTicks;
    /// <summary>Homing: max turn per steering step in angle units. Mines: bullets in the burst ring.</summary>
    public int Extra;
}

/// <summary>The 4 patterns a Gunner brings into a match, in slot order (slot k = pattern button k+1).</summary>
[InlineArray(MatchRules.PatternSlots)]
public struct Loadout
{
    private PatternKind _element0;

    public static Loadout Of(PatternKind a, PatternKind b, PatternKind c, PatternKind d)
    {
        var l = new Loadout();
        l[0] = a;
        l[1] = b;
        l[2] = c;
        l[3] = d;
        return l;
    }

    /// <summary>The original four: slot k = kind k.</summary>
    public static Loadout Classic => Of(PatternKind.Ring, PatternKind.Spiral, PatternKind.Aimed, PatternKind.Wall);
    public static Loadout Hunter => Of(PatternKind.Aimed, PatternKind.Homing, PatternKind.Sweep, PatternKind.Wall);
    public static Loadout Trapper => Of(PatternKind.Ring, PatternKind.Mines, PatternKind.Spiral, PatternKind.Homing);

    /// <summary>Slot holding <paramref name="kind"/>, or -1.</summary>
    public readonly int SlotOf(PatternKind kind)
    {
        for (int k = 0; k < MatchRules.PatternSlots; k++)
            if (this[k] == kind) return k;
        return -1;
    }
}
