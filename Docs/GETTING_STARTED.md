# Getting Started with Rewired

Rewired is a digital logic and circuit simulator for building logic gates, registers, memory, CPUs and other digital hardware.

## 1. Download

Use the latest GitHub Release:

https://github.com/patyczak232323/Digital-Logic-Sim/releases/latest

Available packages:

- `DLSRewired-Windows-x64.zip`
- `DLSRewired-Linux-x86_64.zip`

Extract the archive before running it.

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

## Compatibility note

Rewired is based on Digital Logic Sim but uses a different simulation model. Existing projects may load successfully while timing-sensitive or race-dependent behaviour can differ.
