#!/usr/bin/env python3
"""Deterministic propagation regression suite for Digital Logic Sim.

This is a solver-level executable specification.  It intentionally models the
runtime rules used by DeterministicSimulator.cs: zero-delay custom-chip
boundaries, dirty-gate event propagation, simultaneous output commit per delta
cycle, a bounded settle loop, and a deterministic one-time power-on seed for
feedback storage.
"""

from __future__ import annotations

from collections import defaultdict, deque
from dataclasses import dataclass
from pathlib import Path
import random
import time


class DeltaNet:
    def __init__(self) -> None:
        self.state: dict[str, int] = {}
        self.gates: list[tuple[str, str, str]] = []
        self.fanout: dict[str, list[int]] = defaultdict(list)
        self.aliases: dict[str, list[str]] = defaultdict(list)
        self.alias_sources: dict[str, list[str]] = defaultdict(list)
        self.gate_evaluations = 0
        self.signal_propagations = 0
        self.target_resolutions = 0

    def set(self, name: str, value: int) -> None:
        self.state[name] = int(bool(value))

    def gate(self, a: str, b: str, out: str) -> None:
        index = len(self.gates)
        self.gates.append((a, b, out))
        self.state.setdefault(a, 0)
        self.state.setdefault(b, 0)
        self.state.setdefault(out, 0)
        self.fanout[a].append(index)
        self.fanout[b].append(index)

    def alias(self, source: str, target: str) -> None:
        self.state.setdefault(source, 0)
        self.state.setdefault(target, 0)
        self.aliases[source].append(target)
        self.alias_sources[target].append(source)

    def _queue_fanout(self, source: str, queue: deque[str], queued: set[str]) -> None:
        for target in self.aliases.get(source, ()):
            self.signal_propagations += 1
            if target in queued:
                continue
            queued.add(target)
            queue.append(target)

    def _signal_changed(self, source: str, queue: deque[str], queued: set[str], dirty: set[int]) -> None:
        dirty.update(self.fanout.get(source, ()))
        self._queue_fanout(source, queue, queued)

    def _drain(self, queue: deque[str], queued: set[str], dirty: set[int]) -> None:
        while queue:
            target = queue.popleft()
            queued.discard(target)
            self.target_resolutions += 1
            values = {self.state[source] for source in self.alias_sources[target]}
            value = values.pop() if len(values) == 1 else 0
            if self.state[target] == value:
                continue
            self.state[target] = value
            self._signal_changed(target, queue, queued, dirty)

    def settle(
        self,
        changed: list[str] | None = None,
        max_delta: int | None = None,
        max_gate_evaluations: int = 256,
    ) -> tuple[bool, int]:
        queue: deque[str] = deque()
        queued: set[str] = set()
        dirty: set[int] = set()
        evaluations_this_settle: dict[int, int] = defaultdict(int)

        names = list(self.state) if changed is None else changed
        for name in names:
            self._signal_changed(name, queue, queued, dirty)

        if max_delta is None:
            max_delta = max(256, len(self.gates) + 64)

        delta = 0
        while queue or dirty:
            self._drain(queue, queued, dirty)
            if not dirty:
                continue
            if delta >= max_delta:
                return False, delta

            # Index order only controls evaluation work.  Outputs are staged and
            # committed simultaneously, so result does not depend on this order.
            batch = sorted(dirty)
            dirty.clear()
            pending: list[tuple[str, int]] = []

            for index in batch:
                if evaluations_this_settle[index] >= max_gate_evaluations:
                    return False, delta
                evaluations_this_settle[index] += 1
                a, b, out = self.gates[index]
                new_value = 1 ^ (self.state[a] & self.state[b])
                self.gate_evaluations += 1
                if self.state[out] != new_value:
                    pending.append((out, new_value))

            for out, new_value in pending:
                if self.state[out] == new_value:
                    continue
                self.state[out] = new_value
                self._signal_changed(out, queue, queued, dirty)

            delta += 1

        return True, delta

    def drive(self, name: str, value: int) -> tuple[bool, int]:
        value = int(bool(value))
        if self.state.get(name) == value:
            return True, 0
        self.state[name] = value
        return self.settle([name])

    def prime(self) -> None:
        """One deterministic power-on pass used only to seed undefined feedback."""
        queue: deque[str] = deque()
        queued: set[str] = set()
        dirty: set[int] = set()
        for name in self.state:
            self._signal_changed(name, queue, queued, dirty)
        self._drain(queue, queued, dirty)

        for a, b, out in self.gates:
            new_value = 1 ^ (self.state[a] & self.state[b])
            if self.state[out] == new_value:
                continue
            self.state[out] = new_value
            dirty.clear()
            self._signal_changed(out, queue, queued, dirty)
            self._drain(queue, queued, dirty)


