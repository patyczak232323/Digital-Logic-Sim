#!/usr/bin/env python3
"""Generate a runnable 8-bit computer project for Digital Logic Sim.

The generated machine is deliberately built from the simulator's native chips:
NAND gates implement the combinational datapath, while the stock RAM and ROM
chips provide clocked state.  No special simulator-side CPU implementation is
required.
"""

from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime, timezone
import importlib.util
import json
from pathlib import Path
import shutil
import sys
import zipfile


DLS_VERSION = "2.1.6"
PROJECT_NAME = "DLS 8-bit Computer"
ROOT = Path(__file__).resolve().parents[2]
ARTIFACT_ROOT = ROOT / "Artifacts" / "DLS-8Bit-Computer"
PROJECT_ROOT = ARTIFACT_ROOT / "Projects" / PROJECT_NAME
CHIP_ROOT = PROJECT_ROOT / "Chips"
ARCHIVE_PATH = ROOT / "Artifacts" / "DLS-8Bit-Computer.zip"


@dataclass(frozen=True)
class PinSpec:
    name: str
    pin_id: int
    bits: int
    output: bool


@dataclass(frozen=True)
class PinRef:
    owner: int
    pin: int
    bits: int
    drives: bool
    label: str

    def address(self) -> dict[str, int]:
        return {"PinID": self.pin, "PinOwnerID": self.owner}


class Instance:
    def __init__(self, name: str, instance_id: int, specs: list[PinSpec]):
        self.name = name
        self.id = instance_id
        self._pins = {
            spec.name: PinRef(instance_id, spec.pin_id, spec.bits, spec.output,
                              f"{name}#{instance_id}.{spec.name}")
            for spec in specs
        }

    def inp(self, name: str) -> PinRef:
        ref = self._pins[name]
        assert not ref.drives, f"{ref.label} is not an input"
        return ref

    def out(self, name: str) -> PinRef:
        ref = self._pins[name]
        assert ref.drives, f"{ref.label} is not an output"
        return ref


class Registry:
    def __init__(self) -> None:
        self.specs: dict[str, list[PinSpec]] = {}
        self.custom: list[str] = []
        self._add_builtins()

    def add(self, name: str, specs: list[PinSpec], custom: bool = False) -> None:
        if name in self.specs:
            raise ValueError(f"duplicate chip type: {name}")
        self.specs[name] = specs
        if custom:
            self.custom.append(name)

    def _add_builtins(self) -> None:
        self.add("NAND", [PinSpec("A", 0, 1, False), PinSpec("B", 1, 1, False), PinSpec("Q", 2, 1, True)])
        self.add("CLOCK", [PinSpec("Q", 0, 1, True)])
        self.add("dev.RAM-8", [
            PinSpec("ADDRESS", 0, 8, False), PinSpec("DATA", 1, 8, False),
            PinSpec("WRITE", 2, 1, False), PinSpec("RESET", 3, 1, False),
            PinSpec("CLOCK", 4, 1, False), PinSpec("Q", 5, 8, True),
        ])
        self.add("ROM 256×16", [
            PinSpec("ADDRESS", 0, 8, False), PinSpec("HIGH", 1, 8, True),
            PinSpec("LOW", 2, 8, True),
        ])
        self.add("8-1BIT", [PinSpec("IN", 0, 8, False)] + [
            PinSpec(f"B{7 - i}", i + 1, 1, True) for i in range(8)
        ])
        self.add("1-8BIT", [
            PinSpec(f"B{7 - i}", i, 1, False) for i in range(8)
        ] + [PinSpec("OUT", 8, 8, True)])
        self.add("8-4BIT", [
            PinSpec("IN", 0, 8, False), PinSpec("HIGH", 1, 4, True),
            PinSpec("LOW", 2, 4, True),
        ])
        self.add("4-8BIT", [
            PinSpec("HIGH", 0, 4, False), PinSpec("LOW", 1, 4, False),
            PinSpec("OUT", 2, 8, True),
        ])
        self.add("4-1BIT", [PinSpec("IN", 0, 4, False)] + [
            PinSpec(f"B{3 - i}", i + 1, 1, True) for i in range(4)
        ])
        self.add("1-4BIT", [
            PinSpec(f"B{3 - i}", i, 1, False) for i in range(4)
        ] + [PinSpec("OUT", 4, 4, True)])


REGISTRY = Registry()


