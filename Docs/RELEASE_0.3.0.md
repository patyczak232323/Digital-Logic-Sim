# Rewired v0.3.0

Version 0.3.0 is the next major development milestone for Rewired. Its main theme is **authoring and tooling**: RHDL Studio adds source-driven circuit generation, while the editor and engine integration have been reorganized around clearer Rewired-specific APIs and a unified interface.

> Status: development version on `main`. The latest published release remains v0.2.0 until v0.3.0 is formally released.

## Highlights

- Expanded **RHDL Studio** to RHDL v0.3 while retaining structural authoring compatibility.
- RHDL v0.3 now lowers higher-level source into ordinary Rewired topology rather than introducing a separate execution path:
  - `chip`, `input`, `output`, `wire`, instance and `connect` statements
  - 1/4/8-bit buses with `A[8]` and `A: 8` declaration forms
  - binary, hexadecimal and decimal constants
  - compile-time parameter defaults such as `chip Name(WIDTH=8)`
  - bitwise logic `AND OR XOR NOT` and `& | ^ ! ~`
  - arithmetic `+` and `-`
  - unsigned comparisons `== != < > <= >=`
  - constant shifts `<< >>`
  - ternary mux expressions
  - bit indexing, slicing and concatenation
  - named instance pin bindings
  - internal wires backed by normal Rewired bus topology
  - diagnostics for unknown signals/pins, width errors, multiple drivers and assignment loops
  - line/column diagnostics for expression errors
  - automatic dependency-based circuit layout
  - generation of ordinary Rewired `ChipDescription`, pins, subchips and wires
- Added `BUILD`, `BUILD & OPEN` and project-local RHDL source persistence under `HDL/`.
- Added `OPEN SOURCE` for generated RHDL chips.
- Improved the RHDL editor with automatic bracket pairing, Tab/Enter expansion of `{}` into an indented block, multi-line indentation and a dedicated line-number gutter separator.
- Added executable runtime regression coverage for RHDL-generated NAND logic, 8-bit addition/subtraction, slicing, concatenation, muxes, comparisons, shifts, constants and named pin bindings.

## Editor and UI

- Unified the simulator around the Rewired dark UI language.
- Added shared `RewiredUI` components for headers, cards, frames and accent styling.
- Redesigned Simulation Diagnostics into a compact Rewired workspace.
- Restyled main menu, settings, keybindings, library, search, ROM editor, chip save/customization, context menus, popups, bottom bar and status banners.
- Added configurable editor shortcuts and persistent keybindings.
- Added horizontal and vertical chip mirroring.
- Added bottom-bar drag/drop between collections and standalone starred chips.

## Engine architecture

- Added `RewiredEngine` as the single public integration boundary between editor/game code and the simulation runtime.
- Hid the low-level legacy `Simulator` and `DeterministicSimulator` behind the Rewired engine API.
- Routed diagnostics, replay, waveform capture and live project integration through the Rewired boundary.
- Added regression guards to prevent UI/game code from bypassing `RewiredEngine`.

## Simulation tooling

- Improved deterministic diagnostics and profiler presentation.
- Retained combinational JIT, feedback JIT and persistent FULL LUT acceleration paths.
- Added and expanded regression coverage for large generated circuits, feedback networks, stateful components and RHDL-generated logic.

## Compatibility

Rewired remains its own simulator. Project/file-format compatibility with Digital Logic Sim is still useful and generally intended, but exact traversal-order or race-condition behaviour is not guaranteed.

`DLSVersion` is intentionally kept separate from the Rewired application version because it is used for legacy project-format compatibility.