def wrapped_nand(net: DeltaNet, a: str, b: str, out: str, prefix: str, depth: int) -> None:
    if depth == 0:
        net.gate(a, b, out)
        return

    ai = a
    bi = b
    for level in range(depth):
        next_a = f"{prefix}/L{level}/A"
        next_b = f"{prefix}/L{level}/B"
        net.alias(ai, next_a)
        net.alias(bi, next_b)
        ai, bi = next_a, next_b

    inner_out = f"{prefix}/INNER/Q"
    net.gate(ai, bi, inner_out)
    previous = inner_out
    for level in reversed(range(depth)):
        next_out = out if level == 0 else f"{prefix}/L{level - 1}/Q"
        net.alias(previous, next_out)
        previous = next_out


def inv(net: DeltaNet, a: str, out: str, prefix: str, depth: int = 0) -> None:
    wrapped_nand(net, a, a, out, prefix, depth)


def build_d_latch(net: DeltaNet, prefix: str, d: str, en: str, depth: int = 0) -> tuple[str, str]:
    nd = f"{prefix}/ND"
    set_n = f"{prefix}/SET_N"
    reset_n = f"{prefix}/RESET_N"
    q = f"{prefix}/Q"
    qb = f"{prefix}/QB"

    inv(net, d, nd, f"{prefix}/N0", depth)
    wrapped_nand(net, d, en, set_n, f"{prefix}/N1", depth)
    wrapped_nand(net, nd, en, reset_n, f"{prefix}/N2", depth)
    wrapped_nand(net, set_n, qb, q, f"{prefix}/N3", depth)
    wrapped_nand(net, reset_n, q, qb, f"{prefix}/N4", depth)
    return q, qb


def build_dff(net: DeltaNet, prefix: str, d: str, clk: str, depth: int = 0) -> tuple[str, str]:
    nclk = f"{prefix}/NCLK"
    inv(net, clk, nclk, f"{prefix}/CLK_INV", depth)
    mq, _ = build_d_latch(net, f"{prefix}/MASTER", d, nclk, depth)
    return build_d_latch(net, f"{prefix}/SLAVE", mq, clk, depth)


def build_counter(bits: int, depth: int = 0) -> tuple[DeltaNet, list[tuple[str, str]]]:
    net = DeltaNet()
    net.set("CLK", 0)
    clock = "CLK"
    outputs: list[tuple[str, str]] = []

    for bit in range(bits):
        prefix = f"COUNTER/B{bit}"
        d = f"{prefix}/D"
        q, qb = build_dff(net, prefix, d, clock, depth)
        inv(net, q, d, f"{prefix}/TOGGLE", depth)
        outputs.append((q, qb))
        clock = qb

    net.prime()
    converged, _ = net.settle()
    assert converged, f"{bits}-bit counter failed to settle after prime"
    return net, outputs


def counter_value(net: DeltaNet, outputs: list[tuple[str, str]]) -> int:
    value = 0
    for bit, (q, _) in enumerate(outputs):
        value |= net.state[q] << bit
    return value


def tick_counter(net: DeltaNet) -> None:
    converged, _ = net.drive("CLK", 1)
    assert converged, "counter did not settle on rising edge"
    converged, _ = net.drive("CLK", 0)
    assert converged, "counter did not settle on falling edge"


def legacy_sr_single_sweep(q: int, qb: int, set_n: int, reset_n: int, order: tuple[str, str]) -> tuple[int, int]:
    for gate in order:
        if gate == "Q":
            q = 1 ^ (set_n & qb)
        else:
            qb = 1 ^ (reset_n & q)
    return q, qb


