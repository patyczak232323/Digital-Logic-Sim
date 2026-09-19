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

    active_guard = apply.index("if (invalidate.FeedbackExecutor.RuntimeActive)")
    materialize = apply.index("invalidate.FeedbackExecutor.MaterializeState();")
    clear = apply.index("invalidate.FeedbackExecutor = null;")
    assert active_guard < materialize < clear


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
    assert "if (!enabled || Volatile.Read(ref probeCount) == 0) return;" in wave
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
    remember = nonconv.index("RememberFailure(")
    guard = nonconv.index("if (!DiagnosticsEnabled || DiagnosticSink == null) return;")
    assert remember < guard
    assert "DescribePendingWork()" in nonconv

    recorder = extract_method(sim, "static void RememberFailure(")
    assert "LastNonConvergenceDetails =" in recorder


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



def test_probes_keep_their_signal_path_out_of_collapsed_acceleration() -> None:
    sim = source("Assets/Scripts/Simulation/DeterministicSimulator.cs")
    recorder = source("Assets/Scripts/Simulation/SimulationWaveformRecorder.cs")

    collect = extract_method(sim, "static void CollectTopologyRecursive(")
    ensure = extract_method(sim, "static void EnsureTopology(")

    assert "!SimulationWaveformRecorder.ContainsProbeInSubtree(chip)" in collect
    assert "SimulationWaveformRecorder.PruneToRoot(root);" in ensure
    assert "DeterministicSimulator.InvalidateTopology();" in recorder
    assert "internal static bool ContainsProbeInSubtree" in recorder
    assert "internal static void PruneToRoot" in recorder


def test_replay_ui_requests_are_executed_on_simulation_thread() -> None:
    project = source("Assets/Scripts/Game/Project/Project.cs")
    menu = source("Assets/Scripts/Graphics/UI/Menus/SimulationDiagnosticsMenu.cs")

    sim_thread = extract_method(project, "void SimThread()")
    process = extract_method(project, "void ProcessReplayControlCommand(")

    assert "ProcessReplayControlCommand(initChip);" in sim_thread
    assert "SimulationReplayRecorder.Replay(" in process
    assert "if (!simPaused)" in process
    assert "RequestStartReplayRecording" in project
    assert "RequestStopReplayRecording" in project
    assert "RequestReplayLatest" in project

    assert '"DETERMINISTIC REPLAY"' in menu
    assert "project.RequestStartReplayRecording(5000);" in menu
    assert "project.RequestStopReplayRecording();" in menu
    assert "project.RequestReplayLatest();" in menu
    assert "project.simPaused" in menu



def test_feedback_jit_skips_stable_unchanged_input_ticks() -> None:
    jit = source("Assets/Scripts/Simulation/FeedbackJitCompiler.cs")
    evaluate = extract_method(jit, "public bool Evaluate(")

    assert "hasStableInputSnapshot" in evaluate
    assert "chip.InputPins[i].State == lastStableInputs[i]" in evaluate
    unchanged_return = evaluate.index("if (unchanged) return true;")
    sweep = evaluate.index("program.RunSweep(current, next);")
    assert unchanged_return < sweep
    assert "hasStableInputSnapshot = true;" in evaluate



def test_feedback_state_ownership_uses_runtime_active_not_ready() -> None:
    sim = source("Assets/Scripts/Simulation/DeterministicSimulator.cs")
    jit = source("Assets/Scripts/Simulation/FeedbackJitCompiler.cs")

    ensure = extract_method(sim, "static void EnsureTopology(")
    collect = extract_method(sim, "static void CollectTopologyRecursive(")
    materialize = extract_method(sim, "static void MaterializeOutermostFeedbackState(")
    synchronize = extract_method(sim, "static bool SynchronizeFeedbackExecutors(")

    assert "public bool RuntimeActive { get; private set; }" in jit
    assert "SetRuntimeActive(bool active)" in jit

    assert "MaterializeOutermostFeedbackState(topologyRoot);" in ensure
    assert "SetFeedbackRuntimeActiveRecursive(root, false);" in ensure

    assert "!needsPowerOnSettle" in collect
    assert "chip.FeedbackExecutor.SynchronizeFromChipTree();" in collect
    assert "chip.FeedbackExecutor.SetRuntimeActive(true);" in collect

    assert "executor != null && executor.RuntimeActive" in materialize

    # Activation after power-on must refresh even a previously-ready buffer.
    assert "executor != null && !executor.Disabled" in synchronize
    assert "executor.SynchronizeFromChipTree();" in synchronize



