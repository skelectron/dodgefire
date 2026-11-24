namespace Crossfire.Sim.Patterns;

public enum PatternKind : byte
{
    Ring,
    Spiral,
    Aimed,
    Wall,
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
    /// <summary>Bullets per volley (ring size, spiral arms, fan size, wall length).</summary>
    public int Count;
    /// <summary>Angle units between fan bullets (Aimed) or spacing in arena units (Wall).</summary>
    public int Spread;
    /// <summary>Angle units added per volley (Spiral rotation, Ring stagger).</summary>
    public int SpinPerVolley;
}
