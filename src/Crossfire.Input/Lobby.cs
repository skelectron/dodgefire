namespace Crossfire.Input;

/// <summary>A physical input device that can take a lobby seat.</summary>
public enum DeviceKind : byte
{
    None,
    Mouse,
    KeyboardLeft,
    KeyboardRight,
    Gamepad,
}

public readonly record struct InputDevice(DeviceKind Kind, int Pad = 0)
{
    public static readonly InputDevice None = default;
    public bool IsNone => Kind == DeviceKind.None;

    public string Label => Kind switch
    {
        DeviceKind.Mouse => "MOUSE",
        DeviceKind.KeyboardLeft => "KEYS L",
        DeviceKind.KeyboardRight => "KEYS R",
        DeviceKind.Gamepad => $"PAD {Pad + 1}",
        _ => "BOT",
    };
}

/// <summary>
/// Seat assignment for a local match: each device joins a free seat, moves between free seats,
/// cycles its Gunner loadout preset, and readies up. Empty seats become bots.
/// Pure logic (no input polling), so it is unit-testable; the title menu feeds it per-device actions.
/// </summary>
public sealed class Lobby
{
    public const int LoadoutPresets = 4; // Classic, Hunter, Trapper, Custom (the menu's picked loadout)

    private readonly InputDevice[] _device;
    private readonly bool[] _ready;
    private readonly int[] _preset;

    public int Seats { get; }

    public Lobby(int seats)
    {
        Seats = seats;
        _device = new InputDevice[seats];
        _ready = new bool[seats];
        _preset = new int[seats];
    }

    public InputDevice DeviceAt(int seat) => _device[seat];
    public bool IsReady(int seat) => _ready[seat];
    public bool IsTaken(int seat) => !_device[seat].IsNone;
    public int PresetAt(int seat) => _preset[seat];

    public int Humans
    {
        get
        {
            int n = 0;
            foreach (var d in _device) if (!d.IsNone) n++;
            return n;
        }
    }

    /// <summary>At least one human, and every human is ready.</summary>
    public bool AllReady
    {
        get
        {
            int humans = 0;
            for (int i = 0; i < Seats; i++)
            {
                if (_device[i].IsNone) continue;
                humans++;
                if (!_ready[i]) return false;
            }
            return humans > 0;
        }
    }

    public int SeatOf(InputDevice d)
    {
        if (d.IsNone) return -1;
        for (int i = 0; i < Seats; i++) if (_device[i] == d) return i;
        return -1;
    }

    public void Clear()
    {
        Array.Clear(_device);
        Array.Clear(_ready);
        Array.Clear(_preset);
    }

    /// <summary>Takes <paramref name="seat"/> (or the first free seat when -1). Returns the seat, or -1 if none is free.</summary>
    public int Join(InputDevice d, int seat = -1)
    {
        if (d.IsNone) return -1;
        int existing = SeatOf(d);
        if (existing >= 0) return existing;
        if (seat < 0)
            for (int i = 0; i < Seats && seat < 0; i++)
                if (_device[i].IsNone) seat = i;
        if (seat < 0 || seat >= Seats || !_device[seat].IsNone) return -1;
        _device[seat] = d;
        _ready[seat] = false;
        _preset[seat] = 0;
        return seat;
    }

    public void Leave(InputDevice d)
    {
        int s = SeatOf(d);
        if (s < 0) return;
        _device[s] = InputDevice.None;
        _ready[s] = false;
    }

    /// <summary>Moves to the next free seat in <paramref name="dir"/> (wrapping). Un-readies.</summary>
    public void Move(InputDevice d, int dir)
    {
        int s = SeatOf(d);
        if (s < 0 || dir == 0) return;
        for (int k = 1; k < Seats; k++)
        {
            int t = ((s + dir * k) % Seats + Seats) % Seats;
            if (!_device[t].IsNone) continue;
            MoveTo(s, t);
            return;
        }
    }

    /// <summary>Moves a device straight to a free seat (mouse clicks).</summary>
    public bool MoveTo(InputDevice d, int seat)
    {
        int s = SeatOf(d);
        if (s < 0 || seat < 0 || seat >= Seats || !_device[seat].IsNone) return false;
        MoveTo(s, seat);
        return true;
    }

    private void MoveTo(int from, int to)
    {
        _device[to] = _device[from];
        _preset[to] = _preset[from];
        _ready[to] = false;
        _device[from] = InputDevice.None;
        _ready[from] = false;
    }

    public void CyclePreset(InputDevice d, int dir)
    {
        int s = SeatOf(d);
        if (s < 0 || _ready[s]) return;
        _preset[s] = ((_preset[s] + dir) % LoadoutPresets + LoadoutPresets) % LoadoutPresets;
    }

    public void SetReady(InputDevice d, bool ready)
    {
        int s = SeatOf(d);
        if (s >= 0) _ready[s] = ready;
    }

    /// <summary>Drops seats whose device no longer exists (e.g. an unplugged pad).</summary>
    public void Prune(Func<InputDevice, bool> stillThere)
    {
        for (int i = 0; i < Seats; i++)
            if (!_device[i].IsNone && !stillThere(_device[i])) { _device[i] = InputDevice.None; _ready[i] = false; }
    }

    /// <summary>
    /// One device's menu action this frame. Not seated: Confirm joins. Seated and not ready:
    /// Left/Right change seat, Up/Down change the Gunner loadout, Confirm readies, Back leaves.
    /// Ready: Back un-readies. Returns true when a ready player confirms while everyone is ready (start).
    /// </summary>
    public bool Apply(InputDevice d, MenuActions a)
    {
        if (a == MenuActions.None) return false;
        int s = SeatOf(d);
        if (s < 0)
        {
            if ((a & MenuActions.Confirm) != 0) Join(d);
            return false;
        }
        if (_ready[s])
        {
            if ((a & MenuActions.Back) != 0) { _ready[s] = false; return false; }
            return (a & MenuActions.Confirm) != 0 && AllReady;
        }
        if ((a & MenuActions.Back) != 0) { Leave(d); return false; }
        if ((a & MenuActions.Left) != 0) Move(d, -1);
        else if ((a & MenuActions.Right) != 0) Move(d, 1);
        if ((a & MenuActions.Up) != 0) CyclePreset(d, -1);
        else if ((a & MenuActions.Down) != 0) CyclePreset(d, 1);
        if ((a & MenuActions.Confirm) != 0) _ready[SeatOf(d)] = true;
        return false;
    }
}