class ChipBuilder:
    def __init__(self, name: str, colour: tuple[float, float, float] = (0.16, 0.32, 0.52)) -> None:
        self.name = name
        self.colour = colour
        self.inputs: list[dict] = []
        self.outputs: list[dict] = []
        self.subchips: list[dict] = []
        self.wires: list[dict] = []
        self._pin_specs: list[PinSpec] = []
        self._next_pin_id = 0
        # Element IDs share one namespace with developer-pin IDs.  Keep the
        # generated subchips in a separate, deterministic range.
        self._next_subchip_id = 10_000

    @staticmethod
    def _vec(x: float, y: float) -> dict[str, float]:
        return {"x": x, "y": y}

    def _pin(self, name: str, bits: int, output: bool, display: int = 0) -> PinRef:
        pin_id = self._next_pin_id
        self._next_pin_id += 1
        target = self.outputs if output else self.inputs
        y = -1.8 * len(target)
        target.append({
            "Name": name, "ID": pin_id,
            "Position": self._vec(8 if output else -8, y),
            "BitCount": bits, "Colour": 0, "ValueDisplayMode": display,
        })
        spec = PinSpec(name, pin_id, bits, output)
        self._pin_specs.append(spec)
        # A developer input drives the circuit; a developer output consumes it.
        return PinRef(pin_id, 0, bits, not output, f"{self.name}.{name}")

    def add_input(self, name: str, bits: int = 1) -> PinRef:
        return self._pin(name, bits, False)

    def add_output(self, name: str, bits: int = 1, display: int = 0) -> PinRef:
        return self._pin(name, bits, True, display)

    def add(self, chip_name: str, label: str = "", internal_data: list[int] | None = None) -> Instance:
        if chip_name not in REGISTRY.specs:
            raise KeyError(f"unknown chip type {chip_name!r}")
        instance_id = self._next_subchip_id
        self._next_subchip_id += 1
        index = len(self.subchips)
        x = -7 + (index % 12) * 1.3
        y = 4 - (index // 12) * 1.25
        output_colours = [
            {"PinColour": 0, "PinID": spec.pin_id}
            for spec in REGISTRY.specs[chip_name] if spec.output
        ]
        self.subchips.append({
            "Name": chip_name, "ID": instance_id, "Label": label,
            "Position": self._vec(x, y),
            "OutputPinColourInfo": output_colours,
            "InternalData": internal_data,
        })
        return Instance(chip_name, instance_id, REGISTRY.specs[chip_name])

    def wire(self, source: PinRef, target: PinRef) -> None:
        if not source.drives:
            raise ValueError(f"wire source does not drive: {source.label}")
        if target.drives:
            raise ValueError(f"wire target is not an input: {target.label}")
        if source.bits != target.bits:
            raise ValueError(f"wire width mismatch: {source.label} ({source.bits}) -> {target.label} ({target.bits})")
        self.wires.append({
            "SourcePinAddress": source.address(),
            "TargetPinAddress": target.address(),
            "ConnectionType": 0,
            "ConnectedWireIndex": -1,
            "ConnectedWireSegmentIndex": -1,
            "Points": [self._vec(0, 0), self._vec(0, 0)],
        })

    def nand(self, a: PinRef, b: PinRef, label: str = "") -> PinRef:
        gate = self.add("NAND", label)
        self.wire(a, gate.inp("A"))
        self.wire(b, gate.inp("B"))
        return gate.out("Q")

    def inv(self, a: PinRef) -> PinRef:
        return self.nand(a, a)

    def and1(self, a: PinRef, b: PinRef) -> PinRef:
        n = self.nand(a, b)
        return self.nand(n, n)

    def or1(self, a: PinRef, b: PinRef) -> PinRef:
        return self.nand(self.inv(a), self.inv(b))

    def xor1(self, a: PinRef, b: PinRef) -> PinRef:
        n = self.nand(a, b)
        return self.nand(self.nand(a, n), self.nand(b, n))

    def mux1(self, a: PinRef, b: PinRef, select: PinRef) -> PinRef:
        ns = self.inv(select)
        return self.nand(self.nand(a, ns), self.nand(b, select))

    def or_many(self, values: list[PinRef]) -> PinRef:
        if not values:
            raise ValueError("or_many needs at least one value")
        result = values[0]
        for value in values[1:]:
            result = self.or1(result, value)
        return result

    def split8(self, bus: PinRef, label: str = "") -> list[PinRef]:
        split = self.add("8-1BIT", label)
        self.wire(bus, split.inp("IN"))
        return [split.out(f"B{i}") for i in range(8)]

    def merge8(self, bits_lsb_first: list[PinRef], label: str = "") -> PinRef:
        assert len(bits_lsb_first) == 8
        merge = self.add("1-8BIT", label)
        for index, bit in enumerate(bits_lsb_first):
            self.wire(bit, merge.inp(f"B{index}"))
        return merge.out("OUT")

    def split4(self, bus: PinRef, label: str = "") -> list[PinRef]:
        split = self.add("4-1BIT", label)
        self.wire(bus, split.inp("IN"))
        return [split.out(f"B{i}") for i in range(4)]

    def merge4(self, bits_lsb_first: list[PinRef], label: str = "") -> PinRef:
        assert len(bits_lsb_first) == 4
        merge = self.add("1-4BIT", label)
        for index, bit in enumerate(bits_lsb_first):
            self.wire(bit, merge.inp(f"B{index}"))
        return merge.out("OUT")

    def add_custom(self) -> None:
        REGISTRY.add(self.name, self._pin_specs.copy(), custom=True)

    def description(self) -> dict:
        rows = max(1, (len(self.subchips) + 11) // 12)
        return {
            "DLSVersion": DLS_VERSION,
            "Name": self.name,
            "NameLocation": 0,
            "ChipType": 0,
            "Size": self._vec(18, max(12, rows * 1.3 + 5)),
            "Colour": {"r": self.colour[0], "g": self.colour[1], "b": self.colour[2], "a": 1},
            "InputPins": self.inputs,
            "OutputPins": self.outputs,
            "SubChips": self.subchips,
            "Wires": self.wires,
            "Displays": [],
        }


def build_mux8() -> ChipBuilder:
    c = ChipBuilder("MUX-8")
    a = c.add_input("A", 8)
    b = c.add_input("B", 8)
    select = c.add_input("S")
    out = c.add_output("Q", 8)
    aa, bb = c.split8(a, "A bits"), c.split8(b, "B bits")
    result = [c.mux1(aa[i], bb[i], select) for i in range(8)]
    c.wire(c.merge8(result, "result"), out)
    c.add_custom()
    return c


def full_adder(c: ChipBuilder, a: PinRef, b: PinRef, carry: PinRef) -> tuple[PinRef, PinRef]:
    ab = c.xor1(a, b)
    total = c.xor1(ab, carry)
    carry_out = c.or1(c.and1(a, b), c.and1(carry, ab))
    return total, carry_out


def build_add8() -> ChipBuilder:
    c = ChipBuilder("ADD-8", (0.18, 0.46, 0.28))
    a = c.add_input("A", 8)
    b = c.add_input("B", 8)
    carry = c.add_input("CIN")
    result = c.add_output("SUM", 8)
    carry_out = c.add_output("COUT")
    aa, bb = c.split8(a, "A bits"), c.split8(b, "B bits")
    sums: list[PinRef] = []
    for index in range(8):
        bit, carry = full_adder(c, aa[index], bb[index], carry)
        sums.append(bit)
    c.wire(c.merge8(sums, "sum"), result)
    c.wire(carry, carry_out)
    c.add_custom()
    return c


def build_const8() -> ChipBuilder:
    c = ChipBuilder("CONST-8", (0.35, 0.35, 0.35))
    x = c.add_input("X")
    zero8 = c.add_output("ZERO8", 8)
    one8 = c.add_output("ONE8", 8)
    ones8 = c.add_output("ONES8", 8)
    zero1 = c.add_output("ZERO1")
    one1 = c.add_output("ONE1")
    nx = c.inv(x)
    one = c.nand(x, nx, "logic 1")
    zero = c.inv(one)
    c.wire(c.merge8([zero] * 8, "00"), zero8)
    c.wire(c.merge8([one] + [zero] * 7, "01"), one8)
    c.wire(c.merge8([one] * 8, "FF"), ones8)
    c.wire(zero, zero1)
    c.wire(one, one1)
    c.add_custom()
    return c


def build_decoder4() -> ChipBuilder:
    c = ChipBuilder("DECODER-4X16", (0.48, 0.28, 0.16))
    op = c.add_input("OP", 4)
    outs = [c.add_output(f"D{i:X}") for i in range(16)]
    bits = c.split4(op, "opcode")
    inv = [c.inv(bit) for bit in bits]
    for value in range(16):
        selected = [bits[i] if value & (1 << i) else inv[i] for i in range(4)]
        decoded = c.and1(c.and1(selected[0], selected[1]), c.and1(selected[2], selected[3]))
        c.wire(decoded, outs[value])
    c.add_custom()
    return c


def build_regfile() -> ChipBuilder:
    """A true two-read/one-write 16×8 register file.

    The simulator RAM primitive is single-port, so each architectural register
    gets its own one-byte RAM.  Two MUX trees provide independent read ports.
    """
    c = ChipBuilder("REGFILE-16X8", (0.20, 0.42, 0.52))
    clock = c.add_input("CLK")
    reset = c.add_input("RESET")
    ra = c.add_input("RA", 4)
    rb = c.add_input("RB", 4)
    rd = c.add_input("RD", 4)
    data = c.add_input("DATA", 8)
    write = c.add_input("WRITE")
    a_out = c.add_output("A", 8)
    b_out = c.add_output("B", 8)
    r14_out = c.add_output("R14", 8)

    const = c.add("CONST-8", "address zero")
    c.wire(reset, const.inp("X"))
    zero8 = const.out("ZERO8")
    decoder = c.add("DECODER-4X16", "write decoder")
    c.wire(rd, decoder.inp("OP"))
    values: list[PinRef] = []
    for index in range(16):
        ram = c.add("dev.RAM-8", f"R{index}")
        c.wire(zero8, ram.inp("ADDRESS"))
        c.wire(data, ram.inp("DATA"))
        c.wire(c.and1(write, decoder.out(f"D{index:X}")), ram.inp("WRITE"))
        c.wire(reset, ram.inp("RESET"))
        c.wire(clock, ram.inp("CLOCK"))
        values.append(ram.out("Q"))

    def select_bus(selector: PinRef, label: str) -> PinRef:
        selects = c.split4(selector, label + " selector")
        level = values
        for bit_index, select in enumerate(selects):
            next_level: list[PinRef] = []
            for pair in range(0, len(level), 2):
                mux = c.add("MUX-8", f"{label} L{bit_index}")
                c.wire(level[pair], mux.inp("A"))
                c.wire(level[pair + 1], mux.inp("B"))
                c.wire(select, mux.inp("S"))
                next_level.append(mux.out("Q"))
            level = next_level
        assert len(level) == 1
        return level[0]

    c.wire(select_bus(ra, "read A"), a_out)
    c.wire(select_bus(rb, "read B"), b_out)
    c.wire(values[14], r14_out)
    c.add_custom()
    return c


def mux_tree(c: ChipBuilder, values: list[PinRef], selects_lsb_first: list[PinRef]) -> PinRef:
    level = values
    for select in selects_lsb_first:
        level = [c.mux1(level[i], level[i + 1], select) for i in range(0, len(level), 2)]
    assert len(level) == 1
    return level[0]


def build_alu8() -> ChipBuilder:
    c = ChipBuilder("ALU-8", (0.58, 0.24, 0.18))
    a = c.add_input("A", 8)
    b = c.add_input("B", 8)
    op = c.add_input("OP", 4)
    result_out = c.add_output("RESULT", 8)
    zero_out = c.add_output("Z")
    carry_out = c.add_output("C")
    aa, bb, selects = c.split8(a, "A"), c.split8(b, "B"), c.split4(op, "OP")

    one = c.nand(aa[0], c.inv(aa[0]))
    zero = c.inv(one)
    add_bits: list[PinRef] = []
    sub_bits: list[PinRef] = []
    add_carry, sub_carry = zero, one
    for index in range(8):
        add_bit, add_carry = full_adder(c, aa[index], bb[index], add_carry)
        sub_bit, sub_carry = full_adder(c, aa[index], c.inv(bb[index]), sub_carry)
        add_bits.append(add_bit)
        sub_bits.append(sub_bit)

    result_bits: list[PinRef] = []
    for index in range(8):
        candidates = [zero] * 16
        candidates[0] = aa[index]
        candidates[1] = add_bits[index]
        candidates[2] = sub_bits[index]
        candidates[3] = c.and1(aa[index], bb[index])
        candidates[4] = c.or1(aa[index], bb[index])
        candidates[5] = c.xor1(aa[index], bb[index])
        candidates[6] = sub_bits[index]
        candidates[7] = aa[index - 1] if index > 0 else zero
        candidates[8] = aa[index + 1] if index < 7 else zero
        result_bits.append(mux_tree(c, candidates, selects))

    carry_candidates = [zero] * 16
    carry_candidates[1] = add_carry
    carry_candidates[2] = sub_carry
    carry_candidates[6] = sub_carry
    carry_candidates[7] = aa[7]
    carry_candidates[8] = aa[0]
    selected_carry = mux_tree(c, carry_candidates, selects)
    any_set = c.or_many(result_bits)
    selected_zero = c.inv(any_set)
    c.wire(c.merge8(result_bits, "ALU result"), result_out)
    c.wire(selected_zero, zero_out)
    c.wire(selected_carry, carry_out)
    c.add_custom()
    return c


def diagnostic_rom() -> list[int]:
    MOV, ADD, SUB, AND, OR, XOR, CMP, SHL, SHR, LD, ST, LDI, JMP, JNZ, CALL, RET = range(16)

    def rrr(op: int, rd: int, ra: int, rb: int) -> int:
        return (op << 12) | (rd << 8) | (ra << 4) | rb

    def mem(op: int, reg: int, value: int) -> int:
        return (op << 12) | (reg << 8) | value

    def branch(op: int, address: int) -> int:
        return (op << 12) | address

    words = [branch(JMP, 0xFF)] * 256
    program = {
        0x00: mem(LDI, 1, 10), 0x01: mem(LDI, 2, 3),
        0x02: rrr(ADD, 3, 1, 2), 0x03: rrr(SUB, 4, 1, 2),
        0x04: rrr(AND, 5, 1, 2), 0x05: rrr(OR, 6, 1, 2),
        0x06: rrr(XOR, 7, 1, 2), 0x07: rrr(SHL, 8, 2, 0),
        0x08: rrr(SHR, 9, 1, 0), 0x09: rrr(MOV, 10, 7, 0),
        0x0A: mem(ST, 10, 0x40), 0x0B: mem(LD, 11, 0x40),
        0x0C: rrr(CMP, 0, 11, 10), 0x0D: branch(JNZ, 0x20),
        0x0E: mem(LDI, 12, 0xFF), 0x0F: rrr(CMP, 0, 1, 2),
        0x10: branch(JNZ, 0x18), 0x11: mem(LDI, 0, 0xEE),
        0x18: branch(CALL, 0x30), 0x19: branch(JMP, 0x40),
        0x30: rrr(ADD, 13, 12, 2), 0x31: branch(RET, 0),
        0x40: mem(LDI, 14, 0x5A), 0x41: mem(ST, 14, 0xFF),
        0x42: branch(JMP, 0xFF), 0xFF: branch(JMP, 0xFF),
    }
    for address, word in program.items():
        words[address] = word
    return words


def build_cpu8() -> ChipBuilder:
    c = ChipBuilder("CPU-8", (0.10, 0.38, 0.60))
    clock = c.add_input("CLK")
    reset = c.add_input("RESET")
    pc_out = c.add_output("PC", 8, 3)
    r14_out = c.add_output("R14", 8, 3)
    mem_out = c.add_output("MEM", 8, 3)
    flags_out = c.add_output("FLAGS", 8, 3)
    ir_high_out = c.add_output("IR-H", 8, 3)
    ir_low_out = c.add_output("IR-L", 8, 3)

    const = c.add("CONST-8", "constants")
    c.wire(reset, const.inp("X"))
    zero8, one8, ones8 = const.out("ZERO8"), const.out("ONE8"), const.out("ONES8")
    zero1, one1 = const.out("ZERO1"), const.out("ONE1")

    pc_ram = c.add("dev.RAM-8", "program counter")
    c.wire(zero8, pc_ram.inp("ADDRESS"))
    c.wire(one1, pc_ram.inp("WRITE"))
    c.wire(reset, pc_ram.inp("RESET"))
    c.wire(clock, pc_ram.inp("CLOCK"))
    pc = pc_ram.out("Q")

    rom = c.add("ROM 256×16", "diagnostic ROM", diagnostic_rom())
    c.wire(pc, rom.inp("ADDRESS"))
    ir_high, ir_low = rom.out("HIGH"), rom.out("LOW")
    high_split = c.add("8-4BIT", "opcode / destination")
    low_split = c.add("8-4BIT", "source A / source B")
    c.wire(ir_high, high_split.inp("IN"))
    c.wire(ir_low, low_split.inp("IN"))
    opcode, rd = high_split.out("HIGH"), high_split.out("LOW")
    ra, rb = low_split.out("HIGH"), low_split.out("LOW")
    decoder = c.add("DECODER-4X16", "instruction decoder")
    c.wire(opcode, decoder.inp("OP"))
    d = [decoder.out(f"D{i:X}") for i in range(16)]

    # ST uses the destination nibble as its source-register address.
    ra_bits, rd_bits = c.split4(ra, "RA bits"), c.split4(rd, "RD bits")
    read_a_selector = c.merge4(
        [c.mux1(ra_bits[i], rd_bits[i], d[10]) for i in range(4)],
        "read A selector",
    )

    regfile = c.add("REGFILE-16X8", "16 registers")
    c.wire(clock, regfile.inp("CLK"))
    c.wire(reset, regfile.inp("RESET"))
    c.wire(read_a_selector, regfile.inp("RA"))
    c.wire(rb, regfile.inp("RB"))
    c.wire(rd, regfile.inp("RD"))
    a_value, b_value, r14 = regfile.out("A"), regfile.out("B"), regfile.out("R14")

    alu = c.add("ALU-8", "arithmetic logic unit")
    c.wire(a_value, alu.inp("A"))
    c.wire(b_value, alu.inp("B"))
    c.wire(opcode, alu.inp("OP"))

    data_ram = c.add("dev.RAM-8", "data memory")
    c.wire(ir_low, data_ram.inp("ADDRESS"))
    c.wire(a_value, data_ram.inp("DATA"))
    c.wire(d[10], data_ram.inp("WRITE"))
    c.wire(reset, data_ram.inp("RESET"))
    c.wire(clock, data_ram.inp("CLOCK"))
    memory_value = data_ram.out("Q")

    load_mux = c.add("MUX-8", "load data")
    immediate_mux = c.add("MUX-8", "immediate data")
    c.wire(alu.out("RESULT"), load_mux.inp("A"))
    c.wire(memory_value, load_mux.inp("B"))
    c.wire(d[9], load_mux.inp("S"))
    c.wire(load_mux.out("Q"), immediate_mux.inp("A"))
    c.wire(ir_low, immediate_mux.inp("B"))
    c.wire(d[11], immediate_mux.inp("S"))
    register_write_data = immediate_mux.out("Q")
    register_write = c.or_many([d[i] for i in (0, 1, 2, 3, 4, 5, 7, 8, 9, 11)])
    c.wire(register_write_data, regfile.inp("DATA"))
    c.wire(register_write, regfile.inp("WRITE"))

    flags_ram = c.add("dev.RAM-8", "Z/C flags")
    c.wire(zero8, flags_ram.inp("ADDRESS"))
    c.wire(reset, flags_ram.inp("RESET"))
    c.wire(clock, flags_ram.inp("CLOCK"))
    flags = flags_ram.out("Q")
    flag_bits = c.split8(flags, "flags bits")
    old_zero, old_carry = flag_bits[0], flag_bits[1]
    zero_update = c.or_many([d[i] for i in (0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 11)])
    carry_update = c.or_many([d[i] for i in (1, 2, 6, 7, 8)])
    write_data_bits = c.split8(register_write_data, "write-data zero test")
    write_data_zero = c.inv(c.or_many(write_data_bits))
    zero_candidate = c.mux1(write_data_zero, alu.out("Z"), d[6])
    next_zero = c.mux1(old_zero, zero_candidate, zero_update)
    next_carry = c.mux1(old_carry, alu.out("C"), carry_update)
    next_flags = c.merge8([next_zero, next_carry] + [zero1] * 6, "next flags")
    c.wire(next_flags, flags_ram.inp("DATA"))
    c.wire(zero_update, flags_ram.inp("WRITE"))

    pc_increment = c.add("ADD-8", "PC + 1")
    c.wire(pc, pc_increment.inp("A"))
    c.wire(zero8, pc_increment.inp("B"))
    c.wire(one1, pc_increment.inp("CIN"))
    pc_plus_one = pc_increment.out("SUM")

    sp_ram = c.add("dev.RAM-8", "stack pointer")
    c.wire(zero8, sp_ram.inp("ADDRESS"))
    c.wire(reset, sp_ram.inp("RESET"))
    c.wire(clock, sp_ram.inp("CLOCK"))
    sp = sp_ram.out("Q")
    sp_inc = c.add("ADD-8", "SP + 1")
    sp_dec = c.add("ADD-8", "SP - 1")
    for adder, rhs, cin in ((sp_inc, zero8, one1), (sp_dec, ones8, zero1)):
        c.wire(sp, adder.inp("A"))
        c.wire(rhs, adder.inp("B"))
        c.wire(cin, adder.inp("CIN"))
    sp_plus_one, sp_minus_one = sp_inc.out("SUM"), sp_dec.out("SUM")
    sp_call_mux = c.add("MUX-8", "SP after CALL")
    sp_ret_mux = c.add("MUX-8", "SP after RET")
    c.wire(sp, sp_call_mux.inp("A"))
    c.wire(sp_plus_one, sp_call_mux.inp("B"))
    c.wire(d[14], sp_call_mux.inp("S"))
    c.wire(sp_call_mux.out("Q"), sp_ret_mux.inp("A"))
    c.wire(sp_minus_one, sp_ret_mux.inp("B"))
    c.wire(d[15], sp_ret_mux.inp("S"))
    c.wire(sp_ret_mux.out("Q"), sp_ram.inp("DATA"))
    stack_activity = c.or1(d[14], d[15])
    c.wire(stack_activity, sp_ram.inp("WRITE"))

    def low_three(value: PinRef, label: str) -> PinRef:
        bits = c.split8(value, label + " split")
        return c.merge8(bits[:3] + [zero1] * 5, label)

    stack_write_address = low_three(sp, "stack write address")
    stack_read_address = low_three(sp_minus_one, "stack read address")
    stack_addr_mux = c.add("MUX-8", "stack read/write address")
    c.wire(stack_write_address, stack_addr_mux.inp("A"))
    c.wire(stack_read_address, stack_addr_mux.inp("B"))
    c.wire(d[15], stack_addr_mux.inp("S"))
    stack_ram = c.add("dev.RAM-8", "8-entry return stack")
    c.wire(stack_addr_mux.out("Q"), stack_ram.inp("ADDRESS"))
    c.wire(pc_plus_one, stack_ram.inp("DATA"))
    c.wire(d[14], stack_ram.inp("WRITE"))
    c.wire(reset, stack_ram.inp("RESET"))
    c.wire(clock, stack_ram.inp("CLOCK"))

    not_zero = c.inv(old_zero)
    jnz_taken = c.and1(d[13], not_zero)
    direct_branch = c.or_many([d[12], d[14], jnz_taken])
    branch_mux = c.add("MUX-8", "branch target")
    return_mux = c.add("MUX-8", "return target")
    c.wire(pc_plus_one, branch_mux.inp("A"))
    c.wire(ir_low, branch_mux.inp("B"))
    c.wire(direct_branch, branch_mux.inp("S"))
    c.wire(branch_mux.out("Q"), return_mux.inp("A"))
    c.wire(stack_ram.out("Q"), return_mux.inp("B"))
    c.wire(d[15], return_mux.inp("S"))
    c.wire(return_mux.out("Q"), pc_ram.inp("DATA"))

    c.wire(pc, pc_out)
    c.wire(r14, r14_out)
    c.wire(memory_value, mem_out)
    c.wire(flags, flags_out)
    c.wire(ir_high, ir_high_out)
    c.wire(ir_low, ir_low_out)
    c.add_custom()
    return c


def build_computer() -> ChipBuilder:
    c = ChipBuilder("8-BIT COMPUTER", (0.08, 0.42, 0.66))
    reset = c.add_input("RESET")
    outputs = [
        c.add_output("PC", 8, 3), c.add_output("R14", 8, 3),
        c.add_output("MEM", 8, 3), c.add_output("FLAGS", 8, 3),
        c.add_output("IR-H", 8, 3), c.add_output("IR-L", 8, 3),
    ]
    clock = c.add("CLOCK", "automatic clock")
    cpu = c.add("CPU-8", "8-bit processor")
    c.wire(clock.out("Q"), cpu.inp("CLK"))
    c.wire(reset, cpu.inp("RESET"))
    for name, output in zip(("PC", "R14", "MEM", "FLAGS", "IR-H", "IR-L"), outputs):
        c.wire(cpu.out(name), output)
    c.add_custom()
    return c


def validate_chip(chip: ChipBuilder) -> None:
    desc = chip.description()
    ids = [item["ID"] for item in desc["SubChips"]]
    assert all(value > 0 for value in ids)
    assert len(ids) == len(set(ids))
    pin_ids = [pin["ID"] for pin in desc["InputPins"] + desc["OutputPins"]]
    assert len(pin_ids) == len(set(pin_ids))
    assert all(len(wire["Points"]) >= 2 for wire in desc["Wires"])
    driven_targets = [
        (wire["TargetPinAddress"]["PinOwnerID"], wire["TargetPinAddress"]["PinID"])
        for wire in desc["Wires"]
    ]
    duplicates = {target for target in driven_targets if driven_targets.count(target) > 1}
    assert not duplicates, f"{chip.name}: inputs with multiple drivers: {duplicates}"


def validate_diagnostic() -> None:
    regression_path = ROOT / "Tools" / "ComputerRegression" / "regression.py"
    spec = importlib.util.spec_from_file_location("computer_regression", regression_path)
    assert spec and spec.loader
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    cpu = module.Tiny8Computer()
    cpu.rom = diagnostic_rom()
    for _ in range(80):
        cpu.step()
        if cpu.pc == 0xFF and cpu.ram[0xFF] == 0x5A:
            break
    else:
        raise AssertionError("diagnostic did not reach the FF stop loop")
    assert cpu.registers[14] == 0x5A
    assert cpu.ram[0x40] == 9
    assert cpu.ram[0xFF] == 0x5A
    assert cpu.zero is False and cpu.carry is True
    assert cpu.return_stack == []


def write_project(chips: list[ChipBuilder]) -> None:
    if ARTIFACT_ROOT.exists():
        shutil.rmtree(ARTIFACT_ROOT)
    CHIP_ROOT.mkdir(parents=True)
    for chip in chips:
        validate_chip(chip)
        path = CHIP_ROOT / f"{chip.name}.json"
        path.write_text(json.dumps(chip.description(), indent=2, ensure_ascii=False) + "\n", encoding="utf-8")

    now = datetime.now(timezone.utc).isoformat(timespec="milliseconds").replace("+00:00", "Z")
    custom_names = [chip.name for chip in chips]
    project = {
        "ProjectName": PROJECT_NAME,
        "DLSVersion_LastSaved": DLS_VERSION,
        "DLSVersion_EarliestCompatible": "2.0.0",
        "CreationTime": now,
        "LastSaveTime": now,
        "Prefs_MainPinNamesDisplayMode": 0,
        "Prefs_ChipPinNamesDisplayMode": 0,
        "Prefs_GridDisplayMode": 1,
        "Prefs_Snapping": 1,
        "Prefs_StraightWires": 1,
        "Prefs_SimPaused": False,
        "Prefs_SimTargetStepsPerSecond": 1000,
        "Prefs_SimStepsPerClockTick": 2,
        "AllCustomChipNames": custom_names,
        "StarredList": [{"Name": "8-BIT COMPUTER", "IsCollection": False}],
        "ChipCollections": [{
            "Chips": custom_names, "IsToggledOpen": True, "Name": "8-bit computer"
        }],
    }
    (PROJECT_ROOT / "ProjectDescription.json").write_text(
        json.dumps(project, indent=2, ensure_ascii=False) + "\n", encoding="utf-8"
    )

    readme = f"""# {PROJECT_NAME}

Gotowy, sprzętowy komputer 8-bit dla Digital Logic Sim {DLS_VERSION}.

## Instalacja (Linux)

1. Skopiuj folder `Projects/{PROJECT_NAME}` do:
   `~/.config/unity3d/SebastianLague/Digital-Logic-Sim/Projects/`
2. Uruchom Digital Logic Sim i otwórz projekt **{PROJECT_NAME}**.
3. Z biblioteki (gwiazdka na dolnym pasku) otwórz układ **8-BIT COMPUTER**.
4. Symulacja startuje automatycznie. Wejście `RESET` wyzeruje cały komputer.

## Poprawny wynik autotestu

Po krótkiej chwili wyjścia powinny pokazać:

- `PC = FF`
- `R14 = 5A`
- `MEM = 5A`
- `FLAGS = 02` (C=1, Z=0)
- `IR-H = C0`, `IR-L = FF` (`JMP FF`)

## Architektura

- 8-bitowa magistrala danych, 16 rejestrów 8-bitowych
- ROM programu 256×16 i RAM danych 256×8 (Harvard)
- 8-bitowy PC, flagi Z/C, 8-pozycyjny stos powrotów
- 16 instrukcji: MOV, ADD, SUB, AND, OR, XOR, CMP, SHL, SHR, LD, ST,
  LDI, JMP, JNZ, CALL, RET
- stałe instrukcje 16-bitowe: `[opcode:4][rd:4][ra/imm:4][rb/imm:4]`

Program w ROM wykonuje wszystkie instrukcje, sprawdza skoki i CALL/RET, zapisuje
wynik 5A pod adresem FF, po czym zatrzymuje się w pętli `JMP FF`.
"""
    (ARTIFACT_ROOT / "README.md").write_text(readme, encoding="utf-8")

    ARCHIVE_PATH.parent.mkdir(parents=True, exist_ok=True)
    if ARCHIVE_PATH.exists():
        ARCHIVE_PATH.unlink()
    with zipfile.ZipFile(ARCHIVE_PATH, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for path in sorted(ARTIFACT_ROOT.rglob("*")):
            if path.is_file():
                archive.write(path, path.relative_to(ARTIFACT_ROOT.parent))


def main() -> None:
    chips = [
        build_mux8(), build_add8(), build_const8(), build_decoder4(),
        build_regfile(), build_alu8(), build_cpu8(), build_computer(),
    ]
    validate_diagnostic()
    write_project(chips)
    print(f"Generated {len(chips)} custom chips")
    print(f"Subchips: {sum(len(chip.subchips) for chip in chips)}")
    print(f"Wires: {sum(len(chip.wires) for chip in chips)}")
    print(f"Project: {PROJECT_ROOT}")
    print(f"Archive: {ARCHIVE_PATH} ({ARCHIVE_PATH.stat().st_size} bytes)")
    print("Diagnostic: PC=FF R14=5A MEM=5A FLAGS=02")


if __name__ == "__main__":
    main()
