# CPU4 — tiny 4-bit computer in RHDL

CPU4 is the smallest complete processor example in Rewired. It deliberately avoids a data-RAM dependency so it can be built with the components that are actually available.

## Build

Build these first:

1. `../Basics/08_DFF.rhdl`
2. `../Basics/09_REG4.rhdl`
3. `00_CPU4.rhdl`

CPU4 uses the stock ROM for program storage. Its registers are built from NAND-based D flip-flops; it does **not** use `dev.RAM-8`.

## Architecture

- 4-bit accumulator `A`
- 4-bit program counter: 16 program addresses
- 4-bit `OUT_PORT`
- 8-bit fixed instruction word
- instruction format: `OOOO DDDD`
- `Z` flag is true when the accumulator equals zero
- one instruction per rising clock edge
- synchronous reset
- no data RAM

The stock simulator ROM is 256 x 16. CPU4 uses only addresses 0..15 and the **low byte** of each ROM word. Therefore an 8-bit instruction `0x13` is entered in the ROM editor as `0x0013`.

## ISA

| Opcode | Mnemonic | Operation |
| --- | --- | --- |
| 0 | NOP | no operation |
| 1 | LDI n | A <- n |
| 2 | ADDI n | A <- A + n |
| 3 | SUBI n | A <- A - n |
| 4 | ANDI n | A <- A AND n |
| 5 | ORI n | A <- A OR n |
| 6 | XORI n | A <- A XOR n |
| 7 | JMP a | PC <- a |
| 8 | JZ a | if Z, PC <- a |
| 9 | JNZ a | if !Z, PC <- a |
| A | NOT | A <- NOT A |
| B | INC | A <- A + 1 |
| C | DEC | A <- A - 1 |
| D | reserved | currently acts like NOP |
| E | OUT | OUT_PORT <- A |
| F | HLT | halt until RESET |

Arithmetic wraps modulo 16.

## First program

This calculates 3 + 2, sends 5 to `OUT_PORT`, then halts.

| Address | ROM word | Assembly |
| ---: | ---: | --- |
| 0 | `0013` | `LDI 3` |
| 1 | `0022` | `ADDI 2` |
| 2 | `00E0` | `OUT` |
| 3 | `00F0` | `HLT` |

Expected final state:

- `ACC = 5`
- `OUT_PORT = 5`
- `PC = 3`
- `HALTED = 1`

## Loop example

A continuously increasing 4-bit counter:

| Address | ROM word | Assembly |
| ---: | ---: | --- |
| 0 | `0010` | `LDI 0` |
| 1 | `00E0` | `OUT` |
| 2 | `00B0` | `INC` |
| 3 | `0071` | `JMP 1` |

`OUT_PORT` cycles through 0..15 and wraps back to 0.
