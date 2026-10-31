# CPU8

CPU8 is an 8-bit single-cycle processor with 32 instructions, eight registers and 256 bytes of RAM. It is built the way you would build it by hand in the editor: every level is its own chip, made only from the chips of the level below. Nothing above the gates uses RHDL's `+`, `==` or `if`: the adder is eight full adders, the RAM is 256 registers with decoders and multiplexers, and the control unit is a 5-to-32 decoder with OR gates.

Rewired installs it as the **CPU8** project. Open it and the test program below runs at 10 instructions per second; the registers show on the outputs of `Cpu8Computer`. Press **R** to start again.

## Architecture

- 8 registers R0..R7, 8 bits each; flags Z (zero) and C (carry / borrow)
- 16-bit instruction: 5-bit opcode, 3-bit register, 8-bit immediate or address
- 11-bit program counter: 2048 program words in eight stock 256x16 ROMs
- 256 bytes of RAM, read combinationally, written on the rising clock edge
- one instruction per rising clock edge; `RESET` clears the PC, the registers and the flags on the next edge

## Chips

Build them in file order, after `../Basics/04_DFF.rhdl` (`DffBit`):

| File | Chip | Built from | NAND gates |
| --- | --- | --- | ---: |
| `00_NOT` | `Not` | NAND | 1 |
| `01_AND` | `And` | NAND, Not | 2 |
| `02_OR` | `Or` | NAND, Not | 3 |
| `03_XOR` | `Xor` | 4 NAND | 4 |
| `04_OR4` | `Or4` | 3 Or | 9 |
| `05_MUX` | `Mux` | NAND, Not | 4 |
| `06_FULL_ADDER` | `Add1` | Xor, And, Or | 15 |
| `07_ADD8` | `Add8` | 8 Add1 (ripple carry) | 120 |
| `08_MUX2X8` .. `11_MUX16X8` | `Mux2x8`, `Mux4x8`, `Mux8x8`, `Mux16x8` | Mux, then trees of the smaller ones | 32 .. 480 |
| `12_DEC2` .. `15_DEC5` | `Dec2`, `Dec3`, `Dec4`, `Dec5` | And, Not, then trees of Dec2 | 18 .. 183 |
| `16_IS_ZERO8` | `IsZero8` | Or4, Or, Not | 22 |
| `17_REGISTER8` | `Register8` | 8 DffBit, 8 Mux (write enable) | 120 |
| `18_RAM16X8` | `Ram16x8` | 16 Register8, Dec4, Mux16x8 | 2 490 |
| `19_RAM256X8` | `Ram256x8` | 16 Ram16x8, Dec4, Mux16x8 | 40 410 |
| `20_REGISTER_FILE` | `RegisterFile` | 8 Register8, Dec3, 2 Mux8x8 | 1 507 |
| `21_ALU` | `Alu` | Add8, Xor, And, Or, Not, Mux8x8, IsZero8 | 533 |
| `22_FLAGS` | `Flags` | 2 DffBit, Mux, And | 35 |
| `23_PROGRAM_COUNTER` | `ProgramCounter` | 2 Register8, 2 Add8, Mux2x8 | 613 |
| `24_PROGRAM_ROM` | `ProgramRom` | 8 ROM 256x16, 2 Mux8x8 | 448 |
| `25_CONTROL_UNIT` | `ControlUnit` | Dec5, Or, Or4, And, Not | 344 |
| `26_CPU8` | `Cpu8` | everything above | 44 113 |
| `27_CPU8_COMPUTER` | `Cpu8Computer` | Cpu8, CLOCK, KEY, PULSE | |

Inside `Cpu8` the data flows from left to right: the PC addresses `ProgramRom`; the control unit decodes the opcode; the register file reads Rd (port A) and Rs (port B); the ALU or the RAM produces the result; a multiplexer writes it back into Rd on the clock edge.

## Instruction formats

| Format | Bits 15-11 | Bits 10-8 | Bits 7-0 | Used by |
| --- | --- | --- | --- | --- |
| OP | opcode | - | - | NOP, HLT |
| REG | opcode | Rd | - | INC, DEC, NOT, SHL, SHR |
| REG-REG | opcode | Rd | Rs in bits 7-5, bits 4-0 unused | MOV, ADD, ADC, SUB, SBC, AND, OR, XOR, CMP, LDR, STR |
| IMM8 | opcode | Rd | imm8 | LDI, ADDI, SUBI, ANDI, ORI, XORI, CMPI |
| MEM8 | opcode | R | addr8 | LD, ST |
| JUMP11 | opcode | address bits 10-8 | address bits 7-0 | JMP, JZ, JNZ, JC, JNC |

## Instructions

