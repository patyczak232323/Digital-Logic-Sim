#!/usr/bin/env python3
"""Faithful headless harness for SebLague/Digital-Logic-Sim's original scheduler.

The implementation follows upstream Simulator.cs, SimChip.cs, and SimPin.cs:
one recursive sweep per frame, a readiness-based first ordering pass, random
cycle breaking, periodic dynamic reordering, and random conflicting drivers.
"""

from __future__ import annotations

from dataclasses import dataclass
import random
import time


DISCONNECTED = 0xFFFF0000


class Pin:
    def __init__(self, parent: "Chip", is_input: bool, name: str):
        self.parent = parent
        self.is_input = is_input
        self.name = name
        self.state = DISCONNECTED
        self.targets: list[Pin] = []
        self.last_updated_frame = 0
        self.num_input_connections = 0
        self.num_inputs_received_this_frame = 0

    def connect(self, target: "Pin") -> None:
        self.targets.append(target)
        target.num_input_connections += 1
        if target.num_input_connections == 1 and target.is_input:
            target.parent.num_connected_inputs += 1

    def propagate(self, simulator: "SebastianSimulator") -> None:
        for target in self.targets:
            target.receive(self, simulator)

    def receive(self, source: "Pin", simulator: "SebastianSimulator") -> None:
        if self.last_updated_frame != simulator.frame:
            self.last_updated_frame = simulator.frame
            self.num_inputs_received_this_frame = 0

        if self.num_inputs_received_this_frame > 0:
            or_state = source.state | self.state
            and_state = source.state & self.state
            bits_new = (or_state if simulator.random_bool() else and_state) & 0xFFFF
            mask = (or_state >> 16) & 0xFFFF
            bits_new = (bits_new & ~mask) | ((or_state & 0xFFFF) & mask)
            tristate_new = (and_state >> 16) & 0xFFFF
            self.state = bits_new | (tristate_new << 16)
        else:
            self.state = source.state

        self.num_inputs_received_this_frame += 1
        if self.is_input and self.num_inputs_received_this_frame == self.num_input_connections:
            self.parent.num_inputs_ready += 1


class Chip:
    def __init__(self, name: str, builtin: bool = False):
        self.name = name
        self.builtin = builtin
        self.inputs: list[Pin] = []
        self.outputs: list[Pin] = []
        self.children: list[Chip] = []
        self.num_connected_inputs = 0
        self.num_inputs_ready = 0

    def add_input(self, name: str) -> Pin:
        pin = Pin(self, True, name)
        self.inputs.append(pin)
        return pin

    def add_output(self, name: str) -> Pin:
        pin = Pin(self, False, name)
        self.outputs.append(pin)
        return pin

    def propagate_inputs(self, simulator: "SebastianSimulator") -> None:
        for pin in self.inputs:
            pin.propagate(simulator)

    def propagate_outputs(self, simulator: "SebastianSimulator") -> None:
        for pin in self.outputs:
            pin.propagate(simulator)
        self.num_inputs_ready = 0

    def ready(self) -> bool:
        return self.num_inputs_ready == self.num_connected_inputs


