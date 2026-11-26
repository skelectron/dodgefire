# CROSSFIRE

Competitive team bullet-hell PvP on one split arena, built in C# / .NET 10 on the BRUTAL framework (Vulkan).
Dodgers survive in their own half; Gunners place telegraphed bullet patterns in the enemy half.

Status: **Phases 0–7 done**. You can play against bots, play with up to 3 friends on one PC (mouse, both halves of the keyboard and gamepads), or watch Bot vs Bot. Teammate Gunners can link **crossfire combos**, and 4v4 holds a locked 60 FPS with 30,000 bullets on an Intel HD 620. `tools/publish.ps1` makes a ready-to-upload Windows build. Phase 8 (online play) is next and needs its own plan.

## Modes and bots

The title menu works with mouse or keyboard and has two pages. First you pick a mode: hovering only highlights, and a click (or Enter) opens that mode's options page. That page lists only the settings that mode uses, plus START and BACK. Settings change only when you click them or press Left/Right. The modes are:
- **Team 4v4:** you and 3 bot teammates against 4 bots. Each team has 2 Dodgers and 2 Gunners. You pick your role (Dodger or Gunner), and your bot teammates fill the other slots. **ROLES** decides whether roles stay **FIXED** (the default) or **SWAP EACH ROUND**, so every Dodger becomes a Gunner and back. Local 2v2 has the same setting; with swapping on, every lobby seat picks a pattern loadout.
- **Vs Bot:** 1v1 against a bot. You pick your starting role, and roles swap every round.
- **Local 1v1:** two players on one machine. P1 uses the mouse, a gamepad or the left side of the keyboard; P2 a second gamepad or the right side of the keyboard.
- **Local 2v2:** up to 4 players on one PC, best of 3. START on the options page opens a **lobby** with four seats: blue Dodger, blue Gunner, red Dodger, red Gunner. Each device joins on its own: click a seat (mouse), Space (WASD keys), Enter (arrow keys) or A (gamepad).
  - Left/Right changes seat.
  - Up/Down picks a Gunner seat's patterns: Classic, Hunter, Trapper, or Custom (the GUNNER PATTERNS row).
  - Press the join key again to mark yourself ready.
  - Right click, X, Right Ctrl or B un-readies you, or leaves the seat if you weren't ready.
  - When everyone seated is ready, press the join key again or click START.

  Empty seats are bots of the chosen difficulty. Roles stay fixed unless ROLES is set to swap. Pads can be plugged in while the lobby is open; unplugging one frees its seat.
- **Bot vs Bot:** watch two bots play.

Settings: **controls** (Mouse is the default; Keyboard or Gamepad) and **bot difficulty** (Easy, Normal or Hard). A live Bot vs Bot match runs behind the menu. Esc returns to the menu.

Bots live in `src/Crossfire.Sim/Bots`. They are deterministic: integer maths plus their own seeded RNG, and their output is a normal `SimInput`, so replays and future netcode are unaffected.
- **Dodger bot:** searches 9 directions × focus/no focus over a short horizon, predicting bullets linearly. It avoids danger and walls and is pulled toward the centre.
- **Gunner bot:** picks a spot just outside the anti-spawn radius, steers the cursor there, and chooses a pattern based on the situation. It uses Wall when the Dodger is at an edge, Aimed when energy is low, and Hard chains Ring into Aimed.
- **Dodger specials:** Normal and Hard bots flip when even their best move passes very close to a bullet. All bots use Break once the bar is full and enough bullets are crowding them; Easy waits for a bigger crowd.
- **Difficulty** changes lookahead, reaction time, noise, reaction lapses and how readily the bot uses its specials. Lives lost in 100 s against a Hard Gunner bot: idle 27, Easy 18, Normal 5, Hard 2 (`BotTests`). Bot runs are chaotic, so expect ±1 between versions.

## Run

```sh
dotnet run -c Release --project src/Crossfire.App
dotnet test                                   # determinism, rules, fixed-point, zero-allocation tests
dotnet run -c Release --project tests/Crossfire.Benchmarks -- --filter "*"
```

Needs a Vulkan 1.3 driver. Install the Vulkan SDK if you want `--validate`.

Every launch starts with a short splash: the BRUTAL logo, then the CROSSFIRE title. After 1.2 s, a key, click or gamepad button skips it. The BRUTAL licence requires the logo before gameplay, so the splash has no off switch.

### Release build (itch.io or anywhere)

```sh
powershell -ExecutionPolicy Bypass -File tools\publish.ps1
```

