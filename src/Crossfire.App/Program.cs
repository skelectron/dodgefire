using System.Diagnostics;
using Brutal.GlfwApi;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Crossfire.App;
using Crossfire.Audio;
using Crossfire.Input;
using Crossfire.Render;
using Crossfire.Sim;
using Crossfire.Sim.Bots;
using Crossfire.Sim.Replays;
using Crossfire.Sim.Stats;

// CROSSFIRE: team bullet-hell PvP.
// Args: --mode team|vsbot|local|demo (skip the title menu), --difficulty easy|normal|hard, --role dodger|gunner,
//       --keyboard (keyboard instead of mouse), --2v2 (1v1 modes: bots fill slots 3-4)
//       --validate (Vulkan validation layers), --novsync, --seed N
//       --stress N (keep ~N bullets alive), --sprites N (static sprite stress), --god (Dodgers invulnerable),
//       --perf-log (stats to stdout every 5 s), --replay <file> (watch a saved replay)

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
    "team" => GameMode.Team,
    "local" => GameMode.Local,
    "demo" => GameMode.Demo,
    _ => null,
};
var startRole = StrArg("--role") == "gunner" ? Crossfire.Sim.Players.PlayerRole.Gunner : Crossfire.Sim.Players.PlayerRole.Dodger;
var startControls = args.Contains("--keyboard") ? ControlScheme.Keyboard : ControlScheme.Mouse;
int replayArgIndex = Array.IndexOf(args, "--replay");
string? replayPath = replayArgIndex >= 0 && replayArgIndex + 1 < args.Length ? args[replayArgIndex + 1] : null;
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
var audio = new AudioSystem(Path.Combine(AppContext.BaseDirectory, "assets", "kenney", "audio"));
var keyboard = new KeyboardInput(window);
var mouse = new MouseInput(window);
var menu = new TitleMenu(keyboard, mouse);
menu.SetPatternCosts(Crossfire.Sim.Rules.MatchRules.Default);
var results = new ResultsScreen(keyboard, mouse);
var sim = new Simulation();
var debug = new DebugUi(vk.DeviceName, vk.ValidationEnabled) { GodMode = args.Contains("--god"), SpriteStress = IntArg("--sprites", 0), Audio = audio };
var perf = new PerfLog();
var inputs = new SimInput[SimState.MaxPlayers];
var stats = new MatchStats();
var recorder = new ReplayRecorder();
string replayFile = Path.Combine(AppContext.BaseDirectory, "replays", "last.cfreplay");

// --- Match lifecycle --------------------------------------------------------------------------
bool inMenu = cliMode == null;
SimState state = null!;
SlotController[] controllers = [];
var setup = twoVsTwo ? MatchSetups.TwoVsTwo : MatchSetups.OneVsOne;
MatchOptions currentOptions = default;
uint matchSeed = seed;
int matchCounter = 0;
MatchPhase lastPhase = MatchPhase.Countdown;
Replay? lastReplay = null;
ReplayView? replayView = null;

void StartMatch(MatchOptions options, Crossfire.Sim.PlayerSetup[]? layoutOverride = null)
{
    state?.Dispose();
    var layout = layoutOverride ?? MatchSetups.Layout(options);
    if (layoutOverride == null && twoVsTwo && options.Mode != GameMode.Team && layout.Length == 2) layout = MatchSetups.WithLoadouts(MatchSetups.TwoVsTwo, options);
    setup = layout;
    currentOptions = options;
    var rules = debug.Rules;
    if (options.Mode == GameMode.Team) rules.SwapRolesEachRound = false; // you picked your role: keep it
    state = SimState.CreateMatch(layout, matchSeed, rules);
    controllers = MatchSetups.Controllers(options, layout.Length);
    ApplyControllerLabels();
    recorder.Begin(state, matchSeed, layout);
    debug.Tampered = false;
    stats.Reset();
    results.Close();
    lastPhase = state.Phase;
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

    // Team headers ("BOT · HARD", "YOU + 3 BOTS · HARD") and the YOU marker for a lone human.
    int humans = 0, you = -1;
    for (int i = 0; i < controllers.Length; i++) if (!controllers[i].IsBot) { humans++; you = i; }
    renderer.YouSlot = humans == 1 ? you : -1;
    for (int team = 0; team < SimState.TeamCount; team++)
    {
        int bots = 0, people = 0;
        BotDifficulty d = BotDifficulty.Normal;
        for (int i = 0; i < controllers.Length && i < state.PlayerCount; i++)
        {
            if (state.Players[i].Team != team) continue;
            if (controllers[i].IsBot) { bots++; d = controllers[i].Difficulty; } else people++;
        }
        string level = d.ToString().ToUpperInvariant();
        renderer.TeamTags[team] = bots == 0 ? null
            : people == 0 ? $"BOT · {level}"
            : humans == 1 ? $"YOU + {bots} BOT{(bots > 1 ? "S" : "")} · {level}"
            : $"+ {bots} BOT{(bots > 1 ? "S" : "")} · {level}";
    }
}

