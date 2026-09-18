# Simulation Diagnostics

The development version on `main` includes an integrated diagnostics panel for the Rewired simulation engine.

Open:

`MENU -> SIM DIAGNOSTICS`

## Performance

### Hot-chip profiler

Enable **Hot-chip profiler** to collect per-chip execution timings.

The panel reports:

- execution path: live, FULL LUT, native JIT or feedback JIT
- average time per evaluated chip
- evaluation count
- gate evaluations and signal propagations for the last simulation step
- cache/JIT hit counts
- deterministic convergence state

Profiling is disabled by default and adds timing overhead only while enabled.

### Raw benchmark

Press **START BENCH** to measure compute time spent inside the simulation step.

The benchmark intentionally excludes the project's target-rate sleep/limiter. It reports raw steps/s and average/max CPU time per step without advancing any extra simulation frames.

## Logic analyzer

Right-click an observable pin and select:

`TOGGLE PROBE`

The probe appears in the **LOGIC ANALYZER** section.

- 1-bit pins are drawn as LOW / HIGH / Z traces.
- 4-bit and 8-bit buses are displayed as value transitions.
- samples are stored as transitions rather than one entry per simulation tick
- every transition keeps its simulation-frame timestamp
- removing the last probe from a branch allows LUT/JIT acceleration to be restored

A probed internal signal must remain live and observable. Therefore the simulator temporarily avoids collapsing the Custom Chip subtree containing that signal into LUT/JIT execution.

Use **CLEAR SAMPLES** to keep probes but erase captured transitions, or **REMOVE ALL** to remove all probes.

## Deterministic replay

The diagnostics panel can record external input/keyboard stimuli and replay them from an exact simulation-state snapshot.

1. Press **START RECORD**.
2. Exercise the circuit.
3. Press **STOP RECORD**.
4. Pause the simulation.
5. Press **REPLAY**.

Replay runs on the simulation thread. It restores the initial snapshot, replays recorded inputs and keyboard state, and verifies the root outputs after every frame. A mismatch reports the first divergent replay frame.

Replay recordings are cleared when switching the active chip.

## Feedback JIT diagnostics

> **Compatibility-first release note (v0.3.0):** the root compatibility classifier runs before the deterministic runtime. Projects that require upstream timing because of feedback/stateful semantics are routed to the Sebastian-compatible engine, so the feedback JIT is not used for those ordinary compatibility-mode roots. The code and diagnostics remain available for deterministic-runtime testing and eligible contexts.

Safe cyclic Custom Chips made only from supported logic primitives can use the feedback JIT. This covers gate-built storage structures such as SR latches, D latches, flip-flops and registers.

Important behavior:

- power-on and the first normal tick run through the proven live deterministic solver
- native feedback state is synchronized only after the circuit has settled
- native feedback evaluation uses two delta buffers (`current -> next`)
- stable feedback regions with unchanged boundary inputs require zero native sweeps
- if native feedback does not converge, the accelerator is disabled and the live solver is restored
- state ownership is explicit: only the currently active native executor may materialize state back into primitive gates

Entering a compiled chip for inspection, probing an internal signal, or structurally editing the circuit safely hands state back to the live gate tree.

## CI coverage

Every push to `main` runs:

- engine regression guards
- semantic compilation of the simulation runtime
- executable native runtime tests
- whole-repository C# syntax parsing
- the full simulation stress suite
- 8-bit computer behavioral regression
- generated 8-bit computer netlist execution
- whole-program regression checks

The executable runtime suite includes NAND feedback storage, zero-sweep feedback reuse, state materialization, waveform transition compression, deterministic replay, parallel 8-bit latch-bank isolation, and feedback-JIT ownership/deoptimization tests.
