using Crossfire.Input;
using Crossfire.Sim;
using Crossfire.Sim.Bots;
using Crossfire.Sim.Players;

namespace Crossfire.App;

public enum ControllerKind
{
    KeyboardLeft,
    KeyboardRight,
    /// <summary>Single human: either half of the keyboard works.</summary>
    KeyboardAny,
    Bot,
}

public enum GameMode
{
    VsBot,
    Local,
    Demo,
}

/// <summary>Who drives a player slot. Bots keep their brain here (outside the simulation, like a human's head).</summary>
public sealed class SlotController(ControllerKind kind, BotDifficulty difficulty = BotDifficulty.Normal, uint seed = 1)
{
    public ControllerKind Kind { get; private set; } = kind;
    public BotBrain Brain = BotBrain.Create(difficulty, seed);
    private readonly uint _seed = seed;

    public bool IsBot => Kind == ControllerKind.Bot;
    public BotDifficulty Difficulty => Brain.Difficulty;

    public void SetBot(BotDifficulty d)
    {
        Kind = ControllerKind.Bot;
        Brain = BotBrain.Create(d, _seed);
    }

    public void SetKind(ControllerKind kind) => Kind = kind;

    public SimInput Read(SimState s, int slot, KeyboardInput keyboard, bool keyboardBlocked) => Kind switch
    {
        ControllerKind.Bot => BotController.Think(s, slot, ref Brain),
        _ when keyboardBlocked => default,
        ControllerKind.KeyboardLeft => keyboard.Read(KeyboardScheme.LeftSide),
        ControllerKind.KeyboardRight => keyboard.Read(KeyboardScheme.RightSide),
        _ => keyboard.ReadAny(KeyboardScheme.LeftSide, KeyboardScheme.RightSide),
    };

    /// <summary>HUD hints [move, focus, patterns, start], or null for bots.</summary>
    public string[]? Hints => Kind switch
    {
        ControllerKind.KeyboardLeft => KeyboardScheme.LeftSide.Hints,
        ControllerKind.KeyboardRight => KeyboardScheme.RightSide.Hints,
        ControllerKind.KeyboardAny => ["WASD/Arrows", "Shift", "QERF / UIOP", "Space"],
        _ => null,
    };

    public string Label => Kind switch
    {
        ControllerKind.KeyboardLeft => "Keyboard L",
        ControllerKind.KeyboardRight => "Keyboard R",
        ControllerKind.KeyboardAny => "Keyboard",
        _ => $"Bot ({Difficulty})",
    };
}

/// <summary>Player layouts + controllers for each game mode.</summary>
public static class MatchSetups
{
    public static readonly PlayerSetup[] OneVsOne = [new(0, PlayerRole.Dodger), new(1, PlayerRole.Gunner)];
    public static readonly PlayerSetup[] TwoVsTwo = [new(0, PlayerRole.Dodger), new(1, PlayerRole.Gunner), new(1, PlayerRole.Dodger), new(0, PlayerRole.Gunner)];

    public static SlotController[] Controllers(GameMode mode, BotDifficulty difficulty, int players)
    {
        var c = new SlotController[players];
        for (int i = 0; i < players; i++)
        {
            uint seed = 0xB07u + (uint)i * 7919u;
            c[i] = (mode, i) switch
            {
                (GameMode.VsBot, 0) => new SlotController(ControllerKind.KeyboardAny, difficulty, seed),
                (GameMode.Local, 0) => new SlotController(ControllerKind.KeyboardLeft, difficulty, seed),
                (GameMode.Local, 1) => new SlotController(ControllerKind.KeyboardRight, difficulty, seed),
                _ => new SlotController(ControllerKind.Bot, difficulty, seed),
            };
        }
        return c;
    }
}