def test_all_projects_use_rewired_deterministic_engine() -> None:
    facade = source("Assets/Scripts/Game/Project/SimulationFacade.cs")
    run = extract_method(facade, "public static void RunSimulationStep(")
    ensure = extract_method(facade, "public static void EnsureInitialized(")
    apply = extract_method(facade, "public static void ApplyModifications()")

    assert "DeterministicSimulator.RunSimulationStep" in run
    assert "DLS.Simulation.Simulator.RunSimulationStep" not in run
    assert "UseLegacyCompatibilityEngine" not in facade
    assert "RequiresUpstreamTiming" not in facade
    assert "forceLegacyCompatibilityAfterEdit" not in facade
    assert "CompatibilityReason" not in facade
    assert "topologyRecoveryPending" in ensure
    assert "DeterministicSimulator.InvalidateTopology();" in apply


def test_feedback_graphs_use_feedback_jit_instead_of_legacy_routing() -> None:
    feedback = source("Assets/Scripts/Simulation/FeedbackJitCompiler.cs")
    simulator = source("Assets/Scripts/Simulation/Simulator.cs")
    deterministic = source("Assets/Scripts/Simulation/DeterministicSimulator.cs")

    compile_program = extract_method(feedback, "static CompiledFeedbackProgram CompileProgram(")
    attach = extract_method(feedback, "internal static void Attach(")

    assert "if (!ContainsCycle(indegree, outgoing)) return null;" in compile_program
    assert "simChip.FeedbackExecutor = executor;" in attach
    assert "FeedbackJitCompiler.Attach(simChip, chipDesc, library);" in simulator
    assert "chip.FeedbackExecutor != null" in deterministic


def test_android_touch_context_menu_and_safe_area() -> None:
    input_helper = source("Assets/Scripts/Seb/Helpers/Input/InputHelper.cs")
    context = source("Assets/Scripts/Graphics/UI/Menus/ContextMenu.cs")
    ui = source("Assets/Scripts/Graphics/UI/UIDrawer.cs")
    bar = source("Assets/Scripts/Graphics/UI/Menus/BottomBarUI.cs")
    diag = source("Assets/Scripts/Graphics/UI/Menus/SimulationDiagnosticsMenu.cs")

    assert "TouchLongPressTriggeredThisFrame" in input_helper
    assert "TouchLongPressSeconds = 0.45f" in input_helper
    assert "Input.touchCount != 1" in input_helper
    assert "TouchLongPressMaxMovePixels" in input_helper

    assert "TouchLongPressTriggeredThisFrame" in context
    assert "TouchUILayout.Enabled ? 4.0f : 2f" in context
    assert "Mathf.Max(menuWidth, 28)" in context

    assert "public static float SafeLeft" in ui
    assert "public static float SafeRight" in ui
    assert "public static float SafeBottom" in ui
    assert "public static float SafeTop" in ui
    assert "Screen.safeArea" in ui

    assert "TouchUILayout.BottomBarTotalHeight" in bar
    assert "TouchUILayout.SafeLeft" in bar
    assert "TouchUILayout.SafeRight" in bar
    assert "TouchUILayout.SafeBottom" in bar

    assert "TouchUILayout.SafeTop" in diag
    assert "TouchUILayout.SafeLeft" in diag
    assert "TouchUILayout.SafeRight" in diag
    assert "Long-press a pin" in diag


def test_android_touch_ui_is_separate_from_desktop_layout() -> None:
    ui = source("Assets/Scripts/Graphics/UI/UIDrawer.cs")
    bar = source("Assets/Scripts/Graphics/UI/Menus/BottomBarUI.cs")
    diag = source("Assets/Scripts/Graphics/UI/Menus/SimulationDiagnosticsMenu.cs")
    camera = source("Assets/Scripts/Game/Interaction/CameraController.cs")
    main_menu = source("Assets/Scripts/Graphics/UI/Menus/MainMenu.cs")

    assert "public static class TouchUILayout" in ui
    assert "Application.isMobilePlatform" in ui
    assert "ForceTouchLayoutForTesting" in ui
    assert "TouchUILayout.Enabled" in ui
    assert "UI.CreateUIScope()" in ui
    assert "UI.CreateFixedAspectUIScope(drawLetterbox: true)" in ui

    assert "static void DrawTouchBottomBar(" in bar
    assert "static void DrawTouchPopupMenu(" in bar
    assert '"MENU"' in bar and '"ADD"' in bar and '"LIBRARY"' in bar and '"DIAG"' in bar
    assert "project.description.Prefs_SimPaused = !project.description.Prefs_SimPaused;" in bar
    assert "ActiveBarHeight" in camera

    assert "static void DrawTouchMenu()" in diag
    assert 'TouchPageNames = { "STATUS", "PROFILER", "ANALYZER", "REPLAY" }' in diag
    assert "DrawTouchStatus(" in diag
    assert "DrawTouchProfiler(" in diag
    assert "DrawTouchAnalyzer(" in diag
    assert "DrawTouchReplay(" in diag

    assert "TouchUILayout.TouchButtonHeight" in main_menu
    assert "!TouchUILayout.Enabled" in main_menu