This builds a self-contained Windows x64 release in `artifacts/crossfire-win-x64-<commit>/` (with `CROSSFIRE.exe`) and zips it next to that folder (about 42 MB). Players don't need .NET installed. The script refuses to package a build that is missing `NOTICE.txt`, `LICENSES/`, the BRUTAL logo or the Kenney assets. It never uploads anything.

| Flag | Effect |
|---|---|
| `--validate` | Vulkan validation layers (when installed) |
| `--novsync` | Mailbox/immediate present instead of FIFO |
| `--perf-log` | FPS, frame/sim time and managed bytes allocated per frame, printed every 5 s |
| `--stress N` | Keep about N bullets alive (perf testing) |
| `--sprites N` | Draw N extra static sprites (renderer stress) |
| `--god` | Dodgers can't be hit |
| `--mode team\|vsbot\|local\|demo` | Skip the menu and start that mode |
| `--role dodger\|gunner` | Your role for `--mode team` (your starting role for `vsbot`) |
| `--keyboard` | Use keyboard controls instead of the mouse for `--mode` |
| `--replay <file>` | Watch a saved replay (e.g. `replays/last.cfreplay`) |
| `--difficulty easy\|normal\|hard` | Bot difficulty for `--mode` |
| `--2v2` | 2v2 layout for `--mode vsbot\|local`. Slots 3–4 go to connected gamepads, otherwise to bots. The menu's Local 2v2 lobby is the usual way to play 2v2. |
| `--seed N` | RNG seed |

## Controls

### Mouse (default)

| | Dodger | Gunner |
|---|---|---|
| Move | Ship flies straight to the pointer and stops on it | Reticle sticks to the pointer |
| Focus (slow, precise) | Shift or middle button | |
| Flip | Left click | |
| Break / Overdrive | Right click | Right click (Overdrive) |
| Choose pattern | | Mouse wheel (or 1–4); the selected pattern is outlined in the HUD |
| Place pattern | | Left click |
| Rematch | Left click | Left click |

The pointer position is sent to the simulation as a target in quarter-unit precision, so replays stay deterministic. The ship moves straight toward it and lands exactly on it, at up to `DodgerPointerSpeed` (10 units per tick, about 2.2× keyboard speed). That cap stays below 2 × (hitbox + smallest bullet radius) = 11, so a fast flick can't skip over a bullet between ticks. The Gunner reticle is capped at 80 units per tick, so it effectively sticks to the pointer. Mouse players are drawn at their latest simulated position, without the usual interpolation, so they don't trail the pointer by a frame. The OS cursor is hidden during a match and replaced by a small reticle; it comes back when the F1 panel is open.

### Gamepad

Any pad GLFW has a mapping for works: XInput pads, DualShock/DualSense, Switch Pro and most others. Pads can be connected at any time.

| | Dodger | Gunner |
|---|---|---|
| Move / aim | Left stick or d-pad (pushing part-way moves slower) | Left or right stick, or d-pad |
| Focus | LB or LT | LB or LT (slow aim) |
| Flip | A | |
| Patterns 1–4 | | A, B, X, Y |
| Break / Overdrive | RB or RT | RB or RT |
| Rematch | Start or A | Start or A |
| Leave the match (like Esc) | Back / Select | Back / Select |

The sticks have a 20% radial dead zone, rescaled so movement starts slow and diagonals keep their angle. Menus work with the d-pad or stick: A confirms, B goes back. When a mouse player shares the keyboard with keyboard players, Shift and the number keys belong to the keyboard players, and the mouse player focuses with the middle button.

### Keyboard (one keyboard, two players; in 1v1 roles swap every round)

| | Left player (P1, blue) | Right player (P2, red) |
|---|---|---|
| Move / aim cursor | WASD | Arrows |
| Focus (slow, precise) | Left Shift | Right Shift |
| Patterns: Ring, Spiral, Aimed, Wall (Gunner) | Q E R F (or 1–4) | U I O P (or Numpad 1–4) |
| Flip: dash that absorbs one bullet (Dodger) | Space or C | Enter or Numpad 0 |
| Super: Break (Dodger) / Overdrive (Gunner) | X | Right Ctrl |
| Rematch | Space | Enter |

In Vs Bot, the human can use either side of the keyboard. F1 opens the debug panel: frame graph, stress buttons, a state hash, per-slot controllers (click to switch between keyboard and bot difficulties), and live tuning of every rule and pattern.

The Dodger wins an asymmetric round (1v1) by surviving 40 s, and the Gunner wins it by taking all 3 lives. First to 2 rounds wins.

### Gunner patterns, loadouts and Overdrive

