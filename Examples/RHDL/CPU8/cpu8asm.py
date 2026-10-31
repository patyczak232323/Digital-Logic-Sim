#!/usr/bin/env python3
"""Assembler for the CPU8 example processor (see README.md).

    python3 cpu8asm.py program.asm          # one hex word per line, with addresses
    python3 cpu8asm.py program.asm --rom    # words only, ROM by ROM (ROM n = 0xn00..0xnFF)

One instruction per line, "label:" on its own line or before an instruction,
';' or '#' comments, numbers in decimal, 0x hex or 0b binary.
"""

import sys

INSTRUCTIONS = {
    "NOP": (0, "op"), "HLT": (1, "op"), "MOV": (2, "rr"), "LDI": (3, "ri"),
    "LD": (4, "load"), "ST": (5, "store"), "LDR": (6, "loadr"), "STR": (7, "storer"),
    "ADD": (8, "rr"), "ADDI": (9, "ri"), "ADC": (10, "rr"), "SUB": (11, "rr"),
    "SUBI": (12, "ri"), "SBC": (13, "rr"), "INC": (14, "r"), "DEC": (15, "r"),
    "AND": (16, "rr"), "ANDI": (17, "ri"), "OR": (18, "rr"), "ORI": (19, "ri"),
    "XOR": (20, "rr"), "XORI": (21, "ri"), "NOT": (22, "r"), "SHL": (23, "r"),
    "SHR": (24, "r"), "CMP": (25, "rr"), "CMPI": (26, "ri"), "JMP": (27, "jump"),
    "JZ": (28, "jump"), "JNZ": (29, "jump"), "JC": (30, "jump"), "JNC": (31, "jump"),
}

OPERAND_COUNT = {"op": 0, "r": 1, "jump": 1}


class AsmError(Exception):
    pass


def assemble(source):
    labels = {}
    instructions = []
    for number, text in enumerate(source.splitlines(), 1):
        for mark in ";#":
            text = text.split(mark, 1)[0]
        text = text.strip()
        if ":" in text:
            label, text = text.split(":", 1)
            label = label.strip().upper()
            if label in labels:
                raise AsmError(f"line {number}: label '{label}' is defined twice")
            labels[label] = len(instructions)
            text = text.strip()
        if not text:
            continue
        op, _, rest = text.partition(" ")
        args = [a.strip() for a in rest.split(",")] if rest.strip() else []
        instructions.append((number, op.upper(), args))

    def reg(line, arg):
        if len(arg) == 2 and arg[0] in "Rr" and arg[1] in "01234567":
            return int(arg[1])
        raise AsmError(f"line {line}: expected a register R0..R7, got '{arg}'")

    def bracket(line, arg):
        if not (arg.startswith("[") and arg.endswith("]")):
            raise AsmError(f"line {line}: expected [address], got '{arg}'")
        return arg[1:-1].strip()

    def number(line, arg, largest):
        if arg.upper() in labels:
            value = labels[arg.upper()]
        else:
            try:
                value = int(arg, 0)
            except ValueError:
                raise AsmError(f"line {line}: expected a number or label, got '{arg}'") from None
        if not 0 <= value <= largest:
            raise AsmError(f"line {line}: {arg} is not between 0 and {largest}")
        return value

    words = []
    for line, op, args in instructions:
        if op not in INSTRUCTIONS:
            raise AsmError(f"line {line}: unknown instruction '{op}'")
        opcode, form = INSTRUCTIONS[op]
        expected = OPERAND_COUNT.get(form, 2)
        if len(args) != expected:
            raise AsmError(f"line {line}: {op} takes {expected} operand(s), got {len(args)}")

        word = opcode << 11
        if form == "r":
            word |= reg(line, args[0]) << 8
        elif form == "rr":
            word |= reg(line, args[0]) << 8 | reg(line, args[1]) << 5
        elif form == "ri":
            word |= reg(line, args[0]) << 8 | number(line, args[1], 0xFF)
        elif form == "load":
            word |= reg(line, args[0]) << 8 | number(line, bracket(line, args[1]), 0xFF)
        elif form == "store":
            word |= reg(line, args[1]) << 8 | number(line, bracket(line, args[0]), 0xFF)
        elif form == "loadr":
            word |= reg(line, args[0]) << 8 | reg(line, bracket(line, args[1])) << 5
        elif form == "storer":
            word |= reg(line, bracket(line, args[0])) << 8 | reg(line, args[1]) << 5
        elif form == "jump":
            word |= number(line, args[0], 0x7FF)
        words.append(word)

    if len(words) > 2048:
        raise AsmError(f"the program has {len(words)} words; the ROMs hold 2048")
    return words


def main(argv):
    if len(argv) < 2 or argv[1] in ("-h", "--help"):
        print(__doc__.strip())
        return 0
    with open(argv[1], encoding="utf-8") as f:
        source = f.read()
    try:
        words = assemble(source)
    except AsmError as e:
        print(f"{argv[1]}: {e}", file=sys.stderr)
        return 1

    if "--rom" in argv[2:]:
        for start in range(0, len(words), 256):
            print(f"ROM {start // 256}:")
            print(" ".join(f"{w:04X}" for w in words[start:start + 256]))
    else:
        for address, word in enumerate(words):
            print(f"{address:03X}  {word:04X}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
