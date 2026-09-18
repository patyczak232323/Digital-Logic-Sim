using System;
using System.Collections.Generic;
using DLS.Description;
using DLS.Simulation;

namespace DLS.Game
{
	// Game-side compatibility facade. Existing project/editor code can keep using
	// Simulator while the runtime step is handled by the deterministic solver.
	public static class Simulator
	{
		static bool topologyRecoveryPending;
		static SimChip compatibilityModeRoot;
		static bool compatibilityModeLegacy;
		static bool forceLegacyCompatibilityAfterEdit;

		public static bool UsingLegacyCompatibilityEngine => compatibilityModeLegacy || forceLegacyCompatibilityAfterEdit;

		readonly struct SequentialEdgeSnapshot
		{
			public readonly SimChip Chip;
			public readonly int StateIndex;
			public readonly uint Value;

			public SequentialEdgeSnapshot(SimChip chip, int stateIndex, uint value)
			{
				Chip = chip;
				StateIndex = stateIndex;
				Value = value;
			}
		}

		public static Random rng => DLS.Simulation.Simulator.rng;

		public static int stepsPerClockTransition
		{
			get => DLS.Simulation.Simulator.stepsPerClockTransition;
			set => DLS.Simulation.Simulator.stepsPerClockTransition = value;
		}

		public static int simulationFrame
		{
			get => DLS.Simulation.Simulator.simulationFrame;
			set => DLS.Simulation.Simulator.simulationFrame = value;
		}

		public static bool needsOrderPass
		{
			get => DLS.Simulation.Simulator.needsOrderPass;
			set => DLS.Simulation.Simulator.needsOrderPass = value;
		}

		public static bool canDynamicReorderThisFrame
		{
			get => DLS.Simulation.Simulator.canDynamicReorderThisFrame;
			set => DLS.Simulation.Simulator.canDynamicReorderThisFrame = value;
		}

		public static void RunSimulationStep(SimChip rootSimChip, DevPinInstance[] inputPins, SimAudio audioState)
		{
			if (UseLegacyCompatibilityEngine(rootSimChip))
			{
				// Stateful/feedback projects must keep Sebastian's one-pass-per-tick timing
				// semantics. The deterministic fixed-point solver is faster for acyclic logic,
				// but changes observable behaviour of NAND-built latches/DFFs and therefore
				// can break existing computers that are valid in the upstream simulator.
				topologyRecoveryPending = false;
				DLS.Simulation.Simulator.RunSimulationStep(rootSimChip, inputPins, audioState);
				return;
			}
			// Debug/main-thread simulation does not call the explicit initialization pass
			// used by Project.SimThread. Only pay for the extra call when an edit actually
			// queued a topology recovery check; the steady-state hot path is unchanged.
			if (topologyRecoveryPending)
			{
				EnsureInitialized(rootSimChip, inputPins, audioState);
			}

			DeterministicSimulator.RunSimulationStep(rootSimChip, inputPins, audioState);
		}

