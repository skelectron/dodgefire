using System.Runtime.CompilerServices;
using Crossfire.Sim.Patterns;

namespace Crossfire.Sim.Rules;

[InlineArray(MatchRules.PatternKinds)]
public struct PatternDefArray
{
    private PatternDef _element0;
}

/// <summary>
/// All tunable gameplay numbers. Stored inside <see cref="SimState"/> (so they are part of the
/// deterministic state and the hash) and editable live from the ImGui tuning panel.
/// Distances are arena units (arena is 1280x720), times are ticks (60 per second).
/// </summary>
public struct MatchRules
{
    /// <summary>Patterns a Gunner brings into a match (pattern buttons 1-4).</summary>
    public const int PatternSlots = 4;
    /// <summary>Patterns that exist (<see cref="PatternKind"/> values); a loadout picks 4 of them.</summary>
    public const int PatternKinds = 7;
    public const int TicksPerSecond = 60;

    public int ArenaWidth;
    public int ArenaHeight;

    // Dodgers
    public int DodgerLives;
    public Fix DodgerSpeed;
    public Fix DodgerFocusSpeed;
    /// <summary>
    /// Max Dodger speed under pointer (mouse) control. Must stay below 2 x (hitbox + smallest bullet radius) = 11,
    /// or a fast flick could step clean over a bullet between ticks.
    /// </summary>
    public Fix DodgerPointerSpeed;
    public Fix HitboxRadius;
    public Fix GrazeRadius;
    public Fix DodgerEdgeMargin;
    public int InvulnTicks;
    public Fix HitClearRadius;
    public int FlipTicks;
    public int FlipCooldownTicks;
    public Fix FlipSpeedMultiplier;
    public Fix BreakMax;
    public Fix BreakPerGraze;
    public Fix BreakHitPenalty;
    public int BreakInvulnTicks;

    // Gunners
    public Fix CursorSpeed;
    /// <summary>Max Gunner cursor speed under pointer control (high: the reticle sticks to the mouse).</summary>
    public Fix CursorPointerSpeed;
    public Fix EnergyMax;
    public Fix EnergyStart;
    public Fix EnergyRegenPerTick;
    public Fix AntiSpawnRadius;
    public int TelegraphTicks;
    /// <summary>Definition of every pattern, indexed by <see cref="PatternKind"/>.</summary>
    public PatternDefArray Patterns;
    /// <summary>Overdrive charge needed to activate.</summary>
    public Fix OverdriveMax;
    /// <summary>Overdrive charge gained per point of energy spent on patterns.</summary>
    public Fix OverdrivePerEnergy;
    /// <summary>Overdrive charge gained when one of your bullets takes a life.</summary>
    public Fix OverdrivePerKill;
    /// <summary>How long Overdrive lasts.</summary>
    public int OverdriveTicks;
    /// <summary>During Overdrive, cooldowns tick this many times per tick and energy regen is multiplied by <see cref="OverdriveRegenMultiplier"/>.</summary>
    public int OverdriveCooldownRate;
    public int OverdriveRegenMultiplier;

    // Crossfire combos (team play): a pattern placed near a teammate's telegraphing pattern links with it.
    public bool CombosEnabled;
    /// <summary>Max distance between two teammates' patterns that can link.</summary>
    public Fix ComboRadius;
    /// <summary>Min distance (closer patterns would make a degenerate line).</summary>
    public Fix ComboMinDistance;
    /// <summary>Spacing of the combo volley's bullets along the line between the two patterns.</summary>
    public Fix ComboSpacing;
    /// <summary>Bullets left out of the line (the escape gap), at a random spot shown by the telegraph.</summary>
    public int ComboGapSlots;
    /// <summary>Speed of the two walls that split off the line, one to each side.</summary>
    public Fix ComboBulletSpeed;
    /// <summary>Energy refunded to both Gunners and Overdrive charge gained by both when a combo links.</summary>
    public Fix ComboRefund;
    public Fix ComboOverdrive;
    /// <summary>After linking, neither Gunner can link again for this long (keeps combos special and readable).</summary>
    public int ComboCooldownTicks;