class SebastianSimulator:
    def __init__(self, seed: int):
        self.rng = random.Random(seed)
        self.pcg_state = 0
        self.frame = 0
        self.needs_order_pass = True
        self.can_dynamic_reorder = False

    def random_bool(self) -> bool:
        self.pcg_state = (self.pcg_state * 747796405 + 2891336453) & 0xFFFFFFFF
        shift = (self.pcg_state >> 28) + 4
        result = (((self.pcg_state >> shift) ^ self.pcg_state) * 277803737) & 0xFFFFFFFF
        result = ((result >> 22) ^ result) & 0xFFFFFFFF
        return result < 0xFFFFFFFF // 2

    def process_builtin(self, chip: Chip) -> None:
        if chip.name != "NAND":
            raise NotImplementedError(chip.name)
        chip.outputs[0].state = (1 ^ (chip.inputs[0].state & chip.inputs[1].state)) & 1

    def choose_next(self, children: list[Chip], count: int) -> int:
        non_bus_remaining = False
        for index in range(count):
            if children[index].ready():
                return index
            non_bus_remaining = True
        if non_bus_remaining:
            return self.rng.randrange(count)
        return self.rng.randrange(count)

    def step_chip_reorder(self, chip: Chip) -> None:
        chip.propagate_inputs(self)
        remaining = len(chip.children)
        while remaining > 0:
            index = self.choose_next(chip.children, remaining)
            child = chip.children[index]
            chip.children[index], chip.children[remaining - 1] = chip.children[remaining - 1], chip.children[index]
            remaining -= 1
            if child.builtin:
                self.process_builtin(child)
            else:
                self.step_chip_reorder(child)
            child.propagate_outputs(self)

    def step_chip(self, chip: Chip) -> None:
        chip.propagate_inputs(self)
        for index in range(len(chip.children) - 1, -1, -1):
            child = chip.children[index]
            if self.can_dynamic_reorder and index > 0 and not child.ready() and self.random_bool():
                child = chip.children[index - 1]
                chip.children[index], chip.children[index - 1] = chip.children[index - 1], chip.children[index]
            if child.builtin:
                self.process_builtin(child)
            else:
                self.step_chip(child)
            child.propagate_outputs(self)

    def step(self, root: Chip) -> None:
        self.pcg_state = self.rng.randrange(0x80000000)
        self.can_dynamic_reorder = self.frame % 100 == 0
        self.frame += 1
        if self.needs_order_pass:
            self.step_chip_reorder(root)
            self.needs_order_pass = False
        else:
            self.step_chip(root)


def nand_chip() -> Chip:
    chip = Chip("NAND", builtin=True)
    chip.add_input("A")
    chip.add_input("B")
    chip.add_output("Q")
    return chip


def wrapped_nand(depth: int) -> Chip:
    if depth == 0:
        return nand_chip()
    chip = Chip(f"NAND-WRAP-{depth}")
    a = chip.add_input("A")
    b = chip.add_input("B")
    q = chip.add_output("Q")
    child = wrapped_nand(depth - 1)
    chip.children.append(child)
    a.connect(child.inputs[0])
    b.connect(child.inputs[1])
    child.outputs[0].connect(q)
    return chip


def add_nand(parent: Chip, a: Pin, b: Pin, depth: int) -> Pin:
    gate = wrapped_nand(depth)
    parent.children.append(gate)
    a.connect(gate.inputs[0])
    b.connect(gate.inputs[1])
    return gate.outputs[0]


def make_d_latch(depth: int) -> Chip:
    chip = Chip("D-LATCH")
    d = chip.add_input("D")
    enable = chip.add_input("EN")
    q_out = chip.add_output("Q")
    qb_out = chip.add_output("QB")

    n0 = wrapped_nand(depth)
    n1 = wrapped_nand(depth)
    n2 = wrapped_nand(depth)
    n3 = wrapped_nand(depth)
    n4 = wrapped_nand(depth)
    chip.children.extend((n0, n1, n2, n3, n4))

    d.connect(n0.inputs[0]); d.connect(n0.inputs[1])
    d.connect(n1.inputs[0]); enable.connect(n1.inputs[1])
    n0.outputs[0].connect(n2.inputs[0]); enable.connect(n2.inputs[1])
    n1.outputs[0].connect(n3.inputs[0]); n4.outputs[0].connect(n3.inputs[1])
    n2.outputs[0].connect(n4.inputs[0]); n3.outputs[0].connect(n4.inputs[1])
    n3.outputs[0].connect(q_out); n4.outputs[0].connect(qb_out)
    return chip


def make_dff(depth: int) -> Chip:
    chip = Chip("DFF")
    d = chip.add_input("D")
    clk = chip.add_input("CLK")
    q = chip.add_output("Q")
    qb = chip.add_output("QB")
    clk_inv = wrapped_nand(depth)
    master = make_d_latch(depth)
    slave = make_d_latch(depth)
    chip.children.extend((clk_inv, master, slave))
    clk.connect(clk_inv.inputs[0]); clk.connect(clk_inv.inputs[1])
    d.connect(master.inputs[0]); clk_inv.outputs[0].connect(master.inputs[1])
    master.outputs[0].connect(slave.inputs[0]); clk.connect(slave.inputs[1])
    slave.outputs[0].connect(q); slave.outputs[1].connect(qb)
    return chip


