# Rewired v0.4.0

Rewired v0.4.0 brings Rewired to Android phones and tablets and to the browser, adds tools for checking and sharing circuits (truth tables, breakpoints, exports and guided challenges), adds chips shared across projects and makes saving safer. It also adds automated tests that run the real simulation engine.

## Highlights

- **Android (ARM64)**: a touch interface built for phones and tablets. Parts are dragged onto the board from a *PARTS* panel sorted into categories, with a short description of each; tapping a part shows its actions along the bottom of the board; one finger on empty space moves the view and two fingers zoom. The top bar has *MENU*, *UNDO*, *REDO*, *RUN*/*PAUSE*, *STEP* and *SAVE*, and banners say what the next tap will do while a wire or a part is being placed. The main menu opens a project with one tap, an empty board explains the first steps, and a six-step guide opens on the first project and from *MENU → HOW IT WORKS*.
- **WebGL (browser)**: a build that runs from any static web host. Projects are kept in the browser's storage and survive a reload. The native JIT and FULL LUT background builds are not available there, so large circuits run slower.
- **Truth tables**: *TRUTH TABLE* in a custom chip's menu lists every input combination (chips with more than 12 input bits show a 4096-row sample). *RECORD TEST* saves the table; the test runs again whenever the window opens and differing rows are shown in red.
- **Breakpoints**: *BREAKPOINT* in a pin's menu pauses the simulation on the step where the pin changes, rises, falls or reaches a value. *SPACE* (or *STEP* on Android) then advances one tick at a time.
- **Export**: *EXPORT* writes structural Verilog-2001 (also copied to the clipboard), an SVG schematic or a PNG screenshot to the project's `Exports` folder. On Android 10 and later each export is also copied to `Download/Rewired`, and *SHARE LAST EXPORT* opens the system share sheet.
- **Challenges**: nine guided steps from a NOT gate built from NAND to a 4-bit adder. *CHECK* compares your saved chip's truth table with the goal and names the first input combination that is still wrong.
- **Global chips**: save a custom chip with `AVAILABILITY` set to `ALL PROJECTS` and it is available in every project. Renames propagate to other projects, and deleting a global chip warns that other projects may depend on it.
- **Readable RHDL schematics**: chips generated from RHDL use a left-to-right block layout with orthogonal wiring, and keep the root pin order from the source.
- **Built-in name conflicts**: if a custom chip has the same name as a newer built-in chip, the project menus now show which chip to rename to use the built-in one.

## Fixes

- RHDL `not X` no longer changes every later use of `X` in the same circuit. In CPU4 this made `INC` and `DEC` compute `~A + 1` and `~A - 1`.
- RHDL structural `connect` accepts the same short pin names as named bindings, so `connect A -> gate.A` reaches the NAND pin `IN A`.
- Renaming a project no longer reports that its own name already exists.
- On a touch screen, a tap on a pin or a part right after touching somewhere else now acts on what is under the finger: the press used to be handled before the app had looked at the new position.

## Saving and data

- Desktop save data now lives in its own `Rewired` subfolder instead of the folder shared with the original Digital Logic Sim. Existing data is copied there once on first launch; the original folder is left untouched.
- A failed final save step can no longer delete both the new and the previous version of a file, and an interrupted save is recovered on the next load.
- Project and chip names that would resolve outside their save folder are rejected.
- Switching projects waits for the previous simulation thread to stop before starting the next one.
- Recorded truth-table tests are stored per project in `Chip Tests/<chip>.json`, so the chip file format is unchanged.

## Testing

- Unity EditMode tests run the shipped C# engine through `RewiredEngine`: gate-level behaviour (NAND, SR latch, nested chips, oscillator guard, determinism), every curated RHDL example including the CPU4 program, every RHDL circuit shown in the documentation, the save system, truth tables, breakpoints and challenges.
- The native JIT, FULL LUT and feedback JIT paths are compared with the plain reference simulator on every input of a 4-bit ALU and on a register sequence.
- `Tools/EngineHarness` runs the same tests with plain .NET, without Unity, on every push, simulates the exported Verilog with Icarus Verilog, and benchmarks the engine against recorded work counters.

## Downloads

- Windows x64: `DLSRewired-Windows-x64.zip`
- Linux x86_64: `DLSRewired-Linux-x86_64.zip`
- WebGL (browser): `DLSRewired-WebGL.zip` — unzip it and serve the folder from any static web host
- Android ARM64: `DLSRewired-Android-arm64.apk` (experimental)

On Android, allow installing apps from your browser or file manager, then open the APK.

## Compatibility

Projects from v0.3.0 open unchanged. Rewired remains an independent simulator derived from Sebastian Lague's Digital Logic Sim; exact traversal-order, race-condition and timing-dependent behaviour is not guaranteed to match the original runtime.
