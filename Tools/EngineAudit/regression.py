#!/usr/bin/env python3
"""Regression guards for the Rewired simulation runtime.

These tests intentionally mix source-contract checks with tiny executable reference
models. They are fast enough to run on every push and catch dangerous changes to the
power-on/feedback-JIT boundary before a Unity build is produced.
"""

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


def test_feedback_jit_is_dormant_until_after_first_normal_tick() -> None:
    sim = source("Assets/Scripts/Simulation/DeterministicSimulator.cs")
    ensure = extract_method(sim, "public static void EnsureInitialized(")
    run = extract_method(sim, "public static void RunSimulationStep(")

    assert "feedbackActivationPending = true;" in ensure
    assert "SynchronizeFeedbackExecutors(rootSimChip)" not in ensure

    pre = run.index("SettleCombinational();")
    sequential = run.index("AdvanceSequentialComponents();")
    post = run.rindex("SettleCombinational();")
    activate = run.index("SynchronizeFeedbackExecutors(rootSimChip)")
    assert pre < sequential < post < activate


def test_feedback_jit_uses_two_delta_buffers() -> None:
    jit = source("Assets/Scripts/Simulation/FeedbackJitCompiler.cs")
    emit_store = extract_method(jit, "static void EmitStorePrefix(")
    emit_load = extract_method(jit, "static void EmitLoadSignal(")
    evaluate = extract_method(jit, "public bool Evaluate(")

    assert "OpCodes.Ldarg_1" in emit_store  # next buffer
    assert "OpCodes.Ldarg_0" in emit_load   # current buffer
    assert "current = next;" in evaluate
    assert "next = swap;" in evaluate


def test_feedback_jit_rejects_side_effecting_primitives_before_sink_removal() -> None:
    jit = source("Assets/Scripts/Simulation/FeedbackJitCompiler.cs")
    compile_program = extract_method(jit, "static CompiledFeedbackProgram CompileProgram(")

    safety = compile_program.index("if (!IsSupportedPrimitive(primitiveChips[i].ChipType)) return null;")
    remove_sinks = compile_program.index("primitiveChips.RemoveAll")
    assert safety < remove_sinks

    supported = extract_method(jit, "static bool IsSupportedPrimitive(")
    for forbidden in ("ChipType.Clock", "ChipType.Key", "ChipType.Pulse",
                      "ChipType.dev_Ram_8Bit", "ChipType.DisplayRGB",
                      "ChipType.DisplayDot", "ChipType.Buzzer"):
        assert forbidden not in supported


def test_runtime_edits_materialize_feedback_state_before_invalidation() -> None:
    sim = source("Assets/Scripts/Simulation/Simulator.cs")
    apply = extract_method(sim, "public static bool ApplyModifications()")

    materialize = apply.index("invalidate.FeedbackExecutor.MaterializeState();")
    clear = apply.index("invalidate.FeedbackExecutor = null;")
    assert materialize < clear


def test_acceleration_priority_is_lut_then_acyclic_jit_then_feedback_jit() -> None:
    sim = source("Assets/Scripts/Simulation/DeterministicSimulator.cs")
    evaluate = extract_method(sim, "static void EvaluateCombinationalChip(")

    lut = evaluate.index("chip.MemoCache != null && chip.MemoCache.Ready")
    native = evaluate.index("chip.CompiledExecutor != null")
    feedback = evaluate.index("chip.FeedbackExecutor != null && chip.FeedbackExecutor.Ready")
    assert lut < native < feedback


def test_feedback_nonconvergence_disables_accelerator_and_requests_rebuild() -> None:
    sim = source("Assets/Scripts/Simulation/DeterministicSimulator.cs")
    evaluate = extract_method(sim, "static void EvaluateCombinationalChip(")

    assert "chip.FeedbackExecutor.Disable();" in evaluate
    assert "topologyDirty = true;" in evaluate
    assert "LastSettleConverged = false;" in evaluate


