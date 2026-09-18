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
			Run("8-bit feedback latch bank isolation", TestEightBitLatchBank);
			Run("dormant feedback executor cannot overwrite live state", TestDormantFeedbackOwnership);

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

		static void TestDormantFeedbackOwnership()
		{
			(SimChip root, CompiledFeedbackExecutor executor, SimChip nandQ, SimChip nandNotQ) = BuildLatch();

			DevPinInstance sBar = new();
			DevPinInstance rBar = new();
			sBar.Pin.Address = new PinAddress(100, 0);
			rBar.Pin.Address = new PinAddress(101, 0);
			DevPinInstance[] inputs = { sBar, rBar };

			DeterministicSimulator.Reset();
			Simulator.Reset();

			sBar.Pin.PlayerInputState = PinState.LogicHigh;
			rBar.Pin.PlayerInputState = PinState.LogicHigh;
			DeterministicSimulator.RunSimulationStep(root, inputs, null);

			uint initialQ = Bit(nandQ.OutputPins[0].State);
			Assert(executor.Ready, "feedback executor was not synchronized after first settled tick");
			Assert(!executor.RuntimeActive, "root feedback executor must not be authoritative");

			// Force the live gate network to the opposite stable state. The root executor
			// remains ready but intentionally stale because root custom chips are expanded.
			if (initialQ == 1)
			{
				sBar.Pin.PlayerInputState = PinState.LogicHigh;
				rBar.Pin.PlayerInputState = PinState.LogicLow;
			}
			else
			{
				sBar.Pin.PlayerInputState = PinState.LogicLow;
				rBar.Pin.PlayerInputState = PinState.LogicHigh;
			}

			DeterministicSimulator.RunSimulationStep(root, inputs, null);
			uint liveQ = Bit(nandQ.OutputPins[0].State);
			uint liveNotQ = Bit(nandNotQ.OutputPins[0].State);

			Assert(liveQ != initialQ, "test failed to move live latch to opposite state");
			Assert(!executor.RuntimeActive, "dormant root executor unexpectedly became authoritative");

			// Previously SetInspectionChip materialized every Ready executor. That could
			// restore the stale initial state here and corrupt the live latch.
			DeterministicSimulator.SetInspectionChip(nandQ);

			Assert(Bit(nandQ.OutputPins[0].State) == liveQ, "dormant executor overwrote live Q during inspection handoff");
			Assert(Bit(nandNotQ.OutputPins[0].State) == liveNotQ, "dormant executor overwrote live /Q during inspection handoff");

			DeterministicSimulator.Reset();
			Simulator.Reset();
		}

		static void TestEightBitLatchBank()
		{
			const int bitCount = 8;
			ChipDescription nandDescription = new()
			{
				Name = "NAND",
				ChipType = ChipType.Nand,
				InputPins = new[] { Pin(0), Pin(1) },
				OutputPins = new[] { Pin(2) },
				SubChips = Array.Empty<SubChipDescription>(),
				Wires = Array.Empty<WireDescription>(),
				Displays = Array.Empty<DisplayDescription>()
			};

			SimChip[] cells = new SimChip[bitCount * 2];
			PinDescription[] rootInputs = new PinDescription[bitCount * 2];
			PinDescription[] rootOutputs = new PinDescription[bitCount];

			for (int bit = 0; bit < bitCount; bit++)
			{
				cells[bit * 2] = new SimChip(nandDescription, 1 + bit * 2, null, Array.Empty<SimChip>());
				cells[bit * 2 + 1] = new SimChip(nandDescription, 2 + bit * 2, null, Array.Empty<SimChip>());
				rootInputs[bit * 2] = Pin(1000 + bit * 2);
				rootInputs[bit * 2 + 1] = Pin(1001 + bit * 2);
				rootOutputs[bit] = Pin(2000 + bit);
			}

			ChipDescription rootDescription = new()
			{
				Name = "LATCH_BANK_8",
				ChipType = ChipType.Custom,
				InputPins = rootInputs,
				OutputPins = rootOutputs,
				SubChips = Array.Empty<SubChipDescription>(),
				Wires = Array.Empty<WireDescription>(),
				Displays = Array.Empty<DisplayDescription>()
			};

			SimChip root = new(rootDescription, -1, null, cells);

			for (int bit = 0; bit < bitCount; bit++)
			{
				int qId = 1 + bit * 2;
				int notQId = 2 + bit * 2;
				int sBarPin = 1000 + bit * 2;
				int rBarPin = 1001 + bit * 2;
				int outputPin = 2000 + bit;

				root.AddConnection(new PinAddress(sBarPin, 0), new PinAddress(qId, 0));
				root.AddConnection(new PinAddress(notQId, 2), new PinAddress(qId, 1));
				root.AddConnection(new PinAddress(rBarPin, 0), new PinAddress(notQId, 0));
				root.AddConnection(new PinAddress(qId, 2), new PinAddress(notQId, 1));
				root.AddConnection(new PinAddress(qId, 2), new PinAddress(outputPin, 0));

				// Seed all bits to Q=0, /Q=1.
				cells[bit * 2].OutputPins[0].State = PinState.LogicLow;
				cells[bit * 2 + 1].OutputPins[0].State = PinState.LogicHigh;
			}

			FeedbackJitCompiler.Attach(root, rootDescription, new ChipLibrary());
			Assert(root.FeedbackExecutor != null, "feedback JIT did not attach to 8-bit latch bank");
			root.FeedbackExecutor.SynchronizeFromChipTree();

			SetAllLatchControls(root, 1, 1);
			Assert(root.FeedbackExecutor.Evaluate(root, out uint[] outputs, out _), "initial latch-bank settle failed");
			Assert(ReadLatchByte(outputs) == 0x00, "initial latch-bank value was not zero");

			const byte pattern = 0xA5;
			for (int bit = 0; bit < bitCount; bit++)
			{
				bool set = ((pattern >> bit) & 1) != 0;
				root.InputPins[bit * 2].State = set ? PinState.LogicLow : PinState.LogicHigh;
				root.InputPins[bit * 2 + 1].State = set ? PinState.LogicHigh : PinState.LogicLow;
			}

			Assert(root.FeedbackExecutor.Evaluate(root, out outputs, out _), "parallel latch-bank write failed");
			Assert(ReadLatchByte(outputs) == pattern, $"parallel latch write mismatch: got 0x{ReadLatchByte(outputs):X2}");

			SetAllLatchControls(root, 1, 1);
			Assert(root.FeedbackExecutor.Evaluate(root, out outputs, out _), "latch-bank release failed");
			Assert(ReadLatchByte(outputs) == pattern, "latch-bank did not hold written byte");

			// Change only bit 3 from 0 to 1. All other seven bits must remain untouched.
			root.InputPins[3 * 2].State = PinState.LogicLow;
			root.InputPins[3 * 2 + 1].State = PinState.LogicHigh;
			Assert(root.FeedbackExecutor.Evaluate(root, out outputs, out _), "single-bit latch update failed");
			Assert(ReadLatchByte(outputs) == (pattern | 0x08), "single-bit update leaked into another latch cell");

			SetAllLatchControls(root, 1, 1);
			Assert(root.FeedbackExecutor.Evaluate(root, out outputs, out _), "single-bit release failed");
			Assert(ReadLatchByte(outputs) == (pattern | 0x08), "single-bit state was not retained");

			Assert(root.FeedbackExecutor.Evaluate(root, out outputs, out int idleSweeps), "stable latch-bank evaluation failed");
			Assert(idleSweeps == 0, $"stable latch bank expected 0 sweeps, got {idleSweeps}");
		}

		static void SetAllLatchControls(SimChip root, uint sBar, uint rBar)
		{
			for (int bit = 0; bit < 8; bit++)
			{
				root.InputPins[bit * 2].State = sBar;
				root.InputPins[bit * 2 + 1].State = rBar;
			}
		}

		static byte ReadLatchByte(uint[] outputs)
		{
			byte value = 0;
			for (int bit = 0; bit < 8; bit++)
			{
				if (Bit(outputs[bit]) != 0) value |= (byte)(1 << bit);
			}
			return value;
		}

		static void TestReplayRoundTrip()
		{
			ChipDescription nandDescription = new()
			{
				Name = "NAND",
				ChipType = ChipType.Nand,
				InputPins = new[] { Pin(0), Pin(1) },
				OutputPins = new[] { Pin(2) },
				SubChips = Array.Empty<SubChipDescription>(),
				Wires = Array.Empty<WireDescription>(),
				Displays = Array.Empty<DisplayDescription>()
			};

			SimChip nand = new(nandDescription, 1, null, Array.Empty<SimChip>());

			ChipDescription rootDescription = new()
			{
				Name = "REPLAY_ROOT",
				ChipType = ChipType.Custom,
				InputPins = new[] { Pin(300), Pin(301) },
				OutputPins = new[] { Pin(302) },
				SubChips = Array.Empty<SubChipDescription>(),
				Wires = Array.Empty<WireDescription>(),
				Displays = Array.Empty<DisplayDescription>()
			};

			SimChip root = new(rootDescription, -1, null, new[] { nand });
			root.AddConnection(new PinAddress(300, 0), new PinAddress(1, 0));
			root.AddConnection(new PinAddress(301, 0), new PinAddress(1, 1));
			root.AddConnection(new PinAddress(1, 2), new PinAddress(302, 0));

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
