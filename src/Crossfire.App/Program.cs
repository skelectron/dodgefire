using System.Diagnostics;
using Brutal.GlfwApi;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Crossfire.App;
using Crossfire.Input;
using Crossfire.Render;
using Crossfire.Sim;
using Crossfire.Sim.Bots;

// CROSSFIRE: team bullet-hell PvP.
// Args: --mode vsbot|local|demo (skip the title menu), --difficulty easy|normal|hard, --2v2 (bots fill slots 3-4)
//       --validate (Vulkan validation layers), --novsync, --seed N
//       --stress N (keep ~N bullets alive), --sprites N (static sprite stress), --god (Dodgers invulnerable),
//       --perf-log (stats to stdout every 5 s)

bool validate = args.Contains("--validate");
bool vsync = !args.Contains("--novsync");
bool perfLog = args.Contains("--perf-log");
uint seed = (uint)IntArg("--seed", 0xC0FFEE);
int stressBullets = IntArg("--stress", 0);
bool twoVsTwo = args.Contains("--2v2");
string? modeArg = StrArg("--mode");
var startDifficulty = StrArg("--difficulty") switch
{
    "easy" => BotDifficulty.Easy,
    "hard" => BotDifficulty.Hard,
    _ => BotDifficulty.Normal,
};
GameMode? cliMode = modeArg switch
{
    "vsbot" => GameMode.VsBot,
    "local" => GameMode.Local,
    "demo" => GameMode.Demo,
    _ => null,
};
int IntArg(string name, int fallback)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out int v) ? v : fallback;
}
string? StrArg(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1].ToLowerInvariant() : null;
}

if (!Glfw.Init()) throw new Exception("GLFW init failed");
Glfw.WindowHint(GlfwWindowHint.ClientApi, Glfw.Constants.GLFW_NO_API);
Glfw.WindowHint(GlfwWindowHint.Resizable, 1);
var windowInfo = new GlfwWindow.CreateInfo { Title = "CROSSFIRE", Size = new int2(1280, 840) };
GlfwWindow window = Glfw.CreateWindow(in windowInfo);

var vk = new VulkanContext(window, validate, vsync);
// Kenney art (CC0): sprites packed into one atlas; the renderer falls back to SDF shapes if any are missing.
var atlas = new TextureAtlas(vk, GameArt.SpriteFiles(AppContext.BaseDirectory));
var batch = new SpriteBatch(vk, atlas);
var imgui = new ImGuiLayer(window, vk);
var art = new GameArt(atlas)
{
    AtlasTexture = imgui.AddTexture(atlas.Sampler, atlas.View),
    HasAtlasTexture = atlas.Count > 0,
    HudFont = imgui.LoadFont(Path.Combine(AppContext.BaseDirectory, GameArt.FontPath), 18f),
};
Console.WriteLine($"Loaded {atlas.Count} Kenney sprites");
var renderer = new GameRenderer { Art = art };
var keyboard = new KeyboardInput(window);
var menu = new TitleMenu(keyboard);
var sim = new Simulation();
var debug = new DebugUi(vk.DeviceName, vk.ValidationEnabled) { GodMode = args.Contains("--god"), SpriteStress = IntArg("--sprites", 0) };
var perf = new PerfLog();
var inputs = new SimInput[SimState.MaxPlayers];

// --- Match lifecycle --------------------------------------------------------------------------
bool inMenu = cliMode == null;
SimState state = null!;
SlotController[] controllers = [];
var setup = twoVsTwo ? MatchSetups.TwoVsTwo : MatchSetups.OneVsOne;

void StartMatch(GameMode mode, BotDifficulty difficulty, Crossfire.Sim.PlayerSetup[] layout)
{
    state?.Dispose();
    setup = layout;
    state = SimState.CreateMatch(layout, seed, debug.Rules);
    controllers = MatchSetups.Controllers(mode, difficulty, layout.Length);
    ApplyControllerLabels();
}

