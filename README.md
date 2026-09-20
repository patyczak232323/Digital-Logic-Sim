# Rewired

**Rewired** is an independent digital logic simulator project based on Sebastian Lague's [Digital Logic Sim](https://github.com/SebLague/Digital-Logic-Sim).

The project started from the Digital Logic Sim codebase and keeps much of its editor workflow, file format and visual language, but the simulation runtime is being redesigned around a different execution model. Rewired should therefore be treated as its **own simulator**, not as a drop-in replacement or a fully compatible "better version" of Digital Logic Sim.

## What Rewired is

Rewired is focused on experimenting with a high-performance simulation engine for large digital circuits.

The runtime uses ideas such as:

- event-driven propagation
- compiled netlist topology
- dirty-gate scheduling
- deterministic fixed-point settling
- combinational JIT compilation
- feedback JIT for supported cyclic gate networks
- optional FULL LUT caching for suitable combinational Custom Chips
- diagnostics, profiling, waveform capture and regression testing

The goal is to make it practical to simulate large and deeply nested circuits while keeping circuit behaviour well-defined under the Rewired execution model.

## Relationship to Digital Logic Sim

Rewired is derived from Digital Logic Sim and still shares a significant amount of editor, project and UI code with the original project.

However, the simulation engine is intentionally different.

Digital Logic Sim's original runtime processes gates and subchips in traversal order and allows ordering effects to influence some feedback circuits. Rewired instead attempts to propagate affected logic to a stable state using deterministic delta-cycle settling and additional acceleration paths.

That difference is important.

A circuit that was designed around propagation order, race conditions or timing quirks of the original Digital Logic Sim engine may behave differently in Rewired even when the project loads successfully.

Rewired is therefore **not intended to guarantee behavioural compatibility with Digital Logic Sim**.

## Project compatibility

Many Digital Logic Sim projects can still be opened because Rewired retains the familiar project structure and editor concepts.

Compatibility should currently be understood as:

- project/file-format compatibility: generally a goal
- editor/workflow familiarity: generally a goal
- exact simulation behaviour: **not guaranteed**
- timing/order-dependent circuits: may behave differently
- NAND-built latches, flip-flops, counters and gate-level memories: should be tested specifically
- built-in stateful components such as Pulse, Clock, RAM and displays: actively tested, but differences can still exist

Large existing computers are useful compatibility and stress tests, but Rewired does not define correctness as reproducing every race-condition or traversal-order side effect of the original engine.

## Simulation model

The normal Rewired step is broadly structured as:

1. apply external and spontaneous inputs
2. settle combinational propagation
3. advance built-in sequential/stateful components
4. settle resulting combinational changes
5. update diagnostics and visible state

Feedback networks can be handled by the deterministic solver or by supported acceleration paths. Initialization is treated separately so that storage elements can reach a usable starting state without making normal simulation depend on random gate evaluation.

This model is one of the main architectural differences between Rewired and Digital Logic Sim.

## Engine integration architecture

Application and editor code now integrate with the runtime through a single entry point: `RewiredEngine`.

The intended dependency direction is:

```text
Editor / Project / UI
        |
        v
   RewiredEngine
        |
        +-- deterministic runtime
        +-- simulation graph/backend
        +-- JIT / feedback JIT
        +-- LUT cache
        +-- diagnostics / replay / waveform
```

The older low-level `Simulator` implementation and `DeterministicSimulator` are internal runtime details. Game/UI code should not call them directly. This keeps editor integration stable while allowing the engine implementation to be reorganized or optimized independently.

## RHDL Studio

Rewired **0.3.0** includes the experimental **RHDL Studio** source-driven circuit generator.

RHDL v0.3 is a frontend for ordinary Rewired circuits: readable expressions are lowered into normal structural topology (NAND gates, split/merge chips, buses, pins and wires), then compiled into a standard `ChipDescription`. There is no separate RHDL simulation path.

Current features include:

