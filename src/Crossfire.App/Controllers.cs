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
    /// <summary>Another player's seat in an online match (their inputs arrive over the network).</summary>
    Remote,
    /// <summary>A gamepad (<see cref="SlotController.Pad"/>; -1 = whichever pad is connected first).</summary>
    Gamepad,
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
    /// <summary>2v2 on one machine: up to 4 humans (mouse, both keyboard halves, gamepads) take seats in a lobby; bots fill the rest.</summary>
    LocalTeam,
    /// <summary>Bots only (also the attract mode behind the title menu).</summary>
    Demo,
    /// <summary>Online over RakNet (host or join by IP); handled by the online screen, not MatchSetups.</summary>
    Online,
}

public enum ControlScheme
{
    Mouse,
    Keyboard,
    Gamepad,
}

/// <summary>A lobby seat: who sits there (None = bot) and the loadout they bring.</summary>
public readonly record struct LobbySeat(InputDevice Device, Loadout Loadout);

/// <summary>Everything the title menu decides about the next match.</summary>
/// <param name="Loadout">The human players' 4 patterns (null = classic).</param>
/// <param name="Seats">LocalTeam only: one entry per slot of <see cref="MatchSetups.LocalTeam"/>.</param>
/// <param name="SwapRoles">Team modes: swap every player's role each round (1v1 modes always swap).</param>
public readonly record struct MatchOptions(GameMode Mode, BotDifficulty Difficulty, PlayerRole Role, ControlScheme Controls, Loadout? Loadout = null, LobbySeat[]? Seats = null, bool SwapRoles = false);

/// <summary>Who drives a player slot. Bots keep their brain here (outside the simulation, like a human's head).</summary>
public sealed class SlotController(ControllerKind kind, BotDifficulty difficulty = BotDifficulty.Normal, uint seed = 1, int pad = -1)
{
    public ControllerKind Kind { get; private set; } = kind;
    public BotBrain Brain = BotBrain.Create(difficulty, seed);
    /// <summary>Gamepad index for <see cref="ControllerKind.Gamepad"/>; -1 = the first connected pad.</summary>
    public int Pad { get; private set; } = pad;

    public bool IsBot => Kind == ControllerKind.Bot;
    public bool IsMouse => Kind == ControllerKind.Mouse;
    public bool UsesKeyboard => Kind is ControllerKind.KeyboardLeft or ControllerKind.KeyboardRight or ControllerKind.KeyboardAny;
    public BotDifficulty Difficulty => Brain.Difficulty;

    public void SetBot(BotDifficulty d)
    {
        Kind = ControllerKind.Bot;
        Brain = BotBrain.Create(d, seed);
    }

    public void SetKind(ControllerKind kind, int pad = -1)
    {
        Kind = kind;
        Pad = pad;
    }

    public SimInput Read(SimState s, int slot, KeyboardInput keyboard, MouseInput mouse, GamepadInput pads, bool keyboardBlocked, bool mouseBlocked) => Kind switch
    {
        ControllerKind.Bot => BotController.Think(s, slot, ref Brain),
        ControllerKind.Remote => default,
        ControllerKind.Mouse => keyboardBlocked && mouseBlocked ? default : mouse.Read(s, slot, mouseBlocked),
        // Pads are not blocked by ImGui (it does not use them).
        ControllerKind.Gamepad => pads.Read(Pad >= 0 ? Pad : pads.FirstConnected, s.Players[slot].Role),
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
        ControllerKind.Gamepad => GamepadMapping.Hints,
        _ => null,
    };

    public string Label => Kind switch
    {
        ControllerKind.KeyboardLeft => "Keyboard L",
        ControllerKind.KeyboardRight => "Keyboard R",
        ControllerKind.KeyboardAny => "Keyboard",
        ControllerKind.Mouse => "Mouse",
        ControllerKind.Gamepad => Pad >= 0 ? $"Pad {Pad + 1}" : "Gamepad",
        ControllerKind.Remote => "Online",
        _ => $"Bot ({Difficulty})",
    };

    /// <summary>Short device tag for the results table ("MOUSE", "KEYS L", "PAD 2").</summary>
    public string ShortLabel => Kind switch
    {
        ControllerKind.KeyboardLeft => "KEYS L",
        ControllerKind.KeyboardRight => "KEYS R",
        ControllerKind.KeyboardAny => "KEYS",
        ControllerKind.Mouse => "MOUSE",
        ControllerKind.Gamepad => Pad >= 0 ? $"PAD {Pad + 1}" : "PAD",
        ControllerKind.Remote => "ONLINE",
        _ => "BOT",
    };

