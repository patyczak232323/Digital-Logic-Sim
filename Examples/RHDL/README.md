# RHDL examples

This is the small curated RHDL example set for the current Rewired/RHDL v0.6 syntax.

Build them in this order:

1. `Basics/00_AND.rhdl`
2. `Basics/01_HALF_ADDER.rhdl`
3. `Basics/02_FULL_ADDER.rhdl`
4. `Basics/03_MUX4.rhdl`
5. `Basics/04_DFF.rhdl`
6. `Basics/05_REG4.rhdl`
7. `CPU4/00_CPU4.rhdl`

The examples intentionally stay small. CPU4 is the largest processor example in this directory and uses a 4-bit datapath.

CPU4 depends on `DffBit` and `Reg4`, so build the two stateful examples before building the CPU.
