using Crossfire.Sim.Players;

namespace Crossfire.Sim.Rules;

/// <summary>Lives, rounds, role swaps and the win condition.</summary>
public static class MatchFlow
{
    public static void StartMatch(SimState s)
    {
        s.Scores[0] = 0;
        s.Scores[1] = 0;
        s.RoundNumber = 0;
        s.MatchWinner = -1;
        s.LastRoundWinner = -1;
        for (int i = 0; i < s.PlayerCount; i++) s.Players[i].Role = s.Players[i].StartRole;
        StartRound(s, swapRoles: false);
    }

    public static void StartRound(SimState s, bool swapRoles)
    {
        ref readonly var r = ref s.Rules;
        s.RoundNumber++;
        s.RoundTick = 0;
        s.Phase = MatchPhase.Countdown;
        s.PhaseTicks = r.CountdownTicks;
        s.Bullets.Clear();
        Array.Clear(s.Emitters);

        if (swapRoles)
        {
            for (int i = 0; i < s.PlayerCount; i++)
            {
                ref var p = ref s.Players[i];
                p.Role = p.Role == PlayerRole.Dodger ? PlayerRole.Gunner : PlayerRole.Dodger;
            }
        }

        Fix h = Fix.FromInt(r.ArenaHeight);
        for (int team = 0; team < SimState.TeamCount; team++)
        {
            int dodgers = CountRole(s, team, PlayerRole.Dodger);
            int gunners = CountRole(s, team, PlayerRole.Gunner);
            int di = 0, gi = 0;
            r.HalfBounds(team, out Fix ownMin, out Fix ownMax);
            r.HalfBounds(1 - team, out Fix enemyMin, out Fix enemyMax);
            for (int i = 0; i < s.PlayerCount; i++)
            {
                ref var p = ref s.Players[i];
                if (!p.Active || p.Team != team) continue;
                p.InvulnTicks = 0;
                p.Grazes = 0;
                p.Focus = false;
                p.LastRejectTick = -1;
                for (int k = 0; k < MatchRules.PatternSlots; k++) p.Cooldowns[k] = 0;
                if (p.Role == PlayerRole.Dodger)
                {
                    p.Lives = r.DodgerLives;
                    p.Pos = new FixVec2((ownMin + ownMax) / 2, h * (di + 1) / (dodgers + 1));
                    di++;
                }
                else
                {
                    p.Lives = 0;
                    p.Energy = r.EnergyStart;
                    // Start near the midline (well away from the enemy Dodgers spawning mid-half).
                    Fix inset = Fix.FromInt(70);
                    Fix cx = team == 0 ? enemyMin + inset : enemyMax - inset;
                    p.Cursor = new FixVec2(cx, h * (gi + 1) / (gunners + 1));
                    gi++;
                }
                p.PrevPos = p.Pos;
                p.PrevCursor = p.Cursor;
            }
        }
    }

    /// <summary>Checks the round-end conditions during the Playing phase.</summary>
    public static void CheckRoundEnd(SimState s)
    {
        bool has0 = GunnerSystem.HasDodgers(s, 0), has1 = GunnerSystem.HasDodgers(s, 1);
        bool out0 = has0 && AliveDodgers(s, 0) == 0;
        bool out1 = has1 && AliveDodgers(s, 1) == 0;

        if (out0 && out1) { EndRound(s, -1); return; }
        if (out0) { EndRound(s, 1); return; }
        if (out1) { EndRound(s, 0); return; }

        // Asymmetric round (only one team has Dodgers): the Dodgers win by surviving.
        if (has0 != has1 && s.RoundTick >= s.Rules.SurvivalTicks)
            EndRound(s, has0 ? 0 : 1);
    }

    public static void EndRound(SimState s, int winner)
    {
        s.LastRoundWinner = winner;
        if (winner >= 0)
        {
            s.Scores[winner]++;
            if (s.Scores[winner] >= s.Rules.RoundsToWin) s.MatchWinner = winner;
        }
        s.Phase = MatchPhase.RoundOver;
        s.PhaseTicks = s.Rules.RoundOverTicks;
        s.PushEvent(SimEventType.RoundEnd, 0, Math.Max(0, winner), FixVec2.Zero);
    }

    /// <summary>Called when the RoundOver banner finishes.</summary>
    public static void AfterRoundOver(SimState s)
    {
        if (s.MatchWinner >= 0)
        {
            s.Phase = MatchPhase.MatchOver;
            s.PushEvent(SimEventType.MatchEnd, 0, s.MatchWinner, FixVec2.Zero);
        }
        else
        {
            StartRound(s, s.Rules.SwapRolesEachRound);
        }
    }

    /// <summary>Seconds of survival still needed in an asymmetric round, else -1.</summary>
    public static int SurvivalTicksLeft(SimState s)
    {
        bool has0 = GunnerSystem.HasDodgers(s, 0), has1 = GunnerSystem.HasDodgers(s, 1);
        if (has0 == has1) return -1;
        return Math.Max(0, s.Rules.SurvivalTicks - s.RoundTick);
    }

    public static int AliveDodgers(SimState s, int team)
    {
        int n = 0;
        for (int i = 0; i < s.PlayerCount; i++)
            if (s.Players[i].IsAliveDodger && s.Players[i].Team == team) n++;
        return n;
    }

    private static int CountRole(SimState s, int team, PlayerRole role)
    {
        int n = 0;
        for (int i = 0; i < s.PlayerCount; i++)
            if (s.Players[i].Active && s.Players[i].Team == team && s.Players[i].Role == role) n++;
        return n;
    }
}
