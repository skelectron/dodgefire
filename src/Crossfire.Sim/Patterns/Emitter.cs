namespace Crossfire.Sim.Patterns;

/// <summary>A placed, running pattern instance. Fixed-size slot in <see cref="SimState.Emitters"/>.</summary>
public struct Emitter
{
    public bool Active;
    public byte PatternSlot;
    public byte Owner;
    /// <summary>The team whose half this emitter is in (the team its bullets can hit).</summary>
    public byte Half;
    public FixVec2 Pos;
    /// <summary>Ticks since placement; the telegraph runs for the first TelegraphTicks.</summary>
    public int Age;
    public int Angle;
}
