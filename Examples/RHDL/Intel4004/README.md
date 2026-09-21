# Intel 4004 — monolithic RHDL example

`00_INTEL4004.rhdl` is a single-source Intel 4004 compatible CPU core for Rewired.

## Build

Build only:

`Examples/RHDL/Intel4004/00_INTEL4004.rhdl`

No custom RHDL dependency has to be built first. The source contains all architectural
state directly as NAND master/slave flip-flops. It does not use the hidden development
`dev.RAM-8` component.

## What is modelled

- 4-bit accumulator and carry/link
- 16 x 4-bit index registers
- 12-bit program counter
- three 12-bit return-stack levels
- 4004 machine, I/O/RAM and accumulator instruction groups
- one-byte and two-byte instruction sequencing
- indirect `FIN` ROM fetch through R0:R1
- `SRC` address latch
- `DCL` RAM bank selection
- TEST input used by `JCN`

## Program interface

- `ROM_ADDR_HIGH:4` + `ROM_ADDR_LOW:8` = 12-bit program/ROM address
- `ROM_DATA:8` = byte supplied by external program ROM

During the second memory cycle of `FIN`, the ROM address output is redirected to
the indirect address from R0:R1. The core preserves the original 4004 page-boundary
behaviour.

## Data and I/O interface

The original MCS-4 multiplexed electrical bus is intentionally represented as a clean
decoded Rewired interface:

- `RAM_ADDR:8`
- `RAM_BANK:4`
- `RAM_DATA_IN/OUT:4`
- `RAM_STATUS_IN/OUT:4`
- `ROM_PORT_IN/OUT:4`
- `RAM_WRITE`
- `RAM_STATUS_WRITE`
- `RAM_PORT_WRITE`
- `ROM_PORT_WRITE`
- `PROGRAM_WRITE`

This makes the CPU practical to wire to user-built Rewired memories while preserving
the instruction-visible CPU behaviour.

## Debug outputs

The example exposes ACC, carry, PC, SRC and R0-R15 so the generated CPU can be
inspected easily in the simulator.

## Compatibility scope

This is an ISA/architectural implementation, not a transistor-level die reconstruction.
It does not attempt to reproduce the original 4004 pin-level four-phase/multiplexed
MCS-4 bus timing. RESET is synchronous to the generated NAND register clock.

The compile audit includes this source so parser/lowerer regressions can be caught by
the normal Rewired runtime test suite.
