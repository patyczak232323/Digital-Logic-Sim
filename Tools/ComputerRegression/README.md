# 8-bit computer regression

This suite validates a complete, fixed-instruction-width 8-bit computer model
and the gate-level blocks needed to build it in Digital Logic Sim.

## Architecture

- 8-bit data path
- 16 general-purpose 8-bit registers
- 8-bit program counter
- 256 x 16-bit program ROM (Harvard architecture)
- 256 bytes of data RAM
- 8-entry return stack
- zero and carry flags
- fixed 16-bit instruction word

Instruction layout:

```text
R-type: [ opcode:4 ][ destination:4 ][ source A:4 ][ source B:4 ]
Memory: [ opcode:4 ][ register:4    ][ address/immediate:8       ]
Branch: [ opcode:4 ][ unused:4      ][ target address:8          ]
```

Opcodes: `MOV ADD SUB AND OR XOR CMP SHL SHR LD ST LDI JMP JNZ CALL RET`.

Run from the repository root:

```bash
python3 Tools/ComputerRegression/regression.py
```

The suite exhaustively checks the architectural ALU, executes a diagnostic
program using all opcodes, verifies deterministic cold starts, tests PC and
stack boundaries, exhaustively checks a NAND-only 8-bit adder, and performs
10,000 writes through a nested NAND/DFF 8-bit register.

## Native project save layout

Digital Logic Sim stores a project as a directory, not one monolithic file:

```text
Project name/
  ProjectDescription.json
  Chips/
    CHIP NAME.json
    ANOTHER CHIP.json
```

`ProjectDescription.json` stores project preferences and the ordered list of
custom chip names. Each file in `Chips/` stores one chip's pins, subchips,
wires, positions, colours, display definitions, and per-instance data. ROM
contents are stored in the ROM subchip's `InternalData` array. Runtime RAM and
register state are deliberately not persisted.
