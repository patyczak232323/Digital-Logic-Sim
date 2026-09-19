# Digital Logic Sim Rewired

**Digital Logic Sim Rewired** is an independently maintained fork of Sebastian Lague's Digital Logic Sim. It keeps the familiar editor and project format while replacing the simulation runtime with the Rewired deterministic engine.

## Release status

Source release candidate: **v0.3.0**

Latest published binary release: **v0.2.0**

The upstream project/save format remains **DLS 2.1.6**. Rewired's release number is separate from the project-format version.

## Simulation architecture

Rewired 0.3.0 uses its own simulation engine for **all projects**, including feedback, latches, flip-flops, registers, counters and CPUs.

Core runtime:

- compiled netlist topology
- event-driven dirty propagation
- bounded deterministic delta cycles
- explicit power-on/topology-recovery settling
- DynamicMethod JIT for pure combinational Custom Chips
- Feedback JIT for cyclic gate-feedback Custom Chips
- persistent FULL LUT cache for eligible combinational chips
- optional experimental native C backend

There is no runtime fallback to Sebastian's `StepChip` scheduler.

## Feedback and registers

Gate-feedback circuits are handled by the Rewired engine itself. Cyclic Custom Chips can receive a `FeedbackExecutor`, which evaluates a two-buffer native sweep while preserving the deterministic engine's simultaneous-update semantics.

Runtime tests cover cross-coupled NAND latches, feedback state materialization, an 8-bit feedback latch bank, deoptimization/inspection handoff and ownership of accelerated feedback state.

Structural edits use the normal deterministic resettle first. If a newly-created symmetric feedback network fails to converge, the runtime performs a bounded asynchronous recovery settle and preserves sequential edge history.

## Experimental preferences

`MENU -> PREFERENCES -> EXPERIMENTAL`

- **Native C fast engine**: Off / NAND only / All supported
- **Engine diagnostics**: Rewired engine status, Feedback JIT availability, accelerator counters and latest engine event
- **C/JIT cross-check**: compares eligible Native C and DynamicMethod JIT outputs bit-for-bit and keeps the JIT output authoritative

All experimental options default to **Off**.

## Diagnostics

`MENU -> SIM DIAGNOSTICS` provides hot-chip profiling, raw engine benchmark, LUT/JIT/Feedback-JIT counters, logic-analyzer probes, waveform capture, deterministic replay and convergence summaries.

See `Docs/SIMULATION_DIAGNOSTICS.md`.

## Performance reference

A controlled .NET 8 core benchmark using a 4,096-NAND acyclic graph, 20,000 evaluations per round and a 7-round median measured approximately:

| Engine | Median time |
| --- | ---: |
| Sebastian-style StepChip baseline | 1099 ms |
| compact managed interpreter | 118 ms |
| experimental native C | 76 ms |
| DynamicMethod JIT | 62 ms |

The DynamicMethod JIT was about **17.7x faster** than the Sebastian-style core baseline in that microbenchmark. Full feedback/CPU workloads use the deterministic engine and Feedback JIT, so real application speed depends on circuit structure.

## Compatibility

Existing Digital Logic Sim project files remain intended to load under DLS 2.1.6 project-format semantics. Rewired-specific preference fields are additive.

Runtime timing semantics are Rewired's own deterministic model; they are not a fallback to Sebastian's original scheduler.

## Validation

Release-gate CI covers engine audits, runtime compilation/tests, whole-repo C# syntax parsing, propagation stress tests, NAND latches/DFFs/registers, 8-bit computer tests, generated computer netlist verification, whole-program regressions and Native C differential tests.

See `Docs/RELEASE_0.3.0.md`.

## Credits and license

Original Digital Logic Sim by **Sebastian Lague**.

Rewired fork maintained by **@patyczak232323**.

Licensed under the MIT License. See `LICENSE`.