def test_legacy_root_cause() -> None:
    a = legacy_sr_single_sweep(1, 0, 1, 0, ("Q", "QB"))
    b = legacy_sr_single_sweep(1, 0, 1, 0, ("QB", "Q"))
    assert a == (1, 1)
    assert b == (0, 1)
    assert a != b, "legacy single-sweep reproduction unexpectedly became order-independent"


def test_nand_and_hierarchy() -> None:
    for depth in range(9):
        for a in (0, 1):
            for b in (0, 1):
                net = DeltaNet()
                net.set("A", a)
                net.set("B", b)
                wrapped_nand(net, "A", "B", "Q", "NAND", depth)
                net.prime()
                converged, _ = net.settle()
                assert converged
                assert net.state["Q"] == (1 ^ (a & b)), (depth, a, b, net.state["Q"])


def test_long_chain() -> None:
    net = DeltaNet()
    net.set("IN", 0)
    source = "IN"
    for i in range(100):
        out = f"N{i}/Q"
        inv(net, source, out, f"N{i}")
        source = out
    net.prime()
    assert net.settle()[0]
    assert net.state[source] == 0
    assert net.drive("IN", 1)[0]
    assert net.state[source] == 1


def test_parallel_instances() -> None:
    net = DeltaNet()
    net.set("A", 1)
    net.set("B", 1)
    outputs = []
    for i in range(100):
        out = f"CUSTOM{i}/Q"
        wrapped_nand(net, "A", "B", out, f"CUSTOM{i}/NAND", 8)
        outputs.append(out)
    net.prime()
    assert net.settle()[0]
    assert {net.state[out] for out in outputs} == {0}
    assert net.drive("B", 0)[0]
    assert {net.state[out] for out in outputs} == {1}


def test_massive_nested_fanout() -> None:
    """Exercise CPU-like reuse: hundreds of deeply wrapped chip instances."""
    net = DeltaNet()
    net.set("A", 1)
    net.set("B", 1)
    outputs = []
    for i in range(512):
        out = f"BANK/CUSTOM{i}/Q"
        wrapped_nand(net, "A", "B", out, f"BANK/CUSTOM{i}/NAND", 64)
        outputs.append(out)

    net.prime()
    assert net.settle()[0]
    assert all(net.state[out] == 0 for out in outputs)
    assert net.drive("A", 0)[0]
    assert all(net.state[out] == 1 for out in outputs)


def test_large_8bit_bus_fanout() -> None:
    net = DeltaNet()
    inputs = [f"BUS_IN/{bit}" for bit in range(8)]
    for name in inputs:
        net.set(name, 0)
    for instance in range(1024):
        for bit, source in enumerate(inputs):
            net.alias(source, f"INSTANCE{instance}/D{bit}")

    net.prime()
    assert net.settle()[0]
    for value in (0x00, 0xFF, 0xA5, 0x5A, 0x81, 0x7E):
        changed = []
        for bit, source in enumerate(inputs):
            state = (value >> bit) & 1
            if net.state[source] != state:
                net.state[source] = state
                changed.append(source)
        assert net.settle(changed)[0]
        for instance in range(1024):
            actual = sum(net.state[f"INSTANCE{instance}/D{bit}"] << bit for bit in range(8))
            assert actual == value, (instance, value, actual)


def test_randomized_combinational_dags() -> None:
    """Compare event propagation against a topological full-sweep oracle."""
    for seed in range(8):
        rng = random.Random(0xD15EA5E + seed)
        inputs = [f"I{i}" for i in range(32)]
        definitions: list[tuple[str, str, str]] = []
        available = inputs.copy()
        for index in range(768):
            a = rng.choice(available)
            b = rng.choice(available)
            out = f"G{index}"
            definitions.append((a, b, out))
            available.append(out)

        net = DeltaNet()
        for name in inputs:
            net.set(name, rng.randrange(2))
        shuffled = definitions.copy()
        rng.shuffle(shuffled)
        for a, b, out in shuffled:
            net.gate(a, b, out)
        net.prime()
        assert net.settle()[0]

        for vector in range(128):
            changed = []
            for name in inputs:
                state = rng.randrange(2)
                if net.state[name] != state:
                    net.state[name] = state
                    changed.append(name)
            assert net.settle(changed)[0], (seed, vector, "did not converge")

            expected = {name: net.state[name] for name in inputs}
            for a, b, out in definitions:
                expected[out] = 1 ^ (expected[a] & expected[b])
            for _, _, out in definitions:
                assert net.state[out] == expected[out], (seed, vector, out, net.state[out], expected[out])


