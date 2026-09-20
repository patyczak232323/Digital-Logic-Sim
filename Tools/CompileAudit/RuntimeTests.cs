using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using DLS.Description;
using DLS.Game;
using DLS.RHDL;

namespace DLS.Simulation
{
	internal static class RuntimeTests
	{
		static int failures;

		static void Main()
		{
			Run("RHDL HalfAdder compilation", TestRhdlHalfAdderCompilation);
			Run("feedback NAND latch", TestFeedbackNandLatch);
			Run("feedback unchanged-input zero sweep", TestFeedbackZeroSweep);
			Run("feedback state materialization", TestFeedbackMaterialization);
			Run("waveform transition compression", TestWaveformTransitionCompression);
			Run("deterministic replay round-trip", TestReplayRoundTrip);
			Run("legacy parity: DisplayDot first-high clock edge", TestDisplayDotFirstHighClockParity);
			Run("legacy parity: seven-segment visual sink propagation", TestSevenSegmentVisualSinkParity);
			Run("cold imported nested custom chip uses live first tick", TestColdImportedNestedCustomStartup);
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


		static void TestRhdlHalfAdderCompilation()
		{
			PinDescription InPin(string name, int id) =>
				new(name, id, new UnityEngine.Vector2(), PinBitCount.Bit1, PinColour.Red, PinValueDisplayMode.Off);

			ChipDescription nand = new()
			{
				Name = "NAND",
				ChipType = ChipType.Nand,
				InputPins = new[] { InPin("IN B", 0), InPin("IN A", 1) },
				OutputPins = new[] { InPin("OUT", 2) },
				SubChips = Array.Empty<SubChipDescription>(),
				Wires = Array.Empty<WireDescription>(),
				Displays = Array.Empty<DisplayDescription>()
			};

			ChipLibrary library = new(nand);
			string source =
@"chip HalfAdder {
  input a
  input b
  output sum
  output carry
  NAND n1
  NAND n2
  NAND n3
  NAND n4
  NAND n5
  connect a -> n1.IN_A
  connect b -> n1.IN_B
  connect a -> n2.IN_A
  connect n1.OUT -> n2.IN_B
  connect b -> n3.IN_A
  connect n1.OUT -> n3.IN_B
  connect n2.OUT -> n4.IN_A
  connect n3.OUT -> n4.IN_B
  connect n4.OUT -> sum
  connect n1.OUT -> n5.IN_A
  connect n1.OUT -> n5.IN_B
  connect n5.OUT -> carry
}";

			RhdlCompileResult result = RhdlCompiler.Compile(source, library);
			Assert(result.Success, "RHDL compile failed: " + string.Join(" | ", result.Diagnostics.Select(d => d.ToString())));
			Assert(result.Description != null, "RHDL returned no chip description");
			Assert(result.Description.Name == "HalfAdder", "RHDL chip name mismatch");
			Assert(result.Description.InputPins.Length == 2, "RHDL input count mismatch");
			Assert(result.Description.OutputPins.Length == 2, "RHDL output count mismatch");
			Assert(result.Description.SubChips.Length == 5, "RHDL NAND count mismatch");
			Assert(result.Description.Wires.Length == 12, "RHDL wire count mismatch");
			Assert(result.Description.SubChips.Select(s => s.ID).Distinct().Count() == 5, "RHDL subchip IDs are not unique");

			Simulator.Reset();
			DeterministicSimulator.Reset();
			SimChip root = Simulator.BuildSimChip(result.Description, library);
			DevPinInstance[] inputs =
			{
				new DevPinInstance(),
				new DevPinInstance()
			};
			inputs[0].Pin.Address = new PinAddress(result.Description.InputPins[0].ID, 0);
			inputs[1].Pin.Address = new PinAddress(result.Description.InputPins[1].ID, 0);

			for (uint a = 0; a <= 1; a++)
			{
				for (uint b = 0; b <= 1; b++)
				{
					inputs[0].Pin.PlayerInputState = a;
					inputs[1].Pin.PlayerInputState = b;
					DeterministicSimulator.RunSimulationStep(root, inputs, new SimAudio());

					uint sum = Bit(root.OutputPins[0].State);
					uint carry = Bit(root.OutputPins[1].State);
					Assert(sum == (a ^ b), $"RHDL HalfAdder sum mismatch for {a}{b}: {sum}");
					Assert(carry == (a & b), $"RHDL HalfAdder carry mismatch for {a}{b}: {carry}");
				}
			}

			DeterministicSimulator.Reset();
			Simulator.Reset();
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

		static void TestColdImportedNestedCustomStartup()
		{
			ChipDescription nand = new()
			{
				Name = "NAND_COLD_IMPORT",
				ChipType = ChipType.Nand,
				InputPins = new[] { Pin(0), Pin(1) },
				OutputPins = new[] { Pin(2) },
				SubChips = Array.Empty<SubChipDescription>(),
				Wires = Array.Empty<WireDescription>(),
				Displays = Array.Empty<DisplayDescription>()
			};

			ChipDescription leaf = new()
			{
				Name = "IMPORTED_LEAF",
				ChipType = ChipType.Custom,
				CacheMode = ChipCacheMode.Normal,
				InputPins = new[] { Pin(10), Pin(11) },
				OutputPins = new[] { Pin(12) },
				SubChips = new[]
				{
					new SubChipDescription { Name = nand.Name, ID = 1 },
					new SubChipDescription { Name = nand.Name, ID = 2 },
					new SubChipDescription { Name = nand.Name, ID = 3 }
				},
				Wires = new[]
				{
					Wire(10, 0, 1, 0),
					Wire(11, 0, 1, 1),
					Wire(1, 2, 2, 0),
					Wire(1, 2, 2, 1),
					Wire(2, 2, 3, 0),
					Wire(2, 2, 3, 1),
					Wire(3, 2, 12, 0)
				},
				Displays = Array.Empty<DisplayDescription>()
			};

			ChipDescription importedWrapper = new()
			{
				Name = "IMPORTED_WRAPPER_NEVER_OPENED",
				ChipType = ChipType.Custom,
				CacheMode = ChipCacheMode.Normal,
				InputPins = new[] { Pin(20), Pin(21) },
				OutputPins = new[] { Pin(22) },
				SubChips = new[]
				{
					new SubChipDescription { Name = leaf.Name, ID = 50 }
				},
				Wires = new[]
				{
					Wire(20, 0, 50, 10),
					Wire(21, 0, 50, 11),
					Wire(50, 12, 22, 0)
				},
				Displays = Array.Empty<DisplayDescription>()
			};

			ChipDescription rootDescription = new()
			{
				Name = "COLD_IMPORT_ROOT",
				ChipType = ChipType.Custom,
				CacheMode = ChipCacheMode.Normal,
				InputPins = new[] { Pin(100), Pin(101) },
				OutputPins = new[] { Pin(102) },
				SubChips = new[]
				{
					new SubChipDescription { Name = importedWrapper.Name, ID = 70 }
				},
				Wires = new[]
				{
					Wire(100, 0, 70, 20),
					Wire(101, 0, 70, 21),
					Wire(70, 22, 102, 0)
				},
				Displays = Array.Empty<DisplayDescription>()
			};

			ChipLibrary library = new(nand, leaf, importedWrapper, rootDescription);

			// Build directly from serialized/library descriptions. No editor/view-mode
			// load happens first: this models an imported nested chip that has never
			// previously been opened in the destination project.
			SimChip root = Simulator.BuildSimChip(rootDescription, library);
			SimChip wrapper = root.SubChips[0];
			SimChip nestedLeaf = wrapper.SubChips[0];

			Assert(wrapper.CompiledExecutor != null, "imported wrapper did not receive combinational JIT");
			Assert(nestedLeaf.CompiledExecutor != null, "nested imported leaf did not receive combinational JIT");

			DevPinInstance inputA = new();
			DevPinInstance inputB = new();
			inputA.Pin.Address = new PinAddress(100, 0);
			inputB.Pin.Address = new PinAddress(101, 0);
			DevPinInstance[] inputs = { inputA, inputB };

			Simulator.Reset();
			DeterministicSimulator.Reset();

			inputA.Pin.PlayerInputState = PinState.LogicHigh;
			inputB.Pin.PlayerInputState = PinState.LogicHigh;
			DeterministicSimulator.RunSimulationStep(root, inputs, null);

			Assert(Bit(root.OutputPins[0].State) == 0,
				"cold imported nested chip returned wrong value on first live tick");
			Assert(DeterministicSimulator.LastJitHits == 0,
				"cold imported nested hierarchy was collapsed before its mandatory live compatibility tick");

			// The next tick may use acceleration; output must remain identical to the
			// live hierarchy and JIT should now actually be selected.
			inputB.Pin.PlayerInputState = PinState.LogicLow;
			DeterministicSimulator.RunSimulationStep(root, inputs, null);

			Assert(Bit(root.OutputPins[0].State) == 1,
				"imported nested chip changed behavior after acceleration activated");
			Assert(DeterministicSimulator.LastJitHits > 0,
				"nested custom hierarchy did not re-enable JIT after cold live tick");

			DeterministicSimulator.Reset();
			Simulator.Reset();
		}

		static WireDescription Wire(int sourceOwner, int sourcePin, int targetOwner, int targetPin)
		{
			return new WireDescription
			{
				SourcePinAddress = new PinAddress(sourceOwner, sourcePin),
				TargetPinAddress = new PinAddress(targetOwner, targetPin),
				Points = Array.Empty<UnityEngine.Vector2>()
			};
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

		static void TestSevenSegmentVisualSinkParity()
		{
			(SimChip Root, SimChip Display, DevPinInstance[] Inputs) legacy = BuildSevenSegmentHarness();
			uint[] pattern = { 1, 0, 1, 1, 0, 0, 1 };
			for (int i = 0; i < pattern.Length; i++) legacy.Inputs[i].Pin.PlayerInputState = pattern[i];

			Simulator.Reset();
			Simulator.RunSimulationStep(legacy.Root, legacy.Inputs, new SimAudio());

			uint[] legacyPins = new uint[legacy.Display.InputPins.Length];
			for (int i = 0; i < legacyPins.Length; i++) legacyPins[i] = legacy.Display.InputPins[i].State;

			(SimChip Root, SimChip Display, DevPinInstance[] Inputs) rewired = BuildSevenSegmentHarness();
			for (int i = 0; i < pattern.Length; i++) rewired.Inputs[i].Pin.PlayerInputState = pattern[i];

			Simulator.Reset();
			DeterministicSimulator.Reset();
			DeterministicSimulator.RunSimulationStep(rewired.Root, rewired.Inputs, new SimAudio());

			for (int i = 0; i < legacyPins.Length; i++)
			{
				Assert(rewired.Display.InputPins[i].State == legacyPins[i],
					$"7-segment visual input {i} mismatch: rewired={rewired.Display.InputPins[i].State} legacy={legacyPins[i]}");
			}

			ChipCacheAnalysis analysis = CombinationalChipCacheManager.Analyze(
				new ChipDescription
				{
					Name = "7SEG_CACHE_GUARD",
					ChipType = ChipType.SevenSegmentDisplay,
					InputPins = Array.Empty<PinDescription>(),
					OutputPins = Array.Empty<PinDescription>(),
					SubChips = Array.Empty<SubChipDescription>(),
					Wires = Array.Empty<WireDescription>(),
					Displays = Array.Empty<DisplayDescription>()
				},
				new ChipLibrary());
			Assert(!analysis.CanCache, "seven-segment display must never be treated as a pure cacheable primitive");

			ChipCacheAnalysis surfacedCustom = CombinationalChipCacheManager.Analyze(
				new ChipDescription
				{
					Name = "CUSTOM_DISPLAY_SURFACE_GUARD",
					ChipType = ChipType.Custom,
					InputPins = Array.Empty<PinDescription>(),
					OutputPins = Array.Empty<PinDescription>(),
					SubChips = Array.Empty<SubChipDescription>(),
					Wires = Array.Empty<WireDescription>(),
					Displays = new[] { new DisplayDescription() }
				},
				new ChipLibrary());
			Assert(!surfacedCustom.CanCache,
				"custom chip exposing a display surface must stay live instead of collapsing to LUT/JIT");

			DeterministicSimulator.Reset();
			Simulator.Reset();
		}

		static (SimChip Root, SimChip Display, DevPinInstance[] Inputs) BuildSevenSegmentHarness()
		{
			PinDescription[] displayInputs = new PinDescription[7];
			PinDescription[] rootInputs = new PinDescription[7];
			for (int i = 0; i < 7; i++)
			{
				displayInputs[i] = Pin(i);
				rootInputs[i] = Pin(200 + i);
			}

			ChipDescription displayDescription = new()
			{
				Name = "SEVEN_SEGMENT_COMPAT",
				ChipType = ChipType.SevenSegmentDisplay,
				InputPins = displayInputs,
				OutputPins = Array.Empty<PinDescription>(),
				SubChips = Array.Empty<SubChipDescription>(),
				Wires = Array.Empty<WireDescription>(),
				Displays = Array.Empty<DisplayDescription>()
			};

			SimChip display = new(displayDescription, 2, null, Array.Empty<SimChip>());

			ChipDescription rootDescription = new()
			{
				Name = "SEVEN_SEGMENT_COMPAT_ROOT",
				ChipType = ChipType.Custom,
				InputPins = rootInputs,
				OutputPins = Array.Empty<PinDescription>(),
				SubChips = Array.Empty<SubChipDescription>(),
				Wires = Array.Empty<WireDescription>(),
				Displays = Array.Empty<DisplayDescription>()
			};

			SimChip root = new(rootDescription, -1, null, new[] { display });
			DevPinInstance[] inputs = new DevPinInstance[7];

			for (int i = 0; i < 7; i++)
			{
				root.AddConnection(new PinAddress(200 + i, 0), new PinAddress(2, i));
				inputs[i] = new DevPinInstance();
				inputs[i].Pin.Address = new PinAddress(200 + i, 0);
			}

			return (root, display, inputs);
		}

		static void TestDisplayDotFirstHighClockParity()
		{
			(SimChip Root, SimChip Display, DevPinInstance[] Inputs) legacy = BuildDisplayDotHarness();
			SetDisplayDotInputs(legacy.Inputs, address: 3, pixel: 1, reset: 0, write: 1, refresh: 1, clock: 1);

			// Legacy/original engine: a high clock on the first normal simulation step
			// is a rising edge because the persisted/default previous clock state is low.
			Simulator.Reset();
			Simulator.RunSimulationStep(legacy.Root, legacy.Inputs, new SimAudio());

			uint legacyFront = legacy.Display.InternalState[3];
			uint legacyBack = legacy.Display.InternalState[256 + 3];
			uint legacyOutput = legacy.Display.OutputPins[0].State;
			uint legacyClockMemory = legacy.Display.InternalState[^1];

			(SimChip Root, SimChip Display, DevPinInstance[] Inputs) rewired = BuildDisplayDotHarness();
			SetDisplayDotInputs(rewired.Inputs, address: 3, pixel: 1, reset: 0, write: 1, refresh: 1, clock: 1);

			Simulator.Reset();
			DeterministicSimulator.Reset();
			DeterministicSimulator.RunSimulationStep(rewired.Root, rewired.Inputs, new SimAudio());

			Assert(rewired.Display.InternalState[3] == legacyFront,
				$"DisplayDot front-buffer startup mismatch: rewired={rewired.Display.InternalState[3]} legacy={legacyFront}");
			Assert(rewired.Display.InternalState[256 + 3] == legacyBack,
				$"DisplayDot back-buffer startup mismatch: rewired={rewired.Display.InternalState[256 + 3]} legacy={legacyBack}");
			Assert(rewired.Display.OutputPins[0].State == legacyOutput,
				$"DisplayDot output startup mismatch: rewired={rewired.Display.OutputPins[0].State} legacy={legacyOutput}");
			Assert(rewired.Display.InternalState[^1] == legacyClockMemory,
				$"DisplayDot clock-memory startup mismatch: rewired={rewired.Display.InternalState[^1]} legacy={legacyClockMemory}");

			Assert(legacyFront == 1 && legacyBack == 1 && Bit(legacyOutput) == 1,
				"legacy DisplayDot did not write+refresh on first high clock edge as expected");

			DeterministicSimulator.Reset();
			Simulator.Reset();
		}

		static (SimChip Root, SimChip Display, DevPinInstance[] Inputs) BuildDisplayDotHarness()
		{
			ChipDescription displayDescription = new()
			{
				Name = "DISPLAY_DOT_COMPAT",
				ChipType = ChipType.DisplayDot,
				InputPins = new[]
				{
					Pin(0, PinBitCount.Bit8),
					Pin(1),
					Pin(2),
					Pin(3),
					Pin(4),
					Pin(5)
				},
				OutputPins = new[] { Pin(6) },
				SubChips = Array.Empty<SubChipDescription>(),
				Wires = Array.Empty<WireDescription>(),
				Displays = Array.Empty<DisplayDescription>()
			};

			SimChip display = new(displayDescription, 1, null, Array.Empty<SimChip>());

			ChipDescription rootDescription = new()
			{
				Name = "DISPLAY_DOT_COMPAT_ROOT",
				ChipType = ChipType.Custom,
				InputPins = new[]
				{
					Pin(100, PinBitCount.Bit8),
					Pin(101),
					Pin(102),
					Pin(103),
					Pin(104),
					Pin(105)
				},
				OutputPins = new[] { Pin(106) },
				SubChips = Array.Empty<SubChipDescription>(),
				Wires = Array.Empty<WireDescription>(),
				Displays = Array.Empty<DisplayDescription>()
			};

			SimChip root = new(rootDescription, -1, null, new[] { display });
			for (int i = 0; i < 6; i++)
			{
				root.AddConnection(new PinAddress(100 + i, 0), new PinAddress(1, i));
			}
			root.AddConnection(new PinAddress(1, 6), new PinAddress(106, 0));

			DevPinInstance[] inputs = new DevPinInstance[6];
			for (int i = 0; i < inputs.Length; i++)
			{
				inputs[i] = new DevPinInstance();
				inputs[i].Pin.Address = new PinAddress(100 + i, 0);
			}

			return (root, display, inputs);
		}

		static void SetDisplayDotInputs(
			DevPinInstance[] inputs,
			uint address,
			uint pixel,
			uint reset,
			uint write,
			uint refresh,
			uint clock)
		{
			inputs[0].Pin.PlayerInputState = address;
			inputs[1].Pin.PlayerInputState = pixel;
			inputs[2].Pin.PlayerInputState = reset;
			inputs[3].Pin.PlayerInputState = write;
			inputs[4].Pin.PlayerInputState = refresh;
			inputs[5].Pin.PlayerInputState = clock;
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
				CacheMode = ChipCacheMode.Cached,
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