    public static SlotController ForDevice(InputDevice d, BotDifficulty difficulty, uint seed) => d.Kind switch
    {
        DeviceKind.Mouse => new SlotController(ControllerKind.Mouse, difficulty, seed),
        DeviceKind.KeyboardLeft => new SlotController(ControllerKind.KeyboardLeft, difficulty, seed),
        DeviceKind.KeyboardRight => new SlotController(ControllerKind.KeyboardRight, difficulty, seed),
        DeviceKind.Gamepad => new SlotController(ControllerKind.Gamepad, difficulty, seed, d.Pad),
        _ => new SlotController(ControllerKind.Bot, difficulty, seed),
    };
}

/// <summary>Player layouts + controllers for each game mode.</summary>
public static class MatchSetups
{
    public static readonly PlayerSetup[] OneVsOne = [new(0, PlayerRole.Dodger), new(1, PlayerRole.Gunner)];
    public static readonly PlayerSetup[] TwoVsTwo = [new(0, PlayerRole.Dodger), new(1, PlayerRole.Gunner), new(1, PlayerRole.Dodger), new(0, PlayerRole.Gunner)];

    /// <summary>Lobby seat order for Local 2v2: blue Dodger, blue Gunner, red Dodger, red Gunner.</summary>
    public static readonly PlayerSetup[] LocalTeam = [new(0, PlayerRole.Dodger), new(0, PlayerRole.Gunner), new(1, PlayerRole.Dodger), new(1, PlayerRole.Gunner)];

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
        GameMode.LocalTeam => LocalTeam,
        GameMode.VsBot when o.Role == PlayerRole.Gunner => [new(0, PlayerRole.Gunner), new(1, PlayerRole.Dodger)],
        _ => OneVsOne,
    }, o);

    public static bool IsHuman(in MatchOptions o, int slot) => o.Mode switch
    {
        GameMode.VsBot or GameMode.Team => slot == 0,
        GameMode.Local => slot < 2,
        GameMode.LocalTeam => o.Seats != null && slot < o.Seats.Length && !o.Seats[slot].Device.IsNone,
        _ => false,
    };

    public static uint SlotSeed(int slot) => 0xB07u + (uint)slot * 7919u;

    /// <summary>Humans get the chosen loadout; bots pick one from their difficulty and seed.</summary>
    public static PlayerSetup[] WithLoadouts(PlayerSetup[] layout, in MatchOptions o)
    {
        var result = new PlayerSetup[layout.Length];
        for (int i = 0; i < layout.Length; i++)
        {
            Loadout loadout = !IsHuman(o, i) ? BotController.LoadoutFor(o.Difficulty, SlotSeed(i))
                : o.Mode == GameMode.LocalTeam ? o.Seats![i].Loadout
                : o.Loadout ?? Crossfire.Sim.Patterns.Loadout.Classic;
            result[i] = layout[i] with { Loadout = loadout };
        }
        return result;
    }

    /// <summary>Controllers per slot. <paramref name="pads"/> lists connected gamepads (for gamepad 1v1 modes).</summary>
    public static SlotController[] Controllers(in MatchOptions o, int players, ReadOnlySpan<int> pads = default)
    {
        var human = o.Controls switch
        {
            ControlScheme.Mouse => ControllerKind.Mouse,
            ControlScheme.Gamepad => ControllerKind.Gamepad,
            _ => ControllerKind.KeyboardAny,
        };
        var c = new SlotController[players];
        for (int i = 0; i < players; i++)
        {
            uint seed = SlotSeed(i);
            c[i] = (o.Mode, i) switch
            {
                (GameMode.LocalTeam, _) => SlotController.ForDevice(o.Seats != null && i < o.Seats.Length ? o.Seats[i].Device : InputDevice.None, o.Difficulty, seed),
                (GameMode.VsBot or GameMode.Team, 0) => new SlotController(human, o.Difficulty, seed),
                // Local: P1 gets the mouse, the first pad or the left keys; P2 the second pad or the right keys.
                (GameMode.Local, 0) => o.Controls switch
                {
                    ControlScheme.Mouse => new SlotController(ControllerKind.Mouse, o.Difficulty, seed),
                    ControlScheme.Gamepad when pads.Length > 0 => new SlotController(ControllerKind.Gamepad, o.Difficulty, seed, pads[0]),
                    _ => new SlotController(ControllerKind.KeyboardLeft, o.Difficulty, seed),
                },
                (GameMode.Local, 1) when o.Controls == ControlScheme.Gamepad && pads.Length > 1 => new SlotController(ControllerKind.Gamepad, o.Difficulty, seed, pads[1]),
                (GameMode.Local, 1) => new SlotController(ControllerKind.KeyboardRight, o.Difficulty, seed),
                _ => new SlotController(ControllerKind.Bot, o.Difficulty, seed),
            };
        }
        return c;
    }
}