def test_diagnostics_menu_compacts_dynamic_text_and_balances_sections() -> None:
    menu = source("Assets/Scripts/Graphics/UI/Menus/SimulationDiagnosticsMenu.cs")

    assert "static string FitTextToWidth(" in menu
    assert "static string CompactPath(" in menu
    assert 'DrawSectionHeader(ref pos, "STABILITY"' in menu
    assert 'DrawSectionHeader(ref pos, "HOT CHIPS"' in menu
    assert 'DrawReplayControls(ref pos);' in menu
    assert 'FitTextToWidth($"{CompactPath(probe.Name, 30)}' in menu
    assert 'CompactPath(chip.Path, 38)' in menu
    assert "Time.unscaledTime >= nextStatsRefreshTime" in menu


def test_non_convergence_diagnostics_are_sticky_and_structured() -> None:
    sim = source("Assets/Scripts/Simulation/DeterministicSimulator.cs")
    menu = source("Assets/Scripts/Graphics/UI/Menus/SimulationDiagnosticsMenu.cs")

    assert "public static int LastFailureFrame" in sim
    assert "public static string LastFailureKind" in sim
    assert "public static string LastFailureChipPath" in sim
    assert "public static string LastFailureSuspects" in sim
    assert "static void RememberFailure(" in sim

    for trace in (
        "static void TraceNonConvergence(",
        "static void TracePowerOnNonConvergence(",
        "static void TraceRepeatedEvaluation(",
        "static void TraceFeedbackJitFallback(",
    ):
        body = extract_method(sim, trace)
        assert "RememberFailure(" in body

    assert "Last failure: frame" in menu
    assert "LastFailureChipPath" in menu
    assert "LastFailureSuspects" in menu


def test_native_c_is_experimental_and_downstream_of_safe_combinational_analysis() -> None:
    project_desc = source("Assets/Scripts/Description/Types/ProjectDescription.cs")
    prefs = source("Assets/Scripts/Graphics/UI/Menus/PreferencesMenu.cs")
    native = source("Assets/Scripts/Simulation/NativeCombinationalBackend.cs")
    jit = source("Assets/Scripts/Simulation/CombinationalJitCompiler.cs")

    for field in (
        "Prefs_ExperimentalNativeCMode",
        "Prefs_ExperimentalEngineDiagnostics",
        "Prefs_ExperimentalNativeCValidation",
    ):
        assert field in project_desc

    assert '"EXPERIMENTAL:"' in prefs
    assert '"Native C fast engine"' in prefs
    assert '"NAND only"' in prefs
    assert '"All supported"' in prefs
    assert '"C/JIT cross-check"' in prefs

    attach = extract_method(jit, "internal static void Attach(")
    assert "CombinationalChipCacheManager.Analyze(description, library)" in attach
    assert "if (!analysis.CanCache) return;" in attach
    assert "NativeCombinationalBackend.TryCreate(" in jit
    assert "mode == 2 || (mode == 1 && nandOnlyProgram)" in native


def test_native_c_crosscheck_keeps_jit_as_authoritative_result() -> None:
    jit = source("Assets/Scripts/Simulation/CombinationalJitCompiler.cs")
    run = extract_method(jit, "public void Run(uint[] scratch, uint[] outputs)")

    assert "NativeCombinationalBackend.ValidationEnabled" in run
    assert "Array.Copy(outputs, validationOutputs" in run
    assert "outputWriter(scratch, outputs);" in run
    assert "native-c-jit-mismatch" in run

    native_copy = run.index("Array.Copy(outputs, validationOutputs")
    jit_writer = run.index("outputWriter(scratch, outputs);")
    mismatch = run.index("native-c-jit-mismatch")
    assert native_copy < jit_writer < mismatch


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
    test_probes_keep_their_signal_path_out_of_collapsed_acceleration,
    test_replay_ui_requests_are_executed_on_simulation_thread,
    test_feedback_jit_skips_stable_unchanged_input_ticks,
    test_feedback_state_ownership_uses_runtime_active_not_ready,
    test_all_projects_use_rewired_deterministic_engine,
    test_feedback_graphs_use_feedback_jit_instead_of_legacy_routing,
    test_android_touch_context_menu_and_safe_area,
    test_android_touch_ui_is_separate_from_desktop_layout,
    test_diagnostics_menu_compacts_dynamic_text_and_balances_sections,
    test_non_convergence_diagnostics_are_sticky_and_structured,
    test_native_c_is_experimental_and_downstream_of_safe_combinational_analysis,
    test_native_c_crosscheck_keeps_jit_as_authoritative_result,
)


def main() -> None:
    for test in TESTS:
        test()
        print(f"PASS  {test.__name__}")
    print(f"ALL {len(TESTS)} ENGINE AUDIT REGRESSIONS PASSED")


if __name__ == "__main__":
    main()
