using System;
using System.Collections.Generic;
using DLS.Description;
using DLS.Game;

namespace DLS.Simulation
{
	public readonly struct SimulationReplayResult
	{
		public readonly bool Success;
		public readonly int FramesReplayed;
		public readonly int DivergenceFrameIndex;
		public readonly string Message;

		public SimulationReplayResult(bool success, int framesReplayed, int divergenceFrameIndex, string message)
		{
			Success = success;
			FramesReplayed = framesReplayed;
			DivergenceFrameIndex = divergenceFrameIndex;
			Message = message ?? string.Empty;
		}
	}

	public sealed class SimulationReplayRecording
	{
		internal readonly SimulationStateSnapshot InitialState;
		internal readonly ReplayFrame[] Frames;

		public readonly bool Truncated;

		public int StartFrame => InitialState?.Frame ?? 0;
		public int FrameCount => Frames?.Length ?? 0;

		internal SimulationReplayRecording(
			SimulationStateSnapshot initialState,
			ReplayFrame[] frames,
			bool truncated)
		{
			InitialState = initialState;
			Frames = frames ?? Array.Empty<ReplayFrame>();
			Truncated = truncated;
		}
	}

	internal sealed class ReplayFrame
	{
		public readonly uint[] ExternalInputs;
		public readonly char[] HeldKeys;
		public uint[] ExpectedRootOutputs;

		public ReplayFrame(uint[] externalInputs, char[] heldKeys)
		{
			ExternalInputs = externalInputs ?? Array.Empty<uint>();
			HeldKeys = heldKeys ?? Array.Empty<char>();
		}
	}

	internal sealed class SimulationStateSnapshot
	{
		internal readonly int Frame;
		readonly ChipState[] chips;

		sealed class ChipState
		{
			public ChipType Type;
			public int Id;
			public uint[] InternalState;
			public uint[] Inputs;
			public uint[] Outputs;
		}

		SimulationStateSnapshot(int frame, ChipState[] chips)
		{
			Frame = frame;
			this.chips = chips;
		}

		public static SimulationStateSnapshot Capture(SimChip root)
		{
			if (root == null) throw new ArgumentNullException(nameof(root));

			List<ChipState> states = new();
			CaptureRecursive(root, states);
			return new SimulationStateSnapshot(RewiredEngine.SimulationFrame, states.ToArray());
		}

		static void CaptureRecursive(SimChip chip, List<ChipState> states)
		{
			ChipState state = new()
			{
				Type = chip.ChipType,
				Id = chip.ID,
				InternalState = (uint[])chip.InternalState.Clone(),
				Inputs = CapturePins(chip.InputPins),
				Outputs = CapturePins(chip.OutputPins)
			};
			states.Add(state);

			for (int i = 0; i < chip.SubChips.Length; i++)
			{
				CaptureRecursive(chip.SubChips[i], states);
			}
		}

		static uint[] CapturePins(SimPin[] pins)
		{
			uint[] states = new uint[pins.Length];
			for (int i = 0; i < pins.Length; i++) states[i] = pins[i].State;
			return states;
		}

		public bool Restore(SimChip root, out string failure)
		{
			if (root == null)
			{
				failure = "missing replay root";
				return false;
			}

			int index = 0;
			if (!RestoreRecursive(root, ref index, out failure)) return false;
			if (index != chips.Length)
			{
				failure = "replay topology contains fewer chips than the recording";
				return false;
			}

			failure = string.Empty;
			return true;
		}

		bool RestoreRecursive(SimChip chip, ref int index, out string failure)
		{
			if (index >= chips.Length)
			{
				failure = "replay topology contains more chips than the recording";
				return false;
			}

			ChipState state = chips[index++];
			if (state.Type != chip.ChipType || state.Id != chip.ID)
			{
				failure =
					$"replay topology mismatch at chip {index - 1}: expected {state.Type}[{state.Id}], got {chip.ChipType}[{chip.ID}]";
				return false;
			}

			if (state.InternalState.Length != chip.InternalState.Length ||
			    state.Inputs.Length != chip.InputPins.Length ||
			    state.Outputs.Length != chip.OutputPins.Length)
			{
				failure = $"replay state shape mismatch at {chip.ChipType}[{chip.ID}]";
				return false;
			}

			Array.Copy(state.InternalState, chip.InternalState, state.InternalState.Length);
			RestorePins(chip.InputPins, state.Inputs);
			RestorePins(chip.OutputPins, state.Outputs);

			for (int i = 0; i < chip.SubChips.Length; i++)
			{
				if (!RestoreRecursive(chip.SubChips[i], ref index, out failure)) return false;
			}

			failure = string.Empty;
			return true;
		}

		static void RestorePins(SimPin[] pins, uint[] states)
		{
			for (int i = 0; i < pins.Length; i++) pins[i].State = states[i];
		}
	}

	// Records one deterministic starting snapshot plus the external stimuli for all
	// following steps. Replaying verifies the root outputs after every recorded frame.
	// Start/stop/replay should be requested while the normal simulation is paused.
	public static class SimulationReplayRecorder
	{
		static readonly object sync = new();
		static readonly List<ReplayFrame> frames = new();

		static volatile bool enabled;
		static int maxFrames = 10000;
		static bool truncated;
		static SimulationStateSnapshot initialState;
		static ReplayFrame pendingFrame;

