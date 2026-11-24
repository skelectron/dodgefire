using Crossfire.Sim.Patterns;
using Crossfire.Sim.Rules;

namespace Crossfire.Sim.Players;

/// <summary>Movement input helper shared by Dodgers and Gunner cursors.</summary>
internal static class MoveInput
{
    private static readonly Fix AxisMax = Fix.FromInt(SimInput.AxisMax);

    /// <summary>Converts input axes into a velocity of at most <paramref name="speed"/> (diagonals normalised).</summary>
    public static FixVec2 Velocity(in SimInput input, Fix speed)
    {
        if (input.MoveX == 0 && input.MoveY == 0) return FixVec2.Zero;
        var dir = new FixVec2(Fix.Ratio(input.MoveX, SimInput.AxisMax), Fix.Ratio(input.MoveY, SimInput.AxisMax));
        // Keep analog magnitude, but never exceed 1 (digital diagonals would otherwise be ~1.41).
        if (dir.LengthSqRaw() > (long)Fix.OneRaw * Fix.OneRaw) dir = dir.WithLength(Fix.One);
        return dir * speed;
    }
}

public static class DodgerSystem
{
    public static void Move(SimState s, int index, in SimInput input)
    {
        ref var p = ref s.Players[index];
        ref readonly var r = ref s.Rules;
        p.Focus = input.Has(InputButtons.Focus);
        if (p.InvulnTicks > 0) p.InvulnTicks--;
        if (p.Lives <= 0) return;

        var vel = MoveInput.Velocity(input, p.Focus ? r.DodgerFocusSpeed : r.DodgerSpeed);
        r.HalfBounds(p.Team, out Fix minX, out Fix maxX);
        Fix m = r.DodgerEdgeMargin;
        var pos = p.Pos + vel;
        p.Pos = new FixVec2(
            Fix.Clamp(pos.X, minX + m, maxX - m),
            Fix.Clamp(pos.Y, m, Fix.FromInt(r.ArenaHeight) - m));
    }
}

public static class GunnerSystem
{
    public static int EnemyTeam(int team) => 1 - team;

    public static void MoveCursor(SimState s, int index, in SimInput input)
    {
        ref var p = ref s.Players[index];
        ref readonly var r = ref s.Rules;
        var vel = MoveInput.Velocity(input, r.CursorSpeed);
        r.HalfBounds(EnemyTeam(p.Team), out Fix minX, out Fix maxX);
        var pos = p.Cursor + vel;
        p.Cursor = new FixVec2(Fix.Clamp(pos.X, minX, maxX), Fix.Clamp(pos.Y, Fix.Zero, Fix.FromInt(r.ArenaHeight)));
    }

    /// <summary>Energy regen, cooldowns, and pattern placement on button press (edge-triggered).</summary>
    public static void Act(SimState s, int index, in SimInput input)
    {
        ref var p = ref s.Players[index];
        ref readonly var r = ref s.Rules;

        Fix regen = r.EnergyRegenPerTick;
        bool symmetric = HasDodgers(s, 0) && HasDodgers(s, 1);
        if (symmetric && s.RoundTick >= r.SuddenDeathTicks) regen *= r.SuddenDeathRegenMultiplier;
        p.Energy = Fix.Min(r.EnergyMax, p.Energy + regen);

        for (int k = 0; k < MatchRules.PatternSlots; k++)
            if (p.Cooldowns[k] > 0) p.Cooldowns[k]--;

        var pressed = input.Buttons & ~p.PrevButtons;
        for (int k = 0; k < MatchRules.PatternSlots; k++)
        {
            var button = (InputButtons)((int)InputButtons.Pattern1 << k);
            if ((pressed & button) != 0) TryPlace(s, index, k);
        }
    }

    public enum PlaceResult
    {
        Ok,
        Cooldown,
        NoEnergy,
        TooCloseToDodger,
        NoEmitterSlot,
    }

    /// <summary>Validation shared by the simulation and the renderer (cursor colour feedback).</summary>
    public static PlaceResult CanPlace(SimState s, int index, int slot)
    {
        ref readonly var p = ref s.Players[index];
        ref readonly var r = ref s.Rules;
        ref readonly var def = ref r.Patterns[slot];
        if (p.Cooldowns[slot] > 0) return PlaceResult.Cooldown;
        if (p.Energy < def.Cost) return PlaceResult.NoEnergy;
        if (IsTooCloseToDodger(s, p.Team, p.Cursor)) return PlaceResult.TooCloseToDodger;
        return PlaceResult.Ok;
    }

    public static bool IsTooCloseToDodger(SimState s, int gunnerTeam, FixVec2 cursor)
    {
        long minR = s.Rules.AntiSpawnRadius.Raw;
        return PatternLibrary.TryNearestDodger(s, EnemyTeam(gunnerTeam), cursor, out _, out long d2) && d2 < minR * minR;
    }

    private static void TryPlace(SimState s, int index, int slot)
    {
        ref var p = ref s.Players[index];
        var result = CanPlace(s, index, slot);
        int emitterIndex = -1;
        if (result == PlaceResult.Ok)
        {
            for (int i = 0; i < SimState.MaxEmitters; i++)
            {
                if (!s.Emitters[i].Active) { emitterIndex = i; break; }
            }
            if (emitterIndex < 0) result = PlaceResult.NoEmitterSlot;
        }

        if (result != PlaceResult.Ok)
        {
            p.LastRejectTick = s.Tick;
            s.PushEvent(SimEventType.Rejected, index, p.Team, p.Cursor);
            return;
        }

        ref readonly var def = ref s.Rules.Patterns[slot];
        p.Energy -= def.Cost;
        p.Cooldowns[slot] = def.CooldownTicks;
        s.Emitters[emitterIndex] = new Emitter
        {
            Active = true,
            PatternSlot = (byte)slot,
            Owner = (byte)index,
            Half = (byte)EnemyTeam(p.Team),
            Pos = p.Cursor,
            Age = 0,
            Angle = s.Rng.NextInt(FixMath.AngleUnitsPerTurn),
        };
        s.PushEvent(SimEventType.Placed, index, p.Team, p.Cursor);
    }

    public static bool HasDodgers(SimState s, int team)
    {
        for (int i = 0; i < s.PlayerCount; i++)
            if (s.Players[i].IsDodger && s.Players[i].Team == team) return true;
        return false;
    }
}

public static class EmitterSystem
{
    public static void Update(SimState s)
    {
        int telegraph = s.Rules.TelegraphTicks;
        for (int i = 0; i < SimState.MaxEmitters; i++)
        {
            ref var e = ref s.Emitters[i];
            if (!e.Active) continue;
            e.Age++;
            if (e.Age <= telegraph) continue;

            ref readonly var def = ref s.Rules.Patterns[e.PatternSlot];
            int t = e.Age - telegraph - 1;
            if (t >= def.DurationTicks)
            {
                e.Active = false;
                continue;
            }
            int interval = Math.Max(1, def.FireInterval);
            if (t % interval == 0) PatternLibrary.Fire(s, ref e, in def, t / interval);
        }
    }

    /// <summary>Telegraph progress 0..1 as a Fix, for the renderer.</summary>
    public static Fix TelegraphProgress(in Emitter e, int telegraphTicks) =>
        telegraphTicks <= 0 ? Fix.One : Fix.Ratio(Math.Min(e.Age, telegraphTicks), telegraphTicks);
}