    // Round flow
    public int RoundsToWin;
    public int CountdownTicks;
    public int RoundOverTicks;
    /// <summary>Asymmetric rounds (one team has no Dodger, e.g. 1v1): the Dodger wins by surviving this long.</summary>
    public int SurvivalTicks;
    /// <summary>Symmetric rounds: after this many ticks, energy regen is multiplied (sudden-death pressure).</summary>
    public int SuddenDeathTicks;
    public int SuddenDeathRegenMultiplier;
    public bool SwapRolesEachRound;

    public static MatchRules Default
    {
        get
        {
            var r = new MatchRules
            {
                ArenaWidth = 1280,
                ArenaHeight = 720,

                DodgerLives = 3,
                DodgerSpeed = Fix.Ratio(9, 2),
                DodgerFocusSpeed = Fix.Ratio(9, 5),
                DodgerPointerSpeed = Fix.FromInt(10),
                HitboxRadius = Fix.Ratio(5, 2),
                GrazeRadius = Fix.FromInt(18),
                DodgerEdgeMargin = Fix.FromInt(10),
                InvulnTicks = 2 * TicksPerSecond,
                HitClearRadius = Fix.FromInt(140),
                FlipTicks = 24,
                FlipCooldownTicks = 150,
                FlipSpeedMultiplier = Fix.Ratio(3, 2),
                BreakMax = Fix.FromInt(100),
                BreakPerGraze = Fix.FromInt(5),
                BreakHitPenalty = Fix.FromInt(25),
                BreakInvulnTicks = 45,

                CursorSpeed = Fix.FromInt(9),
                CursorPointerSpeed = Fix.FromInt(80),
                EnergyMax = Fix.FromInt(100),
                EnergyStart = Fix.FromInt(50),
                EnergyRegenPerTick = Fix.Ratio(12, TicksPerSecond), // 12 energy per second
                AntiSpawnRadius = Fix.FromInt(130),
                TelegraphTicks = 27, // ~0.45 s
                OverdriveMax = Fix.FromInt(100),
                OverdrivePerEnergy = Fix.Ratio(1, 3),
                OverdrivePerKill = Fix.FromInt(20),
                OverdriveTicks = 5 * TicksPerSecond,
                OverdriveCooldownRate = 4,
                OverdriveRegenMultiplier = 2,
                CombosEnabled = true,
                ComboRadius = Fix.FromInt(380),
                ComboMinDistance = Fix.FromInt(120),
                ComboSpacing = Fix.FromInt(24),
                ComboGapSlots = 3,
                ComboBulletSpeed = Fix.Ratio(5, 2),
                ComboRefund = Fix.FromInt(10),
                ComboOverdrive = Fix.FromInt(10),
                ComboCooldownTicks = 4 * TicksPerSecond,

                RoundsToWin = 2,
                CountdownTicks = 2 * TicksPerSecond,
                RoundOverTicks = 5 * TicksPerSecond / 2,
                SurvivalTicks = 40 * TicksPerSecond,
                SuddenDeathTicks = 60 * TicksPerSecond,
                SuddenDeathRegenMultiplier = 3,
                SwapRolesEachRound = true,
            };
            for (int k = 0; k < PatternKinds; k++) r.Patterns[k] = PatternLibrary.Default((PatternKind)k);
            return r;
        }
    }

    /// <summary>The definition behind a player's loadout slot.</summary>
    [System.Diagnostics.CodeAnalysis.UnscopedRef]
    public readonly ref readonly PatternDef DefFor(in Players.Player p, int slot) => ref Patterns[(int)p.Loadout[slot]];

    public readonly Fix HalfWidth => Fix.FromInt(ArenaWidth / 2);

    /// <summary>Team 0 owns the left half, team 1 the right half.</summary>
    public readonly void HalfBounds(int team, out Fix minX, out Fix maxX)
    {
        minX = team == 0 ? Fix.Zero : HalfWidth;
        maxX = team == 0 ? HalfWidth : Fix.FromInt(ArenaWidth);
    }
}
