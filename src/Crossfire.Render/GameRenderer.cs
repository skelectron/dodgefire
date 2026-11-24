using Brutal.ImGuiApi;
using Brutal.Numerics;
using Crossfire.Sim;
using Crossfire.Sim.Patterns;
using Crossfire.Sim.Players;
using Crossfire.Sim.Rules;

namespace Crossfire.Render;

/// <summary>
/// Draws a <see cref="SimState"/>: arena and entities as SDF sprites, HUD and banners via ImGui
/// draw lists. Reads the simulation, never writes it. Visual effects (hit bursts, screen shake)
/// live here because they are presentation only.
/// </summary>
public sealed class GameRenderer
{
    // Team palette: A = cyan, B = magenta. Bullets take the colour of the team that FIRED them.
    public static readonly Color4[] TeamColor = [new(0.30f, 0.78f, 1.00f), new(1.00f, 0.36f, 0.42f)];
    public static readonly string[] TeamName = ["BLUE", "RED"];
    private static readonly Color4[] HalfTint = [new(0.045f, 0.065f, 0.095f), new(0.085f, 0.05f, 0.085f)];
    private static readonly string[] PatternNames = ["Ring", "Spiral", "Aimed", "Wall"];
    private static readonly Color4 White = new(1, 1, 1);
    private static readonly Color4 Danger = new(1f, 0.25f, 0.2f);

    public const float HudTop = 76f;
    /// <summary>P label + role + energy bar + value + 4 pattern slots.</summary>
    private const float TeamPanelWidth = 92 + 120 + 34 + 4 * 50;
    public const float HudBottom = 30f;

    /// <summary>Per player slot: control hints shown in the HUD, e.g. ["WASD", "L-Shift", "Q E R F", "Space"].</summary>
    public string[][] ControlHints { get; set; } = [];

    /// <summary>Per player slot: optional tag shown in the HUD, e.g. "BOT · HARD". Null for humans.</summary>
    public string?[] PlayerTags { get; set; } = new string?[SimState.MaxPlayers];

    /// <summary>Kenney sprites + font. When a region is missing the SDF fallback is drawn instead.</summary>
    public GameArt? Art { get; set; }

    private struct Effect
    {
        public float X, Y, Age, Life, Radius, Spin;
        public Color4 Color;
        public SpriteShape Shape;
        public SpriteRegion Region;
    }

    private readonly Effect[] _effects = new Effect[128];
    private readonly int[] _variantScratch = new int[2];
    private static readonly string[] PlayerLabels = ["P1", "P2", "P3", "P4", "P5", "P6", "P7", "P8"];
    private int _effectCursor;
    private float _shake;
    private readonly Random _fxRandom = new(1);
    private int _goBannerTicks;

