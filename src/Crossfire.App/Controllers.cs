using Crossfire.Input;
using Crossfire.Sim;
using Crossfire.Sim.Bots;
using Crossfire.Sim.Patterns;
using Crossfire.Sim.Players;

namespace Crossfire.App;

public enum ControllerKind
{
    KeyboardLeft,
    KeyboardRight,
    /// <summary>Single human: either half of the keyboard works.</summary>
    KeyboardAny,
    /// <summary>Single human on the mouse (pointer steers, clicks act, wheel picks patterns).</summary>
    Mouse,
    Bot,
}

public enum GameMode
{
    /// <summary>1v1 against a bot.</summary>
    VsBot,
    /// <summary>4v4: you + 3 bot teammates against 4 bots. Roles are fixed; you pick yours.</summary>
    Team,
    /// <summary>1v1, two humans on one machine.</summary>
    Local,
    /// <summary>Bots only (also the attract mode behind the title menu).</summary>
    Demo,
}

public enum ControlScheme
{
    Mouse,
    Keyboard,
}

/// <summary>Everything the title menu decides about the next match.</summary>
/// <param name="Loadout">The human players' 4 patterns (null = classic).</param>
public readonly record struct MatchOptions(GameMode Mode, BotDifficulty Difficulty, PlayerRole Role, ControlScheme Controls, Loadout? Loadout = null);

/// <summary>Who drives a player slot. Bots keep their brain here (outside the simulation, like a human's head).</summary>
public sealed class SlotController(ControllerKind kind, BotDifficulty difficulty = BotDifficulty.Normal, uint seed = 1)
{
    public ControllerKind Kind { get; private set; } = kind;
    public BotBrain Brain = BotBrain.Create(difficulty, seed);
    private readonly uint _seed = seed;

    public bool IsBot => Kind == ControllerKind.Bot;
    public bool IsMouse => Kind == ControllerKind.Mouse;
    public BotDifficulty Difficulty => Brain.Difficulty;

    public void SetBot(BotDifficulty d)
    {
        Kind = ControllerKind.Bot;
        Brain = BotBrain.Create(d, _seed);
    }

    public void SetKind(ControllerKind kind) => Kind = kind;

    public SimInput Read(SimState s, int slot, KeyboardInput keyboard, MouseInput mouse, bool keyboardBlocked, bool mouseBlocked) => Kind switch
    {
        ControllerKind.Bot => BotController.Think(s, slot, ref Brain),
        ControllerKind.Mouse => keyboardBlocked && mouseBlocked ? default : mouse.Read(s, slot, mouseBlocked),
        _ when keyboardBlocked => default,
        ControllerKind.KeyboardLeft => keyboard.Read(KeyboardScheme.LeftSide),
        ControllerKind.KeyboardRight => keyboard.Read(KeyboardScheme.RightSide),
        _ => keyboard.ReadAny(KeyboardScheme.LeftSide, KeyboardScheme.RightSide),
    };

    /// <summary>HUD hints [move, focus, patterns, start, flip, break], or null for bots.</summary>
    public string[]? Hints => Kind switch
    {
        ControllerKind.KeyboardLeft => KeyboardScheme.LeftSide.Hints,
        ControllerKind.KeyboardRight => KeyboardScheme.RightSide.Hints,
        ControllerKind.KeyboardAny => ["WASD/Arrows", "Shift", "QERF / UIOP", "Space", "Space/Enter", "X/R-Ctrl"],
        ControllerKind.Mouse => MouseInput.Hints,
        _ => null,
    };

    public string Label => Kind switch
    {
        ControllerKind.KeyboardLeft => "Keyboard L",
        ControllerKind.KeyboardRight => "Keyboard R",
        ControllerKind.KeyboardAny => "Keyboard",
        ControllerKind.Mouse => "Mouse",
        _ => $"Bot ({Difficulty})",
    };
}

/// <summary>Player layouts + controllers for each game mode.</summary>
public static class MatchSetups
{
    public static readonly PlayerSetup[] OneVsOne = [new(0, PlayerRole.Dodger), new(1, PlayerRole.Gunner)];
    public static readonly PlayerSetup[] TwoVsTwo = [new(0, PlayerRole.Dodger), new(1, PlayerRole.Gunner), new(1, PlayerRole.Dodger), new(0, PlayerRole.Gunner)];

    /// <summary>
    /// 4v4 with slot 0 = the human on team 0 in <paramref name="humanRole"/>. Each team fields two Dodgers
    /// and two Gunners, so the human's three bot teammates fill whatever roles are left.
    /// </summary>
    public static PlayerSetup[] FourVsFour(PlayerRole humanRole)
    {
        var other = humanRole == PlayerRole.Dodger ? PlayerRole.Gunner : PlayerRole.Dodger;
        return
        [
            new(0, humanRole), new(0, humanRole == PlayerRole.Dodger ? PlayerRole.Dodger : PlayerRole.Gunner), new(0, other), new(0, other),
            new(1, PlayerRole.Dodger), new(1, PlayerRole.Dodger), new(1, PlayerRole.Gunner), new(1, PlayerRole.Gunner),
        ];
    }

    /// <summary>Layout for the options: 1v1 modes put the human in slot 0 with the chosen starting role.</summary>
    public static PlayerSetup[] Layout(in MatchOptions o) => WithLoadouts(o.Mode switch
    {
        GameMode.Team => FourVsFour(o.Role),
        GameMode.VsBot when o.Role == PlayerRole.Gunner => [new(0, PlayerRole.Gunner), new(1, PlayerRole.Dodger)],
        _ => OneVsOne,
    }, o);

    public static bool IsHuman(in MatchOptions o, int slot) => o.Mode switch
    {
        GameMode.VsBot or GameMode.Team => slot == 0,
        GameMode.Local => slot < 2,
        _ => false,
    };

    public static uint SlotSeed(int slot) => 0xB07u + (uint)slot * 7919u;

    /// <summary>Humans get the chosen loadout; bots pick one from their difficulty and seed.</summary>
    public static PlayerSetup[] WithLoadouts(PlayerSetup[] layout, in MatchOptions o)
    {
        var result = new PlayerSetup[layout.Length];
        for (int i = 0; i < layout.Length; i++)
            result[i] = layout[i] with { Loadout = IsHuman(o, i) ? o.Loadout ?? Crossfire.Sim.Patterns.Loadout.Classic : BotController.LoadoutFor(o.Difficulty, SlotSeed(i)) };
        return result;
    }

    public static SlotController[] Controllers(in MatchOptions o, int players)
    {
        var human = o.Controls == ControlScheme.Mouse ? ControllerKind.Mouse : ControllerKind.KeyboardAny;
        var c = new SlotController[players];
        for (int i = 0; i < players; i++)
        {
            uint seed = SlotSeed(i);
            c[i] = (o.Mode, i) switch
            {
                (GameMode.VsBot or GameMode.Team, 0) => new SlotController(human, o.Difficulty, seed),
                // Local: P1 gets the mouse (if chosen) or the left keys; P2 always the right keys.
                (GameMode.Local, 0) => new SlotController(o.Controls == ControlScheme.Mouse ? ControllerKind.Mouse : ControllerKind.KeyboardLeft, o.Difficulty, seed),
                (GameMode.Local, 1) => new SlotController(ControllerKind.KeyboardRight, o.Difficulty, seed),
                _ => new SlotController(ControllerKind.Bot, o.Difficulty, seed),
            };
        }
        return c;
    }
}
