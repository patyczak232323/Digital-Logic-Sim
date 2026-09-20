# Rewired v0.3.0

Version 0.3.0 is the next major development milestone for Rewired. Its main theme is **authoring and tooling**: RHDL Studio adds source-driven circuit generation, while the editor and engine integration have been reorganized around clearer Rewired-specific APIs and a unified interface.

> Status: development version on `main`. The latest published release remains v0.2.0 until v0.3.0 is formally released.

## Highlights

- Added **RHDL Studio v0.1**, a dedicated source-driven circuit authoring workspace.
- Added the structural **RHDL** compiler:
  - `chip`, `input`, `output`, instance and `connect` statements
  - 1-bit, 4-bit and 8-bit ports
  - references to existing builtin and custom chips
  - automatic dependency-based circuit layout
  - generation of ordinary Rewired `ChipDescription`, pins, subchips and wires
- Added `BUILD` and `BUILD & OPEN` workflows for RHDL sources.
- Added project-local RHDL source persistence under `HDL/`.
- Added `OPEN SOURCE` for generated RHDL chips.
- Added runtime regression coverage proving that an RHDL-generated HalfAdder executes correctly through Rewired Engine.

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
