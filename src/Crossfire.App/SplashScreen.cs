using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Crossfire.Input;
using Crossfire.Render;

namespace Crossfire.App;

/// <summary>
/// Startup splash required by the BRUTAL License v1.0 (2.2): the official BRUTAL logo, unaltered except for
/// scaling, shown before gameplay for long enough to read, then the CROSSFIRE title. A key, click or pad button
/// skips it, but only after <see cref="MinShowSeconds"/>, so the logo is always legible.
/// </summary>
internal sealed class SplashScreen
{
    public const float MinShowSeconds = 1.2f;
    private const float LogoIn = 0.5f, LogoHold = 2.2f, LogoOut = 0.45f;
    private const float TitleIn = 0.35f, TitleHold = 0.9f, TitleOut = 0.35f;
    private const float LogoEnd = LogoIn + LogoHold + LogoOut;
    private const float TotalSeconds = LogoEnd + TitleIn + TitleHold + TitleOut;

    // From the BRUTAL NOTICE.txt (licence 2.1 asks for it in the credits; shown here as well).
    public const string BrutalCopyright = "BRUTAL game development framework is Copyright (c) 2024 Ahwoo Limited.";
    public const string BrutalUrl = "www.ahwoo.com/brutal";

    private readonly ImTextureRef _logo;
    private readonly bool _hasLogo;
    private readonly float _logoAspect;
    private readonly KeyboardInput _keyboard;
    private readonly MouseInput _mouse;
    private readonly GamepadInput _pads;
    private readonly GlfwWindow _window;
    private float _time;

    public bool Done { get; private set; }

    public SplashScreen(GlfwWindow window, KeyboardInput keyboard, MouseInput mouse, GamepadInput pads, ImTextureRef logo, int logoWidth, int logoHeight)
    {
        _window = window;
        _keyboard = keyboard;
        _mouse = mouse;
        _pads = pads;
        _logo = logo;
        _hasLogo = logoWidth > 0 && logoHeight > 0;
        _logoAspect = _hasLogo ? logoWidth / (float)logoHeight : 1f;
    }

    private static readonly GlfwKey[] SkipKeys = [GlfwKey.Space, GlfwKey.Enter, GlfwKey.KpEnter, GlfwKey.Escape];

    public void Update(float dt)
    {
        if (Done) return;
        _time += dt;
        bool skip = _mouse.LeftDown || _mouse.RightDown || _pads.AnyMenu != MenuActions.None;
        foreach (var k in SkipKeys) skip |= _keyboard.IsDown(k);
        if (skip && _time >= MinShowSeconds)
        {
            // Jump to the title card if the logo was still up, otherwise finish.
            if (_time < LogoEnd) { if (!_held) _time = LogoEnd; }
            else if (!_held) Done = true;
        }
        _held = skip; // one skip per press: holding a key does not also skip the title card
        if (_time >= TotalSeconds) Done = true;
    }

    private bool _held;

    private static float Envelope(float t, float fadeIn, float hold, float fadeOut)
    {
        if (t < 0) return 0;
        if (t < fadeIn) return t / fadeIn;
        if (t < fadeIn + hold) return 1;
        return Math.Clamp(1 - (t - fadeIn - hold) / fadeOut, 0, 1);
    }

    public void Draw(GameRenderer r, float w, float h)
    {
        var dl = ImGui.GetForegroundDrawList(default);
        var a = new float2(0, 0);
        var b = new float2(w, h);
        ImDrawListExtensions.AddRectFilled(dl, in a, in b, new ImColor8(0, 0, 0, 255), 0f, default);
        float cx = w * 0.5f, cy = h * 0.5f;

        float logo = Envelope(_time, LogoIn, LogoHold, LogoOut);
        if (logo > 0)
        {
            byte alpha = (byte)(logo * 255);
            if (_hasLogo)
            {
                // Scaled only (aspect kept); never tinted or recoloured.
                float lw = Math.Min(w * 0.6f, 900f), lh = lw / _logoAspect;
                var pMin = new float2(cx - lw * 0.5f, cy - lh * 0.5f - 30);
                var pMax = new float2(cx + lw * 0.5f, cy + lh * 0.5f - 30);
                float2? uv0 = new float2(0, 0), uv1 = new float2(1, 1);
                ImColor8? tint = new ImColor8(255, 255, 255, alpha);
                ImDrawListExtensions.AddImage(dl, _logo, in pMin, in pMax, in uv0, in uv1, tint);
                float below = pMax.Y + 36;
                r.Label(dl, "MADE WITH BRUTAL", cx, below, 18, new Color4(0.85f, 0.85f, 0.88f, logo), center: true);
                r.Label(dl, BrutalCopyright, cx, below + 30, 13, new Color4(0.6f, 0.6f, 0.65f, logo), center: true);
                r.Label(dl, BrutalUrl, cx, below + 50, 13, new Color4(0.6f, 0.6f, 0.65f, logo), center: true);
            }
            else
            {
                r.Label(dl, "MADE WITH BRUTAL", cx, cy - 40, 48, new Color4(0.95f, 0.78f, 0.1f, logo), center: true);
                r.Label(dl, BrutalCopyright, cx, cy + 30, 14, new Color4(0.7f, 0.7f, 0.75f, logo), center: true);
                r.Label(dl, BrutalUrl, cx, cy + 52, 14, new Color4(0.7f, 0.7f, 0.75f, logo), center: true);
            }
        }

        float title = Envelope(_time - LogoEnd, TitleIn, TitleHold, TitleOut);
        if (title > 0)
        {
            r.Label(dl, "CROSSFIRE", cx + 3, cy - 46 + 3, 80, new Color4(0, 0, 0, 0.6f * title), center: true);
            r.Label(dl, "CROSSFIRE", cx, cy - 46, 80, new Color4(1f, 0.85f, 0.35f, title), center: true);
            r.Label(dl, "TEAM BULLET-HELL PVP", cx, cy + 44, 18, new Color4(0.75f, 0.78f, 0.9f, title), center: true);
        }
    }
}