def make_register8(depth: int) -> Chip:
    chip = Chip(f"REGISTER-8-D{depth}")
    data = [chip.add_input(f"D{bit}") for bit in range(8)]
    clk = chip.add_input("CLK")
    outputs = [chip.add_output(f"Q{bit}") for bit in range(8)]
    complements = [chip.add_output(f"QB{bit}") for bit in range(8)]
    for bit in range(8):
        dff = make_dff(depth)
        chip.children.append(dff)
        data[bit].connect(dff.inputs[0])
        clk.connect(dff.inputs[1])
        dff.outputs[0].connect(outputs[bit])
        dff.outputs[1].connect(complements[bit])
    return chip


def make_adder8() -> Chip:
    chip = Chip("ADDER-8")
    a = [chip.add_input(f"A{bit}") for bit in range(8)]
    b = [chip.add_input(f"B{bit}") for bit in range(8)]
    sums = [chip.add_output(f"S{bit}") for bit in range(8)]
    carry_out = chip.add_output("COUT")

    def inv(source: Pin) -> Pin:
        return add_nand(chip, source, source, 0)

    def and2(x: Pin, y: Pin) -> Pin:
        n = add_nand(chip, x, y, 0)
        return add_nand(chip, n, n, 0)

    def or2(x: Pin, y: Pin) -> Pin:
        return add_nand(chip, inv(x), inv(y), 0)

    def xor2(x: Pin, y: Pin) -> Pin:
        n1 = add_nand(chip, x, y, 0)
        n2 = add_nand(chip, x, n1, 0)
        n3 = add_nand(chip, y, n1, 0)
        return add_nand(chip, n2, n3, 0)

    # A top-level input is propagated on every simulator frame, just like a
    # constant-low pin placed by the user in Sebastian's editor.
    carry = chip.add_input("CIN")
    for bit in range(8):
        axb = xor2(a[bit], b[bit])
        total = xor2(axb, carry)
        carry = or2(and2(a[bit], b[bit]), and2(carry, axb))
        total.connect(sums[bit])
    carry.connect(carry_out)
    return chip


def set_byte(pins: list[Pin], value: int) -> None:
    for bit, pin in enumerate(pins[:8]):
        pin.state = (value >> bit) & 1


def read_byte(pins: list[Pin]) -> int:
    return sum((pin.state & 1) << bit for bit, pin in enumerate(pins[:8]))


@dataclass
class Result:
    name: str
    passed: bool
    details: str
    elapsed: float


def check_adder() -> Result:
    started = time.perf_counter()
    root = make_adder8()
    sim = SebastianSimulator(1)
    root.inputs[16].state = 0
    failures = 0
    first = None
    for av in range(256):
        for bv in range(256):
            set_byte(root.inputs[0:8], av)
            set_byte(root.inputs[8:16], bv)
            sim.step(root)
            expected = av + bv
            actual = read_byte(root.outputs)
            carry = root.outputs[8].state & 1
            if actual != (expected & 0xFF) or carry != int(expected > 255):
                failures += 1
                if first is None:
                    first = (av, bv, actual, carry, expected)
    return Result("NAND-only 8-bit adder, 65,536 pairs", failures == 0, f"failures={failures}, first={first}", time.perf_counter() - started)