void ApplyControllerLabels()
{
    var hints = new string[]?[controllers.Length];
    var tags = new string?[SimState.MaxPlayers];
    for (int i = 0; i < controllers.Length; i++)
    {
        hints[i] = controllers[i].Hints;
        if (controllers[i].IsBot) tags[i] = $"BOT · {controllers[i].Difficulty.ToString().ToUpperInvariant()}";
    }
    renderer.ControlHints = hints;
    renderer.PlayerTags = tags;
}

void OpenMenu()
{
    inMenu = true;
    menu.Open();
    StartMatch(GameMode.Demo, BotDifficulty.Normal, MatchSetups.OneVsOne); // attract mode behind the menu
}

if (inMenu) OpenMenu();
else StartMatch(cliMode!.Value, startDifficulty, setup);

const double TickSeconds = 1.0 / Crossfire.Sim.Rules.MatchRules.TicksPerSecond;
var clock = Stopwatch.StartNew();
double previous = clock.Elapsed.TotalSeconds;
double accumulator = 0;
bool f1Was = false, escWas = true;

Console.WriteLine($"CROSSFIRE running on {vk.DeviceName} (validation {(vk.ValidationEnabled ? "on" : "off")}, vsync {(vsync ? "on" : "off")})");

while (!window.ShouldClose)
{
    Glfw.PollEvents();
    bool f1 = window.GetKey(GlfwKey.F1) == GlfwKeyAction.Press;
    if (f1 && !f1Was) debug.Visible = !debug.Visible;
    f1Was = f1;

    long allocBefore = GC.GetAllocatedBytesForCurrentThread();
    double now = clock.Elapsed.TotalSeconds;
    double frameDt = Math.Min(now - previous, 0.25); // avoid spiral of death after a stall
    previous = now;

    // Menu / Esc handling.
    bool esc = window.GetKey(GlfwKey.Escape) == GlfwKeyAction.Press;
    if (inMenu)
    {
        switch (menu.Update((float)frameDt))
        {
            case TitleMenu.Result.Quit:
                window.ShouldClose = true;
                break;
            case TitleMenu.Result.Start:
                inMenu = false;
                StartMatch(menu.Mode, menu.Difficulty, setup.Length == 4 ? MatchSetups.TwoVsTwo : MatchSetups.OneVsOne);
                accumulator = 0;
                break;
        }
    }
    else if (esc && !escWas)
    {
        OpenMenu();
        accumulator = 0;
    }
    escWas = esc;

    // Fixed-timestep simulation.
    if (!debug.Paused) accumulator += frameDt;
    int steps = debug.ConsumeSingleStep() ? 1 : 0;
    while (accumulator >= TickSeconds) { accumulator -= TickSeconds; steps++; }
    long simStart = Stopwatch.GetTimestamp();
    bool keyboardBlocked = inMenu || imgui.WantsKeyboard;
    for (int i = 0; i < steps; i++)
    {
        if (stressBullets > 0 && state.Bullets.Count < stressBullets)
            DebugUi.SpawnStressBullets(state, stressBullets - state.Bullets.Count);
        for (int p = 0; p < state.PlayerCount; p++)
            inputs[p] = p < controllers.Length ? controllers[p].Read(state, p, keyboard, keyboardBlocked) : default;
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
    var oldSetup = setup;
    debug.Draw(state, sim, ref setup, seed, out bool restart, frameDt, simMs, batch.Count, batch.Dropped, controllers, out bool controllersChanged);
    if (restart)
    {
        var mode = controllers.Length > 1 && !controllers[0].IsBot && !controllers[1].IsBot ? GameMode.Local
            : controllers.Length > 0 && !controllers[0].IsBot ? GameMode.VsBot : GameMode.Demo;
        inMenu = false;
        StartMatch(mode, controllers.Length > 1 ? controllers[^1].Difficulty : BotDifficulty.Normal, setup);
        accumulator = 0;
    }
    else if (controllersChanged)
    {
        ApplyControllerLabels();
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
        if (inMenu) menu.Draw(renderer, window.Width, window.Height);
        else renderer.DrawHud(state, view.ScaledBy(1f / uiScale), window.Width, window.Height, alpha);
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
atlas.Dispose();
sim.Dispose();
state.Dispose();
vk.Dispose();
window.Dispose();
Glfw.Terminate();