    private static float F(Fix f) => f.Raw * (1f / Fix.OneRaw);
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    /// <summary>Call after every simulation step to turn sim events into effects.</summary>
    public void ConsumeEvents(SimState s)
    {
        for (int i = 0; i < s.EventCount; i++)
        {
            ref readonly var e = ref s.Events[i];
            float x = F(e.Pos.X), y = F(e.Pos.Y);
            switch (e.Type)
            {
                case SimEventType.Hit:
                    Spawn(x, y, F(s.Rules.HitClearRadius), 0.45f, White, SpriteShape.Ring);
                    Spawn(x, y, 60, 0.3f, TeamColor[e.Team].WithAlpha(0.6f), SpriteShape.Glow);
                    _shake = Math.Max(_shake, 9f);
                    break;
                case SimEventType.Death:
                    Spawn(x, y, 220, 0.8f, TeamColor[e.Team], SpriteShape.Ring);
                    Spawn(x, y, 120, 0.6f, White.WithAlpha(0.8f), SpriteShape.Glow);
                    if (Art != null)
                        for (int k = 0; k < 10; k++)
                        {
                            float ang = k * MathF.Tau / 10f + (float)_fxRandom.NextDouble() * 0.5f;
                            float dist = 20 + _fxRandom.Next(0, 50);
                            SpawnSprite(x + MathF.Cos(ang) * dist, y + MathF.Sin(ang) * dist, 18 + _fxRandom.Next(0, 14), 0.7f, Color4.Lerp(White, TeamColor[e.Team], 0.4f), Art.Stars[k % 3]);
                        }
                    _shake = Math.Max(_shake, 16f);
                    break;
                case SimEventType.Graze:
                    if (Art != null && Art.Stars[0].IsValid)
                        SpawnSprite(x + _fxRandom.Next(-14, 15), y + _fxRandom.Next(-14, 15), 10, 0.3f, new Color4(1f, 0.95f, 0.6f), Art.Stars[_fxRandom.Next(0, 3)]);
                    else
                        Spawn(x + _fxRandom.Next(-8, 9), y + _fxRandom.Next(-8, 9), 8, 0.2f, White.WithAlpha(0.7f), SpriteShape.Glow);
                    break;
                case SimEventType.Placed:
                    Spawn(x, y, 34, 0.25f, TeamColor[e.Team], SpriteShape.Ring);
                    break;
                case SimEventType.Rejected:
                    Spawn(x, y, 22, 0.25f, Danger, SpriteShape.Cross);
                    break;
                case SimEventType.RoundStart:
                    _goBannerTicks = 40;
                    break;
            }
        }
    }

    private void Spawn(float x, float y, float radius, float life, Color4 c, SpriteShape shape)
    {
        _effects[_effectCursor] = new Effect { X = x, Y = y, Radius = radius, Life = life, Color = c, Shape = shape };
        _effectCursor = (_effectCursor + 1) % _effects.Length;
    }

    private void SpawnSprite(float x, float y, float size, float life, Color4 c, SpriteRegion region)
    {
        if (!region.IsValid) return;
        _effects[_effectCursor] = new Effect
        {
            X = x, Y = y, Radius = size, Life = life, Color = c, Shape = SpriteShape.Textured, Region = region,
            Spin = (float)(_fxRandom.NextDouble() * 2 - 1) * 6f,
        };
        _effectCursor = (_effectCursor + 1) % _effects.Length;
    }

    /// <summary>Advances render-only effects by real time.</summary>
    public void Update(float dt)
    {
        for (int i = 0; i < _effects.Length; i++)
            if (_effects[i].Life > 0) _effects[i].Age += dt;
        _shake = Math.Max(0, _shake - dt * 60f);
        if (_goBannerTicks > 0) _goBannerTicks--;
    }

    /// <summary>Builds the arena view for the current framebuffer, including screen shake.</summary>
    public ArenaView MakeView(SimState s, float fbW, float fbH, float uiScale)
    {
        var v = new ArenaView(s.Rules.ArenaWidth, s.Rules.ArenaHeight, fbW, fbH, HudTop * uiScale, HudBottom * uiScale);
        if (_shake <= 0) return v;
        float sx = (float)(_fxRandom.NextDouble() * 2 - 1) * _shake * 0.5f;
        float sy = (float)(_fxRandom.NextDouble() * 2 - 1) * _shake * 0.5f;
        return new ArenaView(s.Rules.ArenaWidth, s.Rules.ArenaHeight, fbW, fbH, HudTop * uiScale + sy, HudBottom * uiScale - sy, sx);
    }

