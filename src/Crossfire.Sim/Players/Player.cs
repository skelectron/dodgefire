using System.Runtime.CompilerServices;
using Crossfire.Sim.Patterns;
using Crossfire.Sim.Rules;

namespace Crossfire.Sim.Players;

public enum PlayerRole : byte
{
    Dodger,
    Gunner,
}

[InlineArray(MatchRules.PatternSlots)]
public struct CooldownArray
{
    private int _element0;
}

/// <summary>
/// One player slot. Holds both Dodger and Gunner fields so a role swap is just a flag flip.
/// Dodgers move inside their own team's half; Gunners move a cursor inside the enemy half.
/// </summary>
public struct Player
{
    public bool Active;
    public byte Team;
    public PlayerRole Role;
    /// <summary>Role at match start; restored on rematch.</summary>
    public PlayerRole StartRole;
    public InputButtons PrevButtons;

    // Dodger
    public FixVec2 Pos;
    public FixVec2 PrevPos;
    public int Lives;
    public int InvulnTicks;
    public bool Focus;
    public int Grazes;
    /// <summary>Ticks left in the current flip (0 = not flipping).</summary>
    public int FlipTicks;
    /// <summary>Ticks until the next flip is allowed.</summary>
    public int FlipCooldown;
    /// <summary>True while the flip can still absorb one bullet.</summary>
    public bool FlipGuard;
    /// <summary>Break meter, filled by grazing; full = can clear the half.</summary>
    public Fix Break;

    // Gunner
    public FixVec2 Cursor;
    public FixVec2 PrevCursor;
    public Fix Energy;
    public CooldownArray Cooldowns;
    /// <summary>The 4 patterns on this player's buttons (used whenever they are the Gunner).</summary>
    public Loadout Loadout;
    /// <summary>Overdrive charge (full = can activate).</summary>
    public Fix Overdrive;
    /// <summary>Ticks of Overdrive left (0 = inactive).</summary>
    public int OverdriveTicks;
    /// <summary>Ticks until this Gunner can link another crossfire combo.</summary>
    public int ComboCooldown;
    /// <summary>Tick of the last rejected placement (UI feedback), -1 if none.</summary>
    public int LastRejectTick;

    public readonly bool IsDodger => Active && Role == PlayerRole.Dodger;
    public readonly bool IsGunner => Active && Role == PlayerRole.Gunner;
    public readonly bool IsAliveDodger => IsDodger && Lives > 0;
}
