#!/usr/bin/env python3
"""Execute the generated 8-bit computer as a flattened native-chip netlist."""

from __future__ import annotations

from dataclasses import dataclass, field
import importlib.util
from pathlib import Path
import sys


HERE = Path(__file__).resolve().parent
BUILDER_PATH = HERE / "build_project.py"
spec = importlib.util.spec_from_file_location("dls_computer_builder", BUILDER_PATH)
assert spec and spec.loader
builder = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = builder
spec.loader.exec_module(builder)


class UnionFind:
    def __init__(self) -> None:
        self.parent: list[int] = []

    def new(self) -> int:
        value = len(self.parent)
        self.parent.append(value)
        return value

    def find(self, value: int) -> int:
        while self.parent[value] != value:
            self.parent[value] = self.parent[self.parent[value]]
            value = self.parent[value]
        return value

    def union(self, a: int, b: int) -> None:
        ra, rb = self.find(a), self.find(b)
        if ra != rb:
            self.parent[rb] = ra


@dataclass
class Component:
    name: str
    pins: dict[str, int]
    internal_data: list[int] | None = None
    memory: list[int] = field(default_factory=lambda: [0] * 256)


class NativeNetlist:
    def __init__(self, chips: list) -> None:
        self.uf = UnionFind()
        self.values: dict[int, int] = {}
        self.components: list[Component] = []
        self.chips = {chip.name: chip for chip in chips}

    def instantiate(self, chip_name: str, external: dict[int, int]) -> None:
        chip = self.chips[chip_name]
        desc = chip.description()
        endpoints: dict[tuple[int, int], int] = {}
        for pin in desc["InputPins"] + desc["OutputPins"]:
            local = self.uf.new()
            endpoints[(pin["ID"], 0)] = local
            self.uf.union(local, external[pin["ID"]])

        for subchip in desc["SubChips"]:
            chip_specs = builder.REGISTRY.specs[subchip["Name"]]
            sub_external: dict[int, int] = {}
            for pin_spec in chip_specs:
                net = self.uf.new()
                endpoints[(subchip["ID"], pin_spec.pin_id)] = net
                sub_external[pin_spec.pin_id] = net
            if subchip["Name"] in self.chips:
                self.instantiate(subchip["Name"], sub_external)
            else:
                pins = {pin_spec.name: sub_external[pin_spec.pin_id] for pin_spec in chip_specs}
                self.components.append(Component(subchip["Name"], pins, subchip["InternalData"]))

        for wire in desc["Wires"]:
            source = wire["SourcePinAddress"]
            target = wire["TargetPinAddress"]
            a = endpoints[(source["PinOwnerID"], source["PinID"])]
            b = endpoints[(target["PinOwnerID"], target["PinID"])]
            self.uf.union(a, b)

    def get(self, net: int) -> int:
        return self.values.get(self.uf.find(net), 0)

    def set(self, net: int, value: int) -> bool:
        root = self.uf.find(net)
        if self.values.get(root, 0) != value:
            self.values[root] = value
            return True
        return False

    def _evaluate(self, component: Component) -> bool:
        p = component.pins
        name = component.name
        changed = False
        if name == "NAND":
            return self.set(p["Q"], 1 ^ (self.get(p["A"]) & self.get(p["B"])))
        if name == "8-1BIT":
            value = self.get(p["IN"])
            for bit in range(8):
                changed |= self.set(p[f"B{bit}"], (value >> bit) & 1)
            return changed
        if name == "1-8BIT":
            value = sum((self.get(p[f"B{bit}"]) & 1) << bit for bit in range(8))
            return self.set(p["OUT"], value)
        if name == "8-4BIT":
            value = self.get(p["IN"])
            changed |= self.set(p["HIGH"], (value >> 4) & 0xF)
            changed |= self.set(p["LOW"], value & 0xF)
            return changed
        if name == "4-8BIT":
            value = ((self.get(p["HIGH"]) & 0xF) << 4) | (self.get(p["LOW"]) & 0xF)
            return self.set(p["OUT"], value)
        if name == "4-1BIT":
            value = self.get(p["IN"])
            for bit in range(4):
                changed |= self.set(p[f"B{bit}"], (value >> bit) & 1)
            return changed
        if name == "1-4BIT":
            value = sum((self.get(p[f"B{bit}"]) & 1) << bit for bit in range(4))
            return self.set(p["OUT"], value)
        if name == "ROM 256×16":
            assert component.internal_data is not None
            word = component.internal_data[self.get(p["ADDRESS"]) & 0xFF]
            changed |= self.set(p["HIGH"], (word >> 8) & 0xFF)
            changed |= self.set(p["LOW"], word & 0xFF)
            return changed
        if name == "dev.RAM-8":
            value = component.memory[self.get(p["ADDRESS"]) & 0xFF]
            return self.set(p["Q"], value)
        raise AssertionError(f"unsupported primitive: {name}")

    def settle(self, limit: int = 1000) -> int:
        for iteration in range(1, limit + 1):
            changed = False
            for component in self.components:
                changed |= self._evaluate(component)
            if not changed:
                return iteration
        raise AssertionError("netlist did not settle")

    def rising_edge(self) -> None:
        writes: list[tuple[Component, list[int]]] = []
        for component in self.components:
            if component.name != "dev.RAM-8":
                continue
            p = component.pins
            memory = component.memory.copy()
            if self.get(p["RESET"]):
                memory = [0] * 256
            elif self.get(p["WRITE"]):
                memory[self.get(p["ADDRESS"]) & 0xFF] = self.get(p["DATA"]) & 0xFF
            writes.append((component, memory))
        for component, memory in writes:
            component.memory = memory


