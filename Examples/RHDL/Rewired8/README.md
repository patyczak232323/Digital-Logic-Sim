# Rewired-8 in RHDL v0.6

This directory contains a complete Rewired-8 CPU core written in the current RHDL v0.6 language.

The CPU uses only ordinary Rewired chips and generated RHDL topology. There is no simulator-side CPU implementation.

## Build order in RHDL Studio

Build these sources in order because later chips instantiate the earlier ones:

1. `00_RW8_REG8.rhdl`
2. `01_RW8_REG16.rhdl`
3. `02_RW8_REGFILE8.rhdl`
4. `03_RW8_ALU8.rhdl`
5. `04_RW8_SHIFTBIT8.rhdl`
6. `05_RW8_CORE.rhdl`

The final chip is **RW8_CORE**.

## Architectural state

- 8-bit data path
- fixed 16-bit instruction word
- 6-bit opcode
- R0-R7: eight 8-bit general registers
- X: 16-bit memory pointer
- Y: 16-bit memory pointer
- SP: 16-bit stack pointer
- PC: 16-bit program counter
- IR: 16-bit instruction register
- flags: Z, C, N, V
- Harvard instruction interface
- 64 KiB byte-addressable data-memory interface
- memory-mapped I/O can be implemented in the external data-memory address space

RHDL v0.6 has 1/4/8-bit buses, so every 16-bit architectural value is represented as an explicit HI/LO pair.

## Instruction formats

```text
Register:  [ opcode:6 ][ Rd:3 ][ Rs:3 ][ AUX:4 ]
Immediate: [ opcode:6 ][ Rd:3 ][ imm7:7       ]
Bit:       [ opcode:6 ][ Rd:3 ][ bit:3 ][ 0000 ]
Branch:    [ opcode:6 ][ signed rel10:10       ]
```

For register-format extraction in the core:

```text
instruction[15:10] = opcode
instruction[9:7]   = Rd
instruction[6:4]   = Rs / bit index
instruction[3:0]   = AUX
```

## RW8 ISA v1 opcode map

| Hex | Instruction | Operation |
| --- | --- | --- |
| 00 | NOP | no operation |
| 01 | MOV Rd,Rs | Rd <- Rs |
| 02 | LDI0 Rd,#imm7 | Rd <- 0xxxxxxx |
| 03 | LDI1 Rd,#imm7 | Rd <- 1xxxxxxx |
| 04 | LDX Rd | Rd <- MEM[X] |
| 05 | STX Rs | MEM[X] <- Rs |
| 06 | LDY Rd | Rd <- MEM[Y] |
| 07 | STY Rs | MEM[Y] <- Rs |
| 08 | LDX+ Rd | Rd <- MEM[X], X <- X+1 |
| 09 | STX+ Rs | MEM[X] <- Rs, X <- X+1 |
| 0A | LDY+ Rd | Rd <- MEM[Y], Y <- Y+1 |
| 0B | STY+ Rs | MEM[Y] <- Rs, Y <- Y+1 |
| 0C | SETXL Rs | XL <- Rs |
| 0D | SETXH Rs | XH <- Rs |
| 0E | SETYL Rs | YL <- Rs |
| 0F | SETYH Rs | YH <- Rs |
| 10 | ADD Rd,Rs | Rd <- Rd + Rs |
| 11 | ADC Rd,Rs | Rd <- Rd + Rs + C |
| 12 | SUB Rd,Rs | Rd <- Rd - Rs |
| 13 | SBC Rd,Rs | Rd <- Rd - Rs - C |
| 14 | CMP Rd,Rs | flags <- Rd - Rs |
| 15 | AND Rd,Rs | Rd <- Rd AND Rs |
| 16 | OR Rd,Rs | Rd <- Rd OR Rs |
| 17 | XOR Rd,Rs | Rd <- Rd XOR Rs |
| 18 | NOT Rd | Rd <- NOT Rd |
| 19 | NEG Rd | Rd <- 0 - Rd |
| 1A | INC Rd | Rd <- Rd + 1 |
| 1B | DEC Rd | Rd <- Rd - 1 |
| 1C | ADDI Rd,#imm7 | Rd <- Rd + signext(imm7) |
| 1D | SUBI Rd,#imm7 | Rd <- Rd - signext(imm7) |
| 1E | CMPI Rd,#imm7 | flags <- Rd - signext(imm7) |
| 1F | TEST Rd,Rs | flags <- Rd AND Rs |
| 20 | SHL Rd | logical left shift |
| 21 | SHR Rd | logical right shift |
| 22 | SAR Rd | arithmetic right shift |
| 23 | ROL Rd | rotate left |
| 24 | ROR Rd | rotate right |
| 25 | RCL Rd | rotate left through C |
| 26 | RCR Rd | rotate right through C |
| 27 | SWAP Rd | swap high/low nibble |
| 28 | BTST Rd,#bit | test bit |
| 29 | BSET Rd,#bit | set bit |
| 2A | BCLR Rd,#bit | clear bit |
| 2B | BTGL Rd,#bit | toggle bit |
| 2C | CLC | C <- 0 |
| 2D | SEC | C <- 1 |
| 2E | PUSH Rs | push byte to data-memory stack |
| 2F | POP Rd | pop byte from data-memory stack |
| 30 | JMP rel10 | relative jump |
| 31 | JZ rel10 | jump if Z |
| 32 | JNZ rel10 | jump if !Z |
| 33 | JC rel10 | jump if C |
| 34 | JNC rel10 | jump if !C |
| 35 | JN rel10 | jump if N |
| 36 | JNN rel10 | jump if !N |
| 37 | JV rel10 | jump if V |
| 38 | JNV rel10 | jump if !V |
| 39 | JLT rel10 | signed less-than: N xor V |
| 3A | JGE rel10 | signed greater/equal: !(N xor V) |
| 3B | CALL rel10 | push return PC and branch |
| 3C | RET | restore PC from stack |
| 3D | JMPX | PC <- X |
| 3E | CALLX | push return PC and PC <- X |
| 3F | HALT | stop fetch/execute until reset |

