using Brutal.ImGuiApi;
using Brutal.Numerics;
using Crossfire.Sim;
using Crossfire.Sim.Bullets;
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
    public static readonly string[] PatternNames = ["Ring", "Spiral", "Aimed", "Wall", "Sweep", "Homing", "Mines"];
    private static readonly Color4 White = new(1, 1, 1);
    private static readonly Color4 Danger = new(1f, 0.25f, 0.2f);

    /// <summary>Minimum height of the top HUD (1 player per team); grows by <see cref="HudRowHeight"/> per extra player.</summary>
    public const float HudTop = 76f;
    private const float HudRowHeight = 22f;

    /// <summary>Top HUD height for this match: room for every player row of the largest team.</summary>
    public static float HudTopFor(SimState s)
    {
        int max = 0;
        Span<int> perTeam = stackalloc int[SimState.TeamCount];
        for (int i = 0; i < s.PlayerCount; i++)
            if (s.Players[i].Active) max = Math.Max(max, ++perTeam[s.Players[i].Team]);
        return Math.Max(HudTop, 34 + max * HudRowHeight);
    }

    /// <summary>The arena view without screen shake (for mapping the mouse into the arena).</summary>
    public static ArenaView StableView(SimState s, float fbW, float fbH, float uiScale) =>
        new(s.Rules.ArenaWidth, s.Rules.ArenaHeight, fbW, fbH, HudTopFor(s) * uiScale, HudBottom * uiScale);

    /// <summary>Per player slot: the pattern a mouse Gunner has selected (highlighted in the HUD), or -1.</summary>
    public int[] SelectedPattern { get; } = [-1, -1, -1, -1, -1, -1, -1, -1];

    /// <summary>
    /// Per player slot: draw at the latest simulated position instead of interpolating between ticks.
    /// Used for mouse players, where interpolation would add a frame of visible lag behind the pointer.
    /// </summary>
    public bool[] Immediate { get; } = new bool[SimState.MaxPlayers];

    /// <summary>False hides the match-over banner (the results screen / replay view show their own).</summary>
    public bool ShowBanners { get; set; } = true;
    /// <summary>Hide the per-player control hints in the bottom bar (replay view uses it).</summary>
    public bool ShowControlHints { get; set; } = true;
    /// <summary>P label + role + energy bar + value + 4 pattern slots.</summary>
    private const float TeamPanelWidth = 92 + 120 + 34 + 4 * 50;
    public const float HudBottom = 30f;

    /// <summary>Per player slot: control hints shown in the HUD, e.g. ["WASD", "L-Shift", "Q E R F", "Space"].</summary>
    public string[]?[] ControlHints
    {
        get => _controlHints;
        set
        {
            _controlHints = value;
            Array.Clear(_hintCache);
        }
    }
    private string[]?[] _controlHints = [];

    /// <summary>Per player slot: optional tag shown in the HUD, e.g. "BOT · HARD". Null for humans.</summary>
    public string?[] PlayerTags { get; set; } = new string?[SimState.MaxPlayers];

    /// <summary>Per team: header tag, e.g. "BOT · HARD" or "YOU + 3 BOTS · HARD". Null = no tag.</summary>
    public string?[] TeamTags { get; } = new string?[SimState.TeamCount];

    /// <summary>The single human player's slot (labelled YOU), or -1 when there are 0 or several humans.</summary>
    public int YouSlot { get; set; } = -1;

    private static readonly Color4 YouColor = new(1f, 0.9f, 0.4f);
    private static readonly Color4 OverdriveColor = new(1f, 0.78f, 0.25f);

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
    private float _flash;
    private int _suddenDeathMult = -1;
    private string _suddenDeathText = "";
    private int _flashTeam;

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
                case SimEventType.Flip:
                    Spawn(x, y, 30, 0.25f, White.WithAlpha(0.5f), SpriteShape.Ring);
                    break;
                case SimEventType.FlipBlock:
                    // The absorbed bullet bursts into sparks.
                    Spawn(x, y, 46, 0.35f, White, SpriteShape.Ring);
                    Spawn(x, y, 40, 0.25f, new Color4(1f, 0.95f, 0.7f, 0.8f), SpriteShape.Glow);
                    if (Art != null)
                        for (int k = 0; k < 5; k++)
                        {
                            float ang = k * MathF.Tau / 5f + (float)_fxRandom.NextDouble();
                            SpawnSprite(x + MathF.Cos(ang) * 22, y + MathF.Sin(ang) * 22, 12, 0.4f, new Color4(1f, 0.95f, 0.7f), Art.Stars[k % 3]);
                        }
                    _shake = Math.Max(_shake, 4f);
                    break;
                case SimEventType.Overdrive:
                    Spawn(x, y, 90, 0.5f, OverdriveColor, SpriteShape.Ring);
                    Spawn(x, y, 60, 0.4f, OverdriveColor.WithAlpha(0.6f), SpriteShape.Glow);
                    _shake = Math.Max(_shake, 3f);
                    break;
                case SimEventType.MineBurst:
                    Spawn(x, y, 40, 0.3f, TeamColor[e.Team], SpriteShape.Ring);
                    Spawn(x, y, 26, 0.2f, White.WithAlpha(0.7f), SpriteShape.Glow);
                    break;
                case SimEventType.Break:
                {
                    // Shockwave across the whole half, flash, stars flung outward.
                    float halfW = s.Rules.ArenaWidth * 0.5f;
                    Spawn(x, y, halfW * 1.2f, 0.7f, TeamColor[e.Team], SpriteShape.Ring);
                    Spawn(x, y, halfW * 0.8f, 0.5f, White.WithAlpha(0.8f), SpriteShape.Ring);
                    Spawn(x, y, 180, 0.6f, TeamColor[e.Team].WithAlpha(0.7f), SpriteShape.Glow);
                    if (Art != null)
                        for (int k = 0; k < 14; k++)
                        {
                            float ang = k * MathF.Tau / 14f + (float)_fxRandom.NextDouble() * 0.4f;
                            float dist = 40 + _fxRandom.Next(0, 160);
                            SpawnSprite(x + MathF.Cos(ang) * dist, y + MathF.Sin(ang) * dist, 16 + _fxRandom.Next(0, 16), 0.8f, Color4.Lerp(White, TeamColor[e.Team], 0.5f), Art.Stars[k % 3]);
                        }
                    _flash = 1f;
                    _flashTeam = e.Team;
                    _shake = Math.Max(_shake, 14f);
                    break;
                }
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
        _time = (_time + dt) % 3600f; // wrap hourly to keep float precision in the shader
        if (_flash > 0) _flash = Math.Max(0, _flash - dt * 2.5f);
        for (int i = 0; i < _effects.Length; i++)
            if (_effects[i].Life > 0) _effects[i].Age += dt;
        _shake = Math.Max(0, _shake - dt * 60f);
        if (_goBannerTicks > 0) _goBannerTicks--;
    }

    /// <summary>Builds the arena view for the current framebuffer, including screen shake.</summary>
    public ArenaView MakeView(SimState s, float fbW, float fbH, float uiScale)
    {
        float hudTop = HudTopFor(s);
        var v = new ArenaView(s.Rules.ArenaWidth, s.Rules.ArenaHeight, fbW, fbH, hudTop * uiScale, HudBottom * uiScale);
        if (_shake <= 0) return v;
        float sx = (float)(_fxRandom.NextDouble() * 2 - 1) * _shake * 0.5f;
        float sy = (float)(_fxRandom.NextDouble() * 2 - 1) * _shake * 0.5f;
        return new ArenaView(s.Rules.ArenaWidth, s.Rules.ArenaHeight, fbW, fbH, hudTop * uiScale + sy, HudBottom * uiScale - sy, sx);
    }

    public unsafe void DrawArena(SpriteBatch b, SimState s, float alpha)
    {
        ref readonly var r = ref s.Rules;
        float w = r.ArenaWidth, h = r.ArenaHeight, mid = w * 0.5f;
        bool playing = s.Phase == MatchPhase.Playing;
        float a = playing ? alpha : 0f;

        // 1. Background: animated nebula per half, parallax stars, vignette, energy beam on the midline.
        DrawBackground(b, w, h);

        // 2. Anti-spawn-kill zones: shown around Dodgers when an enemy cursor is close.
        for (int i = 0; i < s.PlayerCount; i++)
        {
            ref readonly var d = ref s.Players[i];
            if (!d.IsAliveDodger) continue;
            float ai = Immediate[i] ? 1f : a;
            float dx = Lerp(F(d.PrevPos.X), F(d.Pos.X), ai), dy = Lerp(F(d.PrevPos.Y), F(d.Pos.Y), ai);
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
            var bc = pool.Half[i] == 0 ? c0 : c1;
            if (pool.SpecialCount > 0 && (pool.Flags[i] & (byte)BulletFlags.Special) != 0)
            {
                if ((pool.Flags[i] & (byte)BulletFlags.Mine) != 0)
                {
                    // Mine: pulsing warning ring that speeds up as the fuse runs out.
                    float fuse = pool.Timer[i];
                    float blink = 0.5f + 0.5f * MathF.Sin((80f - fuse) * (0.25f + (80f - Math.Min(fuse, 80f)) * 0.01f));
                    b.Circle(x, y, rad + 8 + 4 * blink, bc.WithAlpha(0.3f + 0.4f * blink), SpriteShape.Ring, 0.12f);
                    b.Circle(x, y, rad + 14, bc.WithAlpha(0.18f), SpriteShape.Glow);
                }
                else
                {
                    // Homing: soft trailing glow while it is still steering.
                    float tx = pool.VelX[i] * inv, ty = pool.VelY[i] * inv;
                    b.Circle(x - tx * 3, y - ty * 3, rad + 5, bc.WithAlpha(0.35f), SpriteShape.Glow);
                }
            }
            b.Add(x, y, rad, rad, bc, SpriteShape.Bullet);
        }

        // 5. Dodgers: Kenney ships facing the midline, thrusters, shield; the hitbox dot always on top.
        int[] variantCounter = _variantScratch;
        variantCounter[0] = variantCounter[1] = 0;
        for (int i = 0; i < s.PlayerCount; i++)
        {
            ref readonly var p = ref s.Players[i];
            if (!p.IsDodger) continue;
            int variant = Math.Min(1, variantCounter[p.Team]++);
            float ai = Immediate[i] ? 1f : a;
            float x = Lerp(F(p.PrevPos.X), F(p.Pos.X), ai), y = Lerp(F(p.PrevPos.Y), F(p.Pos.Y), ai);
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
                // Flip: a full barrel roll (wingspan squashes through edge-on and back).
                float roll = 1f;
                if (p.FlipTicks > 0 && r.FlipTicks > 0)
                {
                    float prog = Math.Clamp((r.FlipTicks - p.FlipTicks + a) / r.FlipTicks, 0f, 1f);
                    roll = MathF.Max(0.12f, MathF.Abs(MathF.Cos(prog * MathF.Tau)));
                    // Afterimage trail behind the roll.
                    float tx = F(p.PrevPos.X) - F(p.Pos.X), ty = F(p.PrevPos.Y) - F(p.Pos.Y);
                    b.Sprite(ship, x + tx * 2.5f, y + ty * 2.5f, 30, c.WithAlpha(0.25f), rot, roll);
                    b.Sprite(ship, x + tx * 5f, y + ty * 5f, 30, c.WithAlpha(0.12f), rot, roll);
                }
                b.Sprite(ship, x, y, 30, White.WithAlpha(bodyAlpha), rot, roll);
                if (p.InvulnTicks > 0 && Art.Shield.IsValid)
                    b.Sprite(Art.Shield, x, y, 50, c.WithAlpha(0.55f + 0.25f * MathF.Sin(p.InvulnTicks * 0.4f)), facing);
            }
            else
            {
                b.Add(x, y, 13, 13, c.WithAlpha(bodyAlpha), SpriteShape.Diamond, 0.22f);
            }
            if (p.Focus && p.FlipTicks == 0) b.Circle(x, y, 20, White.WithAlpha(0.55f), SpriteShape.Ring, 0.08f);
            if (p.FlipGuard)
            {
                float spin = 0.75f + 0.25f * MathF.Sin(_time * 30f);
                b.Circle(x, y, 26, White.WithAlpha(0.18f * spin), SpriteShape.Glow);
                b.Circle(x, y, 22, new Color4(1f, 0.95f, 0.75f, 0.85f * spin), SpriteShape.Ring, 0.1f);
            }
            if (DodgerSystem.IsBreakReady(in p, in r))
            {
                // Full Break bar: a pulsing team-coloured halo so both players can see it is armed.
                float pulse = 0.5f + 0.5f * MathF.Sin(_time * 6f);
                b.Circle(x, y, 30 + 4 * pulse, c.WithAlpha(0.35f + 0.35f * pulse), SpriteShape.Ring, 0.07f);
            }
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
            float ai = Immediate[i] ? 1f : a;
            float x = Lerp(F(p.PrevCursor.X), F(p.Cursor.X), ai), y = Lerp(F(p.PrevCursor.Y), F(p.Cursor.Y), ai);
            bool blocked = GunnerSystem.IsTooCloseToDodger(s, p.Team, p.Cursor);
            bool rejectedRecently = p.LastRejectTick >= 0 && s.Tick - p.LastRejectTick < 12;
            var c = blocked || rejectedRecently ? new Color4(0.6f, 0.6f, 0.6f) : TeamColor[p.Team];
            b.Circle(x, y, 18, c.WithAlpha(0.85f), SpriteShape.Cross, 0.09f, 0.3f);
            b.Circle(x, y, 13, c.WithAlpha(0.6f), SpriteShape.Ring, 0.1f);
            if (blocked) b.Circle(x, y, 22, Danger.WithAlpha(0.8f), SpriteShape.Ring, 0.08f);
            if (p.OverdriveTicks > 0)
            {
                // Overdrive: a hot gold aura and a spinning arc, visible to the enemy Dodgers.
                float spin = (_time * 3f) % 1f;
                b.Circle(x, y, 44, OverdriveColor.WithAlpha(0.28f), SpriteShape.Glow);
                b.Circle(x, y, 28, OverdriveColor.WithAlpha(0.9f), SpriteShape.Arc, 0.25f + 0.1f * spin, 0.12f);
                b.Circle(x, y, 32, OverdriveColor.WithAlpha(0.5f), SpriteShape.Ring, 0.05f);
            }
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

        // 8. Break flash over the half that was cleared.
        if (_flash > 0)
            b.Rect(_flashTeam == 0 ? 0 : mid, 0, mid, h, Color4.Lerp(White, TeamColor[_flashTeam], 0.4f).WithAlpha(0.35f * _flash * _flash));
    }

    /// <summary>The mouse pointer in arena units (the OS cursor is hidden during a mouse match).</summary>
    public void DrawPointer(SpriteBatch b, float x, float y)
    {
        var c = new Color4(1f, 1f, 1f, 0.75f);
        b.Circle(x, y, 9, new Color4(0, 0, 0, 0.5f), SpriteShape.Ring, 0.3f);
        b.Circle(x, y, 8, c, SpriteShape.Ring, 0.16f);
        b.Circle(x, y, 1.8f, c, SpriteShape.Disc);
    }

    // --- Background ---------------------------------------------------------------------------------

    private const int StarCount = 240;
    private static readonly float[] StarLayerSpeed = [4f, 9f, 18f]; // arena units per second, far -> near
    private readonly float[] _starX = new float[StarCount], _starY = new float[StarCount];
    private readonly float[] _starSize = new float[StarCount], _starPhase = new float[StarCount];
    private readonly byte[] _starLayer = new byte[StarCount];
    private float _time;

    /// <summary>Real time in seconds, for background animation (render only).</summary>
    public float Time => _time;

    public GameRenderer()
    {
        var rng = new Random(7);
        for (int i = 0; i < StarCount; i++)
        {
            // Most stars far away (small, slow); a few close (bigger, faster).
            int layer = i < StarCount * 6 / 10 ? 0 : i < StarCount * 9 / 10 ? 1 : 2;
            _starLayer[i] = (byte)layer;
            _starX[i] = (float)rng.NextDouble();
            _starY[i] = (float)rng.NextDouble();
            _starSize[i] = layer switch { 0 => 0.6f + (float)rng.NextDouble() * 0.6f, 1 => 1.0f + (float)rng.NextDouble() * 0.8f, _ => 1.6f + (float)rng.NextDouble() * 1.2f };
            _starPhase[i] = (float)rng.NextDouble() * MathF.Tau;
        }
    }

    private void DrawBackground(SpriteBatch b, float w, float h)
    {
        float mid = w * 0.5f;
        float t = _time;

        // Nebula (procedural shader) per half; the old tiled texture is the fallback if shapes look wrong.
        b.Add(mid * 0.5f, h * 0.5f, mid * 0.5f, h * 0.5f, new Color4(1f, 1f, 1f, 1f), SpriteShape.Nebula, t, 0f);
        b.Add(mid * 1.5f, h * 0.5f, mid * 0.5f, h * 0.5f, new Color4(1f, 1f, 1f, 1f), SpriteShape.Nebula, t, 1f);

        // Parallax starfield drifting down; each star twinkles on its own phase.
        for (int i = 0; i < StarCount; i++)
        {
            int layer = _starLayer[i];
            float y = (_starY[i] * h + t * StarLayerSpeed[layer]) % h;
            float x = _starX[i] * w;
            float tw = 0.55f + 0.45f * MathF.Sin(t * (1.3f + layer * 0.7f) + _starPhase[i]);
            float size = _starSize[i];
            var tint = x < mid ? new Color4(0.80f, 0.90f, 1f) : new Color4(1f, 0.85f, 0.90f);
            if (layer == 2)
            {
                b.Circle(x, y, size * 4.5f, tint.WithAlpha(0.18f * tw), SpriteShape.Glow);
                if (Art != null && Art.Stars[i % 3].IsValid && i % 4 == 0)
                    b.Sprite(Art.Stars[i % 3], x, y, size * 5f, tint.WithAlpha(0.55f * tw), t * 0.3f + _starPhase[i]);
                else
                    b.Circle(x, y, size, tint.WithAlpha(0.95f * tw), SpriteShape.Disc);
            }
            else
            {
                b.Circle(x, y, size, tint.WithAlpha((layer == 0 ? 0.45f : 0.75f) * tw), SpriteShape.Disc);
            }
        }

        // Faint grid, so movement still reads against the background.
        var grid = new Color4(1, 1, 1, 0.018f);
        for (float gx = 80; gx < w; gx += 80) if (Math.Abs(gx - mid) > 1) b.Rect(gx - 0.5f, 0, 1, h, grid);
        for (float gy = 80; gy < h; gy += 80) b.Rect(0, gy - 0.5f, w, 1, grid);

        // Vignette: darker corners pull the eye to the action.
        b.Add(w * 0.5f, h * 0.5f, w * 0.5f, h * 0.5f, new Color4(0f, 0f, 0.01f, 0.75f), SpriteShape.Vignette);

        // Midline energy beam: soft glow, team-tinted edges, bright core, pulses running along it.
        float breathe = 0.85f + 0.15f * MathF.Sin(t * 2.1f);
        b.Rect(mid - 22, 0, 22, h, TeamColor[0].WithAlpha(0.035f * breathe));
        b.Rect(mid, 0, 22, h, TeamColor[1].WithAlpha(0.035f * breathe));
        b.Rect(mid - 8, 0, 16, h, new Color4(0.8f, 0.85f, 1f, 0.06f * breathe));
        b.Rect(mid - 3, 0, 6, h, new Color4(0.85f, 0.9f, 1f, 0.16f * breathe));
        b.Rect(mid - 1f, 0, 2, h, new Color4(0.95f, 0.97f, 1f, 0.75f));
        for (int k = 0; k < 6; k++)
        {
            float dir = k % 2 == 0 ? 1f : -1f;
            float py = ((k * 0.37f + t * 0.22f) % 1f) * (h + 120) - 60;
            if (dir < 0) py = h - py;
            b.Add(mid, py, 9, 46, new Color4(0.85f, 0.92f, 1f, 0.35f), SpriteShape.Glow);
            b.Add(mid, py, 2.2f, 18, White.WithAlpha(0.8f), SpriteShape.Glow);
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
            if (_suddenDeathMult != r.SuddenDeathRegenMultiplier)
            {
                _suddenDeathMult = r.SuddenDeathRegenMultiplier;
                _suddenDeathText = "SUDDEN DEATH: ENERGY x" + _suddenDeathMult;
            }
            Text(dl, _suddenDeathText, cx, 58, 14, Danger, center: true);
        }
        else if (s.Phase == MatchPhase.Playing)
        {
            // Symmetric rounds (2v2+): count down to sudden death.
            int secs = (r.SuddenDeathTicks - s.RoundTick + MatchRules.TicksPerSecond - 1) / MatchRules.TicksPerSecond;
            if (secs <= 30) Text(dl, $"SUDDEN DEATH IN {secs}s", cx, 58, 14, new Color4(1f, 0.75f, 0.4f), center: true);
        }

        // Team panels.
        for (int team = 0; team < SimState.TeamCount; team++)
        {
            float x0 = team == 0 ? 14 : windowW - 14 - TeamPanelWidth;
            float y = 8;
            Text(dl, TeamName[team], x0, y, 15, TeamColor[team]);
            if (TeamTags[team] is { } teamTag) Text(dl, teamTag, x0 + 70, y + 2, 12, new Color4(1f, 0.85f, 0.35f));
            y += 20;
            for (int i = 0; i < s.PlayerCount; i++)
            {
                ref readonly var p = ref s.Players[i];
                if (!p.Active || p.Team != team) continue;
                DrawPlayerRow(dl, s, i, x0, y);
                y += HudRowHeight;
            }
        }

        // Bottom: control hints per player.
        float by = windowH - HudBottom + 7;
        // With two humans on one side (local 2v2), both hints share that half: compact text, the second one
        // right-aligned against the midline (left team) or left-aligned from it (right team).
        Span<int> perSide = stackalloc int[SimState.TeamCount];
        for (int i = 0; i < s.PlayerCount && i < ControlHints.Length; i++)
            if (ControlHints[i] != null) perSide[s.Players[i].Team == 0 ? 0 : 1]++;
        Span<int> drawn = stackalloc int[SimState.TeamCount];
        bool hintLeft = false, hintRight = false;
        for (int i = 0; i < s.PlayerCount && i < ControlHints.Length; i++)
        {
            ref readonly var p = ref s.Players[i];
            if (ControlHints[i] == null || !ShowControlHints) continue; // bots have no controls to show
            int side = p.Team == 0 ? 0 : 1;
            bool shared = perSide[side] > 1;
            string text = HintText(i, p.Role, compact: shared);
            bool left = side == 0;
            if (left) hintLeft = true; else hintRight = true;
            var color = TeamColor[p.Team].Scale(0.85f);
            if (drawn[side]++ == 0) Text(dl, text, left ? 14 : windowW - 14, by, shared ? 12.5f : 14, color, center: false, rightAlign: !left);
            else Text(dl, text, left ? cx - 14 : cx + 14, by, 12.5f, color, center: false, rightAlign: left);
        }
        // Keep the debug hint out of the way of long control hints: use a free side when there is one.
        var dim = new Color4(0.45f, 0.47f, 0.52f);
        if (!hintRight) Text(dl, "F1 debug · Esc menu", windowW - 14, by, 13, dim, rightAlign: true);
        else if (!hintLeft) Text(dl, "F1 debug · Esc menu", 14, by, 13, dim);
        else if (perSide[0] < 2 && perSide[1] < 2) Text(dl, "F1 debug · Esc menu", cx, by, 13, dim, center: true);

        // Player labels above Dodgers (tells teammates apart in 2v2+).
        for (int i = 0; i < s.PlayerCount; i++)
        {
            ref readonly var p = ref s.Players[i];
            if (!p.IsDodger || p.Lives <= 0) continue;
            float a = s.Phase == MatchPhase.Playing ? alpha : 0f;
            float ai = Immediate[i] ? 1f : a;
            float x = ui.ToPixelX(Lerp(F(p.PrevPos.X), F(p.Pos.X), ai));
            float y = ui.ToPixelY(Lerp(F(p.PrevPos.Y), F(p.Pos.Y), ai));
            if (i == YouSlot) Text(fg, "YOU", x, y - 36 * ui.Scale, 14, YouColor, center: true);
            else Text(fg, PlayerTags[i] != null ? "BOT" : PlayerLabels[i], x, y - 36 * ui.Scale, 13, TeamColor[p.Team].WithAlpha(0.8f), center: true);
        }

        if (ShowBanners || s.Phase != MatchPhase.MatchOver) DrawBanner(fg, s, ui);
    }

    private void DrawPlayerRow(ImDrawListPtr dl, SimState s, int index, float x, float y)
    {
        ref readonly var p = ref s.Players[index];
        ref readonly var r = ref s.Rules;
        var c = TeamColor[p.Team];
        if (index == YouSlot) Rect(dl, x - 6, y + 2, 3, 16, YouColor);
        Text(dl, PlayerLabels[index], x, y + 1, 15, index == YouSlot ? YouColor : c);
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
            // Break bar (filled by grazing) and the Flip cooldown pip.
            float bx = x + 100 + r.DodgerLives * 20 + 4, bw = 110, bh = 12;
            float fill = Math.Clamp(F(p.Break) / Math.Max(1f, F(r.BreakMax)), 0, 1);
            bool ready = fill >= 1f;
            float pulse = ready ? 0.5f + 0.5f * MathF.Sin(_time * 8f) : 0f;
            Rect(dl, bx, y + 4, bw, bh, new Color4(0.12f, 0.12f, 0.16f));
            Rect(dl, bx, y + 4, bw * fill, bh, ready ? Color4.Lerp(c, White, 0.35f * pulse) : c.Scale(0.75f));
            Text(dl, ready ? "BREAK READY" : "BREAK", bx + bw * 0.5f, y + 4, 10.5f,
                ready ? new Color4(0.05f, 0.05f, 0.08f) : new Color4(0.85f, 0.85f, 0.9f), center: true);
            float fx = bx + bw + 16, fy = y + 10;
            float flipReady = r.FlipCooldownTicks > 0 ? 1f - p.FlipCooldown / (float)r.FlipCooldownTicks : 1f;
            var fc = new float2(fx, fy);
            ImDrawListExtensions.AddCircleFilled(dl, in fc, 7, Col(new Color4(0.12f, 0.12f, 0.16f)), 16);
            if (flipReady >= 1f) ImDrawListExtensions.AddCircleFilled(dl, in fc, 7, Col(new Color4(1f, 0.95f, 0.75f)), 16);
            else ImDrawListExtensions.AddCircleFilled(dl, in fc, 7 * flipReady, Col(new Color4(0.6f, 0.6f, 0.65f)), 16);
            Text(dl, "FLIP", fx + 11, y + 4, 10.5f, flipReady >= 1f ? new Color4(1f, 0.95f, 0.75f) : new Color4(0.5f, 0.5f, 0.55f));
        }
        else
        {
            Text(dl, "GUNNER", x + 30, y + 3, 12, new Color4(0.7f, 0.72f, 0.78f));
            // Energy bar with cost ticks.
            float bx = x + 92, bw = 120, bh = 12;
            float fill = Math.Clamp(F(p.Energy) / Math.Max(1f, F(r.EnergyMax)), 0, 1);
            Rect(dl, bx, y + 4, bw, bh, new Color4(0.12f, 0.12f, 0.16f));
            bool odActive = p.OverdriveTicks > 0;
            Rect(dl, bx, y + 4, bw * fill, bh, odActive ? Color4.Lerp(c, OverdriveColor, 0.6f + 0.2f * MathF.Sin(_time * 12f)) : c.Scale(0.9f));
            // Overdrive meter: a thin gold strip under the energy bar (drains while active).
            float od = odActive
                ? p.OverdriveTicks / (float)Math.Max(1, r.OverdriveTicks)
                : Math.Clamp(F(p.Overdrive) / Math.Max(1f, F(r.OverdriveMax)), 0, 1);
            bool odReady = !odActive && od >= 1f;
            Rect(dl, bx, y + 17, bw, 3, new Color4(0.12f, 0.12f, 0.16f));
            Rect(dl, bx, y + 17, bw * od, 3, odReady ? Color4.Lerp(OverdriveColor, White, 0.5f + 0.5f * MathF.Sin(_time * 8f)) : OverdriveColor.WithAlpha(0.85f));
            if (odReady)
            {
                Text(dl, "OVERDRIVE", bx + bw * 0.5f + 1, y + 5, 10.5f, new Color4(0, 0, 0, 0.8f), center: true);
                Text(dl, "OVERDRIVE", bx + bw * 0.5f, y + 4, 10.5f, Color4.Lerp(OverdriveColor, White, 0.3f + 0.3f * MathF.Sin(_time * 8f)), center: true);
            }
            Text(dl, $"{(int)F(p.Energy)}", bx + bw + 6, y + 3, 12, new Color4(0.8f, 0.8f, 0.85f));
            // Pattern slots: dark = cooling down, dim = not enough energy, bright = ready.
            float sx = bx + bw + 34;
            for (int k = 0; k < MatchRules.PatternSlots; k++)
            {
                ref readonly var def = ref r.DefFor(in p, k);
                float cd = def.CooldownTicks > 0 ? p.Cooldowns[k] / (float)def.CooldownTicks : 0;
                bool affordable = p.Energy >= def.Cost;
                var sc = cd > 0 ? new Color4(0.2f, 0.2f, 0.25f) : affordable ? c : c.Scale(0.4f);
                float w = 46;
                if (SelectedPattern[index] == k)
                {
                    // Mouse Gunner: the pattern a left click will place (wheel changes it).
                    float glow = 0.75f + 0.25f * MathF.Sin(_time * 7f);
                    Rect(dl, sx - 2, y, w + 4, 22, new Color4(1f, 0.95f, 0.6f, glow));
                }
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

    private readonly string?[,] _hintCache = new string?[SimState.MaxPlayers, 4];

    /// <summary>Bottom-bar control hint for a player in a role; built once, then cached (no per-frame allocation).</summary>
    private string HintText(int player, PlayerRole role, bool compact = false)
    {
        ref string? cached = ref _hintCache[player, (int)role + (compact ? 2 : 0)];
        if (cached != null) return cached;
        var h = ControlHints[player]!;
        if (compact && h.Length >= 6)
        {
            cached = role == PlayerRole.Dodger
                ? $"P{player + 1}: {h[0]} · {h[4]} flip · {h[5]} break"
                : $"P{player + 1}: {h[0]} aim · {h[2]} · {h[5]} overdrive";
            return cached;
        }
        cached = role == PlayerRole.Dodger
            ? h.Length >= 6
                ? $"P{player + 1} Dodger: {h[0]} move · {h[1]} focus · {h[4]} flip · {h[5]} break"
                : $"P{player + 1} Dodger: {h[0]} move · {h[1]} focus"
            : h.Length >= 6
                ? $"P{player + 1} Gunner: {h[0]} aim · {h[2]} patterns · {h[5]} overdrive"
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

    /// <summary>Draws text with the HUD font (used by the title menu).</summary>
    public void Label(ImDrawListPtr dl, ImString text, float x, float y, float size, Color4 c, bool center = false, bool rightAlign = false) => Text(dl, text, x, y, size, c, center, rightAlign);

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
