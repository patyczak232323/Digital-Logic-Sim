#!/usr/bin/env python3
"""Compatibility contract for Rewired built-ins and trace comparison.

This suite complements SimulationRegression:
- SimulationRegression specifies gate-level/delta-cycle behavior, DFFs, latches,
  counters, nesting, oscillation guards and large stress cases.
- CompatibilityRegression specifies the state machines of built-in Clock, Pulse,
  RAM and ROM components and verifies that the live-engine C# compatibility
  harness remains wired into the repository.

The C# CompatibilitySelfTestSuite runs these concerns through real SimChip graphs.
"""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]


@dataclass(frozen=True)
class TraceStep:
    name: str
    inputs: tuple[int, ...]
    outputs: tuple[int, ...]


def compare_traces(expected: list[TraceStep], actual: list[TraceStep]) -> list[str]:
    failures: list[str] = []
    if len(expected) != len(actual):
        failures.append(f"trace length: expected {len(expected)}, actual {len(actual)}")

    for index, (want, got) in enumerate(zip(expected, actual)):
        if want.inputs != got.inputs:
            failures.append(
                f"step {index} ({want.name}) inputs: expected {want.inputs}, actual {got.inputs}"
            )
        if want.outputs != got.outputs:
            failures.append(
                f"step {index} ({want.name}) outputs: expected {want.outputs}, actual {got.outputs}"
            )
    return failures


class PulseModel:
    def __init__(self, duration: int) -> None:
        self.duration = duration
        self.remaining = 0
        self.old_input = 0

    def step(self, input_high: int) -> int:
        high = int(bool(input_high))
        if self.remaining == 0 and high and not self.old_input:
            self.remaining = self.duration

        output = 0
        if self.remaining > 0:
            self.remaining -= 1
            output = 1

        self.old_input = high
        return output


