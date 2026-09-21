# Rewired — Digital Logic Simulator

**Rewired** is an open-source **digital logic simulator and logic gate circuit simulator** for building and testing digital circuits, CPUs, registers, memory and custom chips. It includes a high-performance simulation runtime, a graphical editor and **RHDL**, a small hardware description language for generating circuits from source.

Rewired is based on Sebastian Lague's [Digital Logic Sim](https://github.com/SebLague/Digital-Logic-Sim), but uses its own simulation engine and should be treated as an independent simulator rather than a drop-in compatible replacement.

[![Latest Release](https://img.shields.io/github/v/release/patyczak232323/Digital-Logic-Sim?label=release)](https://github.com/patyczak232323/Digital-Logic-Sim/releases/latest)
[![Regression](https://github.com/patyczak232323/Digital-Logic-Sim/actions/workflows/simulation-regression.yml/badge.svg)](https://github.com/patyczak232323/Digital-Logic-Sim/actions/workflows/simulation-regression.yml)
[![License](https://img.shields.io/github/license/patyczak232323/Digital-Logic-Sim)](LICENSE)

## What you can build

Rewired is intended for learning and experimenting with **digital electronics, Boolean logic, computer architecture and CPU design**.

You can build:

- logic gates and combinational circuits
- multiplexers, adders and ALUs
- latches, flip-flops and registers
- counters and state machines
- RAM and other gate-level memory structures
- custom reusable chips
- small CPUs and complete computer architectures
- source-generated circuits with RHDL

The repository includes a compact example progression ending with a **4-bit CPU** and a separate **4×4-bit RAM** example.

## Main features

- graphical digital circuit editor
- deterministic event-driven simulation
- compiled circuit/netlist topology
- dirty-gate scheduling
- fixed-point settling for signal propagation
- support for deeply nested Custom Chips
- combinational JIT acceleration
- feedback JIT for supported cyclic gate networks
- persistent FULL LUT cache for suitable combinational circuits
- simulation diagnostics and profiling
- waveform capture and deterministic replay
- non-convergence diagnostics
- **RHDL v0.6** hardware description language
- Windows x64 and Linux x86_64 release builds

## Download

The latest prebuilt version is available in **GitHub Releases**:

**[Download the latest Rewired release](https://github.com/patyczak232323/Digital-Logic-Sim/releases/latest)**

Release packages:

- Windows x64: `DLSRewired-Windows-x64.zip`
- Linux x86_64: `DLSRewired-Linux-x86_64.zip`

On Linux, after extracting the archive, the executable may need permission:

```bash
chmod +x DLSRewired.x86_64
./DLSRewired.x86_64
```

For a first project, see **[Getting Started](Docs/GETTING_STARTED.md)**.

## RHDL — hardware description language

Rewired includes **RHDL Studio**, a source-driven circuit generator. RHDL describes hardware concurrently and lowers into normal Rewired components and wires; it does not use a separate simulation runtime.

Example:

```text
circuit Adder
    input A: 8
    input B: 8
    output Y: 8

    Y = A + B
end
```

RHDL supports:

- 1, 4 and 8-bit signals
- Boolean logic: `and`, `or`, `xor`, `not`
- arithmetic and comparisons
- bit indexing and slicing
- `join(...)`
- `choose(...)` multiplexing
- reusable component instances
- explicit structural `connect` wiring
- feedback/stateful circuits through ordinary Rewired topology

See the **[RHDL v0.6 Guide](Docs/RHDL_GUIDE.md)**.

## Examples

The curated RHDL examples are intentionally small and easy to follow:

```text
AND
 └─ Half Adder
     └─ Full Adder
         └─ MUX4
             └─ D Flip-Flop
                 └─ 4-bit Register
                     ├─ RAM 4×4
                     └─ CPU4
```

Files are under **[Examples/RHDL](Examples/RHDL/)**.

### CPU4

CPU4 is a simple educational processor example with:

- 4-bit accumulator
- 4-bit program counter
- 4-bit output port
- 16 program addresses
- fixed 8-bit instructions
- arithmetic, logic, jumps, output and halt

See **[CPU4 documentation](Examples/RHDL/CPU4/README.md)**.

### RAM4x4

The memory example contains four 4-bit words built from normal RHDL registers rather than a hidden RAM primitive.

See **[RAM4x4 documentation](Examples/RHDL/Memory/README.md)**.

## Simulation engine

Rewired's runtime is organized around a single integration boundary:

```text
Editor / Project / UI
        |
        v
   RewiredEngine
        |
        +-- deterministic runtime
        +-- simulation graph/backend
        +-- JIT / feedback JIT
        +-- FULL LUT cache
        +-- diagnostics / replay / waveform
```

The goal is predictable simulation semantics while still allowing acceleration of suitable circuit regions.

More detail:

- **[Simulation Diagnostics](Docs/SIMULATION_DIAGNOSTICS.md)**
- **[Native JIT](Docs/NATIVE_JIT.md)**
- **[Persistent FULL LUT Cache](Docs/PERSISTENT_FULL_LUT_CACHE.md)**

## Compatibility with Digital Logic Sim

Rewired keeps much of the original Digital Logic Sim editor workflow, project structure and visual language, so many existing projects can be opened.

However, exact simulation behaviour is **not guaranteed to match** Digital Logic Sim.

In particular, circuits that depend on:

- traversal order
- race conditions
- propagation timing quirks
- feedback initialization side effects

may behave differently under Rewired's deterministic simulation model.

Rewired is therefore best considered a separate **digital circuit simulator** derived from the original project.

## Documentation

Start here:

- **[Documentation index](Docs/README.md)**
- **[Getting Started](Docs/GETTING_STARTED.md)**
- **[RHDL Guide](Docs/RHDL_GUIDE.md)**
- **[Simulation Diagnostics](Docs/SIMULATION_DIAGNOSTICS.md)**
- **[Native JIT](Docs/NATIVE_JIT.md)**
- **[Persistent FULL LUT Cache](Docs/PERSISTENT_FULL_LUT_CACHE.md)**
- **[Rewired v0.3.0 release notes](Docs/RELEASE_0.3.0.md)**

## Development

Current version: **Rewired v0.3.0**

The Unity version is defined in `ProjectSettings/ProjectVersion.txt`.

Clone:

```bash
git clone https://github.com/patyczak232323/Digital-Logic-Sim.git
cd Digital-Logic-Sim
```

The repository includes regression tooling for the simulation engine and computer-level behaviour.

Release builds are produced automatically by GitHub Actions when a version tag such as `v0.3.0` is pushed.

## Project status

Rewired is under active development. Complex stateful circuits and very large designs may still expose engine bugs or unsupported edge cases.

Bug reports and reproducible test circuits are useful, especially for:

- sequential logic
- feedback networks
- large nested chips
- CPU designs
- imported Digital Logic Sim projects

## Credits

Rewired is based on [Sebastian Lague's Digital Logic Sim](https://github.com/SebLague/Digital-Logic-Sim).

The original project provides the foundation for much of the editor, project format and surrounding application code. Rewired develops a separate simulation runtime, diagnostics tooling, acceleration paths and RHDL authoring workflow on top of that foundation.

## License

Licensed under the **MIT License**. See [LICENSE](LICENSE).
