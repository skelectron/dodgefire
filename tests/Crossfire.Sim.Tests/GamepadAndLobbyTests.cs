using Crossfire.Input;
using Crossfire.Sim.Players;

namespace Crossfire.Sim.Tests;

/// <summary>Phase 5: gamepad → SimInput mapping and the local 2v2 lobby (pure logic, no GLFW).</summary>
public class GamepadAndLobbyTests
{
    // --- Gamepad mapping -------------------------------------------------------------------------

    [Fact]
    public void Stick_inside_dead_zone_is_zero()
    {
        Assert.Equal(((sbyte)0, (sbyte)0), GamepadMapping.Stick(0.1f, -0.12f));
        Assert.Equal(((sbyte)0, (sbyte)0), GamepadMapping.Stick(float.NaN, 0f));
    }

    [Fact]
    public void Stick_full_deflection_is_full_speed_and_keeps_direction()
    {
        var (x, y) = GamepadMapping.Stick(1f, 0f);
        Assert.Equal(SimInput.AxisMax, x);
        Assert.Equal(0, y);

        // A full diagonal keeps 45° (no squaring into the axes) and has length ~AxisMax.
        float d = MathF.Sqrt(0.5f);
        (x, y) = GamepadMapping.Stick(d, -d);
        Assert.Equal(x, -y);
        Assert.InRange(MathF.Sqrt(x * x + y * y), SimInput.AxisMax - 2, SimInput.AxisMax + 1);
    }

    [Fact]
    public void Stick_rescales_just_outside_the_dead_zone_to_slow()
    {
        var (x, _) = GamepadMapping.Stick(GamepadMapping.DeadZone + 0.08f, 0f);
        Assert.InRange(x, 1, 20); // starts slow instead of jumping to 20% speed
    }

    [Fact]
    public void Dodger_mapping_flip_break_focus_and_dpad()
    {
        var pad = new PadSnapshot { Buttons = PadButtons.A | PadButtons.DpadLeft | PadButtons.DpadUp, RightTrigger = 0.9f, LeftTrigger = 0f };
        var input = GamepadMapping.Map(pad, PlayerRole.Dodger);
        Assert.True(input.Has(InputButtons.Flip));
        Assert.True(input.Has(InputButtons.Super));
        Assert.True(input.Has(InputButtons.Start));
        Assert.False(input.Has(InputButtons.Focus));
        Assert.False(input.Has(InputButtons.Pattern1));
        Assert.Equal(-SimInput.AxisMax, input.MoveX);
        Assert.Equal(-SimInput.AxisMax, input.MoveY);

        pad = new PadSnapshot { Buttons = PadButtons.LeftBumper, LeftX = 0.5f };
        input = GamepadMapping.Map(pad, PlayerRole.Dodger);
        Assert.True(input.Has(InputButtons.Focus));
        Assert.False(input.Has(InputButtons.Pointer));
        Assert.InRange(input.MoveX, 30, 100); // analog: part speed
    }

    [Fact]
    public void Gunner_mapping_face_buttons_fire_patterns_and_right_stick_aims()
    {
        var pad = new PadSnapshot { Buttons = PadButtons.B | PadButtons.Y | PadButtons.RightBumper, RightX = 1f };
        var input = GamepadMapping.Map(pad, PlayerRole.Gunner);
        Assert.False(input.Has(InputButtons.Pattern1));
        Assert.True(input.Has(InputButtons.Pattern2));
        Assert.False(input.Has(InputButtons.Pattern3));
        Assert.True(input.Has(InputButtons.Pattern4));
        Assert.True(input.Has(InputButtons.Super)); // Overdrive
        Assert.False(input.Has(InputButtons.Flip));
        Assert.Equal(SimInput.AxisMax, input.MoveX);

        // Left stick wins over the right stick when both are pushed.
        pad = new PadSnapshot { LeftX = -1f, RightX = 1f };
        Assert.Equal(-SimInput.AxisMax, GamepadMapping.Map(pad, PlayerRole.Gunner).MoveX);
    }

    [Fact]
    public void Gamepad_input_drives_a_dodger_in_the_simulation()
    {
        using var sim = new Simulation();
        using var s = SimState.CreateMatch(TestHelpers.OneVsOne, 1);
        TestHelpers.SkipCountdown(sim, s);
        var start = s.Players[0].Pos;
        var inputs = new SimInput[2];
        inputs[0] = GamepadMapping.Map(new PadSnapshot { LeftX = 1f }, PlayerRole.Dodger);
        for (int t = 0; t < 10; t++) sim.Step(s, inputs);
        Assert.True(s.Players[0].Pos.X > start.X);
    }