    public unsafe void DrawArena(SpriteBatch b, SimState s, float alpha)
    {
        ref readonly var r = ref s.Rules;
        float w = r.ArenaWidth, h = r.ArenaHeight, mid = w * 0.5f;
        bool playing = s.Phase == MatchPhase.Playing;
        float a = playing ? alpha : 0f;

        // 1. Space background (tiled Kenney texture, dimmed), team-tinted halves, faint grid, midline.
        b.Rect(0, 0, mid, h, HalfTint[0]);
        b.Rect(mid, 0, mid, h, HalfTint[1]);
        if (Art != null && Art.Background.IsValid)
        {
            const float tile = 192;
            var bgTint = new Color4(0.42f, 0.42f, 0.5f, 1f);
            for (float ty = 0; ty < h; ty += tile)
                for (float tx = 0; tx < w; tx += tile)
                    b.Sprite(Art.Background, tx + tile * 0.5f, ty + tile * 0.5f, tile, bgTint);
            b.Rect(0, 0, mid, h, TeamColor[0].WithAlpha(0.06f));
            b.Rect(mid, 0, mid, h, TeamColor[1].WithAlpha(0.06f));
        }
        var grid = new Color4(1, 1, 1, 0.025f);
        for (float gx = 80; gx < w; gx += 80) if (Math.Abs(gx - mid) > 1) b.Rect(gx - 0.5f, 0, 1, h, grid);
        for (float gy = 80; gy < h; gy += 80) b.Rect(0, gy - 0.5f, w, 1, grid);
        b.Rect(mid - 6, 0, 12, h, new Color4(1, 1, 1, 0.04f));
        b.Rect(mid - 1.5f, 0, 3, h, new Color4(0.9f, 0.9f, 1f, 0.45f));

        // 2. Anti-spawn-kill zones: shown around Dodgers when an enemy cursor is close.
        for (int i = 0; i < s.PlayerCount; i++)
        {
            ref readonly var d = ref s.Players[i];
            if (!d.IsAliveDodger) continue;
            float dx = Lerp(F(d.PrevPos.X), F(d.Pos.X), a), dy = Lerp(F(d.PrevPos.Y), F(d.Pos.Y), a);
            float zone = F(r.AntiSpawnRadius);
            float nearest = float.MaxValue;
            for (int g = 0; g < s.PlayerCount; g++)
            {
                ref readonly var gp = ref s.Players[g];
                if (!gp.IsGunner || gp.Team == d.Team) continue;
                float cx = F(gp.Cursor.X) - dx, cy = F(gp.Cursor.Y) - dy;
                nearest = Math.Min(nearest, MathF.Sqrt(cx * cx + cy * cy));
            }
            float vis = Math.Clamp(1f - (nearest - zone) / zone, 0f, 1f);
            if (vis > 0) b.Circle(dx, dy, zone, Danger.WithAlpha(0.35f * vis), SpriteShape.Ring, 0.03f);
        }

        // 3. Emitters: telegraph (filling arc + shrinking ring), then a pulsing core while firing.
        int tele = r.TelegraphTicks;
        for (int i = 0; i < SimState.MaxEmitters; i++)
        {
            ref readonly var e = ref s.Emitters[i];
            if (!e.Active) continue;
            var c = TeamColor[1 - e.Half];
            float ex = F(e.Pos.X), ey = F(e.Pos.Y);
            if (e.Age <= tele)
            {
                float t = Math.Clamp((e.Age + a) / Math.Max(1, tele), 0f, 1f);
                b.Circle(ex, ey, 30, c.WithAlpha(0.12f), SpriteShape.Disc);
                b.Circle(ex, ey, 30, c, SpriteShape.Arc, t, 0.16f);
                b.Circle(ex, ey, Lerp(70, 30, t), c.WithAlpha(0.25f + 0.5f * t), SpriteShape.Ring, 0.05f);
                b.Circle(ex, ey, 5, White.WithAlpha(0.9f), SpriteShape.Disc);
            }
            else
            {
                float pulse = 0.5f + 0.5f * MathF.Sin(e.Age * 0.5f);
                b.Circle(ex, ey, 16 + 4 * pulse, c.WithAlpha(0.3f), SpriteShape.Glow);
                b.Circle(ex, ey, 6, c.WithAlpha(0.8f), SpriteShape.Disc);
            }
        }

        // 4. Bullets (extrapolated by velocity for smooth motion between ticks).
        var pool = s.Bullets;
        Span<float> visualRadius = [4.5f, 6.5f, 9.5f];
        const float inv = 1f / Fix.OneRaw;
        var c0 = TeamColor[1];
        var c1 = TeamColor[0];
        for (int i = 0; i < pool.Count; i++)
        {
            float x = (pool.PosX[i] + pool.VelX[i] * a) * inv;
            float y = (pool.PosY[i] + pool.VelY[i] * a) * inv;
            float rad = visualRadius[pool.Type[i]];
            b.Add(x, y, rad, rad, pool.Half[i] == 0 ? c0 : c1, SpriteShape.Bullet);
        }

        // 5. Dodgers: Kenney ships facing the midline, thrusters, shield; the hitbox dot always on top.
        int[] variantCounter = _variantScratch;
        variantCounter[0] = variantCounter[1] = 0;
        for (int i = 0; i < s.PlayerCount; i++)
        {
            ref readonly var p = ref s.Players[i];
            if (!p.IsDodger) continue;
            int variant = Math.Min(1, variantCounter[p.Team]++);
            float x = Lerp(F(p.PrevPos.X), F(p.Pos.X), a), y = Lerp(F(p.PrevPos.Y), F(p.Pos.Y), a);
            var c = TeamColor[p.Team];
            var ship = Art != null ? Art.Ship[p.Team, variant] : default;
            float facing = p.Team == 0 ? MathF.PI / 2 : -MathF.PI / 2; // nose toward the midline
            if (p.Lives <= 0)
            {
                if (!b.Sprite(ship, x, y, 30, new Color4(0.35f, 0.35f, 0.4f, 0.55f), facing))
                    b.Circle(x, y, 12, new Color4(0.5f, 0.5f, 0.5f, 0.5f), SpriteShape.Cross, 0.12f, 0f);
                continue;
            }
            bool blink = p.InvulnTicks > 0 && (p.InvulnTicks / 4) % 2 == 0;
            float bodyAlpha = blink ? 0.35f : 1f;
            b.Circle(x, y, 34, c.WithAlpha(0.18f * bodyAlpha), SpriteShape.Glow);
            if (ship.IsValid)
            {
                // Bank slightly with vertical movement (screen-space "lean"); thrust flame while moving.
                float vy = F(p.Pos.Y) - F(p.PrevPos.Y), vx = F(p.Pos.X) - F(p.PrevPos.X);
                float bank = Math.Clamp(vy * 0.06f, -0.3f, 0.3f) * (p.Team == 0 ? 1 : -1);
                float rot = facing + bank;
                bool moving = MathF.Abs(vx) + MathF.Abs(vy) > 0.01f;
                var fire = Art!.Fire[(s.Tick / 3 + i) & 3];
                if (fire.IsValid)
                {
                    // Behind the ship: opposite of the nose direction.
                    float bx = -MathF.Sin(rot), by = MathF.Cos(rot);
                    float len = moving ? (p.Focus ? 12 : 18) : 8;
                    b.Sprite(fire, x + bx * (14 + len * 0.5f), y + by * (14 + len * 0.5f), len, White.WithAlpha(0.9f * bodyAlpha), rot);
                }
                b.Sprite(ship, x, y, 30, White.WithAlpha(bodyAlpha), rot);
                if (p.InvulnTicks > 0 && Art.Shield.IsValid)
                    b.Sprite(Art.Shield, x, y, 50, c.WithAlpha(0.55f + 0.25f * MathF.Sin(p.InvulnTicks * 0.4f)), facing);
            }
            else
            {
                b.Add(x, y, 13, 13, c.WithAlpha(bodyAlpha), SpriteShape.Diamond, 0.22f);
            }
            if (p.Focus) b.Circle(x, y, 20, White.WithAlpha(0.55f), SpriteShape.Ring, 0.08f);
            if (p.InvulnTicks > 0) b.Circle(x, y, 24, White.WithAlpha(0.35f), SpriteShape.Arc, p.InvulnTicks / (float)r.InvulnTicks, 0.08f);
            float hb = MathF.Max(F(r.HitboxRadius), 2.5f);
            b.Circle(x, y, hb + 1.5f, new Color4(0, 0, 0, 0.9f), SpriteShape.Disc);
            b.Circle(x, y, hb, White, SpriteShape.Disc);
        }

        // 6. Gunner cursors (visible to everyone, slightly dim; grey + red ring when placement would be rejected).
        for (int i = 0; i < s.PlayerCount; i++)
        {
            ref readonly var p = ref s.Players[i];
            if (!p.IsGunner) continue;
            float x = Lerp(F(p.PrevCursor.X), F(p.Cursor.X), a), y = Lerp(F(p.PrevCursor.Y), F(p.Cursor.Y), a);
            bool blocked = GunnerSystem.IsTooCloseToDodger(s, p.Team, p.Cursor);
            bool rejectedRecently = p.LastRejectTick >= 0 && s.Tick - p.LastRejectTick < 12;
            var c = blocked || rejectedRecently ? new Color4(0.6f, 0.6f, 0.6f) : TeamColor[p.Team];
            b.Circle(x, y, 18, c.WithAlpha(0.85f), SpriteShape.Cross, 0.09f, 0.3f);
            b.Circle(x, y, 13, c.WithAlpha(0.6f), SpriteShape.Ring, 0.1f);
            if (blocked) b.Circle(x, y, 22, Danger.WithAlpha(0.8f), SpriteShape.Ring, 0.08f);
            // Small team ship marker: shows who owns this reticle.
            if (Art != null) b.Sprite(Art.Ship[p.Team, 0], x + 22, y - 20, 13, White.WithAlpha(0.75f), p.Team == 0 ? MathF.PI / 2 : -MathF.PI / 2);
        }

        // 7. Effects.
        for (int i = 0; i < _effects.Length; i++)
        {
            ref var fx = ref _effects[i];
            if (fx.Life <= 0) continue;
            float t = fx.Age / fx.Life;
            if (t >= 1) { fx.Life = 0; continue; }
            if (fx.Shape == SpriteShape.Textured)
            {
                b.Sprite(fx.Region, fx.X, fx.Y, fx.Radius * (1 - 0.4f * t), fx.Color.WithAlpha(fx.Color.A * (1 - t)), fx.Spin * fx.Age);
                continue;
            }
            float rad = fx.Shape == SpriteShape.Ring ? fx.Radius * (0.3f + 0.7f * t) : fx.Radius;
            b.Circle(fx.X, fx.Y, rad, fx.Color.WithAlpha(fx.Color.A * (1 - t)), fx.Shape, fx.Shape == SpriteShape.Cross ? 0.12f : 0.06f, fx.Shape == SpriteShape.Cross ? 0f : 0f);
        }
    }

