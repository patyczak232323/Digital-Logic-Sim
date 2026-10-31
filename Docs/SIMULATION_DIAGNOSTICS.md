# Simulation Diagnostics

Rewired includes an integrated diagnostics panel for its simulation engine.

Open it from the bottom bar with `MENU -> SIM DIAGNOSTICS`, or on Android with `TOOLS -> DIAGNOSTICS`.

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

Right-click (long-press on Android) an observable pin and select:

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

Safe cyclic Custom Chips made only from supported logic primitives can use the feedback JIT. This covers gate-built storage structures such as SR latches, D latches, flip-flops and registers.

Important behavior:

- power-on and the first normal tick run through the proven live deterministic solver
- native feedback state is synchronized only after the circuit has settled
- native feedback evaluation uses two delta buffers (`current -> next`)
- stable feedback regions with unchanged boundary inputs require zero native sweeps
- if native feedback does not converge, the accelerator is disabled and the live solver is restored
- a chip with more than 1024 gates is not compiled as a whole, because every sweep evaluates all of it; its smaller feedback subchips (registers, latches) are compiled instead
- state ownership is explicit: only the currently active native executor may materialize state back into primitive gates

Entering a compiled chip for inspection, probing an internal signal, or structurally editing the circuit safely hands state back to the live gate tree.

## Test coverage

The engine behaviour described here is covered by automated tests that run in GitHub Actions on pull requests and on `main`:

- **Unity EditMode tests** (`Assets/Tests/EditMode`) run the shipped C# engine through `RewiredEngine`: NAND and SR-latch behaviour, nested Custom Chips, the oscillator guard, determinism after reset, every curated RHDL example and the CPU4 program.
- **Acceleration equivalence tests** run a 4-bit ALU (every input) and a 4-bit register sequence through the plain reference simulator, the native JIT, the FULL LUT and the feedback JIT and require identical outputs on every step. They also check that persistent LUT files are reused, and rebuilt when they are corrupt or stale.
- **Engine harness** (`Tools/EngineHarness`) runs those tests with plain .NET on a GitHub-hosted runner and benchmarks the engine. Speed is reported; gate evaluations and signal propagations per step are checked against `benchmark-baseline.json`, which catches an accelerator that stops engaging.
- **Python regression suites** (`Tools/SimulationRegression`, `Tools/ComputerRegression`, `Tools/ProgramRegression`) check a model of the solver rules (delta cycles, latches, counters, multi-driver resolution, oscillator bounding), an 8-bit computer and save-system invariants.

See [CONTRIBUTING.md](../CONTRIBUTING.md) for how to run them locally.