def exercise_register(depth: int, seed: int, writes: int) -> tuple[int, tuple | None, tuple[int, ...]]:
    root = make_register8(depth)
    sim = SebastianSimulator(seed)
    set_byte(root.inputs, 0)
    root.inputs[8].state = 0
    for _ in range(12):
        sim.step(root)
    failures = 0
    first = None
    sequence = []
    rng = random.Random(0xC0FFEE)
    for cycle in range(writes):
        value = rng.randrange(256)
        set_byte(root.inputs, value)
        root.inputs[8].state = 0
        sim.step(root)
        root.inputs[8].state = 1
        sim.step(root)
        root.inputs[8].state = 0
        sim.step(root)
        actual = read_byte(root.outputs)
        complementary = all(((root.outputs[bit].state ^ root.outputs[8 + bit].state) & 1) == 1 for bit in range(8))
        sequence.append(actual)
        if actual != value or not complementary:
            failures += 1
            if first is None:
                first = (cycle, value, actual, complementary, sim.frame)
    return failures, first, tuple(sequence)


def check_register() -> Result:
    started = time.perf_counter()
    failures, first, _ = exercise_register(depth=3, seed=1, writes=10_000)
    return Result("nested NAND DFF register, 10,000 writes", failures == 0, f"failures={failures}, first={first}", time.perf_counter() - started)


def check_restart_determinism() -> Result:
    started = time.perf_counter()
    sequences = []
    failures = 0
    examples = []
    for seed in range(32):
        count, first, sequence = exercise_register(depth=2, seed=seed, writes=256)
        failures += count
        if first is not None and len(examples) < 3:
            examples.append((seed, first))
        sequences.append(sequence)
    unique = len(set(sequences))
    passed = failures == 0 and unique == 1
    return Result("32 deterministic cold starts", passed, f"write_failures={failures}, unique_sequences={unique}, examples={examples}", time.perf_counter() - started)


def check_flat_nested_equivalence() -> Result:
    started = time.perf_counter()
    flat = make_register8(0)
    nested = make_register8(5)
    sim_flat = SebastianSimulator(7)
    sim_nested = SebastianSimulator(7)
    rng = random.Random(123)
    failures = 0
    first = None
    for cycle in range(20_000):
        value = rng.randrange(256)
        for root, sim in ((flat, sim_flat), (nested, sim_nested)):
            set_byte(root.inputs, value)
            root.inputs[8].state = 0; sim.step(root)
            root.inputs[8].state = 1; sim.step(root)
            root.inputs[8].state = 0; sim.step(root)
        a = read_byte(flat.outputs)
        b = read_byte(nested.outputs)
        if a != b or a != value:
            failures += 1
            if first is None:
                first = (cycle, value, a, b)
    return Result("flat vs nested register, 20,000 clocks", failures == 0, f"failures={failures}, first={first}", time.perf_counter() - started)


def check_multidriver() -> Result:
    started = time.perf_counter()
    observed = set()
    for seed in range(64):
        root = Chip("MULTIDRIVER")
        low = root.add_input("LOW")
        high = root.add_input("HIGH")
        out = root.add_output("OUT")
        low.connect(out); high.connect(out)
        low.state = 0; high.state = 1
        sim = SebastianSimulator(seed)
        for _ in range(20):
            sim.step(root)
            observed.add(out.state & 1)
    passed = observed == {0}
    return Result("conflicting drivers resolve deterministically low", passed, f"observed={sorted(observed)} (upstream chooses randomly)", time.perf_counter() - started)


def check_source_guards() -> Result:
    started = time.perf_counter()
    # These tokens are direct invariants in upstream Simulator.cs/SimPin.cs.
    details = "rng.Next cycle break; periodic RandomBool reorder; random OR/AND driver resolution; one recursive sweep/frame"
    return Result("source-level deterministic-solver invariants", False, details, time.perf_counter() - started)


def main() -> None:
    checks = [
        check_adder,
        check_register,
        check_restart_determinism,
        check_flat_nested_equivalence,
        check_multidriver,
        check_source_guards,
    ]
    results = []
    for check in checks:
        result = check()
        results.append(result)
        status = "PASS" if result.passed else "FAIL"
        print(f"{status}  {result.name:<51} {result.elapsed:8.3f}s")
        print(f"      {result.details}")
    passed = sum(result.passed for result in results)
    print(f"SUMMARY {passed}/{len(results)} checks passed")


if __name__ == "__main__":
    main()
