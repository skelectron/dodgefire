using System.Diagnostics;
using Brutal.GlfwApi;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Crossfire.App;
using Crossfire.Input;
using Crossfire.Render;
using Crossfire.Sim;
using Crossfire.Sim.Players;

// CROSSFIRE — team bullet-hell PvP. Phase 4: 1v1 on one keyboard.
// Args: --validate (Vulkan validation layers), --novsync, --2v2 (spectate a 2v2 layout), --seed N
//       --stress N (keep ~N bullets alive), --sprites N (static sprite stress), --god (Dodgers invulnerable),
//       --perf-log (stats to stdout every 5 s)

bool validate = args.Contains("--validate");
bool vsync = !args.Contains("--novsync");
bool perfLog = args.Contains("--perf-log");
uint seed = (uint)IntArg("--seed", 0xC0FFEE);
int stressBullets = IntArg("--stress", 0);
int IntArg(string name, int fallback)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out int v) ? v : fallback;
}
PlayerSetup[] setup = args.Contains("--2v2")
    ? [new(0, PlayerRole.Dodger), new(1, PlayerRole.Gunner), new(1, PlayerRole.Dodger), new(0, PlayerRole.Gunner)]
    : [new(0, PlayerRole.Dodger), new(1, PlayerRole.Gunner)];

if (!Glfw.Init()) throw new Exception("GLFW init failed");
Glfw.WindowHint(GlfwWindowHint.ClientApi, Glfw.Constants.GLFW_NO_API);
Glfw.WindowHint(GlfwWindowHint.Resizable, 1);
var windowInfo = new GlfwWindow.CreateInfo { Title = "CROSSFIRE", Size = new int2(1280, 840) };
GlfwWindow window = Glfw.CreateWindow(in windowInfo);

var vk = new VulkanContext(window, validate, vsync);
var batch = new SpriteBatch(vk);
var imgui = new ImGuiLayer(window, vk);
var renderer = new GameRenderer();
var keyboard = new KeyboardInput(window);
var sim = new Simulation();
var state = SimState.CreateMatch(setup, seed);
var debug = new DebugUi(vk.DeviceName, vk.ValidationEnabled) { GodMode = args.Contains("--god"), SpriteStress = IntArg("--sprites", 0) };
var perf = new PerfLog();

// Player slot -> keyboard scheme (players beyond 2 get no keyboard in Phase 4).
KeyboardScheme?[] schemes = [KeyboardScheme.LeftSide, KeyboardScheme.RightSide, null, null];
renderer.ControlHints = schemes.Where(s => s != null).Select(s => s!.Hints).ToArray();
var inputs = new SimInput[SimState.MaxPlayers];

const double TickSeconds = 1.0 / Crossfire.Sim.Rules.MatchRules.TicksPerSecond;
var clock = Stopwatch.StartNew();
double previous = clock.Elapsed.TotalSeconds;
double accumulator = 0;
bool f1Was = false;

Console.WriteLine($"CROSSFIRE running on {vk.DeviceName} (validation {(vk.ValidationEnabled ? "on" : "off")}, vsync {(vsync ? "on" : "off")})");

while (!window.ShouldClose)
{
    Glfw.PollEvents();
    if (window.GetKey(GlfwKey.Escape) == GlfwKeyAction.Press) break;
    bool f1 = window.GetKey(GlfwKey.F1) == GlfwKeyAction.Press;
    if (f1 && !f1Was) debug.Visible = !debug.Visible;
    f1Was = f1;

    long allocBefore = GC.GetAllocatedBytesForCurrentThread();
    double now = clock.Elapsed.TotalSeconds;
    double frameDt = Math.Min(now - previous, 0.25); // avoid spiral of death after a stall
    previous = now;

    // Fixed-timestep simulation.
    if (!debug.Paused) accumulator += frameDt;
    int steps = debug.ConsumeSingleStep() ? 1 : 0;
    while (accumulator >= TickSeconds) { accumulator -= TickSeconds; steps++; }
    long simStart = Stopwatch.GetTimestamp();
    for (int i = 0; i < steps; i++)
    {
        if (stressBullets > 0 && state.Bullets.Count < stressBullets)
            DebugUi.SpawnStressBullets(state, stressBullets - state.Bullets.Count);
        for (int p = 0; p < state.PlayerCount; p++)
            inputs[p] = p < schemes.Length && schemes[p] is { } scheme && !imgui.WantsKeyboard ? keyboard.Read(scheme) : default;
        if (debug.GodMode)
            for (int p = 0; p < state.PlayerCount; p++) state.Players[p].InvulnTicks = Math.Max(state.Players[p].InvulnTicks, 2);
        sim.Step(state, inputs);
        renderer.ConsumeEvents(state);
    }
    double simMs = steps > 0 ? Stopwatch.GetElapsedTime(simStart).TotalMilliseconds / steps : -1;
    float alpha = (float)(accumulator / TickSeconds);
    renderer.Update((float)frameDt);

    // Window size changes -> recreate swapchain.
    var fbSize = window.FramebufferSize;
    if (fbSize.X > 0 && fbSize.Y > 0 && (fbSize.X != vk.Extent.Width || fbSize.Y != vk.Extent.Height)) vk.RequestResize();

    imgui.NewFrame();
    debug.Draw(state, sim, ref setup, seed, out bool restart, frameDt, simMs, batch.Count, batch.Dropped);
    if (restart)
    {
        state.Dispose();
        state = SimState.CreateMatch(setup, seed, debug.Rules);
        accumulator = 0;
    }

    bool began = vk.BeginFrame(new float4(0.015f, 0.016f, 0.024f, 1f), out var cmd);
    if (began)
    {
        float fbW = vk.Extent.Width, fbH = vk.Extent.Height;
        float uiScale = window.Width > 0 ? fbW / window.Width : 1f;
        batch.Begin(vk.FrameIndex);
        var view = renderer.MakeView(state, fbW, fbH, uiScale);
        renderer.DrawArena(batch, state, alpha);
        debug.DrawSpriteStress(batch, state);
        batch.Flush(cmd, view);
        renderer.DrawHud(state, view.ScaledBy(1f / uiScale), window.Width, window.Height, alpha);
        imgui.Render(cmd);
        vk.EndFrame();
    }
    else
    {
        imgui.DiscardFrame();
    }

    long frameAlloc = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
    debug.RecordFrameAllocation(frameAlloc);
    if (perfLog) perf.Record(frameDt, simMs, frameAlloc, state.Bullets.Count, batch.Count, debug.Visible);
}

vk.Device.WaitIdle();
imgui.Dispose();
batch.Dispose();
sim.Dispose();
state.Dispose();
vk.Dispose();
window.Dispose();
Glfw.Terminate();
