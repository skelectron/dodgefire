using Crossfire.Sim.Bullets;
using Crossfire.Sim.Patterns;
using Crossfire.Sim.Players;
using Crossfire.Sim.Rules;

namespace Crossfire.Sim.Bots;

public enum BotDifficulty : byte
{
    Easy,
    Normal,
    Hard,
}

/// <summary>Per-difficulty knobs. Bots are deliberately imperfect: slower reactions, noise, shorter lookahead.</summary>
public readonly record struct BotTuning(
    int HorizonTicks,
    int ThinkEvery,
    int NoiseScore,
    bool UseFocus,
    int GunnerThinkEvery,
    int MinEnergy,
    bool AllPatterns,
    bool Combos,
    int ScanRadius,
    int Padding,
    int LapsePercent,
    int LapseTicks)
{
    public static BotTuning For(BotDifficulty d) => d switch
    {
        BotDifficulty.Easy => new(HorizonTicks: 6, ThinkEvery: 10, NoiseScore: 40_000, UseFocus: false, GunnerThinkEvery: 6, MinEnergy: 80, AllPatterns: false, Combos: false,
            ScanRadius: 80, Padding: 0, LapsePercent: 22, LapseTicks: 24),
        BotDifficulty.Normal => new(12, 4, 6_000, true, 3, 0, true, false, ScanRadius: 140, Padding: 2, LapsePercent: 8, LapseTicks: 14),
        _ => new(24, 1, 0, true, 1, 0, true, true, ScanRadius: 240, Padding: 6, LapsePercent: 0, LapseTicks: 0),
    };
}

/// <summary>
/// Per-slot bot memory. Lives outside <see cref="SimState"/> (like a human's brain), but it is a
/// deterministic function of the states it has seen plus its own seeded RNG, so a bot's inputs
/// are reproducible — and they are ordinary <see cref="SimInput"/>s, so netcode is unaffected.
/// </summary>
public struct BotBrain
{
    public BotDifficulty Difficulty;
    public SimRng Rng;

    // Dodger
    public int ThinkCooldown;
    public sbyte HeldX, HeldY;
    public bool HeldFocus;

    // Gunner
    public FixVec2 TargetOffset;
    public bool HasTarget;
    public int GunnerCooldown;
    public int ComboSlot;
    public int ComboDelay;
    public InputButtons LastButtons;

    // Match flow
    public int MatchOverTicks;

    public static BotBrain Create(BotDifficulty difficulty, uint seed) => new()
    {
        Difficulty = difficulty,
        Rng = new SimRng(seed),
        ComboSlot = -1,
    };
}

/// <summary>Produces a <see cref="SimInput"/> for a bot-controlled slot, whatever role it currently has.</summary>
public static class BotController
{
    public static SimInput Think(SimState s, int slot, ref BotBrain brain)
    {
        ref readonly var p = ref s.Players[slot];
        if (!p.Active) return default;

        if (s.Phase == MatchPhase.MatchOver)
        {
            // Bot-only matches restart by themselves after a short pause; with humans this never wins the race.
            brain.MatchOverTicks++;
            return brain.MatchOverTicks > 4 * MatchRules.TicksPerSecond && brain.MatchOverTicks % 2 == 0
                ? new SimInput(0, 0, InputButtons.Start)
                : default;
        }
        brain.MatchOverTicks = 0;

        if (s.Phase == MatchPhase.RoundOver)
        {
            brain.HasTarget = false;
            brain.ComboSlot = -1;
            brain.LastButtons = InputButtons.None;
            return default;
        }

        var tuning = BotTuning.For(brain.Difficulty);
        return p.Role == PlayerRole.Dodger
            ? DodgerBot.Think(s, slot, ref brain, tuning)
            : GunnerBot.Think(s, slot, ref brain, tuning);
    }
}

/// <summary>
/// Dodging by short-horizon search: try 9 directions (x2 with focus), predict own position and nearby
/// bullets linearly at a few future ticks, score danger + walls + centre pull, keep the best.
/// Integer arithmetic in arena units throughout (deterministic).
/// </summary>
internal static class DodgerBot
{

    private const int MaxBullets = 512;
    private const int Diag = 46341; // 1/sqrt(2) in Q16.16

