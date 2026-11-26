using Crossfire.Sim.Rules;

namespace Crossfire.Sim.Patterns;

/// <summary>
/// Crossfire combos: when a Gunner places a pattern close enough to a teammate's pattern that is still
/// telegraphing, the two link. Both telegraphs restart together (so the combo always gets a full warning),
/// both Gunners get a little energy and Overdrive back, and when they fire, a line of bullets appears
/// between them with one gap and splits into two walls moving apart. The renderer draws the exact spawn
/// points (gap included) during the telegraph, using <see cref="Point"/>.
/// </summary>
public static class Combo
{
    public const int MinBullets = 4;
    public const int MaxBullets = 28;

    /// <summary>Teammate emitter (still telegraphing, not linked) that a pattern placed at <paramref name="pos"/> would link with, or -1.</summary>
    public static int FindPartner(SimState s, int gunner, FixVec2 pos)
    {
        ref readonly var r = ref s.Rules;
        if (!r.CombosEnabled || s.Players[gunner].ComboCooldown > 0) return -1;
        int team = s.Players[gunner].Team;
        long maxR = r.ComboRadius.Raw, minR = r.ComboMinDistance.Raw;
        int best = -1;
        long bestD2 = long.MaxValue;
        for (int i = 0; i < SimState.MaxEmitters; i++)
        {
            ref readonly var e = ref s.Emitters[i];
            if (!e.Active || e.Linked || e.Age > r.TelegraphTicks) continue;
            if (e.Owner == gunner || e.Owner >= s.PlayerCount || s.Players[e.Owner].Team != team || s.Players[e.Owner].ComboCooldown > 0) continue;
            long d2 = (e.Pos - pos).LengthSqRaw();
            if (d2 > maxR * maxR || d2 < minR * minR) continue;
            if (d2 < bestD2) { bestD2 = d2; best = i; }
        }
        return best;
    }

    /// <summary>Number of bullets on the line between two linked patterns.</summary>
    public static int Count(in MatchRules r, FixVec2 a, FixVec2 b)
    {
        int len = (b - a).Length().Raw;
        int spacing = Math.Max(1, r.ComboSpacing.Raw);
        return Math.Clamp(len / spacing, MinBullets, MaxBullets);
    }

    /// <summary>First slot of the gap (slots gap..gap+GapSlots-1 stay empty).</summary>
    public static int GapStart(in MatchRules r, int count, byte gap)
    {
        int slots = Math.Clamp(r.ComboGapSlots, 0, count - 1);
        return (count - slots) * gap / 256;
    }

    public static bool IsGap(in MatchRules r, int count, byte gap, int k)
    {
        int g = GapStart(r, count, gap);
        return k >= g && k < g + Math.Clamp(r.ComboGapSlots, 0, count - 1);
    }

    /// <summary>Spawn point <paramref name="k"/> of <paramref name="count"/>, evenly spaced strictly between the two patterns.</summary>
    public static FixVec2 Point(FixVec2 a, FixVec2 b, int k, int count)
    {
        var d = b - a;
        int num = k + 1, den = count + 1;
        return new FixVec2(a.X + new Fix((int)((long)d.X.Raw * num / den)), a.Y + new Fix((int)((long)d.Y.Raw * num / den)));
    }

    /// <summary>Fires the combo volley for a linked pair: each non-gap point spawns two bullets moving apart.</summary>
    public static void Fire(SimState s, in Emitter leader, in Emitter partner)
    {
        ref readonly var r = ref s.Rules;
        var a = partner.Pos;
        var b = leader.Pos;
        var d = b - a;
        // Perpendicular unit direction at the combo speed.
        var perp = new FixVec2(-d.Y, d.X).WithLength(r.ComboBulletSpeed);
        var back = FixVec2.Zero - perp;
        int count = Count(in r, a, b);
        for (int k = 0; k < count; k++)
        {
            if (IsGap(in r, count, leader.ComboGap, k)) continue;
            var p = Point(a, b, k, count);
            s.Bullets.TrySpawn(p, perp, leader.Half, BulletType.Medium, leader.Owner);
            s.Bullets.TrySpawn(p, back, leader.Half, BulletType.Medium, partner.Owner);
        }
    }

}
