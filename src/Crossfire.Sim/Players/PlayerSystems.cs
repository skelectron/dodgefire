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

    /// <summary>Pointer control: step straight toward the target, landing exactly on it when within <paramref name="maxStep"/>.</summary>
    public static FixVec2 Toward(FixVec2 from, FixVec2 target, Fix maxStep)
    {
        var d = target - from;
        long max = maxStep.Raw;
        if (d.LengthSqRaw() <= max * max) return target;
        return from + d.WithLength(maxStep);
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
        if (p.FlipCooldown > 0) p.FlipCooldown--;
        if (p.FlipTicks > 0 && --p.FlipTicks == 0) p.FlipGuard = false;
        if (p.Lives <= 0) return;

        if (s.Phase == MatchPhase.Playing)
        {
            var pressed = input.Buttons & ~p.PrevButtons;
            if ((pressed & InputButtons.Flip) != 0 && p.FlipCooldown == 0 && p.FlipTicks == 0)
            {
                p.FlipTicks = r.FlipTicks;
                p.FlipCooldown = r.FlipCooldownTicks;
                p.FlipGuard = true;
                s.PushEvent(SimEventType.Flip, index, p.Team, p.Pos);
            }
            if ((pressed & InputButtons.Super) != 0) TryBreak(s, index);
        }

        bool flipping = p.FlipTicks > 0;
        FixVec2 pos;
        if (input.Has(InputButtons.Pointer))
        {
            // Mouse: go straight for the pointer. The flip dash is already covered by the higher pointer speed.
            Fix step = p.Focus && !flipping ? r.DodgerFocusSpeed : r.DodgerPointerSpeed;
            pos = MoveInput.Toward(p.Pos, input.Target, step);
        }
        else
        {
            Fix speed = flipping ? r.DodgerSpeed * r.FlipSpeedMultiplier : p.Focus ? r.DodgerFocusSpeed : r.DodgerSpeed;
            pos = p.Pos + MoveInput.Velocity(input, speed);
        }
        r.HalfBounds(p.Team, out Fix minX, out Fix maxX);
        Fix m = r.DodgerEdgeMargin;
        p.Pos = new FixVec2(
            Fix.Clamp(pos.X, minX + m, maxX - m),
            Fix.Clamp(pos.Y, m, Fix.FromInt(r.ArenaHeight) - m));
    }

    public static bool IsBreakReady(in Player p, in MatchRules r) => p.Break >= r.BreakMax;

    /// <summary>Spends a full Break bar: clears every bullet and cancels every emitter in the Dodger's half.</summary>
    public static bool TryBreak(SimState s, int index)
    {
        ref var p = ref s.Players[index];
        ref readonly var r = ref s.Rules;
        if (!p.IsAliveDodger || !IsBreakReady(in p, in r)) return false;
        p.Break = Fix.Zero;
        p.InvulnTicks = Math.Max(p.InvulnTicks, r.BreakInvulnTicks);
        Collision.BulletCollision.ClearHalf(s, p.Team);
        for (int i = 0; i < SimState.MaxEmitters; i++)
            if (s.Emitters[i].Active && s.Emitters[i].Half == p.Team) s.Emitters[i].Active = false;
        s.PushEvent(SimEventType.Break, index, p.Team, p.Pos);
        return true;
    }
}

public static class GunnerSystem
{
    public static int EnemyTeam(int team) => 1 - team;

    public static void MoveCursor(SimState s, int index, in SimInput input)
    {
        ref var p = ref s.Players[index];
        ref readonly var r = ref s.Rules;
        r.HalfBounds(EnemyTeam(p.Team), out Fix minX, out Fix maxX);
        var pos = input.Has(InputButtons.Pointer)
            ? MoveInput.Toward(p.Cursor, input.Target, r.CursorPointerSpeed)
            : p.Cursor + MoveInput.Velocity(input, r.CursorSpeed);
        p.Cursor = new FixVec2(Fix.Clamp(pos.X, minX, maxX), Fix.Clamp(pos.Y, Fix.Zero, Fix.FromInt(r.ArenaHeight)));
    }

    /// <summary>Energy regen, cooldowns, and pattern placement on button press (edge-triggered).</summary>
    public static void Act(SimState s, int index, in SimInput input)
    {
        ref var p = ref s.Players[index];
        ref readonly var r = ref s.Rules;

        var pressed = input.Buttons & ~p.PrevButtons;
        // Overdrive: spend a full charge for a burst of fast cooldowns and regen.
        if ((pressed & InputButtons.Super) != 0 && p.OverdriveTicks == 0 && p.Overdrive >= r.OverdriveMax)
        {
            p.Overdrive = Fix.Zero;
            p.OverdriveTicks = r.OverdriveTicks;
            s.PushEvent(SimEventType.Overdrive, index, p.Team, p.Cursor);
        }
        bool overdrive = p.OverdriveTicks > 0;
        if (overdrive) p.OverdriveTicks--;

        Fix regen = r.EnergyRegenPerTick;
        bool symmetric = HasDodgers(s, 0) && HasDodgers(s, 1);
        if (symmetric && s.RoundTick >= r.SuddenDeathTicks) regen *= r.SuddenDeathRegenMultiplier;
        if (overdrive) regen *= Math.Max(1, r.OverdriveRegenMultiplier);
        p.Energy = Fix.Min(r.EnergyMax, p.Energy + regen);

        int cooldownStep = overdrive ? Math.Max(1, r.OverdriveCooldownRate) : 1;
        for (int k = 0; k < MatchRules.PatternSlots; k++)
            p.Cooldowns[k] = Math.Max(0, p.Cooldowns[k] - cooldownStep);

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
        ref readonly var def = ref r.DefFor(in p, slot);
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

        ref readonly var r = ref s.Rules;
        ref readonly var def = ref r.DefFor(in p, slot);
        p.Energy -= def.Cost;
        p.Cooldowns[slot] = def.CooldownTicks;
        // Spending energy charges Overdrive (not while it is running).
        if (p.OverdriveTicks == 0) p.Overdrive = Fix.Min(r.OverdriveMax, p.Overdrive + def.Cost * r.OverdrivePerEnergy);
        s.Emitters[emitterIndex] = new Emitter
        {
            Active = true,
            Kind = def.Kind,
            Owner = (byte)index,
            Half = (byte)EnemyTeam(p.Team),
            Pos = p.Cursor,
            Age = 0,
            Angle = s.Rng.NextInt(FixMath.AngleUnitsPerTurn),
        };
        s.PushEvent(SimEventType.Placed, index, p.Team, p.Cursor);
    }

    /// <summary>One of <paramref name="owner"/>'s bullets took a life: charge their Overdrive.</summary>
    public static void CreditKill(SimState s, int owner)
    {
        if ((uint)owner >= (uint)s.PlayerCount) return;
        ref var p = ref s.Players[owner];
        if (!p.IsGunner || p.OverdriveTicks > 0) return;
        p.Overdrive = Fix.Min(s.Rules.OverdriveMax, p.Overdrive + s.Rules.OverdrivePerKill);
    }

    public static bool IsOverdriveReady(in Player p, in MatchRules r) => p.OverdriveTicks == 0 && p.Overdrive >= r.OverdriveMax;

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

            ref readonly var def = ref s.Rules.Patterns[(int)e.Kind];
            int t = e.Age - telegraph - 1;
            if (t == 0) s.PushEvent(SimEventType.Fired, e.Owner, 1 - e.Half, e.Pos, (int)e.Kind);
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
