using System;
using DLS.Description;
using DLS.Game;

namespace DLS.Simulation
{
	internal static class RuntimeTests
	{
		static int failures;

		static void Main()
		{
			Run("feedback NAND latch", TestFeedbackNandLatch);
			Run("feedback unchanged-input zero sweep", TestFeedbackZeroSweep);
			Run("feedback state materialization", TestFeedbackMaterialization);
			Run("waveform transition compression", TestWaveformTransitionCompression);
			Run("deterministic replay round-trip", TestReplayRoundTrip);

			if (failures != 0)
			{
				throw new Exception($"{failures} runtime test(s) failed");
			}

			Console.WriteLine("ALL SIMULATION RUNTIME TESTS PASSED");
		}

		static void Run(string name, Action test)
		{
			try
			{
				test();
				Console.WriteLine("PASS  " + name);
			}
			catch (Exception ex)
			{
				failures++;
				Console.WriteLine("FAIL  " + name + ": " + ex.Message);
			}
		}

		static void TestFeedbackNandLatch()
		{
			(SimChip root, CompiledFeedbackExecutor executor, SimChip nandQ, SimChip nandNotQ) = BuildLatch();

			SetRootInputs(root, 1, 1);
			Assert(executor.Evaluate(root, out uint[] outputs, out int initialSweeps), "initial latch state failed to converge");
			Assert(initialSweeps > 0, "first feedback evaluation unexpectedly skipped all sweeps");
			Assert(Bit(outputs[0]) == 1 && Bit(outputs[1]) == 0, "unexpected initial latch state");

			// Active-low reset.
			SetRootInputs(root, 1, 0);
			Assert(executor.Evaluate(root, out outputs, out int resetSweeps), "reset transition failed to converge");
			Assert(resetSweeps > 0, "reset transition did not execute feedback sweeps");
			Assert(Bit(outputs[0]) == 0 && Bit(outputs[1]) == 1, "reset transition produced wrong state");

			// Release reset: state must remain stored.
			SetRootInputs(root, 1, 1);
			Assert(executor.Evaluate(root, out outputs, out int releaseSweeps), "reset release failed to converge");
			Assert(releaseSweeps > 0, "reset release did not execute feedback sweeps");
			Assert(Bit(outputs[0]) == 0 && Bit(outputs[1]) == 1, "latch did not retain reset state");

			_ = nandQ;
			_ = nandNotQ;
		}

		static void TestFeedbackZeroSweep()
		{
			(SimChip root, CompiledFeedbackExecutor executor, _, _) = BuildLatch();

			SetRootInputs(root, 1, 1);
			Assert(executor.Evaluate(root, out uint[] outputs, out _), "initial evaluation failed");
			Assert(Bit(outputs[0]) == 1 && Bit(outputs[1]) == 0, "unexpected initial state");

			Assert(executor.Evaluate(root, out outputs, out int sweeps), "unchanged-input evaluation failed");
			Assert(sweeps == 0, $"expected zero sweeps for unchanged stable inputs, got {sweeps}");
			Assert(executor.LastSweepCount == 0, "LastSweepCount did not report zero-sweep fast path");
			Assert(Bit(outputs[0]) == 1 && Bit(outputs[1]) == 0, "zero-sweep fast path changed outputs");
		}

		static void TestFeedbackMaterialization()
		{
			(SimChip root, CompiledFeedbackExecutor executor, SimChip nandQ, SimChip nandNotQ) = BuildLatch();

			SetRootInputs(root, 1, 0);
			Assert(executor.Evaluate(root, out uint[] outputs, out _), "reset evaluation failed");
			Assert(Bit(outputs[0]) == 0 && Bit(outputs[1]) == 1, "reset state mismatch");

			executor.MaterializeState();
			Assert(Bit(nandQ.OutputPins[0].State) == 0, "Q primitive output was not materialized");
			Assert(Bit(nandNotQ.OutputPins[0].State) == 1, "/Q primitive output was not materialized");
		}

		static void TestReplayRoundTrip()
		{
			ChipDescription description = new()
			{
				Name = "NAND_REPLAY",
				ChipType = ChipType.Nand,
				InputPins = new[] { Pin(300), Pin(301) },
				OutputPins = new[] { Pin(302) },
				SubChips = Array.Empty<SubChipDescription>(),
				Wires = Array.Empty<WireDescription>(),
				Displays = Array.Empty<DisplayDescription>()
			};

			SimChip root = new(description, -1, null, Array.Empty<SimChip>());
			DevPinInstance inputA = new();
			DevPinInstance inputB = new();
			inputA.Pin.Address = new PinAddress(300, 0);
			inputB.Pin.Address = new PinAddress(301, 0);
			DevPinInstance[] inputs = { inputA, inputB };

			DeterministicSimulator.Reset();
			Simulator.Reset();
			SimulationReplayRecorder.StartRecording(8);

			inputA.Pin.PlayerInputState = PinState.LogicHigh;
			inputB.Pin.PlayerInputState = PinState.LogicHigh;
			DeterministicSimulator.RunSimulationStep(root, inputs, null);
			Assert(Bit(root.OutputPins[0].State) == 0, "NAND replay frame 0 output mismatch");

			inputB.Pin.PlayerInputState = PinState.LogicLow;
			DeterministicSimulator.RunSimulationStep(root, inputs, null);
			Assert(Bit(root.OutputPins[0].State) == 1, "NAND replay frame 1 output mismatch");

			SimulationReplayRecording recording = SimulationReplayRecorder.StopRecording();
			Assert(recording.FrameCount == 2, $"expected 2 replay frames, got {recording.FrameCount}");

			// Disturb the live state before restoring the recording.
			inputA.Pin.PlayerInputState = PinState.LogicLow;
			inputB.Pin.PlayerInputState = PinState.LogicLow;
			DeterministicSimulator.RunSimulationStep(root, inputs, null);

			SimulationReplayResult result = SimulationReplayRecorder.Replay(
				recording,
				root,
				inputs,
				null);

			Assert(result.Success, "replay round-trip diverged: " + result.Message);
			Assert(result.FramesReplayed == 2, $"expected 2 replayed frames, got {result.FramesReplayed}");
			Assert(Bit(root.OutputPins[0].State) == 1, "replay did not finish at recorded final NAND state");

			DeterministicSimulator.Reset();
			Simulator.Reset();
		}

