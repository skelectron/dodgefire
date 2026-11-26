using Crossfire.Sim.Players;
using Crossfire.Sim.Rules;

namespace Crossfire.Sim.Tests;

/// <summary>Phase 6: role swap between rounds also works in the team layouts (2v2 lobby order and 4v4).</summary>
public class RoleSwapTests
{
    private static readonly PlayerSetup[] LocalTeam =
        [new(0, PlayerRole.Dodger), new(0, PlayerRole.Gunner), new(1, PlayerRole.Dodger), new(1, PlayerRole.Gunner)];

    private static readonly PlayerSetup[] FourVsFour =
    [
        new(0, PlayerRole.Gunner), new(0, PlayerRole.Gunner), new(0, PlayerRole.Dodger), new(0, PlayerRole.Dodger),
        new(1, PlayerRole.Dodger), new(1, PlayerRole.Dodger), new(1, PlayerRole.Gunner), new(1, PlayerRole.Gunner),
    ];

    public static TheoryData<int> Layouts => new() { 4, 8 };

    private static int Count(SimState s, int team, PlayerRole role)
    {
        int n = 0;
        for (int i = 0; i < s.PlayerCount; i++) if (s.Players[i].Team == team && s.Players[i].Role == role) n++;
        return n;
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void Swapping_flips_every_role_and_keeps_each_team_balanced(int players)
    {
        var layout = players == 4 ? LocalTeam : FourVsFour;
        var rules = MatchRules.Default;
        rules.SwapRolesEachRound = true;
        using var s = SimState.CreateMatch(layout, 7, rules);
        var before = new PlayerRole[players];
        for (int i = 0; i < players; i++) before[i] = s.Players[i].Role;

        MatchFlow.StartRound(s, swapRoles: true);

        for (int i = 0; i < players; i++) Assert.NotEqual(before[i], s.Players[i].Role);
        for (int team = 0; team < SimState.TeamCount; team++)
        {
            Assert.Equal(players / 4, Count(s, team, PlayerRole.Dodger));
            Assert.Equal(players / 4, Count(s, team, PlayerRole.Gunner));
        }
        // Dodgers respawn in their own half, Gunner cursors start in the enemy half.
        for (int i = 0; i < players; i++)
        {
            ref readonly var p = ref s.Players[i];
            if (p.Role != PlayerRole.Dodger) continue;
            s.Rules.HalfBounds(p.Team, out Fix min, out Fix max);
            Assert.InRange(p.Pos.X.Raw, min.Raw, max.Raw);
            Assert.Equal(s.Rules.DodgerLives, p.Lives);
        }
    }

    [Fact]
    public void Fixed_roles_stay_put_across_rounds()
    {
        var rules = MatchRules.Default;
        rules.SwapRolesEachRound = false;
        using var s = SimState.CreateMatch(LocalTeam, 7, rules);
        MatchFlow.StartRound(s, swapRoles: s.Rules.SwapRolesEachRound);
        for (int i = 0; i < LocalTeam.Length; i++) Assert.Equal(LocalTeam[i].Role, s.Players[i].Role);
    }
}