    /// <summary>Top HUD (per-team panels), bottom control hints, centre banners. Coordinates in ImGui (window) units.</summary>
    public void DrawHud(SimState s, in ArenaView ui, float windowW, float windowH, float alpha)
    {
        var dl = ImGui.GetBackgroundDrawList(default);
        var fg = ImGui.GetForegroundDrawList(default);
        ref readonly var r = ref s.Rules;

        // Centre: round + score + timer.
        float cx = windowW * 0.5f;
        Text(dl, $"ROUND {s.RoundNumber}  ·  FIRST TO {r.RoundsToWin}", cx, 8, 15, new Color4(0.75f, 0.78f, 0.85f), center: true);
        Text(dl, $"{s.Scores[0]}", cx - 30, 26, 30, TeamColor[0], center: true);
        Text(dl, "-", cx, 26, 30, new Color4(0.6f, 0.6f, 0.65f), center: true);
        Text(dl, $"{s.Scores[1]}", cx + 30, 26, 30, TeamColor[1], center: true);
        int survive = MatchFlow.SurvivalTicksLeft(s);
        if (survive >= 0 && s.Phase is MatchPhase.Playing or MatchPhase.Countdown)
        {
            int secs = (survive + MatchRules.TicksPerSecond - 1) / MatchRules.TicksPerSecond;
            Text(dl, $"SURVIVE {secs}s", cx, 58, 14, secs <= 5 ? Danger : new Color4(0.85f, 0.85f, 0.9f), center: true);
        }
        else if (s.Phase == MatchPhase.Playing && s.RoundTick >= r.SuddenDeathTicks)
        {
            Text(dl, "SUDDEN DEATH: ENERGY x" + r.SuddenDeathRegenMultiplier, cx, 58, 14, Danger, center: true);
        }

        // Team panels.
        for (int team = 0; team < SimState.TeamCount; team++)
        {
            float x0 = team == 0 ? 14 : windowW - 14 - TeamPanelWidth;
            float y = 8;
            Text(dl, TeamName[team], x0, y, 15, TeamColor[team]);
            for (int i = 0; i < s.PlayerCount; i++)
                if (s.Players[i].Active && s.Players[i].Team == team && PlayerTags[i] is { } tag)
                    Text(dl, tag, x0 + 70, y + 2, 12, new Color4(1f, 0.85f, 0.35f));
            y += 20;
            for (int i = 0; i < s.PlayerCount; i++)
            {
                ref readonly var p = ref s.Players[i];
                if (!p.Active || p.Team != team) continue;
                DrawPlayerRow(dl, s, i, x0, y);
                y += 24;
            }
        }

        // Bottom: control hints per player.
        float by = windowH - HudBottom + 7;
        for (int i = 0; i < s.PlayerCount && i < ControlHints.Length; i++)
        {
            ref readonly var p = ref s.Players[i];
            string text = HintText(i, p.Role);
            bool left = p.Team == 0;
            Text(dl, text, left ? 14 : windowW - 14, by, 14, TeamColor[p.Team].Scale(0.85f), center: false, rightAlign: !left);
        }
        Text(dl, "F1 debug · Esc quit", cx, by, 13, new Color4(0.45f, 0.47f, 0.52f), center: true);

        // Player labels above Dodgers (tells teammates apart in 2v2+).
        for (int i = 0; i < s.PlayerCount; i++)
        {
            ref readonly var p = ref s.Players[i];
            if (!p.IsDodger || p.Lives <= 0) continue;
            float a = s.Phase == MatchPhase.Playing ? alpha : 0f;
            float x = ui.ToPixelX(Lerp(F(p.PrevPos.X), F(p.Pos.X), a));
            float y = ui.ToPixelY(Lerp(F(p.PrevPos.Y), F(p.Pos.Y), a));
            Text(fg, PlayerTags[i] != null ? "BOT" : PlayerLabels[i], x, y - 36 * ui.Scale, 13, TeamColor[p.Team].WithAlpha(0.8f), center: true);
        }

        DrawBanner(fg, s, ui);
    }