| Opcode | Instruction | Effect | Flags |
| ---: | --- | --- | --- |
| 00 | `NOP` | nothing | - |
| 01 | `HLT` | stop until RESET (the PC stays on the HLT) | - |
| 02 | `MOV Rd, Rs` | Rd <- Rs | - |
| 03 | `LDI Rd, imm8` | Rd <- imm8 | - |
| 04 | `LD Rd, [addr8]` | Rd <- RAM[addr8] | - |
| 05 | `ST [addr8], Rs` | RAM[addr8] <- Rs | - |
| 06 | `LDR Rd, [Rs]` | Rd <- RAM[Rs] | - |
| 07 | `STR [Rd], Rs` | RAM[Rd] <- Rs | - |
| 08 | `ADD Rd, Rs` | Rd <- Rd + Rs | Z, C |
| 09 | `ADDI Rd, imm8` | Rd <- Rd + imm8 | Z, C |
| 0A | `ADC Rd, Rs` | Rd <- Rd + Rs + C | Z, C |
| 0B | `SUB Rd, Rs` | Rd <- Rd - Rs | Z, C |
| 0C | `SUBI Rd, imm8` | Rd <- Rd - imm8 | Z, C |
| 0D | `SBC Rd, Rs` | Rd <- Rd - Rs - C | Z, C |
| 0E | `INC Rd` | Rd <- Rd + 1 | Z, C |
| 0F | `DEC Rd` | Rd <- Rd - 1 | Z, C |
| 10 | `AND Rd, Rs` | Rd <- Rd AND Rs | Z |
| 11 | `ANDI Rd, imm8` | Rd <- Rd AND imm8 | Z |
| 12 | `OR Rd, Rs` | Rd <- Rd OR Rs | Z |
| 13 | `ORI Rd, imm8` | Rd <- Rd OR imm8 | Z |
| 14 | `XOR Rd, Rs` | Rd <- Rd XOR Rs | Z |
| 15 | `XORI Rd, imm8` | Rd <- Rd XOR imm8 | Z |
| 16 | `NOT Rd` | Rd <- NOT Rd | Z |
| 17 | `SHL Rd` | Rd <- Rd << 1, C <- bit 7 | Z, C |
| 18 | `SHR Rd` | Rd <- Rd >> 1 (logical), C <- bit 0 | Z, C |
| 19 | `CMP Rd, Rs` | flags of Rd - Rs, nothing written | Z, C |
| 1A | `CMPI Rd, imm8` | flags of Rd - imm8, nothing written | Z, C |
| 1B | `JMP addr11` | PC <- addr11 | - |
| 1C | `JZ addr11` | jump if Z = 1 | - |
| 1D | `JNZ addr11` | jump if Z = 0 | - |
| 1E | `JC addr11` | jump if C = 1 | - |
| 1F | `JNC addr11` | jump if C = 0 | - |

**C after a subtraction is the borrow**: `SUB`, `SUBI`, `DEC`, `CMP` and `CMPI` set C = 1 when the result went below zero (Rd < Rs as unsigned numbers), and `SBC` subtracts it. So after `CMP Rd, Rs`, `JC` jumps if Rd < Rs and `JNC` if Rd >= Rs. The logic instructions leave C alone; `MOV`, `LDI`, loads, stores and jumps leave both flags alone.

## Test program

[`test_program.asm`](test_program.asm) is the program from the instruction sheet. The CPU8 project has it in ROM 0:

| Address | Word | Instruction |
| ---: | ---: | --- |
| 000 | `1805` | LDI R0, 5 |
| 001 | `1901` | LDI R1, 1 |
| 002 | `5820` | SUB R0, R1 |
| 003 | `E802` | JNZ 0x002 |
| 004 | `1AAA` | LDI R2, 0xAA |
| 005 | `2A20` | ST [0x20], R2 |
| 006 | `2320` | LD R3, [0x20] |
| 007 | `CA60` | CMP R2, R3 |
| 008 | `E00A` | JZ 0x00A |
| 009 | `0800` | HLT (the test failed) |
| 00A | `0000` | NOP (end of the test) |
| 00B | `D80B` | JMP 0x00B |

Expected result: R0 = 0, R1 = 1, R2 = R3 = 0xAA, Z = 1, the PC stays at 0x00B and `HALTED` stays 0.

## Writing programs

[`cpu8asm.py`](cpu8asm.py) turns assembly into ROM words:

```bash
python3 Examples/RHDL/CPU8/cpu8asm.py Examples/RHDL/CPU8/test_program.asm
```

Labels end with `:`, comments start with `;` or `#`, numbers can be decimal, `0x` hex or `0b` binary. Type the words into the ROMs inside `ProgramRom` (open `Cpu8`, then `ProgramRom`, and edit a ROM): ROM 0 (the top one) holds addresses 0x000-0x0FF, ROM 1 0x100-0x1FF, and so on. `--rom` prints the words ROM by ROM.

The simulation speed is set in the project's preferences. 44 000 gates are a lot to simulate: a few hundred instructions per second is the most to expect.
