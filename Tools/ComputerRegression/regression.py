#!/usr/bin/env python3
"""Regression tests for a small 8-bit computer and the gate solver.

The architectural model deliberately mirrors the computer that is practical to
build in Digital Logic Sim: 16 registers, fixed 16-bit instructions, separate
program ROM and data RAM, an 8-entry return stack, and Z/C flags.

The file also builds an 8-bit NAND-only adder and an 8-bit edge-triggered
register using the same delta-cycle model as DeterministicSimulator.cs.  This
connects instruction-level tests with real gate propagation and feedback tests.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from enum import IntEnum
import importlib.util
from pathlib import Path
import random
import time


MASK8 = 0xFF
MASK16 = 0xFFFF
ROM_WORDS = 256
RAM_BYTES = 256
REGISTER_COUNT = 16
STACK_DEPTH = 8


class Op(IntEnum):
    MOV = 0x0
    ADD = 0x1
    SUB = 0x2
    AND = 0x3
    OR = 0x4
    XOR = 0x5
    CMP = 0x6
    SHL = 0x7
    SHR = 0x8
    LD = 0x9
    ST = 0xA
    LDI = 0xB
    JMP = 0xC
    JNZ = 0xD
    CALL = 0xE
    RET = 0xF


def rrr(op: Op, rd: int, ra: int, rb: int) -> int:
    return ((int(op) & 0xF) << 12) | ((rd & 0xF) << 8) | ((ra & 0xF) << 4) | (rb & 0xF)


def mem(op: Op, reg: int, address: int) -> int:
    return ((int(op) & 0xF) << 12) | ((reg & 0xF) << 8) | (address & MASK8)


def branch(op: Op, address: int) -> int:
    return ((int(op) & 0xF) << 12) | (address & MASK8)


def decode(word: int) -> tuple[Op, int, int, int, int]:
    word &= MASK16
    return (
        Op((word >> 12) & 0xF),
        (word >> 8) & 0xF,
        (word >> 4) & 0xF,
        word & 0xF,
        word & MASK8,
    )


@dataclass
class Tiny8Computer:
    rom: list[int] = field(default_factory=lambda: [branch(Op.JMP, 0)] * ROM_WORDS)
    ram: bytearray = field(default_factory=lambda: bytearray(RAM_BYTES))
    registers: list[int] = field(default_factory=lambda: [0] * REGISTER_COUNT)
    return_stack: list[int] = field(default_factory=list)
    pc: int = 0
    zero: bool = False
    carry: bool = False
    retired_instructions: int = 0

    def load_program(self, words: dict[int, int]) -> None:
        for address, word in words.items():
            self.rom[address & MASK8] = word & MASK16

    def _write(self, register: int, value: int, update_zero: bool = True) -> None:
        value &= MASK8
        self.registers[register & 0xF] = value
        if update_zero:
            self.zero = value == 0

    def step(self) -> None:
        word = self.rom[self.pc]
        self.pc = (self.pc + 1) & MASK8
        op, rd, ra, rb, imm8 = decode(word)
        a = self.registers[ra]
        b = self.registers[rb]

        if op == Op.MOV:
            self._write(rd, a)
        elif op == Op.ADD:
            result = a + b
            self.carry = result > MASK8
            self._write(rd, result)
        elif op == Op.SUB:
            self.carry = a >= b  # carry means "no borrow"
            self._write(rd, a - b)
        elif op == Op.AND:
            self._write(rd, a & b)
        elif op == Op.OR:
            self._write(rd, a | b)
        elif op == Op.XOR:
            self._write(rd, a ^ b)
        elif op == Op.CMP:
            self.carry = a >= b
            self.zero = ((a - b) & MASK8) == 0
        elif op == Op.SHL:
            self.carry = (a & 0x80) != 0
            self._write(rd, a << 1)
        elif op == Op.SHR:
            self.carry = (a & 0x01) != 0
            self._write(rd, a >> 1)
        elif op == Op.LD:
            self._write(rd, self.ram[imm8])
        elif op == Op.ST:
            self.ram[imm8] = self.registers[rd] & MASK8
        elif op == Op.LDI:
            self._write(rd, imm8)
        elif op == Op.JMP:
            self.pc = imm8
        elif op == Op.JNZ:
            if not self.zero:
                self.pc = imm8
        elif op == Op.CALL:
            if len(self.return_stack) >= STACK_DEPTH:
                raise OverflowError("8-entry return stack overflow")
            self.return_stack.append(self.pc)
            self.pc = imm8
        elif op == Op.RET:
            if not self.return_stack:
                raise IndexError("return stack underflow")
            self.pc = self.return_stack.pop()
        else:  # pragma: no cover - IntEnum decode makes this unreachable
            raise AssertionError(op)

        self.retired_instructions += 1


def test_instruction_encoding() -> None:
    for op in Op:
        for rd in range(16):
            for ra in range(16):
                for rb in range(16):
                    word = rrr(op, rd, ra, rb)
                    decoded = decode(word)
                    assert decoded[:4] == (op, rd, ra, rb)

    for op in (Op.LD, Op.ST, Op.LDI):
        for reg in range(16):
            for address in range(256):
                decoded = decode(mem(op, reg, address))
                assert decoded[0] == op and decoded[1] == reg and decoded[4] == address


def test_exhaustive_alu() -> None:
    cpu = Tiny8Computer()
    operations = (Op.ADD, Op.SUB, Op.AND, Op.OR, Op.XOR)
    for a in range(256):
        for b in range(256):
            for op in operations:
                cpu.registers[1] = a
                cpu.registers[2] = b
                cpu.rom[0] = rrr(op, 3, 1, 2)
                cpu.pc = 0
                cpu.step()
                expected = {
                    Op.ADD: a + b,
                    Op.SUB: a - b,
                    Op.AND: a & b,
                    Op.OR: a | b,
                    Op.XOR: a ^ b,
                }[op] & MASK8
                assert cpu.registers[3] == expected
                assert cpu.zero == (expected == 0)

            cpu.registers[1] = a
            cpu.rom[0] = rrr(Op.SHL, 3, 1, 0)
            cpu.pc = 0
            cpu.step()
            assert cpu.registers[3] == ((a << 1) & MASK8)
            assert cpu.carry == bool(a & 0x80)

            cpu.rom[0] = rrr(Op.SHR, 3, 1, 0)
            cpu.pc = 0
            cpu.step()
            assert cpu.registers[3] == (a >> 1)
            assert cpu.carry == bool(a & 1)


def make_diagnostic_program() -> dict[int, int]:
    return {
        0x00: mem(Op.LDI, 1, 10),
        0x01: mem(Op.LDI, 2, 3),
        0x02: rrr(Op.ADD, 3, 1, 2),       # 13
        0x03: rrr(Op.SUB, 4, 1, 2),       # 7
        0x04: rrr(Op.AND, 5, 1, 2),       # 2
        0x05: rrr(Op.OR, 6, 1, 2),        # 11
        0x06: rrr(Op.XOR, 7, 1, 2),       # 9
        0x07: rrr(Op.SHL, 8, 2, 0),       # 6
        0x08: rrr(Op.SHR, 9, 1, 0),       # 5
        0x09: rrr(Op.MOV, 10, 7, 0),      # 9
        0x0A: mem(Op.ST, 10, 0x40),
        0x0B: mem(Op.LD, 11, 0x40),
        0x0C: rrr(Op.CMP, 0, 11, 10),     # equal, Z=1
        0x0D: branch(Op.JNZ, 0x20),       # must not jump
        0x0E: mem(Op.LDI, 12, 0xFF),
        0x0F: rrr(Op.CMP, 0, 1, 2),       # not equal, Z=0, C=1
        0x10: branch(Op.JNZ, 0x18),        # must jump
        0x11: mem(Op.LDI, 0, 0xEE),       # poison; must not execute
        0x18: branch(Op.CALL, 0x30),
        0x19: branch(Op.JMP, 0x40),
        0x30: rrr(Op.ADD, 13, 12, 2),     # 255 + 3 = 2, C=1
        0x31: branch(Op.RET, 0),
        0x40: mem(Op.LDI, 14, 0x5A),
        0x41: branch(Op.JMP, 0x41),
    }


def run_diagnostic_program() -> Tiny8Computer:
    cpu = Tiny8Computer()
    cpu.load_program(make_diagnostic_program())
    for _ in range(64):
        cpu.step()
        if cpu.pc == 0x41:
            break
    else:
        raise AssertionError("diagnostic program did not reach its stop loop")

    assert cpu.registers == [0, 10, 3, 13, 7, 2, 11, 9, 6, 5, 9, 9, 255, 2, 0x5A, 0]
    assert cpu.ram[0x40] == 9
    assert cpu.return_stack == []
    assert cpu.carry
    return cpu


def test_diagnostic_program_determinism() -> None:
    snapshots = []
    for _ in range(32):
        cpu = run_diagnostic_program()
        snapshots.append((tuple(cpu.registers), bytes(cpu.ram), cpu.pc, cpu.zero, cpu.carry))
    assert len(set(snapshots)) == 1


def test_pc_wrap_and_stack_guards() -> None:
    cpu = Tiny8Computer()
    cpu.rom[0xFF] = mem(Op.LDI, 1, 0xA5)
    cpu.pc = 0xFF
    cpu.step()
    assert cpu.pc == 0 and cpu.registers[1] == 0xA5

    cpu = Tiny8Computer()
    cpu.rom[0] = branch(Op.RET, 0)
    try:
        cpu.step()
    except IndexError:
        pass
    else:
        raise AssertionError("return stack underflow was not detected")

    cpu = Tiny8Computer()
    for address in range(STACK_DEPTH + 1):
        cpu.rom[address] = branch(Op.CALL, address + 1)
    try:
        for _ in range(STACK_DEPTH + 1):
            cpu.step()
    except OverflowError:
        pass
    else:
        raise AssertionError("return stack overflow was not detected")


def load_gate_regression_module():
    path = Path(__file__).resolve().parents[1] / "SimulationRegression" / "regression.py"
    spec = importlib.util.spec_from_file_location("simulation_regression", path)
    module = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(module)
    return module


def build_nand_adder8(regression):
    net = regression.DeltaNet()
    net.set("CIN", 0)
    for bit in range(8):
        net.set(f"A{bit}", 0)
        net.set(f"B{bit}", 0)

    def nand(a: str, b: str, out: str) -> None:
        net.gate(a, b, out)

    def inv(a: str, out: str, prefix: str) -> None:
        nand(a, a, out)

    def and2(a: str, b: str, out: str, prefix: str) -> None:
        tmp = prefix + "/N"
        nand(a, b, tmp)
        inv(tmp, out, prefix + "/I")

    def or2(a: str, b: str, out: str, prefix: str) -> None:
        na, nb = prefix + "/NA", prefix + "/NB"
        inv(a, na, prefix + "/IA")
        inv(b, nb, prefix + "/IB")
        nand(na, nb, out)

    def xor2(a: str, b: str, out: str, prefix: str) -> None:
        n1, n2, n3 = prefix + "/N1", prefix + "/N2", prefix + "/N3"
        nand(a, b, n1)
        nand(a, n1, n2)
        nand(b, n1, n3)
        nand(n2, n3, out)

    carry = "CIN"
    sums: list[str] = []
    for bit in range(8):
        prefix = f"FA{bit}"
        axb = prefix + "/AXB"
        sum_out = f"S{bit}"
        ab = prefix + "/AB"
        cx = prefix + "/CX"
        carry_out = f"C{bit + 1}"
        xor2(f"A{bit}", f"B{bit}", axb, prefix + "/X0")
        xor2(axb, carry, sum_out, prefix + "/X1")
        and2(f"A{bit}", f"B{bit}", ab, prefix + "/A0")
        and2(carry, axb, cx, prefix + "/A1")
        or2(ab, cx, carry_out, prefix + "/O0")
        carry = carry_out
        sums.append(sum_out)

    net.prime()
    assert net.settle()[0]
    return net, sums, carry


def set_byte(net, prefix: str, value: int) -> list[str]:
    changed = []
    for bit in range(8):
        name = f"{prefix}{bit}"
        state = (value >> bit) & 1
        if net.state[name] != state:
            net.state[name] = state
            changed.append(name)
    return changed


def read_byte(net, names: list[str]) -> int:
    return sum(net.state[name] << bit for bit, name in enumerate(names))


def test_nand_only_adder_exhaustive() -> None:
    regression = load_gate_regression_module()
    net, sums, carry = build_nand_adder8(regression)
    for a in range(256):
        for b in range(256):
            changed = set_byte(net, "A", a) + set_byte(net, "B", b)
            assert net.settle(changed)[0]
            expected = a + b
            assert read_byte(net, sums) == (expected & MASK8)
            assert net.state[carry] == int(expected > MASK8)


def test_gate_level_register() -> None:
    regression = load_gate_regression_module()
    net = regression.DeltaNet()
    net.set("CLK", 0)
    outputs: list[str] = []
    complements: list[str] = []
    for bit in range(8):
        net.set(f"D{bit}", 0)
        q, qb = regression.build_dff(net, f"REG/B{bit}", f"D{bit}", "CLK", depth=3)
        outputs.append(q)
        complements.append(qb)
    net.prime()
    assert net.settle()[0]

    rng = random.Random(0xC0FFEE)
    for _ in range(10_000):
        value = rng.randrange(256)
        changed = set_byte(net, "D", value)
        assert net.settle(changed)[0]
        assert net.drive("CLK", 1)[0]
        assert read_byte(net, outputs) == value
        assert all((net.state[q] ^ net.state[qb]) == 1 for q, qb in zip(outputs, complements))
        assert net.drive("CLK", 0)[0]


def benchmark_computer() -> float:
    cpu = Tiny8Computer()
    cpu.load_program({
        0: mem(Op.LDI, 1, 1),
        1: rrr(Op.ADD, 0, 0, 1),
        2: branch(Op.JMP, 1),
    })
    started = time.perf_counter()
    for _ in range(1_000_000):
        cpu.step()
    return time.perf_counter() - started


def main() -> None:
    tests = [
        ("fixed-width instruction encoding", test_instruction_encoding),
        ("exhaustive 8-bit ALU", test_exhaustive_alu),
        ("diagnostic program and all 16 opcodes", run_diagnostic_program),
        ("32 deterministic cold starts", test_diagnostic_program_determinism),
        ("PC wrap and stack guards", test_pc_wrap_and_stack_guards),
        ("NAND-only 8-bit adder, all 65,536 pairs", test_nand_only_adder_exhaustive),
        ("nested NAND DFF register, 10,000 writes", test_gate_level_register),
    ]

    started_all = time.perf_counter()
    for name, test in tests:
        started = time.perf_counter()
        test()
        print(f"PASS  {name:<48} {time.perf_counter() - started:8.3f}s")

    elapsed = benchmark_computer()
    print(f"BENCH 1,000,000 architectural instructions          {elapsed:8.3f}s")
    print(f"ALL COMPUTER TESTS PASSED in {time.perf_counter() - started_all:.3f}s")


if __name__ == "__main__":
    main()
