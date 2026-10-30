# RHDL examples

This is the small curated RHDL example set for RHDL 1.0 (Python-style syntax).

Build them in this order:

1. `Basics/00_AND.rhdl`
2. `Basics/01_HALF_ADDER.rhdl`
3. `Basics/02_FULL_ADDER.rhdl`
4. `Basics/03_MUX4.rhdl`
5. `Basics/04_DFF.rhdl`
6. `Basics/05_REG4.rhdl`
7. `Memory/00_RAM4x4.rhdl`
8. `CPU4/00_CPU4.rhdl`
9. `PIX6/00_REG8.rhdl`, `PIX6/01_PIX6.rhdl`, `PIX6/02_PIX6_COMPUTER.rhdl`

The examples intentionally stay small. CPU4 is a 4-bit accumulator processor. PIX6 is a small computer that draws on the 64 x 64 screen; see `PIX6/README.md` for its instructions and two example programs.

RAM4x4, CPU4 and PIX6 depend on `DffBit` and `Reg4`, so build the two stateful examples first.