    private void DrawPlayerRow(ImDrawListPtr dl, SimState s, int index, float x, float y)
    {
        ref readonly var p = ref s.Players[index];
        ref readonly var r = ref s.Rules;
        var c = TeamColor[p.Team];
        Text(dl, $"P{index + 1}", x, y + 1, 15, c);
        if (p.Role == PlayerRole.Dodger)
        {
            Text(dl, "DODGER", x + 30, y + 3, 12, new Color4(0.7f, 0.72f, 0.78f));
            var life = Art != null ? Art.Life[p.Team, 0] : default;
            for (int l = 0; l < r.DodgerLives; l++)
            {
                var center = new float2(x + 100 + l * 20, y + 10);
                bool alive = l < p.Lives;
                if (life.IsValid && Art!.HasAtlasTexture)
                {
                    // Kenney life icon (33x26) from the shared atlas; lost lives are dimmed.
                    var pMin = new float2(center.X - 8, center.Y - 6.5f);
                    var pMax = new float2(center.X + 8, center.Y + 6.5f);
                    float2? uvMin = new float2(life.U0, life.V0), uvMax = new float2(life.U1, life.V1);
                    ImColor8? tint = alive ? new ImColor8(255, 255, 255, 255) : new ImColor8(70, 70, 80, 160);
                    ImDrawListExtensions.AddImage(dl, Art.AtlasTexture, in pMin, in pMax, in uvMin, in uvMax, tint);
                }
                else
                {
                    ImDrawListExtensions.AddCircleFilled(dl, in center, 7, Col(alive ? c : new Color4(0.25f, 0.25f, 0.3f)), 16);
                    ImDrawListExtensions.AddCircle(dl, in center, 7, Col(new Color4(0, 0, 0, 0.8f)), 16, 1.5f);
                }
            }
            Text(dl, $"graze {p.Grazes}", x + 100 + r.DodgerLives * 20 + 6, y + 3, 12, new Color4(0.6f, 0.62f, 0.68f));
        }
        else
        {
            Text(dl, "GUNNER", x + 30, y + 3, 12, new Color4(0.7f, 0.72f, 0.78f));
            // Energy bar with cost ticks.
            float bx = x + 92, bw = 120, bh = 12;
            float fill = Math.Clamp(F(p.Energy) / Math.Max(1f, F(r.EnergyMax)), 0, 1);
            Rect(dl, bx, y + 4, bw, bh, new Color4(0.12f, 0.12f, 0.16f));
            Rect(dl, bx, y + 4, bw * fill, bh, c.Scale(0.9f));
            Text(dl, $"{(int)F(p.Energy)}", bx + bw + 6, y + 3, 12, new Color4(0.8f, 0.8f, 0.85f));
            // Pattern slots: dark = cooling down, dim = not enough energy, bright = ready.
            float sx = bx + bw + 34;
            for (int k = 0; k < MatchRules.PatternSlots; k++)
            {
                ref readonly var def = ref r.Patterns[k];
                float cd = def.CooldownTicks > 0 ? p.Cooldowns[k] / (float)def.CooldownTicks : 0;
                bool affordable = p.Energy >= def.Cost;
                var sc = cd > 0 ? new Color4(0.2f, 0.2f, 0.25f) : affordable ? c : c.Scale(0.4f);
                float w = 46;
                Rect(dl, sx, y + 2, w, 18, new Color4(0.08f, 0.08f, 0.1f));
                Rect(dl, sx, y + 2, w * (1 - cd), 18, sc.WithAlpha(cd > 0 ? 0.6f : 0.85f));
                Text(dl, PatternNames[(int)def.Kind], sx + w * 0.5f, y + 5, 10.5f, affordable && cd <= 0 ? new Color4(0.05f, 0.05f, 0.08f) : new Color4(0.85f, 0.85f, 0.9f), center: true);
                sx += w + 4;
            }
        }
    }

