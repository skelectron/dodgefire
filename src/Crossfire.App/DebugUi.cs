using Brutal.ImGuiApi;
using Brutal.ImPlotApi;
using Brutal.Numerics;
using Crossfire.Render;
using Crossfire.Sim;
using Crossfire.Sim.Bots;
using Crossfire.Sim.Patterns;
using Crossfire.Sim.Players;
using Crossfire.Sim.Rules;

namespace Crossfire.App;

/// <summary>
/// F1 debug window: performance graphs, stress tests, determinism hash and live rule tuning.
/// Tuning edits the running state's rules (deterministic: rules are part of the state) and are
/// kept for restarts.
/// </summary>
internal sealed class DebugUi(string deviceName, bool validation)
{
    private const int History = 240;
    private readonly float[] _frameMs = new float[History];
    private readonly float[] _simMs = new float[History];
    private int _historyIndex;
    private long _lastFrameAlloc;
    private long _maxFrameAlloc;
    private int _gen0Start = GC.CollectionCount(0);
    private ulong _hash;
    private int _hashTick = -1;
    private bool _stepOnce;

    // Sprite stress (Phase 1 check: 50k static sprites).
    private int _spriteStress;

    public int SpriteStress { get => _spriteStress; set => _spriteStress = Math.Clamp(value, 0, 100_000); }
    private float[]? _stressX, _stressY;

    public bool Visible { get; set; }
    public bool Paused { get; private set; }
    public bool GodMode { get; set; }
    /// <summary>Audio volumes are tuned from the panel when sound is available.</summary>
    public Crossfire.Audio.AudioSystem? Audio { get; set; }
    /// <summary>Set when the panel edited the state outside of inputs (stress spawn): the replay becomes invalid.</summary>
    public bool Tampered { get; set; }
    public MatchRules Rules { get; private set; } = MatchRules.Default;

    public bool ConsumeSingleStep()
    {
        bool s = _stepOnce;
        _stepOnce = false;
        return s;
    }

    public void RecordFrameAllocation(long bytes)
    {
        _lastFrameAlloc = bytes;
        _maxFrameAlloc = Math.Max(_maxFrameAlloc, bytes);
    }