		public static bool Enabled => enabled;

		public static int RecordedFrameCount
		{
			get
			{
				lock (sync) return frames.Count;
			}
		}

		public static int MaxFrames
		{
			get => maxFrames;
			set => maxFrames = Math.Max(1, value);
		}

		public static void StartRecording(int frameLimit = 10000)
		{
			lock (sync)
			{
				maxFrames = Math.Max(1, frameLimit);
				frames.Clear();
				initialState = null;
				pendingFrame = null;
				truncated = false;
				enabled = true;
			}
		}

		public static SimulationReplayRecording StopRecording()
		{
			lock (sync)
			{
				enabled = false;

				// A stop request racing the middle of a simulation step must not expose
				// a frame that has inputs but no verified post-step outputs.
				if (pendingFrame != null && pendingFrame.ExpectedRootOutputs == null)
				{
					frames.Remove(pendingFrame);
				}
				pendingFrame = null;

				return new SimulationReplayRecording(initialState, frames.ToArray(), truncated);
			}
		}

		internal static void CaptureStepStart(SimChip root, DevPinInstance[] inputPins)
		{
			if (!enabled || root == null) return;

			lock (sync)
			{
				if (!enabled) return;

				if (initialState == null)
				{
					RewiredEngine.MaterializeStateForSnapshot(root);
					initialState = SimulationStateSnapshot.Capture(root);
				}

				if (frames.Count >= maxFrames)
				{
					truncated = true;
					pendingFrame = null;
					return;
				}

				uint[] externalInputs = new uint[inputPins?.Length ?? 0];
				for (int i = 0; i < externalInputs.Length; i++)
				{
					externalInputs[i] = inputPins[i]?.Pin?.PlayerInputState ?? 0;
				}

				pendingFrame = new ReplayFrame(externalInputs, SimKeyboardHelper.CaptureHeldKeys());
				frames.Add(pendingFrame);
			}
		}

		internal static void CaptureStepEnd(SimChip root)
		{
			if (root == null) return;

			lock (sync)
			{
				if (pendingFrame == null) return;

				uint[] outputs = new uint[root.OutputPins.Length];
				for (int i = 0; i < outputs.Length; i++) outputs[i] = root.OutputPins[i].State;
				pendingFrame.ExpectedRootOutputs = outputs;
				pendingFrame = null;
			}
		}

		public static SimulationReplayResult Replay(
			SimulationReplayRecording recording,
			SimChip root,
			DevPinInstance[] inputPins,
			SimAudio audioState,
			int maxFramesToReplay = int.MaxValue)
		{
			if (recording == null || recording.InitialState == null)
			{
				return new SimulationReplayResult(false, 0, -1, "recording has no initial state");
			}
			if (root == null)
			{
				return new SimulationReplayResult(false, 0, -1, "missing replay root");
			}
			if (inputPins == null) inputPins = Array.Empty<DevPinInstance>();

			int replayCount = Math.Min(recording.FrameCount, Math.Max(0, maxFramesToReplay));
			for (int i = 0; i < replayCount; i++)
			{
				if (recording.Frames[i].ExternalInputs.Length != inputPins.Length)
				{
					return new SimulationReplayResult(false, 0, i, "external input count differs from recording");
				}
			}

			lock (sync) enabled = false;
			SimKeyboardHelper.ClearReplayInputState();

			try
			{
				RewiredEngine.PrepareForSnapshotRestore(root);
				if (!recording.InitialState.Restore(root, out string restoreFailure))
				{
					return new SimulationReplayResult(false, 0, -1, restoreFailure);
				}

				RewiredEngine.RestoreSimulationFrame(recording.StartFrame);

				for (int frameIndex = 0; frameIndex < replayCount; frameIndex++)
				{
					ReplayFrame frame = recording.Frames[frameIndex];

					for (int input = 0; input < inputPins.Length; input++)
					{
						if (inputPins[input]?.Pin == null)
						{
							return new SimulationReplayResult(false, frameIndex, frameIndex, "missing external input pin");
						}
						inputPins[input].Pin.PlayerInputState = frame.ExternalInputs[input];
					}

					SimKeyboardHelper.SetReplayInputState(frame.HeldKeys);
					RewiredEngine.RunStep(root, inputPins, audioState);

					uint[] expected = frame.ExpectedRootOutputs ?? Array.Empty<uint>();
					if (expected.Length != root.OutputPins.Length)
					{
						return new SimulationReplayResult(false, frameIndex + 1, frameIndex, "root output count differs from recording");
					}

					for (int output = 0; output < expected.Length; output++)
					{
						uint actual = root.OutputPins[output].State;
						if (actual == expected[output]) continue;

						return new SimulationReplayResult(
							false,
							frameIndex + 1,
							frameIndex,
							$"diverged at replay frame {frameIndex}, output {output}: expected {expected[output]}, got {actual}");
					}
				}

				return new SimulationReplayResult(true, replayCount, -1, "replay matched recorded root outputs");
			}
			finally
			{
				SimKeyboardHelper.ClearReplayInputState();
			}
		}

		public static void Reset()
		{
			lock (sync)
			{
				enabled = false;
				frames.Clear();
				initialState = null;
				pendingFrame = null;
				truncated = false;
			}
			SimKeyboardHelper.ClearReplayInputState();
		}
	}
}
