# CROSSFIRE

Competitive team bullet-hell PvP on one split arena, built in C# / .NET 10 on the BRUTAL framework (Vulkan).
Dodgers survive in their own half; Gunners place telegraphed bullet patterns in the enemy half.

Status: **Phases 0–4 done**, plus Kenney art, bots and a title menu. You can play against a bot, against a friend on one keyboard, or watch Bot vs Bot. Next up are Phase 5 (gamepads, lobby, 2v2) and Phase 6 (graze Super, audio, splash, licence notices).

## Modes and bots

The title menu offers **Vs Bot**, **Local 1v1** and **Bot vs Bot**, with bot difficulty set to Easy, Normal or Hard. A live Bot vs Bot match runs behind the menu. Esc returns to the menu.

Bots live in `src/Crossfire.Sim/Bots`. They are deterministic: integer maths plus their own seeded RNG, and their output is a normal `SimInput`, so replays and future netcode are unaffected.
- **Dodger bot:** searches 9 directions × focus/no focus over a short horizon, predicting bullets linearly. It avoids danger and walls and is pulled toward the centre.
- **Gunner bot:** picks a spot just outside the anti-spawn radius, steers the cursor there, and chooses a pattern based on the situation. It uses Wall when the Dodger is at an edge, Aimed when energy is low, and Hard chains Ring into Aimed.
- **Difficulty** changes lookahead, reaction time, noise and reaction lapses. Lives lost in 100 s against a Hard Gunner bot: idle 27, Easy 17, Normal 5, Hard 1 (`BotTests`).

## Run

```sh
dotnet run -c Release --project src/Crossfire.App
dotnet test                                   # determinism, rules, fixed-point, zero-allocation tests
dotnet run -c Release --project tests/Crossfire.Benchmarks -- --filter "*"
```

Needs a Vulkan 1.3 driver. Install the Vulkan SDK if you want `--validate`.

| Flag | Effect |
|---|---|
| `--validate` | Vulkan validation layers (when installed) |
| `--novsync` | Mailbox/immediate present instead of FIFO |
| `--perf-log` | FPS, frame/sim time and managed bytes allocated per frame, printed every 5 s |
| `--stress N` | Keep about N bullets alive (perf testing) |
| `--sprites N` | Draw N extra static sprites (renderer stress) |
| `--god` | Dodgers can't be hit |
| `--mode vsbot\|local\|demo` | Skip the menu and start that mode |
| `--difficulty easy\|normal\|hard` | Bot difficulty for `--mode` |
| `--2v2` | 2v2 layout; slots 3–4 are bots until Phase 5 adds gamepads |
| `--seed N` | RNG seed |

## Controls (1v1, one keyboard; roles swap every round)

| | Left player (P1, blue) | Right player (P2, red) |
|---|---|---|
| Move / aim cursor | WASD | Arrows |
| Focus (slow, precise) | Left Shift | Right Shift / Right Ctrl |
| Patterns: Ring, Spiral, Aimed, Wall | Q E R F (or 1–4) | U I O P (or Numpad 1–4) |
| Rematch | Space | Enter |

In Vs Bot, the human can use either side of the keyboard. F1 opens the debug panel: frame graph, stress buttons, a state hash, per-slot controllers (click to switch between keyboard and bot difficulties), and live tuning of every rule and pattern.

The Dodger wins an asymmetric round (1v1) by surviving 40 s, and the Gunner wins it by taking all 3 lives. First to 2 rounds wins.

## Measured (Intel HD Graphics 620, Release, vsync)

| Check | Target | Result |
|---|---|---|
| Static sprites | 50,000 @ 60 FPS | 60 FPS |
| Bullets in play | 20,000, sim < 4 ms | 60 FPS, sim ≈ 0.3 ms/tick average. With two Hard bots thinking: 0.3–0.7 ms average, occasional spikes up to 7 ms |
| GC allocations in a match | 0 per frame | 0 B/frame steady state, 0 gen0 collections |
| Determinism | same inputs → same hash over 10k ticks | Passes, with a golden hash, in both Debug and Release |

## Layout

- `src/Crossfire.Sim`: pure deterministic simulation. Q16.16 `Fix` math with a checked-in sine table, a seeded RNG, an SoA bullet pool in `Brutal.Memory`, and patterns, collision and rules. It has no floats, no time and no rendering.
- `src/Crossfire.Render`: Vulkan context, an instanced SDF `SpriteBatch` (one draw call for the whole arena), ImGui/ImPlot layer, and `GameRenderer`.
- `src/Crossfire.Input`: keyboard schemes → `SimInput`.
- `src/Crossfire.App`: window, fixed 60 Hz loop with render interpolation, debug UI, perf log.
- `tests/`: xUnit tests and BenchmarkDotNet benchmarks.

## Changes from the original plan

- **GLFW instead of SDL for window and input.** BRUTAL's ImGui backends ship for GLFW, not SDL3. GLFW also handles gamepads, so Phase 5 isn't affected. SDL_mixer can still provide audio.
- **SDF shapes plus a small texture atlas.** Bullets, rings, telegraph arcs and the aim reticle are signed-distance shapes, kept round and readable. Ships, effects and the background are Kenney sprites packed into one atlas at startup. Both go through the same single instanced draw.
- **Classic render pass instead of dynamic rendering.** The BRUTAL ImGui Vulkan backend requires a `VkRenderPass`.
- **No uniform grid for collision.** Bullets are already split by half, and each half has at most 4 Dodgers, so a per-bullet axis-reject test is cheaper than rebuilding a grid every tick (benchmarked at 0.37 ms for 20k bullets).
- **Parallel bullet movement is implemented** (`ParallelBulletMover`, proven equal to serial by a test). It's off by default because it was slower than serial at 20–30k bullets.
- **1v1 round rule.** With one player per team, a round has a single Dodger, so the Dodger wins by surviving `SurvivalTicks`. Symmetric rounds (2v2+) have no timeout; instead, energy regen ramps up after 60 s.

## Credits

- Art and font: [Kenney](https://www.kenney.nl), Space Shooter Remastered and Kenney Fonts. CC0; see `assets/kenney/*/license.txt`.
- Built with BRUTAL by Ahwoo Limited.