    [Fact]
    public void Menu_direction_needs_a_firm_push()
    {
        Assert.Equal((0, 0), GamepadMapping.MenuDirection(new PadSnapshot { LeftX = 0.4f }));
        Assert.Equal((1, 0), GamepadMapping.MenuDirection(new PadSnapshot { LeftX = 0.8f }));
        Assert.Equal((0, -1), GamepadMapping.MenuDirection(new PadSnapshot { Buttons = PadButtons.DpadUp }));
    }

    // --- Lobby -----------------------------------------------------------------------------------

    private static readonly InputDevice Mouse = new(DeviceKind.Mouse);
    private static readonly InputDevice KeysL = new(DeviceKind.KeyboardLeft);
    private static readonly InputDevice Pad0 = new(DeviceKind.Gamepad, 0);
    private static readonly InputDevice Pad1 = new(DeviceKind.Gamepad, 1);

    [Fact]
    public void Devices_join_free_seats_in_order_and_never_twice()
    {
        var lobby = new Lobby(4);
        Assert.Equal(0, lobby.Join(Pad0));
        Assert.Equal(1, lobby.Join(KeysL));
        Assert.Equal(0, lobby.Join(Pad0)); // already seated
        Assert.Equal(3, lobby.Join(Mouse, 3)); // mouse clicks a specific seat
        Assert.Equal(-1, lobby.Join(Pad1, 3)); // taken
        Assert.Equal(2, lobby.Join(Pad1));
        Assert.Equal(4, lobby.Humans);
        Assert.Equal(-1, lobby.Join(new InputDevice(DeviceKind.KeyboardRight))); // full
    }

    [Fact]
    public void Moving_skips_taken_seats_and_wraps()
    {
        var lobby = new Lobby(4);
        lobby.Join(Pad0, 0);
        lobby.Join(Pad1, 1);
        lobby.Move(Pad0, 1); // seat 1 taken -> 2
        Assert.Equal(2, lobby.SeatOf(Pad0));
        lobby.Move(Pad0, 1); // -> 3
        lobby.Move(Pad0, 1); // wraps to 0
        Assert.Equal(0, lobby.SeatOf(Pad0));
        lobby.Move(Pad0, -1); // wraps back to 3
        Assert.Equal(3, lobby.SeatOf(Pad0));
        Assert.False(lobby.IsTaken(0));
    }

    [Fact]
    public void Apply_runs_the_join_choose_ready_start_flow()
    {
        var lobby = new Lobby(4);
        Assert.False(lobby.Apply(Pad0, MenuActions.Confirm)); // join seat 0
        Assert.False(lobby.Apply(Pad1, MenuActions.Confirm)); // join seat 1
        lobby.Apply(Pad1, MenuActions.Down); // Gunner seat: next loadout preset
        Assert.Equal(1, lobby.PresetAt(1));
        lobby.Apply(Pad1, MenuActions.Up | MenuActions.Up); // flags, not counts: one step back
        Assert.Equal(0, lobby.PresetAt(1));
        lobby.Apply(Pad1, MenuActions.Up);
        Assert.Equal(Lobby.LoadoutPresets - 1, lobby.PresetAt(1)); // wraps

        Assert.False(lobby.Apply(Pad0, MenuActions.Confirm)); // ready
        Assert.False(lobby.AllReady);
        Assert.False(lobby.Apply(Pad0, MenuActions.Confirm)); // ready again, but Pad1 is not
        Assert.False(lobby.Apply(Pad1, MenuActions.Confirm)); // Pad1 ready -> all ready, but this press only readies
        Assert.True(lobby.AllReady);
        lobby.Apply(Pad1, MenuActions.Down); // ready players cannot change their loadout
        Assert.Equal(Lobby.LoadoutPresets - 1, lobby.PresetAt(1));
        Assert.True(lobby.Apply(Pad0, MenuActions.Confirm)); // start
    }

    [Fact]
    public void Back_unreadies_then_leaves()
    {
        var lobby = new Lobby(4);
        lobby.Apply(KeysL, MenuActions.Confirm);
        lobby.Apply(KeysL, MenuActions.Confirm);
        Assert.True(lobby.IsReady(0));
        lobby.Apply(KeysL, MenuActions.Back);
        Assert.False(lobby.IsReady(0));
        Assert.True(lobby.IsTaken(0));
        lobby.Apply(KeysL, MenuActions.Back);
        Assert.False(lobby.IsTaken(0));
        Assert.False(lobby.AllReady); // nobody seated is never "all ready"
    }

    [Fact]
    public void Moving_seat_unreadies_and_unplugged_pads_are_pruned()
    {
        var lobby = new Lobby(4);
        lobby.Join(Pad0);
        lobby.Join(Pad1);
        lobby.SetReady(Pad0, true);
        Assert.True(lobby.MoveTo(Pad0, 3));
        Assert.False(lobby.IsReady(3));

        lobby.Prune(d => d != Pad1); // Pad1 unplugged
        Assert.Equal(-1, lobby.SeatOf(Pad1));
        Assert.Equal(1, lobby.Humans);
    }
}
