# Getting Started with Rewired

Rewired is a digital logic and circuit simulator for building logic gates, registers, memory, CPUs and other digital hardware.

## 1. Download

Use the latest GitHub Release:

https://github.com/patyczak232323/Digital-Logic-Sim/releases/latest

Available packages:

- `DLSRewired-Windows-x64.zip`
- `DLSRewired-Linux-x86_64.zip`
- `DLSRewired-WebGL.zip` (runs in a browser)
- `DLSRewired-Android-arm64.apk` (experimental)

Extract the archive before running it. On Android, open the APK and allow installing apps from that source if asked.

### Browser (WebGL)

Unzip `DLSRewired-WebGL.zip` and serve the folder that contains `index.html` from any static web host. To try it locally, run this inside that folder:

```bash
python3 -m http.server 8000
```

Then open `http://localhost:8000`. Opening `index.html` straight from disk usually fails, because browsers block loading the game files from `file://`. Projects are kept in the browser's own storage; the native JIT and FULL LUT accelerators are not available there, so large circuits run slower.

### Linux

If needed:

```bash
chmod +x DLSRewired.x86_64
./DLSRewired.x86_64
```

## 2. Start with a small circuit

A good first progression is:

1. NAND / AND logic
2. Half Adder
3. Full Adder
4. Multiplexer
5. D Flip-Flop
6. 4-bit Register
7. RAM4x4
8. CPU4

Ready-to-read RHDL versions are available in `Examples/RHDL/`.

## 3. Use the graphical editor

Rewired retains the familiar visual circuit-building workflow inherited from Digital Logic Sim. Components can be connected into reusable Custom Chips and nested into larger systems.

For stateful designs, test clocks, feedback and reset behaviour carefully.

## 4. Try RHDL Studio

RHDL is Rewired's hardware description language.

Minimal example:

```text
circuit And2
    input A, B
    output Y

    Y = A and B
end
```

Build the source in RHDL Studio to generate an ordinary Rewired circuit.

For syntax, components, buses and structural wiring, read [RHDL_GUIDE.md](RHDL_GUIDE.md).

## Global custom chips

The save dialog has an `AVAILABILITY` selector:

- `THIS PROJECT` stores the custom chip only in the open project.
- `ALL PROJECTS` stores one shared copy and makes it available in every project.

A global chip may use built-in chips and other global chips. Make custom dependencies global first. Rewired blocks conflicting global names and blocks moving a shared chip back to one project while another project still depends on it.

## 5. Try the RAM example

`Examples/RHDL/Memory/00_RAM4x4.rhdl` implements four 4-bit words using normal registers.

It demonstrates:

- address decoding
- synchronous writes
- combinational reads
- reusable stateful components

## 6. Try CPU4

`Examples/RHDL/CPU4/00_CPU4.rhdl` is the largest processor example in the repository.

It has:

- 4-bit accumulator
- 4-bit PC
- 16 program addresses
- arithmetic and logic instructions
- conditional and unconditional jumps
- output register
- halt state

See `Examples/RHDL/CPU4/README.md` for the ISA and a test ROM program.

## 7. Debug larger circuits

For difficult circuits, use the simulation diagnostics and profiling tools described in [SIMULATION_DIAGNOSTICS.md](SIMULATION_DIAGNOSTICS.md).

- **Truth table**: right-click (long-press on Android) a custom chip and pick *TRUTH TABLE* to see every input combination. *RECORD TEST* saves the table; the test re-runs whenever the window opens, so a later change that breaks the chip shows up in red.
- **Breakpoints**: right-click a pin and pick *BREAKPOINT* to pause the simulation when it changes, rises, falls or reaches a value. *SPACE* (or *STEP* on Android) then advances one tick at a time.
- **Export**: *EXPORT* in a chip's menu or the bottom-bar menu writes structural Verilog (also copied to the clipboard), an SVG schematic or a PNG screenshot to the project's `Exports` folder. On Android 10 and later each export is also copied to `Download/Rewired`, and *SHARE LAST EXPORT* sends it to another app.

## 8. Learn with challenges

*CHALLENGES* (bottom-bar menu, or *TOOLS* on Android) walks from a NOT gate built from NAND to a 4-bit adder. Each step names the chip to build and its pins; *CHECK* compares your saved chip's truth table with the goal and tells you the first input combination that is still wrong.

## Android controls

- Tap an input pin to toggle it; start a wire on one pin and finish it on another.
- Long-press a chip, pin or wire for its menu.
- Tap a chip in the bottom bar, then tap where it should go.
- Two fingers move and zoom the view; *PAN* in the top bar does it with one finger.
- Swipe in from the left edge, or press *TOOLS*, for the tools sheet.
- The top bar has *PAN*, *UNDO*, *REDO*, *RUN*/*PAUSE* and *STEP*. *TOOLS → HELP* shows the gesture guide again.

## Compatibility note

Rewired is based on Digital Logic Sim but uses a different simulation model. Existing projects may load successfully while timing-sensitive or race-dependent behaviour can differ.
