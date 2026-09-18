using System;
using System.Collections.Generic;
using DLS.Description;
using DLS.Simulation;

namespace DLS.Game
{
	// Game-side facade. All runtime simulation is handled by the Rewired
	// deterministic engine, including feedback/stateful custom chips.
	public static class Simulator
	{
		static bool topologyRecoveryPending;

		public static long EngineSteps { get; private set; }

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
			// Debug/main-thread simulation does not call the explicit initialization pass
			// used by Project.SimThread. Only pay the recovery cost after a structural edit.
			if (topologyRecoveryPending)
			{
				EnsureInitialized(rootSimChip, inputPins, audioState);
			}

			DeterministicSimulator.RunSimulationStep(rootSimChip, inputPins, audioState);
			EngineSteps++;
		}

		public static void EnsureInitialized(SimChip rootSimChip, DevPinInstance[] inputPins, SimAudio audioState)
		{
			Project project = Project.ActiveProject;

			// Keep the currently inspected Custom Chip expanded while paused so internal
			// pins do not become stale behind an accelerated LUT/JIT/feedback region.
			if (project != null && project.simPaused)
			{
				TrySynchronizeInspectionChip(project);
			}

			DeterministicSimulator.EnsureInitialized(rootSimChip, inputPins, audioState);

			if (rootSimChip == null || !topologyRecoveryPending) return;
			topologyRecoveryPending = false;

			// Structural edits preserve existing state with the normal deterministic
			// resettle. A newly-created symmetric feedback network can fail that settle
			// (e.g. cross-coupled NANDs starting at 00). In that case perform the bounded
			// asynchronous power-on recovery already implemented by DeterministicSimulator.
			if (!DeterministicSimulator.LastSettleConverged)
			{
				List<SequentialEdgeSnapshot> edgeState = CaptureSequentialEdgeState(rootSimChip);
				DeterministicSimulator.Reset();

				if (project != null)
				{
					TrySynchronizeInspectionChip(project);
					if (rootSimChip.Description != null && project.chipLibrary != null)
					{
						DeterministicSimulator.RegisterDiagnosticPaths(rootSimChip, rootSimChip.Description, project.chipLibrary);
					}
				}

				DeterministicSimulator.EnsureInitialized(rootSimChip, inputPins, audioState);
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
				// samples it. Keep the previous target and retry on the next loop.
			}
		}

		public static void SetInspectionChip(SimChip chip) => DeterministicSimulator.SetInspectionChip(chip);

		public static void UpdateInPausedState() => DeterministicSimulator.UpdateInPausedState();

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
				topologyRecoveryPending = true;
				DeterministicSimulator.InvalidateTopology();
			}
		}

		public static void Reset()
		{
			topologyRecoveryPending = false;
			EngineSteps = 0;
			NativeCombinationalBackend.ResetCounters();
			EngineDiagnostics.Clear();
			DLS.Simulation.Simulator.Reset();
			DeterministicSimulator.Reset();
		}
	}
}