    public static unsafe SimInput Think(SimState s, int slot, ref BotBrain brain, BotTuning t)
    {
        ref readonly var p = ref s.Players[slot];
        if (p.Lives <= 0) return default;
        if (brain.ThinkCooldown > 0)
        {
            brain.ThinkCooldown--;
            return Held(in brain);
        }
        brain.ThinkCooldown = t.ThinkEvery - 1;
        // Reaction lapse: sometimes keep doing whatever we were doing (makes weaker bots beatable).
        if (t.LapsePercent > 0 && brain.Rng.NextInt(100) < t.LapsePercent)
        {
            brain.ThinkCooldown = t.LapseTicks;
            return Held(in brain);
        }
        int scan = t.ScanRadius;

        ref readonly var r = ref s.Rules;
        int px = p.Pos.X.Raw >> 16, py = p.Pos.Y.Raw >> 16;
        r.HalfBounds(p.Team, out Fix minXf, out Fix maxXf);
        int margin = r.DodgerEdgeMargin.Raw >> 16;
        int minX = (minXf.Raw >> 16) + margin, maxX = (maxXf.Raw >> 16) - margin;
        int minY = margin, maxY = r.ArenaHeight - margin;
        int hit = (r.HitboxRadius.Raw >> 16) + 1;

        // Gather nearby bullets of our half (positions/velocities in 1/256 arena units for sub-unit velocity precision).
        BulletPool pool = s.Bullets;
        int* bx = stackalloc int[MaxBullets];
        int* by = stackalloc int[MaxBullets];
        int* bvx = stackalloc int[MaxBullets];
        int* bvy = stackalloc int[MaxBullets];
        int* brad = stackalloc int[MaxBullets];
        int n = 0, crowded = 0;
        for (int i = 0; i < pool.Count && n < MaxBullets; i++)
        {
            if (pool.Half[i] != p.Team) continue;
            int x = pool.PosX[i] >> 16, y = pool.PosY[i] >> 16;
            int dx = x - px, dy = y - py;
            if (dx > scan || dx < -scan || dy > scan || dy < -scan) continue;
            bx[n] = pool.PosX[i] >> 8;
            by[n] = pool.PosY[i] >> 8;
            bvx[n] = pool.VelX[i] >> 8;
            bvy[n] = pool.VelY[i] >> 8;
            brad[n] = PatternLibrary.BulletRadius((BulletType)pool.Type[i]).Raw >> 16;
            if (dx * dx + dy * dy < 80 * 80) crowded++;
            n++;
        }

        bool allowFocus = t.UseFocus && crowded >= 5;
        int horizon = Math.Max(2, t.HorizonTicks);
        int s1 = Math.Max(2, horizon / 4), s2 = horizon / 2, s3 = horizon;

        long bestScore = long.MaxValue;
        int bestX = 0, bestY = 0;
        bool bestFocus = false;
        int heldDx = Math.Sign(brain.HeldX), heldDy = Math.Sign(brain.HeldY);
        int cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;

        for (int focus = 0; focus <= (allowFocus ? 1 : 0); focus++)
        {
            int speed = (focus == 1 ? r.DodgerFocusSpeed : r.DodgerSpeed).Raw >> 8; // 1/256 units per tick
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int vx = dx * speed, vy = dy * speed;
                if (dx != 0 && dy != 0)
                {
                    vx = (int)((long)vx * Diag >> 16);
                    vy = (int)((long)vy * Diag >> 16);
                }

                long score = 0;
                // Evaluate a few sample times; nearer samples weigh more.
                for (int k = 0; k < 3; k++)
                {
                    int ticks = k == 0 ? s1 : k == 1 ? s2 : s3;
                    int weight = k == 0 ? 4 : k == 1 ? 2 : 1;
                    int qx = Math.Clamp(px + (vx * ticks >> 8), minX, maxX);
                    int qy = Math.Clamp(py + (vy * ticks >> 8), minY, maxY);
                    for (int i = 0; i < n; i++)
                    {
                        int ex = ((bx[i] + bvx[i] * ticks) >> 8) - qx;
                        int ey = ((by[i] + bvy[i] * ticks) >> 8) - qy;
                        int danger = hit + brad[i] + t.Padding;
                        int soft = danger + 34;
                        if (ex > soft || ex < -soft || ey > soft || ey < -soft) continue;
                        int d2 = ex * ex + ey * ey;
                        if (d2 < danger * danger) score += 1_000_000L * weight;
                        else if (d2 < soft * soft) score += (long)(soft * soft - d2) * weight;
                    }
                    if (k == 2)
                    {
                        // Walls and corners trap you: penalise ending near them.
                        int wall = Math.Min(Math.Min(qx - minX, maxX - qx), Math.Min(qy - minY, maxY - qy));
                        if (wall < 50) score += (50 - wall) * (50 - wall) * 6;
                        // Gentle pull toward the centre of our half.
                        score += (Math.Abs(qx - cx) + Math.Abs(qy - cy)) * 3;
                    }
                }
                if (dx == heldDx && dy == heldDy) score -= 150; // stickiness: avoid jitter
                if (focus == 1) score += 50;                    // prefer full speed unless focus clearly helps
                if (t.NoiseScore > 0) score += brain.Rng.NextInt(t.NoiseScore);

                if (score < bestScore)
                {
                    bestScore = score;
                    bestX = dx;
                    bestY = dy;
                    bestFocus = focus == 1;
                }
            }
        }