		static void TestWaveformTransitionCompression()
		{
			ChipDescription description = new()
			{
				Name = "PROBE_TEST",
				ChipType = ChipType.Custom,
				InputPins = new[] { Pin(200) },
				OutputPins = Array.Empty<PinDescription>(),
				SubChips = Array.Empty<SubChipDescription>(),
				Wires = Array.Empty<WireDescription>(),
				Displays = Array.Empty<DisplayDescription>()
			};

			SimChip chip = new(description, -1, null, Array.Empty<SimChip>());
			SimPin pin = chip.InputPins[0];

			SimulationWaveformRecorder.ClearAll();
			SimulationWaveformRecorder.Enabled = true;
			SimulationWaveformRecorder.AddProbe("probe", pin);

			pin.State = PinState.LogicLow;
			for (int frame = 1; frame <= 100; frame++) SimulationWaveformRecorder.Capture(frame);

			pin.State = PinState.LogicHigh;
			for (int frame = 101; frame <= 200; frame++) SimulationWaveformRecorder.Capture(frame);

			WaveformSample[] samples = SimulationWaveformRecorder.GetSamples(
				SimulationWaveformRecorder.GetProbes()[0].Id);

			Assert(samples.Length == 2, $"expected 2 waveform transitions, got {samples.Length}");
			Assert(samples[0].Frame == 1 && Bit(samples[0].State) == 0, "first transition sample mismatch");
			Assert(samples[1].Frame == 101 && Bit(samples[1].State) == 1, "second transition sample mismatch");

			SimulationWaveformRecorder.Enabled = false;
			SimulationWaveformRecorder.ClearAll();
		}

		static (SimChip root, CompiledFeedbackExecutor executor, SimChip nandQ, SimChip nandNotQ) BuildLatch()
		{
			ChipDescription nandDescription = new()
			{
				Name = "NAND",
				ChipType = ChipType.Nand,
				InputPins = new[]
				{
					Pin(0),
					Pin(1)
				},
				OutputPins = new[]
				{
					Pin(2)
				},
				SubChips = Array.Empty<SubChipDescription>(),
				Wires = Array.Empty<WireDescription>(),
				Displays = Array.Empty<DisplayDescription>()
			};

			SimChip nandQ = new(nandDescription, 1, null, Array.Empty<SimChip>());
			SimChip nandNotQ = new(nandDescription, 2, null, Array.Empty<SimChip>());

			ChipDescription rootDescription = new()
			{
				Name = "SR_LATCH",
				ChipType = ChipType.Custom,
				InputPins = new[]
				{
					Pin(100),
					Pin(101)
				},
				OutputPins = new[]
				{
					Pin(102),
					Pin(103)
				},
				SubChips = Array.Empty<SubChipDescription>(),
				Wires = Array.Empty<WireDescription>(),
				Displays = Array.Empty<DisplayDescription>()
			};

			SimChip root = new(rootDescription, -1, null, new[] { nandQ, nandNotQ });

			// Sbar -> Q NAND input 0.
			root.AddConnection(new PinAddress(100, 0), new PinAddress(1, 0));
			// /Q feedback -> Q NAND input 1.
			root.AddConnection(new PinAddress(2, 2), new PinAddress(1, 1));
			// Rbar -> /Q NAND input 0.
			root.AddConnection(new PinAddress(101, 0), new PinAddress(2, 0));
			// Q feedback -> /Q NAND input 1.
			root.AddConnection(new PinAddress(1, 2), new PinAddress(2, 1));
			// Latch outputs.
			root.AddConnection(new PinAddress(1, 2), new PinAddress(102, 0));
			root.AddConnection(new PinAddress(2, 2), new PinAddress(103, 0));

			// Seed a valid fixed point, exactly as the deterministic power-on solver does
			// before feedback JIT is activated.
			nandQ.OutputPins[0].State = PinState.LogicHigh;
			nandNotQ.OutputPins[0].State = PinState.LogicLow;

			FeedbackJitCompiler.Attach(root, rootDescription, new ChipLibrary());
			Assert(root.FeedbackExecutor != null, "feedback JIT compiler did not attach to cyclic NAND latch");

			root.FeedbackExecutor.SynchronizeFromChipTree();
			Assert(root.FeedbackExecutor.Ready, "feedback executor did not become ready");

			return (root, root.FeedbackExecutor, nandQ, nandNotQ);
		}

		static PinDescription Pin(int id)
		{
			return new PinDescription
			{
				ID = id,
				BitCount = PinBitCount.Bit1
			};
		}

		static void SetRootInputs(SimChip root, uint sBar, uint rBar)
		{
			root.InputPins[0].State = sBar;
			root.InputPins[1].State = rBar;
		}

		static uint Bit(uint state) => PinState.GetBitStates(state) & 1u;

		static void Assert(bool condition, string message)
		{
			if (!condition) throw new InvalidOperationException(message);
		}
	}
}