bool HasHuman()
{
    foreach (var c in controllers) if (!c.IsBot) return true;
    return false;
}

void OpenMenu()
{
    ExitReplay();
    inMenu = true;
    menu.Open();
    StartMatch(new MatchOptions(GameMode.Demo, BotDifficulty.Normal, Crossfire.Sim.Players.PlayerRole.Dodger, ControlScheme.Mouse)); // attract mode behind the menu
}

void OnMatchOver()
{
    if (inMenu) return; // attract mode: no results, no replay
    lastReplay = recorder.Finish(state);
    if (lastReplay != null)
    {
        try { lastReplay.Save(replayFile); }
        catch (Exception e) { Console.WriteLine($"Could not save replay: {e.Message}"); }
    }
    results.Open(state, stats, controllers, renderer.YouSlot, lastReplay != null);
}

void EnterReplay(Replay replay, bool fromCli)
{
    ExitReplay();
    replayView = new ReplayView(replay, keyboard, mouse) { FromCommandLine = fromCli };
}

void ExitReplay()
{
    if (replayView == null) return;
    replayView.Dispose();
    replayView = null;
}

if (replayPath != null)
{
    // Watch a saved replay straight away; leaving it goes to the menu.
    inMenu = false;
    StartMatch(new MatchOptions(GameMode.Demo, BotDifficulty.Normal, Crossfire.Sim.Players.PlayerRole.Dodger, ControlScheme.Mouse));
    var loaded = Replay.Load(replayPath);
    controllers = new SlotController[loaded.Players];
    for (int i = 0; i < controllers.Length; i++) controllers[i] = new SlotController(ControllerKind.Bot);
    renderer.ControlHints = [];
    renderer.PlayerTags = new string?[SimState.MaxPlayers];
    renderer.TeamTags[0] = renderer.TeamTags[1] = null;
    renderer.YouSlot = -1;
    EnterReplay(loaded, fromCli: true);
}
else if (inMenu) OpenMenu();
else StartMatch(new MatchOptions(cliMode!.Value, startDifficulty, startRole, startControls));

const double TickSeconds = 1.0 / Crossfire.Sim.Rules.MatchRules.TicksPerSecond;
var clock = Stopwatch.StartNew();
double previous = clock.Elapsed.TotalSeconds;
double accumulator = 0;
bool f1Was = false, escWas = true;
bool cursorHidden = false;
const int CursorNormal = 0x00034001, CursorHidden = 0x00034002; // GLFW_CURSOR_NORMAL / GLFW_CURSOR_HIDDEN
Action<SimState> onReplayStep = s => { renderer.ConsumeEvents(s); audio.ConsumeEvents(s); }; // cached: no per-frame closure
bool mWas = false;
int menuFocus = menu.FocusSignature, menuValue = menu.ValueSignature, resultsFocus = -1;

Console.WriteLine($"CROSSFIRE running on {vk.DeviceName} (validation {(vk.ValidationEnabled ? "on" : "off")}, vsync {(vsync ? "on" : "off")})");