    private void DrawBanner(ImDrawListPtr dl, SimState s, in ArenaView ui)
    {
        float cx = ui.ToPixelX(s.Rules.ArenaWidth * 0.5f);
        float cy = ui.ToPixelY(s.Rules.ArenaHeight * 0.5f);
        var shadow = new Color4(0, 0, 0, 0.6f);
        switch (s.Phase)
        {
            case MatchPhase.Countdown:
            {
                int secs = (s.PhaseTicks + MatchRules.TicksPerSecond - 1) / MatchRules.TicksPerSecond;
                Panel(dl, cx, cy, 460, 130);
                Text(dl, $"ROUND {s.RoundNumber}", cx, cy - 50, 40, White, center: true);
                Text(dl, RoleSummary(s), cx, cy - 4, 16, new Color4(0.8f, 0.82f, 0.9f), center: true);
                Text(dl, $"{secs}", cx, cy + 22, 30, new Color4(1f, 0.85f, 0.3f), center: true);
                break;
            }
            case MatchPhase.Playing when _goBannerTicks > 0:
                Text(dl, "GO!", cx + 2, cy - 28, 56, shadow, center: true);
                Text(dl, "GO!", cx, cy - 30, 56, new Color4(1f, 0.85f, 0.3f, _goBannerTicks / 40f), center: true);
                break;
            case MatchPhase.RoundOver:
            {
                Panel(dl, cx, cy, 560, 120);
                if (s.LastRoundWinner < 0) Text(dl, "DRAW", cx, cy - 40, 40, White, center: true);
                else Text(dl, $"{TeamName[s.LastRoundWinner]} WINS THE ROUND", cx, cy - 40, 34, TeamColor[s.LastRoundWinner], center: true);
                string next = s.MatchWinner >= 0 ? "" : s.Rules.SwapRolesEachRound ? "Roles swap next round" : "";
                Text(dl, next, cx, cy + 12, 16, new Color4(0.8f, 0.82f, 0.9f), center: true);
                break;
            }
            case MatchPhase.MatchOver:
                Panel(dl, cx, cy, 620, 150);
                Text(dl, $"{TeamName[s.MatchWinner]} WINS THE MATCH", cx, cy - 52, 40, TeamColor[s.MatchWinner], center: true);
                Text(dl, $"{s.Scores[0]} - {s.Scores[1]}", cx, cy - 2, 26, White, center: true);
                Text(dl, "Press Space or Enter for a rematch", cx, cy + 36, 16, new Color4(0.8f, 0.82f, 0.9f), center: true);
                break;
        }
    }