## CPU timing

The CPU deliberately uses a small hardwired micro-sequencer instead of relying on simulator ordering.

```text
STATE 0  FETCH:
  IR <- IMEM[PC]
  PC <- PC + 1

STATE 1  EXEC:
  execute normal instruction
  CALL/CALLX -> STATE 2
  RET        -> STATE 3
  HALT       -> STATE 4
  otherwise  -> STATE 0

STATE 2  CALL second byte:
  push low return-PC byte
  load branch target
  -> STATE 0

STATE 3  RET second byte:
  read high return-PC byte
  restore PC
  -> STATE 0

STATE 4  HALT:
  remain halted until RESET
```

Normal instructions therefore use two rising clock edges: one fetch edge and one execute edge. CALL/RET use extra micro-steps because the memory data bus is 8-bit while PC is 16-bit.

## External interfaces

### Instruction memory

```text
IMEM_ADDR_HI:8
IMEM_ADDR_LO:8
IMEM_HI:8
IMEM_LO:8
```

Instruction memory is combinationally read. During FETCH the two input bytes are latched into IR.

### Data memory

```text
DMEM_ADDR_HI:8
DMEM_ADDR_LO:8
DMEM_IN:8
DMEM_OUT:8
DMEM_READ
DMEM_WRITE
```

This exposes the complete 16-bit address required for 64 KiB RAM and memory-mapped I/O.

## Implementation conventions completed by this core

The opcode map and established instruction formats are unchanged. A few details had not previously been nailed down precisely enough to build hardware, so this implementation makes them explicit:

- ADDI/SUBI/CMPI use signed 7-bit immediates extended to 8 bits.
- relative branches use the already-incremented PC as their base, i.e. architectural `next PC + signext(rel10)`.
- stack grows downward; PUSH/CALL pre-decrement SP and POP/RET post-increment SP.
- CALL stores the 16-bit return address as two bytes: high byte first, then low byte.
- shift/rotate instructions update Z, N and C while preserving V.
- SWAP/BSET/BCLR/BTGL update Z and N while preserving C and V.
- BTST updates Z only.
- CLC/SEC update C only.

These conventions are localized in `05_RW8_CORE.rhdl` so they can be changed without changing the opcode map.

## Debug outputs

RW8_CORE exposes the architectural state needed to validate the first hardware build:

- PC, IR, X, Y and SP as HI/LO byte pairs
- R0-R7
- FLAGS
- current micro-state
- HALTED

They can be removed later once the CPU is stable.
