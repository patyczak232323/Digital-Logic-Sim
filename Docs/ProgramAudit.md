# Whole-program reliability audit

Date: 2026-09-16

## Scope

The audit reviewed all 142 C# source files in the repository, with deeper tracing of the save/load pipeline, project and chip rename flows, simulation construction, stateful built-ins, UI confirmation shortcuts, editor wire loading, camera/UI input routing, and simulation-thread pacing. It also cross-checked relevant reports from the upstream issue tracker.

This is a static and executable-regression audit. Unity Editor/Player integration tests are still required before a release build because the CI environment does not contain Unity.

## Fixed findings

| Severity | Area | Failure mode | Remediation |
| --- | --- | --- | --- |
| Critical | Save system | Direct overwrite could leave truncated JSON after interruption | Saves now flush to a same-directory temporary file, preserve `.bak`, and replace the destination |
| Critical | Loading | JSON errors were swallowed and returned default objects | Deserialization now fails explicitly; loader retries `.bak`; app settings safely fall back to defaults |
| Critical | Chip graph | A cyclic custom-chip reference could recurse until a process-level stack overflow | Simulation construction now detects active dependency cycles and reports the dependency path |
| High | Save dialogs | Enter activated save/create even when the visible button was disabled | Keyboard confirmation now uses the same validation gate as the button |
| High | Rename | Delete-before-save could lose a chip; case-only project/chip rename was unreliable | Rename writes first and supports case-only paths through a rollback-capable temporary move |
| High | Project deletion | `DeleteProject(..., false)` performed no deletion | The non-backup branch now deletes the selected project directory |
| High | Wire loading | Invalid or forward connected-wire indices could throw during project load | Indices are bounds-checked and invalid dependencies fall back to a safe pin connection |
| High | Stateful simulation | Malformed pin state could index outside 256-entry RAM/ROM/display buffers | Every 8-bit address is masked before memory access; written data is width-limited |
| High | Built-in state | Missing/short serialized state crashed pulse, key, LED, or ROM components; RAM power-on values polluted pin flags | Required state is padded safely and RAM cells are initialized as true 8-bit values |
| Medium | Simulation settings | Zero/negative clock transition values produced a stuck or invalid clock | Runtime and preferences clamp the value to at least one step |
| Medium | CPU use | Low target rates busy-spun a simulation thread for the full wait interval | Long waits now sleep and use a short spin only near the deadline |
| Medium | Input routing | Wheel input over the bottom bar also zoomed the circuit | Camera zoom is suppressed while the pointer is over the bottom bar |
| Medium | Chip save UI | Typing a long name permanently grew the preview even after shortening it | Name-driven minimum size is recalculated while preserving only deliberate extra size |
| Medium | Portability | Whitespace-only/trailing-space names and path string replacement created ambiguous paths | Portable-name checks and path construction were hardened |

## Verification

- Deterministic solver regression: NAND hierarchy, long chains, latches, DFFs, nested circuits, creation-order independence, restart determinism, oscillation guard, multi-driver resolution, and 4/8/16-bit counters over 100,000 clocks each.
- Cross-program regression: portable names, dependency cycles, 8-bit address containment, backup recovery, and static integration guards for every remediation above.
- GitHub Actions runs both suites on pushes and pull requests.

## Residual risks

- Project/editor behavior should be smoke-tested in Unity on Windows, macOS, and Linux, especially case-only rename and forced termination during save.
- The simulation and rendering threads still share mutable Unity-facing state. Existing exception guards reduce symptoms, but a future architecture pass should publish immutable frame snapshots instead of reading editor objects from the simulation thread.
- Undo/redo does not yet model every wire geometry edit as a first-class command. That should be handled in a dedicated editor-history change with Unity interaction tests.
- Saved projects do not yet have an explicit schema version with structural validation. Backups prevent common corruption loss, but schema validation would produce better recovery diagnostics.