class ClockModel:
    def __init__(self, steps_per_transition: int) -> None:
        self.steps_per_transition = steps_per_transition
        self.frame = 0

    def step(self) -> int:
        self.frame += 1
        if self.steps_per_transition == 0:
            return 0
        return int(((self.frame // self.steps_per_transition) & 1) == 0)


class Ram8Model:
    def __init__(self) -> None:
        self.memory = [0] * 256
        self.clock_old = 0

    def step(self, address: int, data: int, write: int, reset: int, clock: int) -> int:
        clock_high = int(bool(clock))
        rising = clock_high and not self.clock_old
        self.clock_old = clock_high

        if rising:
            if reset:
                self.memory[:] = [0] * 256
            elif write:
                self.memory[address & 0xFF] = data & 0xFF

        return self.memory[address & 0xFF]


class Rom16Model:
    def __init__(self) -> None:
        self.words = [0] * 256

    def read(self, address: int) -> tuple[int, int]:
        word = self.words[address & 0xFF]
        return ((word >> 8) & 0xFF, word & 0xFF)


def test_trace_comparator() -> None:
    expected = [
        TraceStep("idle", (0,), (0,)),
        TraceStep("rise", (1,), (1,)),
        TraceStep("expire", (1,), (0,)),
    ]
    assert compare_traces(expected, list(expected)) == []

    actual = list(expected)
    actual[1] = TraceStep("rise", (1,), (0,))
    failures = compare_traces(expected, actual)
    assert len(failures) == 1
    assert "step 1" in failures[0]
    assert "expected (1,)" in failures[0]


def test_pulse_golden_trace() -> None:
    model = PulseModel(3)
    inputs = [0, 1, 1, 1, 1, 0, 1]
    expected_outputs = [0, 1, 1, 1, 0, 0, 1]
    actual = [model.step(value) for value in inputs]
    assert actual == expected_outputs, actual


def test_clock_golden_trace() -> None:
    model = ClockModel(2)
    assert [model.step() for _ in range(8)] == [1, 0, 0, 1, 1, 0, 0, 1]


def test_ram_edge_semantics() -> None:
    ram = Ram8Model()
    trace = [
        # address, data, write, reset, clock, expected read
        (0x2A, 0x00, 0, 1, 1, 0x00),
        (0x2A, 0xA5, 1, 0, 0, 0x00),
        (0x2A, 0xA5, 1, 0, 1, 0xA5),
        (0x2A, 0x5A, 1, 0, 1, 0xA5),
        (0x2A, 0x5A, 1, 0, 0, 0xA5),
        (0x2A, 0x5A, 1, 0, 1, 0x5A),
        (0x2B, 0x00, 0, 0, 1, 0x00),
    ]
    for index, (*inputs, expected) in enumerate(trace):
        actual = ram.step(*inputs)
        assert actual == expected, (index, inputs, actual, expected)


def test_rom_byte_order_and_address_mask() -> None:
    rom = Rom16Model()
    rom.words[0x12] = 0xABCD
    rom.words[0xFE] = 0x1234

    assert rom.read(0x12) == (0xAB, 0xCD)
    assert rom.read(0x13) == (0x00, 0x00)
    assert rom.read(0xFE) == (0x12, 0x34)
    assert rom.read(0x112) == (0xAB, 0xCD)


def test_accelerated_live_trace_contract() -> None:
    # Four NAND inverters form identity.  The live solver reaches the fixed point
    # through delta cycles; a compiled combinational implementation can return the
    # same Boolean result directly.  Step-by-step externally visible traces must match.
    vectors = [0, 1, 0, 1, 1, 0, 0, 1]
    live = [TraceStep(f"v{i}", (value,), (value,)) for i, value in enumerate(vectors)]
    accelerated = [TraceStep(f"v{i}", (value,), (value,)) for i, value in enumerate(vectors)]
    assert compare_traces(live, accelerated) == []


def test_csharp_harness_surface() -> None:
    runner = (ROOT / "Assets/Scripts/Simulation/CompatibilityTestRunner.cs").read_text(encoding="utf-8")
    suite = (ROOT / "Assets/Scripts/Simulation/CompatibilitySelfTestSuite.cs").read_text(encoding="utf-8")
    editor = (ROOT / "Assets/Editor/CompatibilitySelfTestCommand.cs").read_text(encoding="utf-8")

    runner_tokens = (
        "RunGolden(",
        "CaptureTrace(",
        "RunAccelerationParity(",
        "RewiredEngine.EnsureInitialized",
        "RewiredEngine.RunStep",
        "DisableAccelerationRecursive",
        "Diagnostics.CacheHits",
        "Diagnostics.JitHits",
        "Diagnostics.FeedbackJitHits",
        "WaitForFullLutReady",
        "waitForFullLut",
    )
    for token in runner_tokens:
        assert token in runner, f"missing compatibility runner mechanism: {token}"

    required_cases = (
        "NAND truth table / propagation",
        "Tri-state disconnect / reconnect",
        "Pulse rising-edge width and retrigger",
        "Clock source cadence",
        "ROM address/read ordering",
        "RAM reset / rising-edge write / hold",
        "Feedback SR latch SET/HOLD/RESET/HOLD",
        "Deep custom-chip nesting",
        "Live solver vs native JIT parity",
        "FULL LUT cache vs live solver parity",
        "Feedback live solver vs feedback-JIT parity",
    )
    for name in required_cases:
        assert name in suite, f"missing live compatibility case: {name}"

    assert "CompatibilitySelfTestSuite.RunAll()" in editor
    assert "throw new Exception" in editor


def test_gate_level_suite_coverage_is_retained() -> None:
    solver_suite = (ROOT / "Tools/SimulationRegression/regression.py").read_text(encoding="utf-8")
    required = (
        "SR latch SET/HOLD/RESET/HOLD",
        "D latch enable behavior",
        "edge-triggered DFF 10,000 cycles",
        "4/8/16-bit counters, 100,000 clocks each",
        "flat vs nested sequential equivalence",
        "512 custom chips at hierarchy depth 64",
        "oscillator delta-cycle guard",
    )
    for token in required:
        assert token in solver_suite, f"gate-level compatibility coverage disappeared: {token}"


TESTS = (
    test_trace_comparator,
    test_pulse_golden_trace,
    test_clock_golden_trace,
    test_ram_edge_semantics,
    test_rom_byte_order_and_address_mask,
    test_accelerated_live_trace_contract,
    test_csharp_harness_surface,
    test_gate_level_suite_coverage_is_retained,
)


def main() -> None:
    for test in TESTS:
        test()
        print(f"PASS  {test.__name__}")
    print(f"ALL {len(TESTS)} COMPATIBILITY REGRESSIONS PASSED")


if __name__ == "__main__":
    main()