def build_machine() -> tuple[NativeNetlist, dict[str, int]]:
    chips = [
        builder.build_mux8(), builder.build_add8(), builder.build_const8(),
        builder.build_decoder4(), builder.build_regfile(), builder.build_alu8(),
        builder.build_cpu8(), builder.build_computer(),
    ]
    netlist = NativeNetlist(chips)
    external = {pin.pin_id: netlist.uf.new() for pin in builder.REGISTRY.specs["CPU-8"]}
    netlist.instantiate("CPU-8", external)
    named = {pin.name: external[pin.pin_id] for pin in builder.REGISTRY.specs["CPU-8"]}
    return netlist, named


def pulse(netlist: NativeNetlist, pins: dict[str, int]) -> int:
    netlist.set(pins["CLK"], 0)
    iterations = netlist.settle()
    netlist.set(pins["CLK"], 1)
    iterations = max(iterations, netlist.settle())
    netlist.rising_edge()
    iterations = max(iterations, netlist.settle())
    netlist.set(pins["CLK"], 0)
    iterations = max(iterations, netlist.settle())
    return iterations


def main() -> None:
    netlist, pins = build_machine()
    netlist.set(pins["RESET"], 1)
    pulse(netlist, pins)
    netlist.set(pins["RESET"], 0)
    max_iterations = 0
    trace: list[tuple[int, int, int, int]] = []
    for _ in range(80):
        max_iterations = max(max_iterations, pulse(netlist, pins))
        snapshot = tuple(netlist.get(pins[name]) for name in ("PC", "R14", "MEM", "FLAGS"))
        trace.append(snapshot)
        if snapshot == (0xFF, 0x5A, 0x5A, 0x02):
            break
    else:
        tail = " ".join(f"PC={pc:02X}/R14={r14:02X}/MEM={mem:02X}/F={flags:02X}" for pc, r14, mem, flags in trace[-8:])
        raise AssertionError(f"native netlist diagnostic failed: {tail}")

    assert netlist.get(pins["IR-H"]) == 0xC0
    assert netlist.get(pins["IR-L"]) == 0xFF
    print(f"Native netlist passed after {len(trace)} clock cycles")
    print(f"Flattened primitives: {len(netlist.components)}")
    print(f"Maximum settle passes: {max_iterations}")
    print("Final: PC=FF R14=5A MEM=5A FLAGS=02 IR=C0FF")


if __name__ == "__main__":
    main()
