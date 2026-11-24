using System.Runtime.CompilerServices;
using Crossfire.Sim.Patterns;

namespace Crossfire.Sim.Rules;

[InlineArray(MatchRules.PatternSlots)]
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
    public const int PatternSlots = 4;
    public const int TicksPerSecond = 60;

    public int ArenaWidth;
    public int ArenaHeight;

    // Dodgers
    public int DodgerLives;
    public Fix DodgerSpeed;
    public Fix DodgerFocusSpeed;
    public Fix HitboxRadius;
    public Fix GrazeRadius;
    public Fix DodgerEdgeMargin;
    public int InvulnTicks;
    public Fix HitClearRadius;

    // Gunners
    public Fix CursorSpeed;
    public Fix EnergyMax;
    public Fix EnergyStart;
    public Fix EnergyRegenPerTick;
    public Fix AntiSpawnRadius;
    public int TelegraphTicks;
    public PatternDefArray Patterns;

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
                HitboxRadius = Fix.Ratio(5, 2),
                GrazeRadius = Fix.FromInt(18),
                DodgerEdgeMargin = Fix.FromInt(10),
                InvulnTicks = 2 * TicksPerSecond,
                HitClearRadius = Fix.FromInt(140),

                CursorSpeed = Fix.FromInt(9),
                EnergyMax = Fix.FromInt(100),
                EnergyStart = Fix.FromInt(50),
                EnergyRegenPerTick = Fix.Ratio(12, TicksPerSecond), // 12 energy per second
                AntiSpawnRadius = Fix.FromInt(130),
                TelegraphTicks = 27, // ~0.45 s

                RoundsToWin = 2,
                CountdownTicks = 2 * TicksPerSecond,
                RoundOverTicks = 5 * TicksPerSecond / 2,
                SurvivalTicks = 40 * TicksPerSecond,
                SuddenDeathTicks = 60 * TicksPerSecond,
                SuddenDeathRegenMultiplier = 3,
                SwapRolesEachRound = true,
            };
            r.Patterns[0] = PatternLibrary.Ring;
            r.Patterns[1] = PatternLibrary.Spiral;
            r.Patterns[2] = PatternLibrary.Aimed;
            r.Patterns[3] = PatternLibrary.Wall;
            return r;
        }
    }

    public readonly Fix HalfWidth => Fix.FromInt(ArenaWidth / 2);

    /// <summary>Team 0 owns the left half, team 1 the right half.</summary>
    public readonly void HalfBounds(int team, out Fix minX, out Fix maxX)
    {
        minX = team == 0 ? Fix.Zero : HalfWidth;
        maxX = team == 0 ? HalfWidth : Fix.FromInt(ArenaWidth);
    }
}