while (!window.ShouldClose)
{
    Glfw.PollEvents();
    bool f1 = window.GetKey(GlfwKey.F1) == GlfwKeyAction.Press;
    if (f1 && !f1Was) debug.Visible = !debug.Visible;
    f1Was = f1;
    bool mKey = window.GetKey(GlfwKey.M) == GlfwKeyAction.Press;
    if (mKey && !mWas && !imgui.WantsKeyboard) audio.Muted = !audio.Muted;
    mWas = mKey;

    long allocBefore = GC.GetAllocatedBytesForCurrentThread();
    double now = clock.Elapsed.TotalSeconds;
    double frameDt = Math.Min(now - previous, 0.25); // avoid spiral of death after a stall
    previous = now;

    // Menu / Esc / results handling.
    bool esc = window.GetKey(GlfwKey.Escape) == GlfwKeyAction.Press;
    bool escPressed = esc && !escWas;
    escWas = esc;
    if (inMenu)
    {
        switch (menu.Update((float)frameDt, imgui.WantsMouse))
        {
            case TitleMenu.Result.Quit:
                window.ShouldClose = true;
                break;
            case TitleMenu.Result.Start:
                audio.Play(Sfx.UiConfirm);
                inMenu = false;
                StartMatch(menu.Options);
                accumulator = 0;
                break;
        }
    }
    if (inMenu)
    {
        // Menu feedback sounds: moving the highlight, changing a value.
        int focusSig = menu.FocusSignature, valueSig = menu.ValueSignature;
        if (valueSig != menuValue) audio.Play(Sfx.UiConfirm);
        else if (focusSig != menuFocus) audio.Play(Sfx.UiMove);
        menuFocus = focusSig;
        menuValue = valueSig;
    }
    else if (replayView != null)
    {
        if (escPressed)
        {
            bool cli = replayView.FromCommandLine;
            ExitReplay();
            if (cli) OpenMenu(); // otherwise back to the results screen
        }
    }
    else if (escPressed)
    {
        OpenMenu();
        accumulator = 0;
    }
    else if (results.IsOpen)
    {
        switch (results.Update((float)frameDt, imgui.WantsMouse))
        {
            case ResultsScreen.Action.Rematch:
                audio.Play(Sfx.UiConfirm);
                matchCounter++;
                matchSeed = seed + (uint)matchCounter * 7919u;
                StartMatch(currentOptions);
                accumulator = 0;
                break;
            case ResultsScreen.Action.Replay when lastReplay != null:
                audio.Play(Sfx.UiConfirm);
                EnterReplay(lastReplay, fromCli: false);
                break;
            case ResultsScreen.Action.Menu:
                OpenMenu();
                accumulator = 0;
                break;
        }
    }

    long simStart = Stopwatch.GetTimestamp();
    int steps = 0;
    float alpha;
    if (replayView != null)
    {
        // Replay: re-simulate recorded inputs; the live match is frozen behind it.
        steps = replayView.Update(frameDt, TickSeconds, imgui.WantsMouse, onReplayStep);
        alpha = replayView.Alpha(TickSeconds);
    }
    else
    {
        // Fixed-timestep simulation.
        if (!debug.Paused) accumulator += frameDt;
        if (debug.ConsumeSingleStep()) steps++;
        while (accumulator >= TickSeconds) { accumulator -= TickSeconds; steps++; }
        // While the results screen is up in a match with humans, hold everyone still (no auto-rematch by bots).
        bool holdInputs = results.IsOpen && HasHuman();
        bool keyboardBlocked = inMenu || imgui.WantsKeyboard;
        bool mouseBlocked = inMenu || imgui.WantsMouse;
        // Mouse pointer -> arena units (shake-free view, window units -> framebuffer pixels).
        {
            float scale = window.Width > 0 ? vk.Extent.Width / (float)window.Width : 1f;
            var stable = GameRenderer.StableView(state, vk.Extent.Width, vk.Extent.Height, scale);
            var (mx, my) = mouse.WindowPos;
            mouse.SetArenaTarget(stable.ToArenaX(mx * scale), stable.ToArenaY(my * scale));
        }
        for (int i = 0; i < steps; i++)
        {
            if (stressBullets > 0 && state.Bullets.Count < stressBullets)
                DebugUi.SpawnStressBullets(state, stressBullets - state.Bullets.Count);
            for (int p = 0; p < state.PlayerCount; p++)
                inputs[p] = holdInputs || p >= controllers.Length ? default : controllers[p].Read(state, p, keyboard, mouse, keyboardBlocked, mouseBlocked);
            if (debug.GodMode)
                for (int p = 0; p < state.PlayerCount; p++) state.Players[p].InvulnTicks = Math.Max(state.Players[p].InvulnTicks, 2);
            if (stressBullets > 0 || debug.GodMode || debug.Tampered) recorder.MarkNotReplayable();
            recorder.Record(state, inputs.AsSpan(0, state.PlayerCount));
            sim.Step(state, inputs);
            stats.Consume(state);
            renderer.ConsumeEvents(state);
            if (!inMenu) audio.ConsumeEvents(state); // the attract match behind the menu stays quiet

            if (lastPhase != MatchPhase.MatchOver && state.Phase == MatchPhase.MatchOver) OnMatchOver();
            else if (lastPhase == MatchPhase.MatchOver && state.Phase != MatchPhase.MatchOver)
            {
                // Rematch started inside the simulation (bot-only matches restart by themselves).
                stats.Reset();
                results.Close();
            }
            lastPhase = state.Phase;
        }
        alpha = (float)(accumulator / TickSeconds);
    }
    double simMs = steps > 0 ? Stopwatch.GetElapsedTime(simStart).TotalMilliseconds / steps : -1;
    renderer.Update((float)frameDt);
    audio.SetMusic(inMenu ? Music.Menu : Music.Match);
    audio.Update((float)frameDt);
    if (results.IsOpen && replayView == null && results.Focus != resultsFocus) { if (resultsFocus >= 0) audio.Play(Sfx.UiMove); resultsFocus = results.Focus; }
    else if (!results.IsOpen) resultsFocus = -1;
    var shown = replayView?.Player.State ?? state;
    renderer.ShowBanners = replayView == null && !results.IsOpen;
    renderer.ShowControlHints = replayView == null;

    // Window size changes -> recreate swapchain.
    var fbSize = window.FramebufferSize;
    if (fbSize.X > 0 && fbSize.Y > 0 && (fbSize.X != vk.Extent.Width || fbSize.Y != vk.Extent.Height)) vk.RequestResize();

    imgui.NewFrame();
    if (!imgui.WantsMouse)
    {
        if (replayView != null) replayView.AddWheel(imgui.MouseWheel);
        else if (!inMenu) mouse.AddWheel(imgui.MouseWheel);
    }
    bool mousePlayer = false;
    bool playing = !inMenu && replayView == null && !results.IsOpen;
    for (int i = 0; i < SimState.MaxPlayers; i++)
    {
        bool isMouse = playing && i < controllers.Length && controllers[i].IsMouse;
        mousePlayer |= isMouse;
        renderer.SelectedPattern[i] = isMouse ? mouse.SelectedPattern : -1;
        renderer.Immediate[i] = isMouse;
    }
    // Hide the OS pointer while a mouse player is in a match (the game draws its own reticle).
    bool hideCursor = mousePlayer && !debug.Visible;
    if (hideCursor != cursorHidden)
    {
        cursorHidden = hideCursor;
        window.SetInputMode(GlfwInputMode.Cursor, hideCursor ? CursorHidden : CursorNormal);
    }
    debug.Draw(state, sim, ref setup, seed, out bool restart, frameDt, simMs, batch.Count, batch.Dropped, controllers, out bool controllersChanged);
    if (restart)
    {
        var mode = controllers.Length > 2 && !controllers[0].IsBot ? GameMode.Team
            : controllers.Length > 1 && !controllers[0].IsBot && !controllers[1].IsBot ? GameMode.Local
            : controllers.Length > 0 && !controllers[0].IsBot ? GameMode.VsBot : GameMode.Demo;
        var controls = controllers.Length > 0 && controllers[0].Kind == ControllerKind.Mouse ? ControlScheme.Mouse : ControlScheme.Keyboard;
        inMenu = false;
        ExitReplay();
        StartMatch(new MatchOptions(mode, controllers.Length > 1 ? controllers[^1].Difficulty : BotDifficulty.Normal, startRole, controls, currentOptions.Loadout), setup);
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
        var view = renderer.MakeView(shown, fbW, fbH, uiScale);
        renderer.DrawArena(batch, shown, alpha);
        if (mousePlayer && mouse.HasTarget) renderer.DrawPointer(batch, mouse.ArenaX, mouse.ArenaY);
        debug.DrawSpriteStress(batch, shown);
        batch.Flush(cmd, view);
        if (inMenu) menu.Draw(renderer, window.Width, window.Height);
        else
        {
            renderer.DrawHud(shown, view.ScaledBy(1f / uiScale), window.Width, window.Height, alpha);
            if (replayView != null) replayView.Draw(renderer, window.Width, window.Height);
            else results.Draw(renderer, window.Width, window.Height);
        }
        imgui.Render(cmd);
        vk.EndFrame();
    }
    else
    {
        imgui.DiscardFrame();
    }

    long frameAlloc = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
    debug.RecordFrameAllocation(frameAlloc);
    if (perfLog) perf.Record(frameDt, simMs, frameAlloc, shown.Bullets.Count, batch.Count, debug.Visible);
}

vk.Device.WaitIdle();
ExitReplay();
audio.Dispose();
imgui.Dispose();
batch.Dispose();
atlas.Dispose();
sim.Dispose();
state.Dispose();
vk.Dispose();
window.Dispose();
Glfw.Terminate();