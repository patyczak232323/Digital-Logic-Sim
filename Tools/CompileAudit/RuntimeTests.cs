using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
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
			Run("active child feedback executor materializes on deopt", TestActiveChildFeedbackHandoff);
			Run("FULL LUT 4/8/12/16/20/22-bit background pipeline + persistence", TestFullLutBackgroundPipeline);

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

		static void TestActiveChildFeedbackHandoff()
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

			SimChip nandQ = new(nandDescription, 1, null, Array.Empty<SimChip>());
			SimChip nandNotQ = new(nandDescription, 2, null, Array.Empty<SimChip>());

			ChipDescription latchDescription = new()
			{
				Name = "CHILD_LATCH",
				ChipType = ChipType.Custom,
				InputPins = new[] { Pin(10), Pin(11) },
				OutputPins = new[] { Pin(12), Pin(13) },
				SubChips = Array.Empty<SubChipDescription>(),
				Wires = Array.Empty<WireDescription>(),
				Displays = Array.Empty<DisplayDescription>()
			};

			SimChip latch = new(latchDescription, 50, null, new[] { nandQ, nandNotQ });
			latch.AddConnection(new PinAddress(10, 0), new PinAddress(1, 0));
			latch.AddConnection(new PinAddress(2, 2), new PinAddress(1, 1));
			latch.AddConnection(new PinAddress(11, 0), new PinAddress(2, 0));
			latch.AddConnection(new PinAddress(1, 2), new PinAddress(2, 1));
			latch.AddConnection(new PinAddress(1, 2), new PinAddress(12, 0));
			latch.AddConnection(new PinAddress(2, 2), new PinAddress(13, 0));

			// Seed the same valid power-on state used by the live solver.
			nandQ.OutputPins[0].State = PinState.LogicHigh;
			nandNotQ.OutputPins[0].State = PinState.LogicLow;

			FeedbackJitCompiler.Attach(latch, latchDescription, new ChipLibrary());
			Assert(latch.FeedbackExecutor != null, "child feedback executor was not compiled");

			ChipDescription rootDescription = new()
			{
				Name = "HANDOFF_ROOT",
				ChipType = ChipType.Custom,
				InputPins = new[] { Pin(100), Pin(101) },
				OutputPins = new[] { Pin(102), Pin(103) },
				SubChips = Array.Empty<SubChipDescription>(),
				Wires = Array.Empty<WireDescription>(),
				Displays = Array.Empty<DisplayDescription>()
			};

			SimChip root = new(rootDescription, -1, null, new[] { latch });
			root.AddConnection(new PinAddress(100, 0), new PinAddress(50, 10));
			root.AddConnection(new PinAddress(101, 0), new PinAddress(50, 11));
			root.AddConnection(new PinAddress(50, 12), new PinAddress(102, 0));
			root.AddConnection(new PinAddress(50, 13), new PinAddress(103, 0));

			DevPinInstance sBar = new();
			DevPinInstance rBar = new();
			sBar.Pin.Address = new PinAddress(100, 0);
			rBar.Pin.Address = new PinAddress(101, 0);
			DevPinInstance[] inputs = { sBar, rBar };

			DeterministicSimulator.Reset();
			Simulator.Reset();

			// First step performs power-on/live settle and synchronizes feedback buffers.
			sBar.Pin.PlayerInputState = PinState.LogicHigh;
			rBar.Pin.PlayerInputState = PinState.LogicHigh;
			DeterministicSimulator.RunSimulationStep(root, inputs, null);

			// Second step rebuilds topology and selects CHILD_LATCH as an accelerated
			// feedback region.
			DeterministicSimulator.RunSimulationStep(root, inputs, null);
			Assert(latch.FeedbackExecutor.RuntimeActive, "child feedback executor did not become authoritative");

			// Change the stored state while the child is collapsed. Primitive NAND pins
			// should remain materialized at the old value until deoptimization.
			sBar.Pin.PlayerInputState = PinState.LogicHigh;
			rBar.Pin.PlayerInputState = PinState.LogicLow;
			DeterministicSimulator.RunSimulationStep(root, inputs, null);
			Assert(Bit(root.OutputPins[0].State) == 0 && Bit(root.OutputPins[1].State) == 1,
				"accelerated child latch did not change state");

			Assert(Bit(nandQ.OutputPins[0].State) == 1 && Bit(nandNotQ.OutputPins[0].State) == 0,
				"primitive gate state unexpectedly changed while child was collapsed");

			// Entering the child must materialize the authoritative native state before
			// the topology expands for inspection.
			DeterministicSimulator.SetInspectionChip(nandQ);

			Assert(Bit(nandQ.OutputPins[0].State) == 0, "active feedback executor did not materialize Q on deopt");
			Assert(Bit(nandNotQ.OutputPins[0].State) == 1, "active feedback executor did not materialize /Q on deopt");

			// Next step runs the expanded live network and must preserve the same state.
			sBar.Pin.PlayerInputState = PinState.LogicHigh;
			rBar.Pin.PlayerInputState = PinState.LogicHigh;
			DeterministicSimulator.RunSimulationStep(root, inputs, null);
			Assert(Bit(root.OutputPins[0].State) == 0 && Bit(root.OutputPins[1].State) == 1,
				"deoptimized child failed to preserve materialized latch state");

			DeterministicSimulator.Reset();
			Simulator.Reset();
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

		static void TestFullLutBackgroundPipeline()
		{
			int[] bitCounts = { 4, 8, 12, 16, 20, 22 };
			string persistedPath = Path.Combine(Path.GetTempPath(), "rewired-full-lut-" + Guid.NewGuid().ToString("N") + ".dlscache");

			try
			{
				for (int i = 0; i < bitCounts.Length; i++)
				{
					int bits = bitCounts[i];
					ChipDescription description = BuildTrivialCombinationalDescription("FULL_LUT_" + bits, bits, 5000);
					ChipCacheAnalysis analysis = new(true, "cacheable", bits, 1UL << bits);
					byte[] fingerprint = GetLogicFingerprintForRuntimeTest(description);

					string path = bits == 22 ? persistedPath : string.Empty;
					CombinationalChipMemoCache cache = new(description, analysis, fingerprint, path);
					CombinationalChipCacheManager.CacheBuildSnapshot snapshot = Snapshot(description);

					cache.EnsureReadyAsync(snapshot);
					bool sawIntermediateProgress = WaitForCache(cache, TimeSpan.FromSeconds(60));

					int expectedEntries = CombinationalChipCacheManager.GetFullCacheEntryCount(bits);
					Assert(cache.Ready, $"{bits}-bit FULL LUT did not become ready: {cache.BuildFailureReason}");
					Assert(cache.EntryCount == expectedEntries,
						$"{bits}-bit FULL LUT entry count mismatch: {cache.EntryCount}/{expectedEntries}");

					if (bits >= 20)
					{
						Assert(sawIntermediateProgress,
							$"{bits}-bit FULL LUT never exposed progress > 0 before completion");
					}

					if (bits != 22) continue;

					Assert(File.Exists(persistedPath), "22-bit FULL LUT was not persisted to disk");
					Assert(cache.PersistenceMessage == "DISK VALID",
						"22-bit FULL LUT persistence did not finish successfully: " + cache.PersistenceMessage);

					// Same fingerprint must load from disk instead of rebuilding.
					CombinationalChipMemoCache loaded = new(description, analysis, fingerprint, persistedPath);
					loaded.EnsureReadyAsync(null);
					WaitForCache(loaded, TimeSpan.FromSeconds(30));

					Assert(loaded.Ready, "22-bit persisted FULL LUT did not load");
					Assert(loaded.LoadedFromDisk, "22-bit persisted FULL LUT rebuilt instead of loading from disk");
					Assert(loaded.EntryCount == expectedEntries, "22-bit loaded FULL LUT has wrong entry count");

					// Change one structural field that participates in the real topology
					// fingerprint. The old file must be rejected and rebuilt.
					ChipDescription changed = BuildTrivialCombinationalDescription("FULL_LUT_" + bits, bits, 5001);
					byte[] changedFingerprint = GetLogicFingerprintForRuntimeTest(changed);
					Assert(!CombinationalChipCacheManager.FingerprintsEqual(fingerprint, changedFingerprint),
						"topology change did not invalidate FULL LUT fingerprint");

					CombinationalChipMemoCache invalidated = new(changed, analysis, changedFingerprint, persistedPath);
					invalidated.EnsureReadyAsync(Snapshot(changed));
					bool invalidationProgress = WaitForCache(invalidated, TimeSpan.FromSeconds(60));

					Assert(invalidated.Ready, "invalidated 22-bit FULL LUT did not rebuild: " + invalidated.BuildFailureReason);
					Assert(!invalidated.LoadedFromDisk, "invalidated 22-bit FULL LUT incorrectly loaded stale disk cache");
					Assert(invalidationProgress, "invalidated 22-bit FULL LUT rebuild never exposed intermediate progress");
					Assert(invalidated.PersistenceMessage == "DISK VALID",
						"rebuilt 22-bit FULL LUT was not saved: " + invalidated.PersistenceMessage);
				}
			}
			finally
			{
				try { if (File.Exists(persistedPath)) File.Delete(persistedPath); } catch { }
				try { if (File.Exists(persistedPath + ".tmp")) File.Delete(persistedPath + ".tmp"); } catch { }
			}
		}

		static ChipDescription BuildTrivialCombinationalDescription(string name, int inputBits, int outputPinId)
		{
			List<PinDescription> inputs = new();
			int remaining = inputBits;
			int id = 100;

			while (remaining >= 8)
			{
				inputs.Add(Pin(id++, PinBitCount.Bit8));
				remaining -= 8;
			}

			if (remaining >= 4)
			{
				inputs.Add(Pin(id++, PinBitCount.Bit4));
				remaining -= 4;
			}

			while (remaining-- > 0)
			{
				inputs.Add(Pin(id++, PinBitCount.Bit1));
			}

			return new ChipDescription
			{
				Name = name,
				ChipType = ChipType.Custom,
				CacheMode = ChipCacheMode.Full,
				InputPins = inputs.ToArray(),
				OutputPins = new[] { Pin(outputPinId, PinBitCount.Bit1) },
				SubChips = Array.Empty<SubChipDescription>(),
				Wires = Array.Empty<WireDescription>(),
				Displays = Array.Empty<DisplayDescription>()
			};
		}

		static CombinationalChipCacheManager.CacheBuildSnapshot Snapshot(ChipDescription description)
		{
			Dictionary<string, ChipDescription> descriptions = new(ChipDescription.NameComparer)
			{
				[description.Name] = description
			};
			return new CombinationalChipCacheManager.CacheBuildSnapshot(description, descriptions);
		}

		static byte[] GetLogicFingerprintForRuntimeTest(ChipDescription description)
		{
			MethodInfo method = typeof(CombinationalChipCacheManager).GetMethod(
				"GetLogicFingerprint",
				BindingFlags.Static | BindingFlags.NonPublic);
			Assert(method != null, "GetLogicFingerprint reflection lookup failed");
			return (byte[])method.Invoke(null, new object[] { description, new ChipLibrary() });
		}

		static bool WaitForCache(CombinationalChipMemoCache cache, TimeSpan timeout)
		{
			Stopwatch sw = Stopwatch.StartNew();
			bool sawIntermediateProgress = false;

			while (cache.Working && sw.Elapsed < timeout)
			{
				int progress = cache.EntryCount;
				if (progress > 0 && progress < cache.TargetEntryCount)
				{
					sawIntermediateProgress = true;
				}
				Thread.Sleep(1);
			}

			Assert(!cache.Working, "FULL LUT worker timed out");
			return sawIntermediateProgress;
		}

		static PinDescription Pin(int id, PinBitCount bitCount)
		{
			return new PinDescription
			{
				ID = id,
				BitCount = bitCount
			};
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