        brain.HeldX = (sbyte)(bestX * SimInput.AxisMax);
        brain.HeldY = (sbyte)(bestY * SimInput.AxisMax);
        brain.HeldFocus = bestFocus;
        return Held(in brain);
    }

    private static SimInput Held(in BotBrain b) =>
        new(b.HeldX, b.HeldY, b.HeldFocus ? InputButtons.Focus : InputButtons.None);
}

/// <summary>
/// Gunner: orbit a placement point just outside the anti-spawn radius of the nearest enemy Dodger,
/// steer the cursor there, and fire a pattern chosen from the situation once it is valid to place.
/// </summary>
internal static class GunnerBot
{
    public static SimInput Think(SimState s, int slot, ref BotBrain brain, BotTuning t)
    {
        ref readonly var p = ref s.Players[slot];
        ref readonly var r = ref s.Rules;
        int enemy = GunnerSystem.EnemyTeam(p.Team);
        r.HalfBounds(enemy, out Fix minX, out Fix maxX);
        Fix h = Fix.FromInt(r.ArenaHeight);

        if (!PatternLibrary.TryNearestDodger(s, enemy, p.Cursor, out FixVec2 dodger, out _))
        {
            brain.LastButtons = InputButtons.None;
            return Steer(p.Cursor, new FixVec2((minX + maxX) / 2, h / 2), InputButtons.None);
        }

        if (!brain.HasTarget) PickTarget(s, ref brain, dodger, minX, maxX, h);
        var goal = ClampToHalf(dodger + brain.TargetOffset, minX, maxX, h, Fix.FromInt(24));
        // If the Dodger dragged the goal into the anti-spawn zone (clamped against a wall), pick another side.
        long zone = (long)(r.AntiSpawnRadius.Raw + Fix.FromInt(12).Raw);
        if ((goal - dodger).LengthSqRaw() < zone * zone) PickTarget(s, ref brain, dodger, minX, maxX, h);

        var buttons = InputButtons.None;
        bool canAct = s.Phase == MatchPhase.Playing;
        long arriveTol = Fix.FromInt(14).Raw;
        bool arrived = (goal - p.Cursor).LengthSqRaw() < arriveTol * arriveTol;

        if (brain.GunnerCooldown > 0) brain.GunnerCooldown--;
        if (brain.ComboDelay > 0) brain.ComboDelay--;

        if (canAct && brain.GunnerCooldown == 0)
        {
            int slotToFire = -1;
            if (brain.ComboSlot >= 0 && brain.ComboDelay == 0)
            {
                slotToFire = brain.ComboSlot;
                brain.ComboSlot = -1;
            }
            else if (arrived)
            {
                slotToFire = ChoosePattern(s, slot, ref brain, t, dodger, minX, maxX, h);
            }

            if (slotToFire >= 0 && GunnerSystem.CanPlace(s, slot, slotToFire) == GunnerSystem.PlaceResult.Ok)
            {
                var button = (InputButtons)((int)InputButtons.Pattern1 << slotToFire);
                // Placement is edge-triggered: release for a tick if this button is still held.
                if ((brain.LastButtons & button) == 0)
                {
                    buttons |= button;
                    brain.GunnerCooldown = t.GunnerThinkEvery;
                    if (t.Combos && r.Patterns[slotToFire].Kind == PatternKind.Ring && SlotOf(in r, PatternKind.Aimed) is int aimed and >= 0)
                    {
                        brain.ComboSlot = aimed;
                        brain.ComboDelay = 12;
                    }
                    else
                    {
                        brain.HasTarget = false; // move somewhere new after firing
                    }
                }
            }
        }

        brain.LastButtons = buttons;
        return Steer(p.Cursor, goal, buttons);
    }

