# CPU4

CPU4 is the only processor example in the curated RHDL example pack.

## Architecture

- 4-bit accumulator
- 4-bit program counter
- 4-bit output port
- 16 program addresses
- 8-bit fixed instruction word: `OOOO DDDD`
- Z flag
- no data RAM
- synchronous reset

Build these first:

1. `../Basics/04_DFF.rhdl`
2. `../Basics/05_REG4.rhdl`
3. `00_CPU4.rhdl`

## ISA

| Opcode | Instruction | Effect |
| --- | --- | --- |
| 0 | NOP | no operation |
| 1 | LDI n | A <- n |
| 2 | ADDI n | A <- A + n |
| 3 | SUBI n | A <- A - n |
| 4 | ANDI n | A <- A AND n |
| 5 | ORI n | A <- A OR n |
| 6 | XORI n | A <- A XOR n |
| 7 | JMP a | PC <- a |
| 8 | JZ a | jump if A == 0 |
| 9 | JNZ a | jump if A != 0 |
| A | NOT | A <- NOT A |
| B | INC | A <- A + 1 |
| C | DEC | A <- A - 1 |
| D | reserved | behaves like NOP |
| E | OUT | OUT_PORT <- A |
| F | HLT | halt until RESET |

Arithmetic wraps modulo 16.

## Test program

Enter these words into the stock ROM:

| Address | ROM word | Meaning |
| ---: | ---: | --- |
| 0 | `0013` | LDI 3 |
| 1 | `0022` | ADDI 2 |
| 2 | `00E0` | OUT |
| 3 | `00F0` | HLT |

Expected result: `ACC=5`, `OUT_PORT=5`, `HALTED=1`.