def test_profiler_and_waveform_are_opt_in() -> None:
    profiler = source("Assets/Scripts/Simulation/SimulationProfiler.cs")
    wave = source("Assets/Scripts/Simulation/SimulationWaveformRecorder.cs")
    sim = source("Assets/Scripts/Simulation/DeterministicSimulator.cs")

    assert "static volatile bool enabled;" in profiler
    assert "if (!enabled) return;" in profiler
    assert "static volatile bool enabled;" in wave
    assert "if (!enabled) return;" in wave
    assert "if (SimulationProfiler.Enabled)" in sim
    assert "SimulationWaveformRecorder.Capture(Simulator.simulationFrame);" in sim


def test_waveform_ring_keeps_newest_samples() -> None:
    # Reference model for the recorder's circular ordering.
    capacity = 4
    buf = [None] * capacity
    write = 0
    count = 0
    for frame in range(7):
        buf[write] = frame
        write = (write + 1) % capacity
        count = min(count + 1, capacity)

    start = write if count == capacity else 0
    ordered = [buf[(start + i) % capacity] for i in range(count)]
    assert ordered == [3, 4, 5, 6]


def test_cross_coupled_nand_state_survives_synchronous_feedback_sweeps() -> None:
    def nand(a: int, b: int) -> int:
        return 1 ^ (a & b)

    def settle(q: int, qb: int, ns: int, nr: int):
        for _ in range(32):
            nq = nand(ns, qb)
            nqb = nand(nr, q)
            if (nq, nqb) == (q, qb):
                return q, qb
            q, qb = nq, nqb
        raise AssertionError("reference latch did not converge")

    # Start from a valid power-on fixed point supplied by the old asynchronous solver.
    q, qb = 1, 0
    assert settle(q, qb, 1, 1) == (1, 0)

    # Active-low reset, then release: state must be retained.
    q, qb = settle(q, qb, 1, 0)
    assert (q, qb) == (0, 1)
    assert settle(q, qb, 1, 1) == (0, 1)


def test_feedback_jit_is_invalidated_with_description_cache() -> None:
    cache = source("Assets/Scripts/Simulation/CombinationalChipCache.cs")
    method = extract_method(cache, "public static void NotifyProjectDescriptionsChanged()")
    assert "CombinationalJitCompiler.NotifyDescriptionsChanged();" in method
    assert "FeedbackJitCompiler.NotifyDescriptionsChanged();" in method


def test_nonconvergence_summary_is_available_without_diagnostic_sink() -> None:
    sim = source("Assets/Scripts/Simulation/DeterministicSimulator.cs")
    assert "public static string LastNonConvergenceDetails" in sim

    nonconv = extract_method(sim, "static void TraceNonConvergence(")
    summary = nonconv.index("LastNonConvergenceDetails =")
    guard = nonconv.index("if (!DiagnosticsEnabled || DiagnosticSink == null) return;")
    assert summary < guard
    assert "DescribePendingWork()" in nonconv


def test_combinational_test_runner_uses_isolated_netlist() -> None:
    runner = source("Assets/Scripts/Simulation/CombinationalChipTestRunner.cs")
    run = extract_method(runner, "public static ChipTestRunResult Run(")

    assert "CombinationalChipCacheManager.Analyze(description, library)" in run
    assert "BuildIsolatedChip(description, library)" in run
    assert "Simulator.BuildSimChip(description, library)" not in run
    assert "Simulator.EvaluatePureCombinationalForMemo(chip);" in run



def test_replay_captures_authoritative_state_and_verifies_outputs() -> None:
    replay = source("Assets/Scripts/Simulation/SimulationReplayRecorder.cs")
    sim = source("Assets/Scripts/Simulation/DeterministicSimulator.cs")
    keyboard = source("Assets/Scripts/Simulation/SimKeyboardHelper.cs")

    capture = extract_method(replay, "internal static void CaptureStepStart(")
    assert "DeterministicSimulator.MaterializeStateForSnapshot(root);" in capture
    assert "SimulationStateSnapshot.Capture(root)" in capture
    assert "SimKeyboardHelper.CaptureHeldKeys()" in capture

    run = extract_method(replay, "public static SimulationReplayResult Replay(")
    assert "DeterministicSimulator.PrepareForSnapshotRestore(root);" in run
    assert "SimKeyboardHelper.SetReplayInputState(frame.HeldKeys);" in run
    assert "actual == expected[output]" in run
    assert "diverged at replay frame" in run

    prepare = extract_method(sim, "internal static void PrepareForSnapshotRestore(")
    assert "DisableFeedbackExecutorsRecursive(root);" in prepare
    assert "needsPowerOnSettle = false;" in prepare

    key_read = extract_method(keyboard, "public static bool KeyIsHeld(")
    assert "ReplayOverrideActive" in key_read
    assert "ReplayKeyLookup" in key_read