    public void Draw(SimState s, Simulation sim, ref PlayerSetup[] setup, uint seed, out bool restart, double frameDt, double simMs, int sprites, int dropped,
        SlotController[] controllers, out bool controllersChanged)
    {
        restart = false;
        controllersChanged = false;
        _frameMs[_historyIndex] = (float)(frameDt * 1000);
        _simMs[_historyIndex] = (float)Math.Max(0, simMs);
        _historyIndex = (_historyIndex + 1) % History;
        if (!Visible) return;

        var pos = new float2(10, 90);
        ImGui.SetNextWindowPos(in pos, ImGuiCond.FirstUseEver, default);
        ImGui.SetNextWindowBgAlpha(0.88f);
        if (!ImGui.Begin("CROSSFIRE debug (F1)", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.End();
            return;
        }

        ImGui.Text($"{deviceName}  ·  validation {(validation ? "on" : "off")}");
        ImGui.Text($"FPS {ImGui.GetIO().Framerate:0}  ·  frame {frameDt * 1000:0.00} ms  ·  sim {Average(_simMs):0.000} ms/tick (max {Max(_simMs):0.000})");
        ImGui.Text($"bullets {s.Bullets.Count}/{s.Bullets.Capacity}  ·  sprites {sprites}{(dropped > 0 ? $" (+{dropped} dropped)" : "")}");
        ImGui.Text($"GC: {_lastFrameAlloc} B this frame, max {_maxFrameAlloc} B  ·  gen0 collections {GC.CollectionCount(0) - _gen0Start}");
        if (ImGui.Button("reset GC counters", default))
        {
            _maxFrameAlloc = 0;
            _gen0Start = GC.CollectionCount(0);
        }

        float2? plotSize = new float2(420, 140);
        if (ImPlot.BeginPlot("##frame", in plotSize, ImPlotFlags.NoMenus | ImPlotFlags.NoBoxSelect))
        {
            ImPlot.SetupAxes("", "ms", ImPlotAxisFlags.NoTickLabels, ImPlotAxisFlags.None);
            ImPlot.SetupAxisLimits(ImAxis.Y1, 0, 25, ImPlotCond.Always);
            ImPlot.SetupAxisLimits(ImAxis.X1, 0, History, ImPlotCond.Always);
            ImPlot.PlotLine("frame ms", _frameMs, 1, 0, ImPlotLineFlags.None, _historyIndex, sizeof(float));
            ImPlot.PlotLine("sim ms/tick", _simMs, 1, 0, ImPlotLineFlags.None, _historyIndex, sizeof(float));
            ImPlot.EndPlot();
        }

        if (ImGui.CollapsingHeader("Match & stress", ImGuiTreeNodeFlags.DefaultOpen))
        {
            bool paused = Paused;
            if (ImGui.Checkbox("pause", ref paused)) Paused = paused;
            ImGui.SameLine(0, -1);
            if (ImGui.Button("step 1 tick", default)) _stepOnce = true;
            ImGui.SameLine(0, -1);
            bool god = GodMode;
            if (ImGui.Checkbox("god mode", ref god)) GodMode = god;

            if (ImGui.Button("restart 1v1", default))
            {
                setup = [new(0, PlayerRole.Dodger), new(1, PlayerRole.Gunner)];
                restart = true;
            }
            ImGui.SameLine(0, -1);
            if (ImGui.Button("restart 2v2 layout", default))
            {
                setup = [new(0, PlayerRole.Dodger), new(1, PlayerRole.Gunner), new(1, PlayerRole.Dodger), new(0, PlayerRole.Gunner)];
                restart = true;
            }

            // Per-slot controller: click to cycle Mouse -> Keyboard L -> Keyboard R -> Gamepad (first connected) -> Bot Easy/Normal/Hard.
            for (int i = 0; i < controllers.Length; i++)
            {
                ImGui.PushID(100 + i);
                if (ImGui.Button($"P{i + 1}: {controllers[i].Label}", default))
                {
                    var c = controllers[i];
                    if (!c.IsBot)
                    {
                        if (c.Kind == ControllerKind.Gamepad) c.SetBot(BotDifficulty.Easy);
                        else c.SetKind(c.Kind switch
                        {
                            ControllerKind.Mouse => ControllerKind.KeyboardLeft,
                            ControllerKind.KeyboardLeft => ControllerKind.KeyboardRight,
                            ControllerKind.KeyboardRight => ControllerKind.Gamepad,
                            _ => ControllerKind.Mouse,
                        });
                    }
                    else if (c.Difficulty == BotDifficulty.Hard) c.SetKind(ControllerKind.Mouse);
                    else c.SetBot(c.Difficulty + 1);
                    controllersChanged = true;
                }
                ImGui.PopID();
                if (i % 2 == 0 && i + 1 < controllers.Length) ImGui.SameLine(0, -1);
            }

            if (ImGui.Button("spawn 20k bullets", default)) { SpawnStressBullets(s, 20_000); Tampered = true; }
            ImGui.SameLine(0, -1);
            if (ImGui.Button("clear bullets", default)) s.Bullets.Clear();
            ImGui.SliderInt("static sprite stress", ref _spriteStress, 0, 100_000, "%d", ImGuiSliderFlags.None);

            if (ImGui.Button("hash state", default))
            {
                _hash = s.ComputeHash();
                _hashTick = s.Tick;
            }
            if (_hashTick >= 0)
            {
                ImGui.SameLine(0, -1);
                ImGui.Text($"tick {_hashTick}: {_hash:X16}");
            }
        }

        if (Audio is { Enabled: true } audio && ImGui.CollapsingHeader("Audio", ImGuiTreeNodeFlags.None))
        {
            float master = audio.MasterVolume, music = audio.MusicVolume, sfx = audio.SfxVolume;
            bool muted = audio.Muted;
            if (ImGui.SliderFloat("master", ref master, 0, 1, "%.2f", ImGuiSliderFlags.None)) audio.MasterVolume = master;
            if (ImGui.SliderFloat("music", ref music, 0, 1, "%.2f", ImGuiSliderFlags.None)) audio.MusicVolume = music;
            if (ImGui.SliderFloat("effects", ref sfx, 0, 1, "%.2f", ImGuiSliderFlags.None)) audio.SfxVolume = sfx;
            if (ImGui.Checkbox("mute (M)", ref muted)) audio.Muted = muted;
        }

        if (ImGui.CollapsingHeader("Tuning (applies live)", ImGuiTreeNodeFlags.None))
        {
            ref var r = ref s.Rules;
            FixSlider("dodger speed", ref r.DodgerSpeed, 1, 10);
            FixSlider("focus speed", ref r.DodgerFocusSpeed, 0.5f, 6);
            FixSlider("hitbox radius", ref r.HitboxRadius, 1, 8);
            FixSlider("graze radius", ref r.GrazeRadius, 5, 40);
            TickSlider("invulnerability (s)", ref r.InvulnTicks, 0, 5);
            FixSlider("hit clear radius", ref r.HitClearRadius, 0, 300);
            ImGui.SliderInt("flip length (ticks)", ref r.FlipTicks, 1, 60, "%d", ImGuiSliderFlags.None);
            TickSlider("flip cooldown (s)", ref r.FlipCooldownTicks, 0, 10);
            FixSlider("flip speed x", ref r.FlipSpeedMultiplier, 1, 3);
            FixSlider("break per graze", ref r.BreakPerGraze, 0, 20);
            FixSlider("break hit penalty", ref r.BreakHitPenalty, 0, 100);
            ImGui.SliderInt("break invuln (ticks)", ref r.BreakInvulnTicks, 0, 180, "%d", ImGuiSliderFlags.None);
            FixSlider("cursor speed", ref r.CursorSpeed, 2, 20);
            FixPerSecondSlider("energy regen / s", ref r.EnergyRegenPerTick, 1, 50);
            FixSlider("anti-spawn radius", ref r.AntiSpawnRadius, 0, 300);
            ImGui.SliderInt("telegraph (ticks)", ref r.TelegraphTicks, 0, 90, "%d", ImGuiSliderFlags.None);
            TickSlider("survival time (s)", ref r.SurvivalTicks, 5, 120);
            ImGui.SliderInt("rounds to win", ref r.RoundsToWin, 1, 5, "%d", ImGuiSliderFlags.None);
            ImGui.Checkbox("crossfire combos", ref r.CombosEnabled);
            FixSlider("combo radius", ref r.ComboRadius, 100, 640);
            FixSlider("combo spacing", ref r.ComboSpacing, 12, 60);
            ImGui.SliderInt("combo gap (bullets)", ref r.ComboGapSlots, 1, 8, "%d", ImGuiSliderFlags.None);
            FixSlider("combo bullet speed", ref r.ComboBulletSpeed, 0.5f, 8);
            FixSlider("combo refund", ref r.ComboRefund, 0, 50);
            TickSlider("combo cooldown (s)", ref r.ComboCooldownTicks, 0, 15);
            ImGui.Checkbox("swap roles each round", ref r.SwapRolesEachRound);

            for (int k = 0; k < MatchRules.PatternKinds; k++)
            {
                ref var def = ref r.Patterns[k];
                if (!ImGui.CollapsingHeader($"pattern {k + 1}: {def.Kind}", ImGuiTreeNodeFlags.None)) continue;
                ImGui.PushID(k);
                FixSlider("cost", ref def.Cost, 0, 100);
                TickSlider("cooldown (s)", ref def.CooldownTicks, 0, 10);
                FixSlider("bullet speed", ref def.BulletSpeed, 0.5f, 10);
                ImGui.SliderInt("count", ref def.Count, 1, 64, "%d", ImGuiSliderFlags.None);
                ImGui.SliderInt("fire interval", ref def.FireInterval, 1, 60, "%d", ImGuiSliderFlags.None);
                ImGui.SliderInt("duration (ticks)", ref def.DurationTicks, 1, 600, "%d", ImGuiSliderFlags.None);
                ImGui.PopID();
            }
            if (ImGui.Button("reset to defaults", default)) r = MatchRules.Default;
            Rules = r;
        }

        ImGui.End();
    }

    public void DrawSpriteStress(SpriteBatch batch, SimState s)
    {
        if (_spriteStress <= 0) return;
        if (_stressX == null || _stressX.Length < _spriteStress)
        {
            var rng = new Random(42);
            _stressX = new float[100_000];
            _stressY = new float[100_000];
            for (int i = 0; i < _stressX.Length; i++)
            {
                _stressX[i] = (float)rng.NextDouble() * s.Rules.ArenaWidth;
                _stressY[i] = (float)rng.NextDouble() * s.Rules.ArenaHeight;
            }
        }
        var c = new Color4(0.9f, 0.9f, 0.3f, 0.8f);
        for (int i = 0; i < _spriteStress; i++) batch.Add(_stressX[i], _stressY![i], 3, 3, c, SpriteShape.Bullet);
    }

    public static void SpawnStressBullets(SimState s, int count)
    {
        var rng = new SimRng((uint)s.Tick + 1);
        int w = s.Rules.ArenaWidth / 2, h = s.Rules.ArenaHeight;
        for (int i = 0; i < count; i++)
        {
            int half = i & 1;
            var pos = new FixVec2(Fix.FromInt(10 + rng.NextInt(w - 20) + half * w), Fix.FromInt(10 + rng.NextInt(h - 20)));
            s.Bullets.TrySpawn(pos, FixMath.FromAngle(rng.NextInt(4096), Fix.Ratio(1 + rng.NextInt(4), 8)), half, (BulletType)(i % 3));
        }
    }

    private static float Average(float[] a)
    {
        float sum = 0;
        foreach (var v in a) sum += v;
        return sum / a.Length;
    }

    private static float Max(float[] a)
    {
        float m = 0;
        foreach (var v in a) m = Math.Max(m, v);
        return m;
    }

    private static float ToFloat(Fix f) => f.Raw / (float)Fix.OneRaw;
    private static Fix FromFloat(float f) => new((int)MathF.Round(f * Fix.OneRaw));

    private static void FixSlider(string label, ref Fix value, float min, float max)
    {
        float v = ToFloat(value);
        if (ImGui.SliderFloat(label, ref v, min, max, "%.2f", ImGuiSliderFlags.None)) value = FromFloat(v);
    }

    private static void FixPerSecondSlider(string label, ref Fix perTick, float min, float max)
    {
        float v = ToFloat(perTick) * MatchRules.TicksPerSecond;
        if (ImGui.SliderFloat(label, ref v, min, max, "%.1f", ImGuiSliderFlags.None)) perTick = FromFloat(v / MatchRules.TicksPerSecond);
    }

    private static void TickSlider(string label, ref int ticks, float minSeconds, float maxSeconds)
    {
        float v = ticks / (float)MatchRules.TicksPerSecond;
        if (ImGui.SliderFloat(label, ref v, minSeconds, maxSeconds, "%.2f", ImGuiSliderFlags.None)) ticks = (int)MathF.Round(v * MatchRules.TicksPerSecond);
    }
}
