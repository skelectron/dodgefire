# CROSSFIRE

Competitive team bullet-hell PvP on one split arena, built in C# / .NET 10 on the BRUTAL framework (Vulkan).
Dodgers survive in their own half; Gunners place telegraphed bullet patterns in the enemy half.

Status: **Phases 0–4 done**, so a 1v1 match is playable on one keyboard. Next up are Phase 5 (gamepads, lobby, 2v2) and Phase 6 (graze Super, audio, splash, licence notices).

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
| `--2v2` | Spectate the 2v2 layout (only players 1–2 have keyboard controls until Phase 5) |
| `--seed N` | RNG seed |

## Controls (1v1, one keyboard; roles swap every round)

| | Left player (P1, cyan) | Right player (P2, magenta) |
|---|---|---|
| Move / aim cursor | WASD | Arrows |
| Focus (slow, precise) | Left Shift | Right Shift / Right Ctrl |
| Patterns: Ring, Spiral, Aimed, Wall | Q E R F (or 1–4) | U I O P (or Numpad 1–4) |
| Rematch | Space | Enter |

F1 opens the debug panel: frame graph, stress buttons, state hash, and live tuning of every rule and pattern. Esc quits.

The Dodger wins an asymmetric round (1v1) by surviving 40 s, and the Gunner wins it by taking all 3 lives. First to 2 rounds wins.

## Measured (Intel HD Graphics 620, Release, vsync)

| Check | Target | Result |
|---|---|---|
| Static sprites | 50,000 @ 60 FPS | 60 FPS |
| Bullets in play | 20,000, sim < 4 ms | 60 FPS, sim ≈ 0.3 ms/tick average |
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
- **SDF shapes instead of a texture atlas.** Bullets, rings, telegraph arcs and cursors are drawn by the fragment shader, so the renderer needs no textures or descriptor sets.
- **Classic render pass instead of dynamic rendering.** The BRUTAL ImGui Vulkan backend requires a `VkRenderPass`.
- **No uniform grid for collision.** Bullets are already split by half, and each half has at most 4 Dodgers, so a per-bullet axis-reject test is cheaper than rebuilding a grid every tick (benchmarked at 0.37 ms for 20k bullets).
- **Parallel bullet movement is implemented** (`ParallelBulletMover`, proven equal to serial by a test). It's off by default because it was slower than serial at 20–30k bullets.
- **1v1 round rule.** With one player per team, a round has a single Dodger, so the Dodger wins by surviving `SurvivalTicks`. Symmetric rounds (2v2+) have no timeout; instead, energy regen ramps up after 60 s.
