# RHDL examples

These examples are ordered from simple combinational logic to stateful circuits and complete CPUs.

The public Rewired palette does **not** provide a general-purpose RAM block. The examples therefore do not depend on the hidden development-only `dev.RAM-8` primitive. Stateful examples are built from NAND feedback.

## Recommended order

### Basics

1. `Basics/00_AND.rhdl`
2. `Basics/01_BUS_ALU.rhdl`
3. `Basics/02_STRUCTURAL_NAND.rhdl`
4. `Basics/03_HALF_ADDER.rhdl`
5. `Basics/04_FULL_ADDER.rhdl`
6. `Basics/05_MUX8.rhdl`
7. `Basics/06_SR_LATCH.rhdl`
8. `Basics/07_D_LATCH.rhdl`
9. `Basics/08_DFF.rhdl`
10. `Basics/09_REG4.rhdl`
11. `Basics/10_COUNTER4.rhdl`

Build `08_DFF.rhdl` before `09_REG4.rhdl`, and build `09_REG4.rhdl` before `10_COUNTER4.rhdl`.

### CPU4

Build the Basics through `09_REG4.rhdl`, then build `CPU4/00_CPU4.rhdl`.

CPU4 intentionally has no data RAM. It is a compact accumulator CPU intended to demonstrate ROM fetch, state, ALU operations, branches, output and halt without relying on unavailable memory primitives.

### Rewired-8

Build `Basics/08_DFF.rhdl` first, then build the files in `Rewired8/` in the order listed in its README.

The Rewired-8 core exposes instruction-memory and data-memory interfaces. A real RAM implementation can be attached externally when one exists as a user-built Rewired chip.


## Intel 4004

`Intel4004/00_INTEL4004.rhdl` is a monolithic Intel 4004 compatible CPU example.
Unlike the staged CPU examples, it is intentionally self-contained: build that one RHDL
file directly; no custom RHDL dependency has to be built first.
