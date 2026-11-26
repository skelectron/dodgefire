namespace Crossfire.Sim.Patterns;

/// <summary>A placed, running pattern instance. Fixed-size slot in <see cref="SimState.Emitters"/>.</summary>
public struct Emitter
{
    public bool Active;
    /// <summary>Which pattern this is (index into <see cref="Rules.MatchRules.Patterns"/>).</summary>
    public PatternKind Kind;
    public byte Owner;
    /// <summary>The team whose half this emitter is in (the team its bullets can hit).</summary>
    public byte Half;
    public FixVec2 Pos;
    /// <summary>Ticks since placement; the telegraph runs for the first TelegraphTicks.</summary>
    public int Age;
    public int Angle;
    /// <summary>Crossfire combo partner: emitter index + 1 (0 = not linked). Each emitter links at most once.</summary>
    public byte ComboPartner;
    /// <summary>The second pattern of a combo fires the combo volley (exactly one per pair).</summary>
    public bool ComboLeader;
    /// <summary>Where the escape gap sits along the combo line (0..255 of the free range), picked when linking.</summary>
    public byte ComboGap;

    public readonly bool Linked => ComboPartner != 0;
}