def test_sr_latch() -> None:
    net = DeltaNet()
    net.set("SET_N", 1)
    net.set("RESET_N", 1)
    net.gate("SET_N", "QB", "Q")
    net.gate("RESET_N", "Q", "QB")
    net.prime()

    assert net.drive("SET_N", 0)[0]
    assert (net.state["Q"], net.state["QB"]) == (1, 0)
    assert net.drive("SET_N", 1)[0]
    assert (net.state["Q"], net.state["QB"]) == (1, 0)
    assert net.drive("RESET_N", 0)[0]
    assert (net.state["Q"], net.state["QB"]) == (0, 1)
    assert net.drive("RESET_N", 1)[0]
    assert (net.state["Q"], net.state["QB"]) == (0, 1)


def test_d_latch() -> None:
    net = DeltaNet()
    net.set("D", 0)
    net.set("EN", 1)
    q, qb = build_d_latch(net, "DL", "D", "EN")
    net.prime()
    assert net.settle()[0]

    assert net.drive("D", 1)[0]
    assert (net.state[q], net.state[qb]) == (1, 0)
    assert net.drive("EN", 0)[0]
    assert net.drive("D", 0)[0]
    assert (net.state[q], net.state[qb]) == (1, 0), "D changed while latch disabled"
    assert net.drive("EN", 1)[0]
    assert (net.state[q], net.state[qb]) == (0, 1)


def test_dff_10000_cycles() -> None:
    net = DeltaNet()
    net.set("D", 0)
    net.set("CLK", 0)
    q, qb = build_dff(net, "DFF", "D", "CLK")
    net.prime()
    assert net.settle()[0]

    for cycle in range(10_000):
        d = ((cycle * 1103515245 + 12345) >> 8) & 1
        assert net.drive("D", d)[0]
        assert net.drive("CLK", 1)[0]
        assert net.state[q] == d and net.state[qb] == (1 ^ d), (cycle, d, net.state[q], net.state[qb])

        # D is allowed to move while high; edge-triggered output must not follow it.
        opposite = 1 ^ d
        assert net.drive("D", opposite)[0]
        assert net.state[q] == d, (cycle, "transparent while CLK high")
        assert net.drive("CLK", 0)[0]
        assert net.state[q] == d


def test_counters_100000() -> dict[int, float]:
    timings: dict[int, float] = {}
    for bits in (4, 8, 16):
        net, outputs = build_counter(bits)
        start_value = counter_value(net, outputs)
        started = time.perf_counter()
        for tick in range(1, 100_001):
            tick_counter(net)
            expected = (start_value + tick) & ((1 << bits) - 1)
            actual = counter_value(net, outputs)
            assert actual == expected, (bits, tick, actual, expected)
        timings[bits] = time.perf_counter() - started
    return timings


def test_flat_vs_nested() -> None:
    flat, flat_out = build_counter(8, depth=0)
    nested, nested_out = build_counter(8, depth=5)
    assert counter_value(flat, flat_out) == counter_value(nested, nested_out)

    for tick in range(1, 5_001):
        tick_counter(flat)
        tick_counter(nested)
        a = counter_value(flat, flat_out)
        b = counter_value(nested, nested_out)
        assert a == b, ("flat-vs-nested", tick, a, b)


def test_creation_order_independence() -> None:
    definitions = []
    previous = "IN"
    for i in range(64):
        out = f"G{i}"
        definitions.append((previous, previous, out))
        previous = out

    baseline = None
    for seed in range(20):
        net = DeltaNet()
        net.set("IN", 1)
        shuffled = definitions.copy()
        random.Random(seed).shuffle(shuffled)
        for a, b, out in shuffled:
            net.gate(a, b, out)
        net.prime()
        assert net.settle()[0]
        result = net.state[previous]
        if baseline is None:
            baseline = result
        assert result == baseline, (seed, result, baseline)