def test_benchmark_measures_inside_step_without_advancing_extra_steps() -> None:
    benchmark = source("Assets/Scripts/Simulation/SimulationBenchmark.cs")
    sim = source("Assets/Scripts/Simulation/DeterministicSimulator.cs")

    run = extract_method(sim, "public static void RunSimulationStep(")
    assert "SimulationBenchmark.BeginStep();" in run

    end_profile = extract_method(sim, "static void EndProfilingStep(")
    assert "SimulationBenchmark.EndStep(" in end_profile

    # Benchmark is observational; it must never call the simulator itself.
    assert "RunSimulationStep(" not in benchmark
    assert "RawStepsPerSecond" in benchmark
    assert "Stopwatch.GetTimestamp()" in benchmark



def test_waveform_probe_context_menu_is_wired_to_live_simpin() -> None:
    context = source("Assets/Scripts/Graphics/UI/Menus/ContextMenu.cs")

    assert 'new(Format("TOGGLE PROBE"), ToggleProbe, CanProbePin)' in context
    assert "viewedSimChip.GetSimPinFromAddress(pin.Address)" in context
    assert "SimulationWaveformRecorder.TryGetProbeId(simPin" in context
    assert "SimulationWaveformRecorder.RemoveProbe(existingId);" in context
    assert "SimulationWaveformRecorder.AddProbe(" in context
    assert "SimulationWaveformRecorder.Enabled = true;" in context


def test_waveform_ui_supports_scalar_and_bus_traces() -> None:
    menu = source("Assets/Scripts/Graphics/UI/Menus/SimulationDiagnosticsMenu.cs")
    recorder = source("Assets/Scripts/Simulation/SimulationWaveformRecorder.cs")

    assert '"LOGIC ANALYZER"' in menu
    assert "SimulationWaveformRecorder.GetProbes()" in menu
    assert "SimulationWaveformRecorder.GetSamples(probe.Id)" in menu
    assert "DrawSingleBitTrace(traceBounds, samples)" in menu
    assert "DrawBusHistory(traceBounds, samples, probe.BitCount)" in menu
    assert "PinState.GetBitTristatedValue(state, 0)" in menu
    assert "public readonly int BitCount;" in recorder
    assert "ResolveBitCount(pin)" in recorder



TESTS = (
    test_feedback_jit_is_dormant_until_after_first_normal_tick,
    test_feedback_jit_uses_two_delta_buffers,
    test_feedback_jit_rejects_side_effecting_primitives_before_sink_removal,
    test_runtime_edits_materialize_feedback_state_before_invalidation,
    test_acceleration_priority_is_lut_then_acyclic_jit_then_feedback_jit,
    test_feedback_nonconvergence_disables_accelerator_and_requests_rebuild,
    test_profiler_and_waveform_are_opt_in,
    test_waveform_ring_keeps_newest_samples,
    test_cross_coupled_nand_state_survives_synchronous_feedback_sweeps,
    test_feedback_jit_is_invalidated_with_description_cache,
    test_nonconvergence_summary_is_available_without_diagnostic_sink,
    test_combinational_test_runner_uses_isolated_netlist,
    test_replay_captures_authoritative_state_and_verifies_outputs,
    test_benchmark_measures_inside_step_without_advancing_extra_steps,
    test_waveform_probe_context_menu_is_wired_to_live_simpin,
    test_waveform_ui_supports_scalar_and_bus_traces,
)


def main() -> None:
    for test in TESTS:
        test()
        print(f"PASS  {test.__name__}")
    print(f"ALL {len(TESTS)} ENGINE AUDIT REGRESSIONS PASSED")


if __name__ == "__main__":
    main()