There are 7 patterns. Each Gunner brings **4** into a match, set under **GUNNER PATTERNS** on the options page. The order you pick them in is the button order (1–4, Q E R F, or the mouse wheel).

| Pattern | What it does |
|---|---|
| Ring | Expanding rings with shifting gaps |
| Spiral | Rotating arms; area denial |
| Aimed | Fast fan at the nearest Dodger; cheap |
| Wall | Slow wall of big bullets with a gap |
| **Sweep** | A laser-like stream that sweeps across a ~100° arc through the Dodger |
| **Homing** | A fan that steers toward the nearest Dodger for 1.5 s (at most ~3.5° per step), then flies straight |
| **Mines** | Slow drifting mines that burst into a ring of 10 small bullets after 1.2 s |

**Overdrive** is the Gunner's super. It charges from energy you spend (1 per 3) and from lives your bullets take (+20 each; every bullet remembers who placed it). When it's full, Super (right click, X or Right Ctrl) gives you 5 s of cooldowns at 4× speed and energy regen at 2×. The enemy sees a gold aura around your reticle.

Bots pick a loadout from their difficulty: Easy plays the classic four; Normal and Hard use Hunter, Trapper or Classic. Bots also fire Overdrive when it's ready.

### Crossfire combos (2v2 and 4v4)

When a Gunner places a pattern close to a **teammate's pattern that is still telegraphing**, the two link into a **crossfire combo**. "Close" means 120–380 units apart.
- **Both telegraphs restart together**, so the combo always gets a full 0.45 s warning, and the two patterns fire on the same tick.
- During the warning, a dotted beam between the two patterns shows **exactly where the combo bullets will appear**. The escape gap (3 bullets wide) is ringed in white.
- When they fire, the line of bullets appears and splits into **two walls moving apart**, one to each side.
- Both Gunners get 10 energy back and 10 Overdrive charge. Then each has a **4 s combo cooldown**, so combos stay special moments.

The best combos sit on opposite sides of a Dodger, so the line crosses them. Normal and Hard bots go for exactly that: when a teammate's pattern is telegraphing and the far side of the Dodger is reachable in time, they move there and fire. In bot 4v4 matches that comes to about 17–20 combos a minute across both teams.

Every number is a rule, live-tunable in F1 (radius, spacing, gap, speed, refund, cooldown, on/off). The results screen counts COMBOS per player.

### Dodger abilities

- **Flip:** a 0.4 s barrel-roll dash at 1.5× speed. The first bullet that touches you during the roll is destroyed instead of costing a life. 2.5 s cooldown, shown by the FLIP pip in the HUD.
- **Break** (X / Right Ctrl / right click): each bullet you graze adds 5 to the BREAK bar, and a hit takes 25 away. When the bar is full (100), Break clears every bullet in your half, cancels the enemy's pending patterns there and gives you 0.75 s of invulnerability. A full bar pulses in the HUD and around your ship, so the Gunner can see it coming.

All of the numbers are live-tunable in the F1 debug panel.

## Results, stats and replays

When a match ends, a **results screen** shows each player's lives lost and taken, grazes, flips and blocks, breaks, patterns placed and Overdrives, and marks an MVP. From there you can pick **REMATCH**, **WATCH REPLAY** or **MENU**.

Replays are tiny, because the simulation is deterministic: only the seed, the rules, the loadouts and each tick's inputs are recorded (about 16 KB per minute for 1v1). Rule edits made live in F1 are recorded too. The last match is saved to `replays/last.cfreplay` next to the executable, and `--replay <file>` plays one straight away.

In the replay viewer: Space or click pauses, Left/Right or the wheel sets speed (0.5×–4×), R restarts, and Esc goes back. At the end the viewer checks that the re-simulated state hashes to the recorded value; a mismatch shows DESYNC. Using god mode or stress spawning makes a match not replayable.

## Audio

Sound effects and music play through SDL3_mixer (BRUTAL bindings). Sounds follow simulation events: telegraph, firing, grazes, flips, blocks, breaks, Overdrive, hits and round jingles. Each is panned by where it happened in the arena. Menu and match have their own music loop. Press **M** to mute; master, music and effect volumes are in the F1 panel. If no audio device is available, the game runs silent.

## Measured (Intel HD Graphics 620, Release, vsync)