def test_restart_determinism() -> None:
    sequences = []
    for _ in range(3):
        net, outputs = build_counter(8, depth=3)
        sequence = [counter_value(net, outputs)]
        for _tick in range(512):
            tick_counter(net)
            sequence.append(counter_value(net, outputs))
        sequences.append(sequence)
    assert sequences[0] == sequences[1] == sequences[2]


def test_oscillator_guard() -> None:
    net = DeltaNet()
    net.set("X", 0)
    net.gate("X", "X", "X")
    net.prime()
    start = time.perf_counter()
    converged, delta = net.settle(["X"], max_delta=64)
    elapsed = time.perf_counter() - start
    assert not converged
    assert delta == 64
    assert elapsed < 1.0, elapsed


def test_local_oscillator_guard_with_large_unrelated_netlist() -> None:
    net = DeltaNet()
    net.set("X", 0)
    net.gate("X", "X", "X")
    for index in range(10_000):
        net.set(f"A{index}", 0)
        net.set(f"B{index}", 0)
        net.gate(f"A{index}", f"B{index}", f"Q{index}")

    net.prime()
    converged, delta = net.settle(["X"], max_gate_evaluations=64)
    assert not converged
    assert delta == 64, "unrelated gates must not delay local oscillation detection"


def test_multidriver_resolution_and_coalescing() -> None:
    net = DeltaNet()
    drivers = [f"D{i}" for i in range(256)]
    for source in drivers:
        net.set(source, 0)
        net.alias(source, "BUS")

    net.prime()
    assert net.settle()[0]

    before = net.target_resolutions
    for source in drivers:
        net.set(source, 1)
    assert net.settle(drivers)[0]
    assert net.state["BUS"] == 1
    assert net.target_resolutions - before == 1, "shared target was resolved more than once"

    net.set(drivers[0], 0)
    assert net.settle([drivers[0]])[0]
    assert net.state["BUS"] == 0, "conflicting active drivers must resolve deterministically low"


def test_source_integration_static() -> None:
    root = Path(__file__).resolve().parents[2]
    solver = (root / "Assets/Scripts/Simulation/DeterministicSimulator.cs").read_text(encoding="utf-8")
    facade = (root / "Assets/Scripts/Game/Project/SimulationFacade.cs").read_text(encoding="utf-8")

    required_solver_tokens = (
        "SettleCombinational",
        "PowerOnAsynchronousSettle",
        "FullDeterministicResettle",
        "maxDeltaCycles",
        "ResolveDrivenState",
        "AdvanceSequentialComponents",
        "RegisterDiagnosticPaths",
        "TraceNonConvergence",
        "Queue<int> targetQueue",
        "bool[] queuedTargets",
        "int[] targetOffsetsBySource",
        "int[] targetIndicesBySource",
        "int[] sourceOffsetsByTarget",
        "BuildAdjacency",
        "sourceCount == 1",
        "target.numInputsReceivedThisFrame = sourceCount;",
        "if (inputPins == null) inputPins = Array.Empty<DevPinInstance>();",
        "MaxEvaluationsPerChipPerSettle",
        "TryBeginChipEvaluation",
        "evaluationEpochByChip",
        "boundInputPinIndices",
        "LastTargetResolutions",
    )
    for token in required_solver_tokens:
        assert token in solver, f"missing deterministic solver mechanism: {token}"

    assert "sourceIndices.Length" not in solver, "stale jagged-adjacency reference breaks the CSR build"

    assert "RandomBool()" not in solver
    # Randomized ordering is intentionally restricted to the one-time power-on
    # settle used to choose a stable state for symmetric feedback circuits.
    assert solver.count("Simulator.rng.Next(") == 1
    assert "int slot = Simulator.rng.Next(dirtyChips.Count);" in solver
    assert "HashSet<SimPin>" not in solver
    # One SimChip hash-set is allowed for editor inspection/deoptimization paths;
    # it is not part of the per-step propagation hot path.
    assert solver.count("HashSet<SimChip>") == 1
    assert "static readonly HashSet<SimChip> inspectionPath = new();" in solver
    assert "Dictionary<SimPin, SimPin[]>" not in solver
    assert "DeterministicSimulator.RunSimulationStep" in facade
    assert "bool topologyChanged = DLS.Simulation.Simulator.ApplyModifications();" in facade
    assert "if (topologyChanged) DeterministicSimulator.InvalidateTopology();" in facade
    assert "pendingTopologyModification" not in facade


