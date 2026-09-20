using System;
using DLS.Description;
using DLS.Game;

namespace DLS.Simulation
{
	/// <summary>
	/// Single public integration point for the Rewired simulation runtime.
	///
	/// Editor/game code should depend on this type instead of calling the
	/// deterministic solver or the legacy low-level simulator directly.
	/// Runtime implementation details remain free to change behind this API.
	/// </summary>
	public static class RewiredEngine
	{
		public readonly struct DiagnosticsSnapshot
		{
			public readonly int SimulationFrame;
			public readonly int DeltaCycles;
			public readonly int GateEvaluations;
			public readonly int SignalPropagations;
			public readonly int TargetResolutions;
			public readonly int CacheHits;
			public readonly int CacheMisses;
			public readonly int JitHits;
			public readonly int FeedbackJitHits;
			public readonly int FeedbackJitSweeps;
			public readonly int FeedbackJitFallbacks;
			public readonly bool SettleConverged;
			public readonly string NonConvergenceDetails;

			internal DiagnosticsSnapshot(
				int simulationFrame,
				int deltaCycles,
				int gateEvaluations,
				int signalPropagations,
				int targetResolutions,
				int cacheHits,
				int cacheMisses,
				int jitHits,
				int feedbackJitHits,
				int feedbackJitSweeps,
				int feedbackJitFallbacks,
				bool settleConverged,
				string nonConvergenceDetails)
			{
				SimulationFrame = simulationFrame;
				DeltaCycles = deltaCycles;
				GateEvaluations = gateEvaluations;
				SignalPropagations = signalPropagations;
				TargetResolutions = targetResolutions;
				CacheHits = cacheHits;
				CacheMisses = cacheMisses;
				JitHits = jitHits;
				FeedbackJitHits = feedbackJitHits;
				FeedbackJitSweeps = feedbackJitSweeps;
				FeedbackJitFallbacks = feedbackJitFallbacks;
				SettleConverged = settleConverged;
				NonConvergenceDetails = nonConvergenceDetails ?? string.Empty;
			}
		}

		public static int SimulationFrame => Simulator.simulationFrame;

		public static int StepsPerClockTransition
		{
			get => Simulator.stepsPerClockTransition;
			set => Simulator.stepsPerClockTransition = value;
		}

		public static DiagnosticsSnapshot Diagnostics =>
			new(
				Simulator.simulationFrame,
				DeterministicSimulator.LastDeltaCycles,
				DeterministicSimulator.LastGateEvaluations,
				DeterministicSimulator.LastSignalPropagations,
				DeterministicSimulator.LastTargetResolutions,
				DeterministicSimulator.LastCacheHits,
				DeterministicSimulator.LastCacheMisses,
				DeterministicSimulator.LastJitHits,
				DeterministicSimulator.LastFeedbackJitHits,
				DeterministicSimulator.LastFeedbackJitSweeps,
				DeterministicSimulator.LastFeedbackJitFallbacks,
				DeterministicSimulator.LastSettleConverged,
				DeterministicSimulator.LastNonConvergenceDetails);

		public static void RunStep(SimChip root, DevPinInstance[] inputPins, SimAudio audioState) =>
			DeterministicSimulator.RunSimulationStep(root, inputPins, audioState);

		public static void EnsureInitialized(SimChip root, DevPinInstance[] inputPins, SimAudio audioState) =>
			DeterministicSimulator.EnsureInitialized(root, inputPins, audioState);

		public static void SetInspectionChip(SimChip chip) =>
			DeterministicSimulator.SetInspectionChip(chip);

		public static void UpdatePaused() =>
			DeterministicSimulator.UpdateInPausedState();

		public static void UpdateKeyboardInputFromMainThread() =>
			Simulator.UpdateKeyboardInputFromMainThread();

		public static bool RandomBool() => Simulator.RandomBool();

		public static SimChip BuildChip(ChipDescription description, ChipLibrary library)
		{
			SimChip chip = Simulator.BuildSimChip(description, library);
			AfterBuild(chip, description, library);
			return chip;
		}

		public static SimChip BuildChip(
			ChipDescription description,
			ChipLibrary library,
			int subChipId,
			uint[] internalState)
		{
			SimChip chip = Simulator.BuildSimChip(description, library, subChipId, internalState);
			AfterBuild(chip, description, library);
			return chip;
		}

		public static void AddPin(SimChip chip, int pinId, bool isInput) =>
			Simulator.AddPin(chip, pinId, isInput);

		public static void RemovePin(SimChip chip, int pinId) =>
			Simulator.RemovePin(chip, pinId);

		public static void AddSubChip(
			SimChip chip,
			ChipDescription description,
			ChipLibrary library,
			int subChipId,
			uint[] internalState) =>
			Simulator.AddSubChip(chip, description, library, subChipId, internalState);

		public static void RemoveSubChip(SimChip chip, int id) =>
			Simulator.RemoveSubChip(chip, id);

		public static void AddConnection(SimChip chip, PinAddress source, PinAddress target) =>
			Simulator.AddConnection(chip, source, target);

		public static void RemoveConnection(SimChip chip, PinAddress source, PinAddress target) =>
			Simulator.RemoveConnection(chip, source, target);

		public static void ApplyPendingModifications()
		{
			if (Simulator.ApplyModifications())
			{
				DeterministicSimulator.InvalidateTopology();
			}
		}

		public static void Reset()
		{
			Simulator.Reset();
			DeterministicSimulator.Reset();
		}

		internal static void InvalidateTopology() =>
			DeterministicSimulator.InvalidateTopology();

		internal static void MaterializeStateForSnapshot(SimChip root) =>
			DeterministicSimulator.MaterializeStateForSnapshot(root);

		internal static void PrepareForSnapshotRestore(SimChip root) =>
			DeterministicSimulator.PrepareForSnapshotRestore(root);

		internal static void RestoreSimulationFrame(int frame) =>
			Simulator.simulationFrame = frame;

		static void AfterBuild(SimChip chip, ChipDescription description, ChipLibrary library)
		{
			DeterministicSimulator.RegisterDiagnosticPaths(chip, description, library);
			DeterministicSimulator.InvalidateTopology();
		}
	}
}