		public static void EnsureInitialized(SimChip rootSimChip, DevPinInstance[] inputPins, SimAudio audioState)
		{
			if (UseLegacyCompatibilityEngine(rootSimChip))
			{
				// Upstream-compatible simulation has no separate fixed-point power-on phase.
				// In particular, do not advance a paused circuit merely to initialize it.
				topologyRecoveryPending = false;
				return;
			}

			Project project = Project.ActiveProject;

			// The simulation loop calls EnsureInitialized even while paused, before the
			// normal per-step SetInspectionChip call. Keep the viewed Custom Chip expanded
			// in that path too; otherwise a ready LUT/JIT block can leave its internal pin
			// states stale for the entire time the simulation remains paused.
			if (project != null && project.simPaused)
			{
				TrySynchronizeInspectionChip(project);
			}

			DeterministicSimulator.EnsureInitialized(rootSimChip, inputPins, audioState);

			if (rootSimChip == null || !topologyRecoveryPending) return;
			topologyRecoveryPending = false;

			// A structural edit normally uses a deterministic resettle so existing latch
			// and RAM state is preserved. A newly-created feedback network can, however,
			// start perfectly symmetrically (for example a cross-coupled NAND latch can
			// alternate 00 -> 11 -> 00 forever under simultaneous delta cycles). Only if
			// that first deterministic resettle actually failed, retry initialization with
			// the solver's bounded asynchronous power-on settle. Stable edited circuits
			// never take this path, so there is no extra work or randomization for them.
			if (!DeterministicSimulator.LastSettleConverged)
			{
				List<SequentialEdgeSnapshot> edgeState = CaptureSequentialEdgeState(rootSimChip);
				DeterministicSimulator.Reset();

				// Reset intentionally clears the compiled inspection/diagnostic bookkeeping,
				// but does not alter SimChip pin state or builtin InternalState. Restore the
				// useful metadata before the recovery settle.
				if (project != null)
				{
					TrySynchronizeInspectionChip(project);
					if (rootSimChip.Description != null && project.chipLibrary != null)
					{
						DeterministicSimulator.RegisterDiagnosticPaths(rootSimChip, rootSimChip.Description, project.chipLibrary);
					}
				}

				DeterministicSimulator.EnsureInitialized(rootSimChip, inputPins, audioState);

				// Recovery initialization deliberately synchronizes edge-triggered builtins so
				// power-on itself cannot manufacture an edge. This recovery, however, happens
				// between ordinary simulation steps after an edit, so retain the pre-recovery
				// previous-clock/input state. A real edge that arrived together with the edit
				// must still be seen by the following normal simulation step.
				RestoreSequentialEdgeState(edgeState);
			}
		}

		static List<SequentialEdgeSnapshot> CaptureSequentialEdgeState(SimChip root)
		{
			List<SequentialEdgeSnapshot> snapshots = new();
			CaptureRecursive(root);
			return snapshots;

			void CaptureRecursive(SimChip chip)
			{
				if (chip == null) return;

				int stateIndex = GetSequentialEdgeStateIndex(chip);
				if (stateIndex >= 0)
				{
					snapshots.Add(new SequentialEdgeSnapshot(chip, stateIndex, chip.InternalState[stateIndex]));
				}

				for (int i = 0; i < chip.SubChips.Length; i++) CaptureRecursive(chip.SubChips[i]);
			}
		}

		static void RestoreSequentialEdgeState(List<SequentialEdgeSnapshot> snapshots)
		{
			for (int i = 0; i < snapshots.Count; i++)
			{
				SequentialEdgeSnapshot snapshot = snapshots[i];
				if (snapshot.Chip != null &&
				    snapshot.StateIndex >= 0 &&
				    snapshot.StateIndex < snapshot.Chip.InternalState.Length)
				{
					snapshot.Chip.InternalState[snapshot.StateIndex] = snapshot.Value;
				}
			}
		}

		static int GetSequentialEdgeStateIndex(SimChip chip)
		{
			if (chip.InternalState.Length == 0) return -1;

			return chip.ChipType switch
			{
				ChipType.Pulse when chip.InternalState.Length > 2 => 2,
				ChipType.dev_Ram_8Bit => chip.InternalState.Length - 1,
				ChipType.DisplayRGB => chip.InternalState.Length - 1,
				ChipType.DisplayDot => chip.InternalState.Length - 1,
				_ => -1
			};
		}

		static void TrySynchronizeInspectionChip(Project project)
		{
			try
			{
				if (project.chipViewStack.Count > 0)
				{
					DeterministicSimulator.SetInspectionChip(project.ViewedChip.SimChip);
				}
			}
			catch (InvalidOperationException)
			{
				// The main thread can replace the view stack while the simulation thread
				// is sampling it. Keep the previous inspection target for this pass and
				// retry on the next loop rather than collapsing the wrong hierarchy.
			}
		}

		static bool UseLegacyCompatibilityEngine(SimChip rootSimChip)
		{
			if (rootSimChip == null) return false;

			if (!ReferenceEquals(compatibilityModeRoot, rootSimChip))
			{
				compatibilityModeRoot = rootSimChip;
				forceLegacyCompatibilityAfterEdit = false;
				compatibilityModeLegacy = RequiresUpstreamTiming(rootSimChip);
			}

			return compatibilityModeLegacy || forceLegacyCompatibilityAfterEdit;
		}