def benchmark_sparse_parallel_bank() -> tuple[float, int, int]:
    net = DeltaNet()
    gate_count = 10_000
    for i in range(gate_count):
        net.set(f"A{i}", 0)
        net.set(f"B{i}", 1)
        net.gate(f"A{i}", f"B{i}", f"Q{i}")
    net.prime()
    net.settle()
    before = net.gate_evaluations
    started = time.perf_counter()
    for i in range(2000):
        assert net.drive("A0", 1 ^ (i & 1))[0]
    elapsed = time.perf_counter() - started
    evaluations = net.gate_evaluations - before
    full_sweeps = 2000 * len(net.gates)
    return elapsed, evaluations, full_sweeps


def benchmark_fanin_coalescing() -> tuple[int, int, int]:
    net = DeltaNet()
    drivers = [f"S{i}" for i in range(2048)]
    for source in drivers:
        net.set(source, 0)
        net.alias(source, "BUS")
    net.prime()
    net.settle()

    before = net.target_resolutions
    for source in drivers:
        net.set(source, 1)
    assert net.settle(drivers)[0]
    compiled_resolutions = net.target_resolutions - before
    legacy_driver_scans = len(drivers) * len(drivers)
    compiled_driver_scans = compiled_resolutions * len(drivers)
    return compiled_resolutions, compiled_driver_scans, legacy_driver_scans


def main() -> None:
    tests = [
        ("legacy root-cause reproduction", test_legacy_root_cause),
        ("NAND truth table + hierarchy depth 0..8", test_nand_and_hierarchy),
        ("100-gate chain", test_long_chain),
        ("100 parallel custom-chip instances", test_parallel_instances),
        ("512 custom chips at hierarchy depth 64", test_massive_nested_fanout),
        ("8-bit bus fanout to 1,024 instances", test_large_8bit_bus_fanout),
        ("randomized NAND DAGs versus full-sweep oracle", test_randomized_combinational_dags),
        ("SR latch SET/HOLD/RESET/HOLD", test_sr_latch),
        ("D latch enable behavior", test_d_latch),
        ("edge-triggered DFF 10,000 cycles", test_dff_10000_cycles),
        ("flat vs nested sequential equivalence", test_flat_vs_nested),
        ("creation-order independence", test_creation_order_independence),
        ("restart determinism", test_restart_determinism),
        ("oscillator delta-cycle guard", test_oscillator_guard),
        ("local oscillator guard in large netlist", test_local_oscillator_guard_with_large_unrelated_netlist),
        ("multi-driver resolution + target coalescing", test_multidriver_resolution_and_coalescing),
        ("C# integration static checks", test_source_integration_static),
    ]

    total_start = time.perf_counter()
    for name, test in tests:
        started = time.perf_counter()
        test()
        print(f"PASS  {name:<48} {time.perf_counter() - started:8.3f}s")

    started = time.perf_counter()
    counter_timings = test_counters_100000()
    counter_elapsed = time.perf_counter() - started
    print(f"PASS  {'4/8/16-bit counters, 100,000 clocks each':<48} {counter_elapsed:8.3f}s")
    for bits, elapsed in counter_timings.items():
        print(f"      counter-{bits}: {elapsed:.3f}s")

    bench_elapsed, event_evals, full_sweeps = benchmark_sparse_parallel_bank()
    print("BENCH sparse 10,000-gate bank, 2,000 input transitions")
    print(f"      wall={bench_elapsed:.3f}s gate_evaluations={event_evals} full_sweep_reference={full_sweeps}")
    print(f"      evaluation_ratio={event_evals / full_sweeps:.6f}")
    resolutions, compiled_scans, legacy_scans = benchmark_fanin_coalescing()
    print("BENCH 2,048 simultaneous drivers targeting one shared net")
    print(f"      target_resolutions={resolutions} driver_scans={compiled_scans} legacy_reference={legacy_scans}")
    print(f"      driver_scan_ratio={compiled_scans / legacy_scans:.6f}")
    print(f"ALL TESTS PASSED in {time.perf_counter() - total_start:.3f}s")


if __name__ == "__main__":
    main()