| Check | Target | Result |
|---|---|---|
| Static sprites | 50,000 @ 60 FPS | 60 FPS |
| Bullets in play | 20,000, sim < 4 ms | 60 FPS, sim ≈ 0.3 ms/tick average. With two Hard bots thinking: 0.3–0.7 ms average, occasional spikes up to 7 ms |
| **4v4, 30,000 bullets** (Phase 7) | locked 60 FPS | 60.0–60.1 FPS in every 5 s window, frame avg 16.65 ms. Sim plus 8 Hard bots average 0.5–0.7 ms/tick, max about 3 ms; 0 B/frame. Uncapped, the same scene renders at about 134 FPS (7.4 ms). Run: `--mode team --stress 30000 --god --perf-log` |
| GC allocations in a match | 0 per frame | 0 B/frame steady state, 0 gen0 collections |
| Determinism | same inputs → same hash over 10k ticks | Passes, with a golden hash, in both Debug and Release |

## Layout

- `src/Crossfire.Sim`: pure deterministic simulation. Q16.16 `Fix` math with a checked-in sine table, a seeded RNG, an SoA bullet pool in `Brutal.Memory`, and patterns, collision and rules. It has no floats, no time and no rendering.
- `src/Crossfire.Render`: Vulkan context, an instanced SDF `SpriteBatch` (one draw call for the whole arena), ImGui/ImPlot layer, and `GameRenderer`.
- `src/Crossfire.Input`: keyboard, mouse and gamepad → `SimInput`, plus the pure lobby seat logic.
- `src/Crossfire.Audio`: SDL3_mixer sound effects and music, driven by simulation events.
- `src/Crossfire.App`: window, fixed 60 Hz loop with render interpolation, debug UI, perf log.
- `tests/`: xUnit tests and BenchmarkDotNet benchmarks.

## Changes from the original plan

- **GLFW instead of SDL for window and input.** BRUTAL's ImGui backends ship for GLFW, not SDL3. GLFW also handles gamepads, so Phase 5 isn't affected. SDL_mixer can still provide audio.
- **SDF shapes plus a small texture atlas.** Bullets, rings, telegraph arcs and the aim reticle are signed-distance shapes, kept round and readable. Ships and effects are Kenney sprites packed into one atlas at startup. The background is a procedural, domain-warped nebula in the fragment shader (`SpriteShape.Nebula`), plus a parallax starfield, a vignette and an animated midline. All of it goes through the same single instanced draw.
- **Graze → Break bar** delivers Phase 6's "graze → Super" early, as a Dodger-side screen clear. The Dodger also gets a Flip.
- **Classic render pass instead of dynamic rendering.** The BRUTAL ImGui Vulkan backend requires a `VkRenderPass`.
- **Bots use a bucket grid, collision doesn't.** Dodger bots gather the 256 *nearest* bullets by walking a 32-unit bucket grid outward in rings. The grid is built at most once per tick and shared by all bots, which keeps eight bots cheap at 30k bullets. Before this, a bot took the first 512 bullets in pool order, which at high density could miss the bullets right next to it.
- **No uniform grid for collision.** Bullets are already split by half, and each half has at most 4 Dodgers, so a per-bullet axis-reject test is cheaper than rebuilding a grid every tick (benchmarked at 0.37 ms for 20k bullets).
- **Parallel bullet movement is implemented** (`ParallelBulletMover`, proven equal to serial by a test). It's off by default because it was slower than serial at 20–30k bullets.
- **1v1 round rule.** With one player per team, a round has a single Dodger, so the Dodger wins by surviving `SurvivalTicks`. Symmetric rounds (2v2+) have no timeout; instead, energy regen ramps up after 60 s.

## Credits

- Art, font and audio: [Kenney](https://www.kenney.nl): Space Shooter Remastered, Kenney Fonts, Sci-Fi Sounds, Interface Sounds, UI Audio, Digital Audio, Music Jingles and Music Loops. All CC0; see `assets/kenney/*/license*.txt` and `assets/kenney/audio/SOURCES.txt`.
- Built with BRUTAL. BRUTAL game development framework is Copyright (c) 2024 Ahwoo Limited (www.ahwoo.com/brutal).
- In-game: **CREDITS** on the title menu lists BRUTAL, Kenney and every third-party component.

### Licence checklist (BRUTAL License v1.0)

- [x] **2.1** The BRUTAL copyright notice is on the credits page and the splash.
- [x] **2.2** The official BRUTAL logo shows before gameplay on every launch, unaltered except for scaling: an exact 4:1 box downscale of `LOGO_BRUTAL_WHT.png`. It can only be skipped after 1.2 s. Source: `assets/brutal/SOURCE.txt`.
- [x] **2.3** `NOTICE.txt` ships next to the executable (copied by the build, and checked by `tools/publish.ps1`).
- [x] **4.0** The third-party licences from each BRUTAL package's `LICENSES/` are checked in under `licenses/` and ship as `LICENSES/`, together with the BRUTAL License itself.