- `chip`, `input`, `output`, `wire`, instance and `connect` statements
- 1-bit, 4-bit and 8-bit signals using either `A[8]` or `A: 8` declaration syntax
- compile-time constants: binary (`0b1010`), hexadecimal (`0xA5`) and decimal
- compile-time parameters/defaults such as `chip Name(WIDTH=8)`
- bitwise logic: `AND`, `OR`, `XOR`, `NOT` and `& | ^ ! ~`
- arithmetic: `+` and `-`
- unsigned comparisons: `== != < > <= >=`
- constant shifts: `<<` and `>>`
- ternary mux expressions: `sel ? A : B`
- bit selection and slicing: `A[3]`, `A[7:4]`
- concatenation: `{A[7:4], B[3:0]}`
- named instance bindings, for example `NAND n(IN_A=a, IN_B=b, OUT=y)`
- structural authoring with existing builtin or custom chips
- diagnostics for invalid references, width mismatches, multiple drivers and assignment loops
- line and column information for expression diagnostics
- automatic dependency-based placement of generated topology
- project-local source persistence under `HDL/`
- `BUILD`, `BUILD & OPEN` and `OPEN SOURCE`

Example:

```text
chip AluMini(WIDTH=8) {
  input A: WIDTH, B: WIDTH
  input sel
  output Y: WIDTH
  output equal

  wire sum: WIDTH
  wire mixed: WIDTH

  sum = A + B
  mixed = {A[7:4], B[3:0]}

  Y = sel ? sum : mixed
  equal = A == B
}
```

The structural form remains available when exact topology is desired:

```text
NAND n1
connect a -> n1.IN_A
connect b -> n1.IN_B
connect n1.OUT -> y
```

Pin names containing spaces can be written with underscores, for example `IN_A` resolves to `IN A`.

RHDL Studio also provides document-wide selection/clipboard editing, automatic `{}`, `()` and `[]` pairing, automatic indentation, and block expansion: pressing **Tab** or **Enter** with the caret between `{}` expands the pair onto separate indented lines. A dedicated gutter and vertical separator visually separate line numbers from source text.

Current bus widths are intentionally limited to **1, 4 and 8 bits**, matching the underlying Rewired pin types. Shift counts are compile-time constants/parameters. Chip parameters are currently compile-time defaults within one source unit rather than fully generic parameterized saved-chip instances.

## Current development

Current development version: **Rewired v0.3.0**.

Current `main` includes work on:

- deterministic event-driven simulation
- deeply nested Custom Chip propagation
- native combinational JIT
- native feedback JIT for supported cyclic gate networks
- persistent FULL LUT caching
- state materialization when accelerated regions are inspected
- simulation profiling and diagnostics
- waveform recording
- deterministic replay
- non-convergence reporting
- regression tests for latches, registers, counters, nested circuits and large netlists
- compatibility tests for imported and stateful projects
- RHDL Studio structural circuit generation and source persistence
- unified Rewired UI styling and diagnostics workspace

Rewired is still experimental. Complex circuits are expected to expose engine bugs and edge cases, and those projects are especially valuable for development.

See `Docs/SIMULATION_DIAGNOSTICS.md` for current diagnostics and debugging tools.

## Rewired-8

**Rewired-8** is an 8-bit CPU being designed as a technology demo for the Rewired simulation engine.

Its purpose is to exercise the engine with a complete computer designed specifically around Rewired's simulation semantics rather than around compatibility quirks of another simulator.

## Downloads

Prebuilt releases currently use the existing Rewired package names:

- **Windows x64:** `DLSRewired-Windows-x64.zip`
- **Linux x86_64:** `DLSRewired-Linux-x86_64.zip`

Latest published release: **v0.2.0**

https://github.com/patyczak232323/Digital-Logic-Sim/releases/tag/v0.2.0

Development on `main` is **v0.3.0** and is ahead of the latest published v0.2.0 release.

## Repository policy

The canonical project is maintained by **@patyczak232323**.

External contributors should use forks and pull requests. Direct write access to the canonical repository is not intended for third parties.

## Credits

Rewired is based on [Sebastian Lague's Digital Logic Sim](https://github.com/SebLague/Digital-Logic-Sim).

The original project provided the foundation for the editor, circuit format and much of the surrounding application code. Rewired's simulation runtime and related tooling are being developed separately from that foundation.

## License

Licensed under the MIT License. See `LICENSE`.