    private readonly string?[,] _hintCache = new string?[SimState.MaxPlayers, 2];

    /// <summary>Bottom-bar control hint for a player in a role; built once, then cached (no per-frame allocation).</summary>
    private string HintText(int player, PlayerRole role)
    {
        ref string? cached = ref _hintCache[player, (int)role];
        if (cached != null) return cached;
        var h = ControlHints[player];
        cached = role == PlayerRole.Dodger
            ? $"P{player + 1} Dodger: {h[0]} move · {h[1]} focus"
            : $"P{player + 1} Gunner: {h[0]} aim · {h[2]} patterns";
        return cached;
    }

    private int _roleSummaryRound = -1;
    private string _roleSummary = "";

    /// <summary>"P1 dodges · P2 shoots" — rebuilt once per round so the countdown does not allocate per frame.</summary>
    private string RoleSummary(SimState s)
    {
        if (_roleSummaryRound == s.RoundNumber) return _roleSummary;
        var parts = new string[s.PlayerCount];
        for (int i = 0; i < s.PlayerCount; i++)
            parts[i] = $"P{i + 1} {(s.Players[i].Role == PlayerRole.Dodger ? "dodges" : "shoots")}";
        _roleSummary = string.Join("  ·  ", parts);
        _roleSummaryRound = s.RoundNumber;
        return _roleSummary;
    }

