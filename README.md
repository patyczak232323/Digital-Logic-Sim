<p align="center"><img src="Docs/logo.png" width="128" alt="Rewired logo: a chip with a gold R"></p>

# Rewired — Digital Logic Simulator

**Rewired** is an open-source **digital logic simulator and logic gate circuit simulator** for building and testing digital circuits, CPUs, registers, memory and custom chips. It includes a high-performance simulation runtime, a graphical editor and **RHDL**, a small hardware description language for generating circuits from source.

Rewired is based on Sebastian Lague's [Digital Logic Sim](https://github.com/SebLague/Digital-Logic-Sim), but uses its own simulation engine and should be treated as an independent simulator rather than a drop-in compatible replacement.

[![Latest Release](https://img.shields.io/github/v/release/patyczak232323/Digital-Logic-Sim?label=release)](https://github.com/patyczak232323/Digital-Logic-Sim/releases/latest)
[![Regression](https://github.com/patyczak232323/Digital-Logic-Sim/actions/workflows/simulation-regression.yml/badge.svg)](https://github.com/patyczak232323/Digital-Logic-Sim/actions/workflows/simulation-regression.yml)
[![Unity tests](https://github.com/patyczak232323/Digital-Logic-Sim/actions/workflows/unity-tests.yml/badge.svg)](https://github.com/patyczak232323/Digital-Logic-Sim/actions/workflows/unity-tests.yml)
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
- global Custom Chips shared safely between projects
- combinational JIT acceleration
- feedback JIT for supported cyclic gate networks
- persistent FULL LUT cache for suitable combinational circuits
- simulation diagnostics and profiling
- waveform capture and deterministic replay
- non-convergence diagnostics
- **RHDL 1.0** hardware description language with Python syntax
- truth table for any combinational chip, recordable as a regression test
- breakpoints: pause when a pin changes, rises, falls or reaches a value
- export to structural Verilog, SVG schematics and PNG screenshots
- 64 x 64 colour screen (4 bits per channel, double-buffered) for games and graphics demos
- Snake example project, installed on first start
- updates from GitHub Releases inside the app on Windows, Linux and Android
- guided challenges from a NOT gate built from NAND to a 4-bit adder
- Windows x64, Linux x86_64, WebGL (browser) and Android ARM64 (experimental) release builds
- touch interface for Android tablets and phones: drag parts from a categorised panel, tap a part for its actions, one-finger panning and a step-by-step guide

## Download

The latest prebuilt version is available in **GitHub Releases**:

**[Download the latest Rewired release](https://github.com/patyczak232323/Digital-Logic-Sim/releases/latest)**

Release packages:

- Windows x64: `DLSRewired-Windows-x64.zip`
- Linux x86_64: `DLSRewired-Linux-x86_64.zip`
- WebGL (browser): `DLSRewired-WebGL.zip`. Unzip it and serve the folder from any static web host. In the browser, projects are kept in the browser's own storage. The native JIT and LUT accelerators are not available there, so large circuits run slower.
- Android ARM64 (experimental): `DLSRewired-Android-arm64.apk`

On Linux, after extracting the archive, the executable may need permission:

```bash
chmod +x DLSRewired.x86_64
./DLSRewired.x86_64
```

### Updating

Rewired checks GitHub for a newer release when it starts (turn this off in Settings). The bottom bar of the main menu, or the header on Android, then offers the update:

- **Windows and Linux**: *Update* downloads the release, quits Rewired, copies the new files over the installed ones and starts the new version. If the install folder is not writable, the release page opens instead.
- **Android**: *Download* opens the new APK in the browser. Open the downloaded file to update; Android may ask once to allow installs from the browser. Projects are kept.
- **WebGL**: the site is updated by whoever hosts it.

For a first project, see **[Getting Started](Docs/GETTING_STARTED.md)**.

## RHDL — hardware description language

Rewired includes **RHDL Studio**, a source-driven circuit generator. RHDL uses Python syntax to describe hardware and builds an ordinary Rewired chip from NAND gates and your other chips; it does not use a separate simulation runtime.

Example:

```python
circuit FullAdder(A, B, CIN) -> (SUM, COUT):
    ab = A ^ B
    SUM = ab ^ CIN
    COUT = (A & B) | (CIN & ab)
```

RHDL supports:

- Python operators: `& | ^ ~`, `+ -`, comparisons, `<< >>`, and `and`/`or`/`not` on bits
- `a if select else b` multiplexing
- bit indexing and Python slices (`A[0:4]`), `cat(...)` and `u8(...)` width changes
- `for i in range(n):` loops and variables that build chains such as ripple-carry adders
- calling other chips like functions, with tuple unpacking (`q, nq = DffBit(d, clk)`)
- feedback and memory: a name can be used above the line that assigns it
- constant folding, shared gates and readable left-to-right schematics

See the **[RHDL Guide](Docs/RHDL_GUIDE.md)**.

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
                     ├─ CPU4
                     └─ 8-bit Register
                         └─ PIX6 computer + 64×64 screen
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

### PIX6

PIX6 is a small computer that draws on the 64 × 64 colour screen:

- X and Y pen registers, a 12-bit colour register and an 8-bit program counter
- 16-bit instructions: set and move the pen, set or generate a colour, plot, clear, show, read the screen, keys, random numbers, jumps, halt
- a 12-instruction program that fills the screen with a colour pattern and draws a diagonal, and a moving-dot animation
- **Snake** in 70 instructions, installed as the first project when Rewired starts: open it and play with W A S D

See **[PIX6 documentation](Examples/RHDL/PIX6/README.md)**.

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
- **[Rewired v0.4.0 release notes](Docs/RELEASE_0.4.0.md)**
- **[Contributing](CONTRIBUTING.md)**

## Development

Current version: **Rewired v0.4.0**

The Unity version is defined in `ProjectSettings/ProjectVersion.txt`.

Clone:

```bash
git clone https://github.com/patyczak232323/Digital-Logic-Sim.git
cd Digital-Logic-Sim
```

Tests:

- **Unity EditMode tests** in `Assets/Tests/EditMode` run the real C# engine, compile and simulate every example in `Examples/RHDL`, and compile every RHDL circuit shown in the documentation. Run them from *Window → General → Test Runner* in the Unity editor; CI runs them in `unity-tests.yml`.
- **Engine harness** (`Tools/EngineHarness`) runs the same EditMode tests with plain .NET in under two minutes, simulates exported Verilog with Icarus Verilog, and benchmarks the engine. See its README. Run the tests with `dotnet test Tools/EngineHarness --filter "Category!=Benchmark"` (the .NET 8 SDK is enough; Unity is not needed).
- **Python regression suites** in `Tools/` check solver rules and source-level invariants without Unity:

```bash
python3 Tools/SimulationRegression/regression.py
python3 Tools/ComputerRegression/regression.py
python3 Tools/ProgramRegression/regression.py
```

Release builds for Windows, Linux, WebGL and Android are produced automatically by GitHub Actions when a version tag such as `v0.4.0` is pushed; the tests run first and the tag must match `VERSION`. The release text comes from `Docs/RELEASE_<version>.md` when that file exists. Every change to `main` also refreshes the `android-unofficial` pre-release with development builds for all platforms.

See **[CONTRIBUTING.md](CONTRIBUTING.md)** for the repository layout, coding conventions and the release checklist.

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