		static bool RequiresUpstreamTiming(SimChip rootSimChip)
		{
			Project project = Project.ActiveProject;
			if (rootSimChip?.Description == null || project?.chipLibrary == null)
			{
				// Unsaved/incomplete editor graphs cannot be proven acyclic and pure.
				// Compatibility is the safe default while they are being constructed.
				return true;
			}

			ChipCacheAnalysis analysis = CombinationalChipCacheManager.Analyze(rootSimChip.Description, project.chipLibrary);
			return !analysis.CanCache;
		}

		public static void SetInspectionChip(SimChip chip)
		{
			if (!UsingLegacyCompatibilityEngine) DeterministicSimulator.SetInspectionChip(chip);
		}

		public static void UpdateInPausedState()
		{
			if (UsingLegacyCompatibilityEngine) DLS.Simulation.Simulator.UpdateInPausedState();
			else DeterministicSimulator.UpdateInPausedState();
		}

		public static void UpdateKeyboardInputFromMainThread() => DLS.Simulation.Simulator.UpdateKeyboardInputFromMainThread();

		public static bool RandomBool() => DLS.Simulation.Simulator.RandomBool();

		public static SimChip BuildSimChip(ChipDescription chipDesc, ChipLibrary library)
		{
			SimChip chip = DLS.Simulation.Simulator.BuildSimChip(chipDesc, library);
			DeterministicSimulator.RegisterDiagnosticPaths(chip, chipDesc, library);
			DeterministicSimulator.InvalidateTopology();
			return chip;
		}

		public static SimChip BuildSimChip(ChipDescription chipDesc, ChipLibrary library, int subChipID, uint[] internalState)
		{
			SimChip chip = DLS.Simulation.Simulator.BuildSimChip(chipDesc, library, subChipID, internalState);
			DeterministicSimulator.RegisterDiagnosticPaths(chip, chipDesc, library);
			DeterministicSimulator.InvalidateTopology();
			return chip;
		}

		public static void AddPin(SimChip simChip, int pinID, bool isInputPin)
		{
			DLS.Simulation.Simulator.AddPin(simChip, pinID, isInputPin);
		}

		public static void RemovePin(SimChip simChip, int pinID)
		{
			DLS.Simulation.Simulator.RemovePin(simChip, pinID);
		}

		public static void AddSubChip(
			SimChip simChip,
			ChipDescription desc,
			ChipLibrary chipLibrary,
			int subChipID,
			uint[] subChipInternalData)
		{
			DLS.Simulation.Simulator.AddSubChip(simChip, desc, chipLibrary, subChipID, subChipInternalData);
		}

		public static void AddConnection(SimChip simChip, PinAddress source, PinAddress target)
		{
			DLS.Simulation.Simulator.AddConnection(simChip, source, target);
		}

		public static void RemoveConnection(SimChip simChip, PinAddress source, PinAddress target)
		{
			DLS.Simulation.Simulator.RemoveConnection(simChip, source, target);
		}

		public static void RemoveSubChip(SimChip simChip, int id)
		{
			DLS.Simulation.Simulator.RemoveSubChip(simChip, id);
		}

		public static void ApplyModifications()
		{
			bool topologyChanged = DLS.Simulation.Simulator.ApplyModifications();
			if (topologyChanged)
			{
				// SimChip.Description represents the saved graph and can temporarily lag live
				// editor modifications. After any structural edit, prefer upstream timing until
				// the root is rebuilt; this prevents a newly-created feedback loop from being
				// misclassified as pure combinational logic.
				forceLegacyCompatibilityAfterEdit = true;
				topologyRecoveryPending = true;
				DeterministicSimulator.InvalidateTopology();
			}
		}

		public static void Reset()
		{
			topologyRecoveryPending = false;
			compatibilityModeRoot = null;
			compatibilityModeLegacy = false;
			forceLegacyCompatibilityAfterEdit = false;
			DLS.Simulation.Simulator.Reset();
			DeterministicSimulator.Reset();
		}
	}
}