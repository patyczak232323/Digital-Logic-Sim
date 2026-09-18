#!/usr/bin/env python3
"""Focused regression checks for the deterministic engine integration layer."""

from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def source(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


def extract_method(text: str, signature: str) -> str:
    start = text.index(signature)
    brace = text.index("{", start)
    depth = 0
    for i in range(brace, len(text)):
        if text[i] == "{":
            depth += 1
        elif text[i] == "}":
            depth -= 1
            if depth == 0:
                return text[start : i + 1]
    raise AssertionError(f"unterminated method: {signature}")


def test_paused_inspection_is_synchronized_before_initialization() -> None:
    facade = source("Assets/Scripts/Game/Project/SimulationFacade.cs")
    method = extract_method(facade, "public static void EnsureInitialized(")

    sync = method.index("TrySynchronizeInspectionChip(project);")
    init = method.index("DeterministicSimulator.EnsureInitialized(rootSimChip, inputPins, audioState);")
    assert sync < init
    assert "project != null && project.simPaused" in method


def test_topology_edit_recovery_is_one_shot_and_nonconvergence_only() -> None:
    facade = source("Assets/Scripts/Game/Project/SimulationFacade.cs")
    apply_method = extract_method(facade, "public static void ApplyModifications()")
    init_method = extract_method(facade, "public static void EnsureInitialized(")
    run_method = extract_method(facade, "public static void RunSimulationStep(")

    assert "topologyRecoveryPending = true;" in apply_method
    assert "DeterministicSimulator.InvalidateTopology();" in apply_method

    assert "topologyRecoveryPending = false;" in init_method
    assert "if (!DeterministicSimulator.LastSettleConverged)" in init_method
    assert "DeterministicSimulator.Reset();" in init_method

    # Recovery power-on synchronization must not consume a real RAM/Pulse/display
    # clock edge that arrived in the same frame as the structural edit.
    capture = init_method.index("CaptureSequentialEdgeState(rootSimChip)")
    reset = init_method.index("DeterministicSimulator.Reset();")
    recovery_init = init_method.rindex("DeterministicSimulator.EnsureInitialized(rootSimChip, inputPins, audioState);")
    restore = init_method.index("RestoreSequentialEdgeState(edgeState);")
    assert capture < reset < recovery_init < restore

    edge_index_method = extract_method(facade, "static int GetSequentialEdgeStateIndex(")
    for chip_type in ("ChipType.Pulse", "ChipType.dev_Ram_8Bit", "ChipType.DisplayRGB", "ChipType.DisplayDot"):
        assert chip_type in edge_index_method

    # The normal hot path must not perform an unconditional extra initialization.
    ensure_call = "EnsureInitialized(rootSimChip, inputPins, audioState);"
    assert ensure_call in run_method
    assert run_method.index("if (topologyRecoveryPending)") < run_method.index(ensure_call)


def test_symmetric_nand_latch_model_needs_asynchronous_recovery() -> None:
    # Lower-bit model of two cross-coupled NANDs with both external inputs held high.
    # New SimPins begin disconnected; their logic-value bit is zero, so a simultaneous
    # delta-cycle evaluation starts from the symmetric (0, 0) state.
    def nand(a: int, b: int) -> int:
        return 1 ^ (a & b)

    q = 0
    qb = 0
    simultaneous = []
    for _ in range(6):
        q, qb = nand(1, qb), nand(1, q)
        simultaneous.append((q, qb))

    assert simultaneous == [(1, 1), (0, 0), (1, 1), (0, 0), (1, 1), (0, 0)]

    # Updating one dirty gate at a time breaks only the scheduling symmetry; NAND
    # truth tables remain exact. The latch reaches one of its valid fixed points.
    q = 0
    qb = 0
    for _ in range(4):
        q = nand(1, qb)
        qb = nand(1, q)

    assert (q, qb) in {(1, 0), (0, 1)}
    assert q == nand(1, qb)
    assert qb == nand(1, q)


def test_jit_still_rejects_multiple_drivers() -> None:
    jit = source("Assets/Scripts/Simulation/CombinationalJitCompiler.cs")
    assert 'throw new InvalidOperationException("multiple drivers reached JIT compiler")' in jit


def test_removed_connection_still_clears_last_driver_state() -> None:
    sim_chip = source("Assets/Scripts/Simulation/SimChip.cs")
    method = extract_method(sim_chip, "public void RemoveConnection(")
    assert "removeTargetPin.numInputConnections == 0" in method
    assert "PinState.SetAllDisconnected(ref removeTargetPin.State);" in method


def test_stateful_projects_route_to_upstream_compatible_timing() -> None:
    facade = source("Assets/Scripts/Game/Project/SimulationFacade.cs")
    run_method = extract_method(facade, "public static void RunSimulationStep(")
    compat_method = extract_method(facade, "static bool RequiresUpstreamTiming(")
    apply_method = extract_method(facade, "public static void ApplyModifications()")

    assert "if (UseLegacyCompatibilityEngine(rootSimChip))" in run_method
    assert "DLS.Simulation.Simulator.RunSimulationStep(rootSimChip, inputPins, audioState);" in run_method
    assert "DeterministicSimulator.RunSimulationStep(rootSimChip, inputPins, audioState);" in run_method
    assert run_method.index("DLS.Simulation.Simulator.RunSimulationStep") < run_method.index(
        "DeterministicSimulator.RunSimulationStep"
    )

    # A graph that cannot be represented as a pure input->output combinational function
    # must preserve the upstream one-pass-per-tick timing model.
    assert "return !analysis.CanCache;" in compat_method

    # Live editor topology can temporarily be newer than SimChip.Description.
    assert "forceLegacyCompatibilityAfterEdit = true;" in apply_method


def test_fixed_point_semantics_are_not_upstream_equivalent_for_feedback() -> None:
    # Cross-coupled NANDs illustrate the semantic difference. The upstream engine
    # evaluates each primitive once in traversal order. A synchronous fixed-point
    # batch instead evaluates both from the same old state and can oscillate.
    def nand(a: int, b: int) -> int:
        return 1 ^ (a & b)

    # Upstream-style immediate one-pass ordering.
    q, qb = 0, 0
    q = nand(1, qb)
    qb = nand(1, q)
    assert (q, qb) == (1, 0)

    # Fixed-point batch ordering from the same starting state.
    q, qb = 0, 0
    states = []
    for _ in range(4):
        q, qb = nand(1, qb), nand(1, q)
        states.append((q, qb))
    assert states == [(1, 1), (0, 0), (1, 1), (0, 0)]


TESTS = (
    test_paused_inspection_is_synchronized_before_initialization,
    test_topology_edit_recovery_is_one_shot_and_nonconvergence_only,
    test_symmetric_nand_latch_model_needs_asynchronous_recovery,
    test_jit_still_rejects_multiple_drivers,
    test_removed_connection_still_clears_last_driver_state,
    test_stateful_projects_route_to_upstream_compatible_timing,
    test_fixed_point_semantics_are_not_upstream_equivalent_for_feedback,
)


def main() -> None:
    for test in TESTS:
        test()
        print(f"PASS  {test.__name__}")
    print(f"ALL {len(TESTS)} ENGINE AUDIT REGRESSIONS PASSED")


if __name__ == "__main__":
    main()
