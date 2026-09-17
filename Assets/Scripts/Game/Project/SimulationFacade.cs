using System;
using DLS.Description;
using DLS.Simulation;

namespace DLS.Game
{
	// Game-side compatibility facade. Existing project/editor code can keep using
	// Simulator while the runtime step is handled by the deterministic solver.
	public static class Simulator
	{
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
			DeterministicSimulator.RunSimulationStep(rootSimChip, inputPins, audioState);
		}

		public static void EnsureInitialized(SimChip rootSimChip, DevPinInstance[] inputPins, SimAudio audioState)
		{
			// The simulation loop calls EnsureInitialized even while paused, before the
			// normal per-step SetInspectionChip call. Keep the viewed Custom Chip expanded
			// in that path too; otherwise a ready LUT/JIT block can leave its internal pin
			// states stale for the entire time the simulation remains paused.
			Project project = Project.ActiveProject;
			if (project != null && project.simPaused)
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

			DeterministicSimulator.EnsureInitialized(rootSimChip, inputPins, audioState);
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
			if (topologyChanged) DeterministicSimulator.InvalidateTopology();
		}

		public static void Reset()
		{
			DLS.Simulation.Simulator.Reset();
			DeterministicSimulator.Reset();
		}
	}
}