    private static ImColor8 Col(Color4 c) =>
        new((byte)(Math.Clamp(c.R, 0, 1) * 255), (byte)(Math.Clamp(c.G, 0, 1) * 255), (byte)(Math.Clamp(c.B, 0, 1) * 255), (byte)(Math.Clamp(c.A, 0, 1) * 255));

    private static void Rect(ImDrawListPtr dl, float x, float y, float w, float h, Color4 c)
    {
        if (w <= 0 || h <= 0) return;
        var a = new float2(x, y);
        var b = new float2(x + w, y + h);
        ImDrawListExtensions.AddRectFilled(dl, in a, in b, Col(c), 2f, default);
    }

    private static void Panel(ImDrawListPtr dl, float cx, float cy, float w, float h)
    {
        Rect(dl, cx - w * 0.5f, cy - h * 0.5f, w, h, new Color4(0.02f, 0.02f, 0.04f, 0.72f));
    }

    private ImFontPtr HudFont => Art?.Font ?? ImGui.GetFont();

    private void Text(ImDrawListPtr dl, ImString text, float x, float y, float size, Color4 c, bool center = false, bool rightAlign = false)
    {
        if (center || rightAlign)
        {
            // Measure at the actual draw size (glyph advances don't scale linearly with size).
            var sz = ImFontExtensions.CalcTextSizeA(HudFont, size, float.MaxValue, 0f, text, default);
            x -= center ? sz.X * 0.5f : sz.X;
        }
        var pos = new float2(x, y);
        Brutal.Numerics.float4? clip = null;
        ImDrawListExtensions.AddText(dl, HudFont, size, in pos, Col(c), text, 0f, in clip);
    }
}