    private static int ChoosePattern(SimState s, int slot, ref BotBrain brain, BotTuning t, FixVec2 dodger, Fix minX, Fix maxX, Fix h)
    {
        ref readonly var p = ref s.Players[slot];
        ref readonly var r = ref s.Rules;
        if (p.Energy < Fix.FromInt(t.MinEnergy)) return -1;

        int ring = SlotOf(in r, PatternKind.Ring), spiral = SlotOf(in r, PatternKind.Spiral);
        int aimed = SlotOf(in r, PatternKind.Aimed), wall = SlotOf(in r, PatternKind.Wall);
        Fix edge = Fix.FromInt(110);
        bool nearEdge = dodger.X - minX < edge || maxX - dodger.X < edge || dodger.Y < edge || h - dodger.Y < edge;

        Span<int> prefs = stackalloc int[4];
        int count = 0;
        if (!t.AllPatterns)
        {
            prefs[count++] = brain.Rng.NextInt(2) == 0 ? ring : aimed;
            prefs[count++] = aimed;
        }
        else
        {
            if (nearEdge) prefs[count++] = wall;
            if (p.Energy < Fix.FromInt(40)) prefs[count++] = aimed;
            int roll = brain.Rng.NextInt(3);
            prefs[count++] = roll == 0 ? ring : roll == 1 ? spiral : aimed;
            prefs[count++] = aimed;
        }
        foreach (int k in prefs[..count])
            if (k >= 0 && GunnerSystem.CanPlace(s, slot, k) == GunnerSystem.PlaceResult.Ok) return k;
        return -1;
    }

    private static void PickTarget(SimState s, ref BotBrain brain, FixVec2 dodger, Fix minX, Fix maxX, Fix h)
    {
        // Candidate points on a circle just outside the anti-spawn zone; take a random valid one.
        Fix dist = s.Rules.AntiSpawnRadius + Fix.FromInt(36);
        Fix pad = Fix.FromInt(24);
        Span<int> valid = stackalloc int[8];
        int n = 0;
        for (int i = 0; i < 8; i++)
        {
            var q = dodger + FixMath.FromAngle(i * 512, dist);
            if (q.X > minX + pad && q.X < maxX - pad && q.Y > pad && q.Y < h - pad) valid[n++] = i;
        }
        int pick = n > 0 ? valid[brain.Rng.NextInt(n)] : brain.Rng.NextInt(8);
        brain.TargetOffset = FixMath.FromAngle(pick * 512 + brain.Rng.NextInt(200) - 100, dist);
        brain.HasTarget = true;
    }

    private static int SlotOf(in MatchRules r, PatternKind kind)
    {
        for (int k = 0; k < MatchRules.PatternSlots; k++)
            if (r.Patterns[k].Kind == kind) return k;
        return -1;
    }

    private static FixVec2 ClampToHalf(FixVec2 v, Fix minX, Fix maxX, Fix h, Fix pad) =>
        new(Fix.Clamp(v.X, minX + pad, maxX - pad), Fix.Clamp(v.Y, pad, h - pad));

    /// <summary>Proportional steering: full deflection when far, gentle when close (avoids overshoot).</summary>
    private static SimInput Steer(FixVec2 from, FixVec2 to, InputButtons buttons)
    {
        var d = to - from;
        const int FullAt = 36; // arena units
        int ax = Math.Clamp((int)((long)d.X.Raw * SimInput.AxisMax / Fix.FromInt(FullAt).Raw), -SimInput.AxisMax, SimInput.AxisMax);
        int ay = Math.Clamp((int)((long)d.Y.Raw * SimInput.AxisMax / Fix.FromInt(FullAt).Raw), -SimInput.AxisMax, SimInput.AxisMax);
        return new SimInput((sbyte)ax, (sbyte)ay, buttons);
    }
}
