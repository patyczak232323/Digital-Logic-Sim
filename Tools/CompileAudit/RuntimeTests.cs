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
			Run("RHDL structural HalfAdder compilation", TestRhdlHalfAdderCompilation);
			Run("RHDL readable logic synthesis", TestRhdlReadableLogicSynthesis);
			Run("RHDL v0.3 buses, arithmetic, slices and mux", TestRhdlV3BusExpressions);
			Run("RHDL implicit width inference", TestRhdlImplicitWidthInference);
			Run("RHDL block layout and orthogonal routing", TestRhdlBlockLayout);
			Run("RHDL v0.3 operators, constants and named ports", TestRhdlV3OperatorsAndNamedPorts);
			Run("RHDL examples and Rewired-8 source pack compile", TestRewired8RhdlPackCompilation);
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


		static void TestRhdlReadableLogicSynthesis()
		{
			PinDescription PinNamed(string name, int id) =>
				new(name, id, new UnityEngine.Vector2(), PinBitCount.Bit1, PinColour.Red, PinValueDisplayMode.Off);

			ChipDescription nand = new()
			{
				Name = "NAND",
				ChipType = ChipType.Nand,
				InputPins = new[] { PinNamed("IN B", 0), PinNamed("IN A", 1) },
				OutputPins = new[] { PinNamed("OUT", 2) },
				SubChips = Array.Empty<SubChipDescription>(),
				Wires = Array.Empty<WireDescription>(),
				Displays = Array.Empty<DisplayDescription>()
			};

			ChipLibrary library = new(nand);
			string source =
@"chip HalfAdderReadable {
  input a, b
  output sum, carry

  sum = a XOR b
  carry = a AND b
}";

			RhdlCompileResult result = RhdlCompiler.Compile(source, library);
			Assert(result.Success, "Readable RHDL compile failed: " + string.Join(" | ", result.Diagnostics.Select(d => d.ToString())));
			Assert(result.Description != null, "Readable RHDL returned no description");
			Assert(result.Description.SubChips.Length == 6,
				$"Readable HalfAdder should synthesize to 6 NAND gates, got {result.Description.SubChips.Length}");
			Assert(result.Description.Wires.Length == 14,
				$"Readable HalfAdder should synthesize to 14 wires, got {result.Description.Wires.Length}");

			Simulator.Reset();
			DeterministicSimulator.Reset();
			SimChip root = Simulator.BuildSimChip(result.Description, library);
			DevPinInstance[] inputs = { new DevPinInstance(), new DevPinInstance() };
			inputs[0].Pin.Address = new PinAddress(result.Description.InputPins[0].ID, 0);
			inputs[1].Pin.Address = new PinAddress(result.Description.InputPins[1].ID, 0);

			for (uint a = 0; a <= 1; a++)
			{
				for (uint b = 0; b <= 1; b++)
				{
					inputs[0].Pin.PlayerInputState = a;
					inputs[1].Pin.PlayerInputState = b;
					DeterministicSimulator.RunSimulationStep(root, inputs, new SimAudio());

					Assert(Bit(root.OutputPins[0].State) == (a ^ b),
						$"Readable RHDL XOR mismatch for {a}{b}");
					Assert(Bit(root.OutputPins[1].State) == (a & b),
						$"Readable RHDL AND mismatch for {a}{b}");
				}
			}

			string typoSource =
@"chip TypoDemo {
  input alpha
  output y
  y = alhpa
}";
			RhdlCompileResult typo = RhdlCompiler.Compile(typoSource, library);
			Assert(!typo.Success, "RHDL typo sample should fail");
			Assert(typo.Diagnostics.Any(d => d.Message.Contains("Did you mean 'alpha'?")),
				"RHDL typo diagnostic did not suggest the nearest signal");

			string chipTypoSource =
@"chip ChipTypo {
  input a, b
  output y
  NANND n(IN_A=a, IN_B=b, OUT=y)
}";
			RhdlCompileResult chipTypo = RhdlCompiler.Compile(chipTypoSource, library);
			Assert(!chipTypo.Success, "Unknown chip typo sample should fail");
			Assert(chipTypo.Diagnostics.Any(d => d.Message.Contains("Did you mean 'NAND'?")),
				"RHDL chip-type diagnostic did not suggest NAND");

			DeterministicSimulator.Reset();
			Simulator.Reset();
		}

		static void TestRhdlV3BusExpressions()
		{
			PinDescription P(string name, int id, PinBitCount bits = PinBitCount.Bit1) =>
				new(name, id, new UnityEngine.Vector2(), bits, PinColour.Red, PinValueDisplayMode.Off);

			ChipDescription Builtin(
				string name,
				ChipType type,
				PinDescription[] inputs,
				PinDescription[] outputs) =>
				new()
				{
					Name = name,
					ChipType = type,
					InputPins = inputs ?? Array.Empty<PinDescription>(),
					OutputPins = outputs ?? Array.Empty<PinDescription>(),
					SubChips = Array.Empty<SubChipDescription>(),
					Wires = Array.Empty<WireDescription>(),
					Displays = Array.Empty<DisplayDescription>()
				};

			ChipDescription nand = Builtin(
				"NAND",
				ChipType.Nand,
				new[] { P("IN B", 0), P("IN A", 1) },
				new[] { P("OUT", 2) });

			ChipDescription bus8 = Builtin(
				"BUS-8",
				ChipType.Bus_8Bit,
				new[] { P("BUS-8 (Hidden)", 0, PinBitCount.Bit8) },
				new[] { P("BUS-8", 1, PinBitCount.Bit8) });

			ChipDescription split8 = Builtin(
				"8-1BIT",
				ChipType.Split_8To1Bit,
				new[] { P("IN", 0, PinBitCount.Bit8) },
				Enumerable.Range(0, 8)
					.Select(i => P("OUT " + (char)('H' - i), 1 + i))
					.ToArray());

			ChipDescription split4 = Builtin(
				"4-1BIT",
				ChipType.Split_4To1Bit,
				new[] { P("IN", 0, PinBitCount.Bit4) },
				Enumerable.Range(0, 4)
					.Select(i => P("OUT " + (char)('D' - i), 1 + i))
					.ToArray());

			ChipDescription merge8 = Builtin(
				"1-8BIT",
				ChipType.Merge_1To8Bit,
				Enumerable.Range(0, 8)
					.Select(i => P("IN " + (char)('H' - i), i))
					.ToArray(),
				new[] { P("OUT", 8, PinBitCount.Bit8) });

			ChipDescription merge4 = Builtin(
				"1-4BIT",
				ChipType.Merge_1To4Bit,
				Enumerable.Range(0, 4)
					.Select(i => P("IN " + (char)('D' - i), i))
					.ToArray(),
				new[] { P("OUT", 4, PinBitCount.Bit4) });

			ChipLibrary library = new(nand, bus8, split8, split4, merge8, merge4);

			string source =
@"chip V3Demo(WIDTH=8) {
  input A: WIDTH, B: WIDTH
  input sel
  output Y: WIDTH
  output equal

  wire sum: WIDTH
  wire mixed: WIDTH

  sum = A + B
  mixed = {A[7:4], B[3:0]}
  Y = sel ? sum : mixed
  equal = A == B
}";

			RhdlCompileResult result = RhdlCompiler.Compile(source, library);
			Assert(result.Success,
				"RHDL v0.3 compile failed: " + string.Join(" | ", result.Diagnostics.Select(d => d.ToString())));
			Assert(result.Description != null, "RHDL v0.3 returned no description");
			Assert(result.Description.InputPins.Length == 3, "RHDL v0.3 input count mismatch");
			Assert(result.Description.OutputPins.Length == 2, "RHDL v0.3 output count mismatch");
			Assert(result.Description.InputPins[0].BitCount == PinBitCount.Bit8, "A is not 8-bit");
			Assert(result.Description.OutputPins[0].BitCount == PinBitCount.Bit8, "Y is not 8-bit");

			Simulator.Reset();
			DeterministicSimulator.Reset();
			SimChip root = Simulator.BuildSimChip(result.Description, library);

			DevPinInstance[] inputs =
			{
				new DevPinInstance(),
				new DevPinInstance(),
				new DevPinInstance()
			};

			for (int i = 0; i < inputs.Length; i++)
				inputs[i].Pin.Address = new PinAddress(result.Description.InputPins[i].ID, 0);

			void RunCase(uint a, uint b, uint sel, uint expectedY, uint expectedEqual)
			{
				inputs[0].Pin.PlayerInputState = a;
				inputs[1].Pin.PlayerInputState = b;
				inputs[2].Pin.PlayerInputState = sel;
				DeterministicSimulator.RunSimulationStep(root, inputs, new SimAudio());

				uint y = PinState.GetBitStates(root.OutputPins[0].State) & 0xFFu;
				uint eq = Bit(root.OutputPins[1].State);
				Assert(y == expectedY, $"RHDL v0.3 Y mismatch: A={a:X2} B={b:X2} sel={sel} got={y:X2} expected={expectedY:X2}");
				Assert(eq == expectedEqual, $"RHDL v0.3 equality mismatch: A={a:X2} B={b:X2} got={eq} expected={expectedEqual}");
			}

			RunCase(0x12, 0x34, 0, 0x14, 0);
			RunCase(0x12, 0x34, 1, 0x46, 0);
			RunCase(0xA5, 0xA5, 0, 0xA5, 1);

			DeterministicSimulator.Reset();
			Simulator.Reset();
		}

		static void TestRhdlBlockLayout()
		{
			PinDescription P(string name, int id, PinBitCount bits = PinBitCount.Bit1) =>
				new(name, id, new UnityEngine.Vector2(), bits, PinColour.Red, PinValueDisplayMode.Off);

			ChipDescription Builtin(
				string name,
				ChipType type,
				PinDescription[] inputs,
				PinDescription[] outputs,
				float width = 2f,
				float height = 2f) =>
				new()
				{
					Name = name,
					ChipType = type,
					Size = new UnityEngine.Vector2(width, height),
					InputPins = inputs ?? Array.Empty<PinDescription>(),
					OutputPins = outputs ?? Array.Empty<PinDescription>(),
					SubChips = Array.Empty<SubChipDescription>(),
					Wires = Array.Empty<WireDescription>(),
					Displays = Array.Empty<DisplayDescription>()
				};

			ChipDescription nand = Builtin(
				"NAND",
				ChipType.Nand,
				new[] { P("IN B", 0), P("IN A", 1) },
				new[] { P("OUT", 2) },
				3f,
				2f);

			ChipDescription bus1 = Builtin(
				"BUS-1",
				ChipType.Bus_1Bit,
				new[] { P("BUS-1 (Hidden)", 0) },
				new[] { P("BUS-1", 1) },
				2f,
				2f);

			ChipLibrary library = new(nand, bus1);
			string source =
@"chip BlockLayout {
  input A, B, C
  output Y

  wire ab
  wire bc
  ab = A & B
  bc = B ^ C
  Y = ab | bc
}";

			RhdlCompileResult result = RhdlCompiler.Compile(source, library);
			Assert(result.Success,
				"Block-layout RHDL failed: " + string.Join(" | ", result.Diagnostics.Select(d => d.ToString())));
			Assert(result.Description != null, "Block-layout RHDL returned no description");
			Assert(result.Description.SubChips.Length > 2, "Block-layout sample did not synthesize enough logic");

			// RHDL-generated visual wires should contain bend points. Start/end are
			// intentionally stored as zero because the editor derives them from pins.
			Assert(result.Description.Wires.Length > 0, "Block-layout sample has no wires");
			Assert(result.Description.Wires.All(w => w.Points != null && w.Points.Length >= 4),
				"RHDL block routing should generate orthogonal bend points");

			// No two generated subchips should occupy exactly the same snapped centre.
			int distinctPositions = result.Description.SubChips
				.Select(c => c.Position.x.ToString("R") + ":" + c.Position.y.ToString("R"))
				.Distinct()
				.Count();
			Assert(distinctPositions == result.Description.SubChips.Length,
				"RHDL block layout placed multiple chips at the same centre");
		}

		static void TestRhdlImplicitWidthInference()
		{
			PinDescription P(string name, int id, PinBitCount bits = PinBitCount.Bit1) =>
				new(name, id, new UnityEngine.Vector2(), bits, PinColour.Red, PinValueDisplayMode.Off);

			ChipDescription Builtin(
				string name,
				ChipType type,
				PinDescription[] inputs,
				PinDescription[] outputs) =>
				new()
				{
					Name = name,
					ChipType = type,
					InputPins = inputs ?? Array.Empty<PinDescription>(),
					OutputPins = outputs ?? Array.Empty<PinDescription>(),
					SubChips = Array.Empty<SubChipDescription>(),
					Wires = Array.Empty<WireDescription>(),
					Displays = Array.Empty<DisplayDescription>()
				};

			ChipDescription nand = Builtin(
				"NAND",
				ChipType.Nand,
				new[] { P("IN B", 0), P("IN A", 1) },
				new[] { P("OUT", 2) });

			ChipDescription bus8 = Builtin(
				"BUS-8",
				ChipType.Bus_8Bit,
				new[] { P("BUS-8 (Hidden)", 0, PinBitCount.Bit8) },
				new[] { P("BUS-8", 1, PinBitCount.Bit8) });

			ChipDescription split8 = Builtin(
				"8-1BIT",
				ChipType.Split_8To1Bit,
				new[] { P("IN", 0, PinBitCount.Bit8) },
				Enumerable.Range(0, 8).Select(i => P("OUT " + (char)('H' - i), 1 + i)).ToArray());

			ChipDescription merge8 = Builtin(
				"1-8BIT",
				ChipType.Merge_1To8Bit,
				Enumerable.Range(0, 8).Select(i => P("IN " + (char)('H' - i), i)).ToArray(),
				new[] { P("OUT", 8, PinBitCount.Bit8) });

			ChipDescription split4 = Builtin(
				"4-1BIT",
				ChipType.Split_4To1Bit,
				new[] { P("IN", 0, PinBitCount.Bit4) },
				Enumerable.Range(0, 4).Select(i => P("OUT " + (char)('D' - i), 1 + i)).ToArray());

			ChipDescription merge4 = Builtin(
				"1-4BIT",
				ChipType.Merge_1To4Bit,
				Enumerable.Range(0, 4).Select(i => P("IN " + (char)('D' - i), i)).ToArray(),
				new[] { P("OUT", 4, PinBitCount.Bit4) });

			ChipLibrary library = new(nand, bus8, split8, merge8, split4, merge4);

			string minimalSource =
@"chip Adder {
  input A:8
  input B:8
  output Y

  Y = A + B
}";
			RhdlCompileResult minimal = RhdlCompiler.Compile(minimalSource, library);
			Assert(minimal.Success,
				"Minimal inferred adder failed: " + string.Join(" | ", minimal.Diagnostics.Select(d => d.ToString())));
			Assert(minimal.Description.OutputPins.Length == 1 &&
			       minimal.Description.OutputPins[0].BitCount == PinBitCount.Bit8,
				"Minimal 'output Y' should infer to 8 bits from A + B");

			string source =
@"chip Adder {
  input A:8
  input B:8
  output Y
  output equal
  output low

  wire sum
  sum = A + B
  Y = sum
  equal = A == B
  low = A[3:0]
}";

			RhdlCompileResult result = RhdlCompiler.Compile(source, library);
			Assert(result.Success,
				"Implicit-width RHDL failed: " + string.Join(" | ", result.Diagnostics.Select(d => d.ToString())));
			Assert(result.Description != null, "Implicit-width RHDL returned no description");
			Assert(result.Description.OutputPins.Length == 3, "Implicit-width output count mismatch");
			Assert(result.Description.OutputPins[0].Name == "Y" &&
			       result.Description.OutputPins[0].BitCount == PinBitCount.Bit8,
				"output Y should infer to 8 bits");
			Assert(result.Description.OutputPins[1].Name == "equal" &&
			       result.Description.OutputPins[1].BitCount == PinBitCount.Bit1,
				"comparison output should infer to 1 bit");
			Assert(result.Description.OutputPins[2].Name == "low" &&
			       result.Description.OutputPins[2].BitCount == PinBitCount.Bit4,
				"slice output should infer to 4 bits");

			Simulator.Reset();
			DeterministicSimulator.Reset();
			SimChip root = Simulator.BuildSimChip(result.Description, library);
			DevPinInstance[] inputs = { new DevPinInstance(), new DevPinInstance() };
			for (int i = 0; i < inputs.Length; i++)
				inputs[i].Pin.Address = new PinAddress(result.Description.InputPins[i].ID, 0);

			inputs[0].Pin.PlayerInputState = 0x2Du;
			inputs[1].Pin.PlayerInputState = 0x13u;
			DeterministicSimulator.RunSimulationStep(root, inputs, new SimAudio());

			Assert((PinState.GetBitStates(root.OutputPins[0].State) & 0xFFu) == 0x40u,
				"inferred 8-bit adder output mismatch");
			Assert(Bit(root.OutputPins[1].State) == 0u, "inferred comparison output mismatch");
			Assert((PinState.GetBitStates(root.OutputPins[2].State) & 0xFu) == 0xDu,
				"inferred 4-bit slice output mismatch");

			DeterministicSimulator.Reset();
			Simulator.Reset();
		}

		static void TestRhdlV3OperatorsAndNamedPorts()
		{
			PinDescription P(string name, int id, PinBitCount bits = PinBitCount.Bit1) =>
				new(name, id, new UnityEngine.Vector2(), bits, PinColour.Red, PinValueDisplayMode.Off);

			ChipDescription Builtin(
				string name,
				ChipType type,
				PinDescription[] inputs,
				PinDescription[] outputs) =>
				new()
				{
					Name = name,
					ChipType = type,
					InputPins = inputs ?? Array.Empty<PinDescription>(),
					OutputPins = outputs ?? Array.Empty<PinDescription>(),
					SubChips = Array.Empty<SubChipDescription>(),
					Wires = Array.Empty<WireDescription>(),
					Displays = Array.Empty<DisplayDescription>()
				};

			ChipDescription nand = Builtin(
				"NAND",
				ChipType.Nand,
				new[] { P("IN B", 0), P("IN A", 1) },
				new[] { P("OUT", 2) });

			ChipDescription split8 = Builtin(
				"8-1BIT",
				ChipType.Split_8To1Bit,
				new[] { P("IN", 0, PinBitCount.Bit8) },
				Enumerable.Range(0, 8)
					.Select(i => P("OUT " + (char)('H' - i), 1 + i))
					.ToArray());

			ChipDescription merge8 = Builtin(
				"1-8BIT",
				ChipType.Merge_1To8Bit,
				Enumerable.Range(0, 8)
					.Select(i => P("IN " + (char)('H' - i), i))
					.ToArray(),
				new[] { P("OUT", 8, PinBitCount.Bit8) });

			ChipLibrary library = new(nand, split8, merge8);

			string source =
@"chip V3Ops {
  input A: 8, B: 8
  input flag

  output sub: 8
  output shl: 8
  output shr: 8
  output neq, less, ge
  output literal: 8
  output nand_named

  sub = A - B
  shl = A << 1
  shr = A >> 2
  neq = A != B
  less = A < B
  ge = A >= B
  literal = 0xA5

  NAND named(IN_A=flag, IN_B=flag, OUT=nand_named)
}";

			RhdlCompileResult result = RhdlCompiler.Compile(source, library);
			Assert(result.Success,
				"RHDL v0.3 operator compile failed: " + string.Join(" | ", result.Diagnostics.Select(d => d.ToString())));

			Simulator.Reset();
			DeterministicSimulator.Reset();
			SimChip root = Simulator.BuildSimChip(result.Description, library);
			DevPinInstance[] inputs =
			{
				new DevPinInstance(),
				new DevPinInstance(),
				new DevPinInstance()
			};
			for (int i = 0; i < inputs.Length; i++)
				inputs[i].Pin.Address = new PinAddress(result.Description.InputPins[i].ID, 0);

			void RunCase(uint a, uint b, uint flag)
			{
				inputs[0].Pin.PlayerInputState = a;
				inputs[1].Pin.PlayerInputState = b;
				inputs[2].Pin.PlayerInputState = flag;
				DeterministicSimulator.RunSimulationStep(root, inputs, new SimAudio());

				uint Out8(int index) => PinState.GetBitStates(root.OutputPins[index].State) & 0xFFu;
				uint Out1(int index) => Bit(root.OutputPins[index].State);

				Assert(Out8(0) == ((a - b) & 0xFFu), $"RHDL SUB mismatch for {a:X2}-{b:X2}");
				Assert(Out8(1) == ((a << 1) & 0xFFu), $"RHDL SHL mismatch for {a:X2}");
				Assert(Out8(2) == ((a >> 2) & 0xFFu), $"RHDL SHR mismatch for {a:X2}");
				Assert(Out1(3) == (a != b ? 1u : 0u), "RHDL != mismatch");
				Assert(Out1(4) == (a < b ? 1u : 0u), "RHDL < mismatch");
				Assert(Out1(5) == (a >= b ? 1u : 0u), "RHDL >= mismatch");
				Assert(Out8(6) == 0xA5u, "RHDL hex constant mismatch");
				Assert(Out1(7) == (flag == 0 ? 1u : 0u), "RHDL named NAND binding mismatch");
			}

			RunCase(0x10, 0x03, 0);
			RunCase(0x03, 0x10, 1);
			RunCase(0xFF, 0xFF, 0);

			DeterministicSimulator.Reset();
			Simulator.Reset();
		}

		static void TestRewired8RhdlPackCompilation()
		{
			PinDescription P(string name, int id, PinBitCount bits = PinBitCount.Bit1) =>
				new(name, id, new UnityEngine.Vector2(), bits, PinColour.Red, PinValueDisplayMode.Off);

			ChipDescription Builtin(
				string name,
				ChipType type,
				PinDescription[] inputs,
				PinDescription[] outputs) =>
				new()
				{
					Name = name,
					ChipType = type,
					InputPins = inputs ?? Array.Empty<PinDescription>(),
					OutputPins = outputs ?? Array.Empty<PinDescription>(),
					SubChips = Array.Empty<SubChipDescription>(),
					Wires = Array.Empty<WireDescription>(),
					Displays = Array.Empty<DisplayDescription>()
				};

			ChipDescription nand = Builtin(
				"NAND",
				ChipType.Nand,
				new[] { P("IN B", 0), P("IN A", 1) },
				new[] { P("OUT", 2) });

			ChipDescription ram = Builtin(
				"dev.RAM-8",
				ChipType.dev_Ram_8Bit,
				new[]
				{
					P("ADDRESS", 0, PinBitCount.Bit8),
					P("DATA", 1, PinBitCount.Bit8),
					P("WRITE", 2),
					P("RESET", 3),
					P("CLOCK", 4)
				},
				new[] { P("OUT", 5, PinBitCount.Bit8) });

			ChipDescription rom = Builtin(
				"ROM 256×16",
				ChipType.Rom_256x16,
				new[] { P("ADDRESS", 0, PinBitCount.Bit8) },
				new[]
				{
					P("OUT B", 1, PinBitCount.Bit8),
					P("OUT A", 2, PinBitCount.Bit8)
				});

			ChipDescription split8 = Builtin(
				"8-1BIT",
				ChipType.Split_8To1Bit,
				new[] { P("IN", 0, PinBitCount.Bit8) },
				Enumerable.Range(0, 8).Select(i => P("OUT " + (char)('H' - i), 1 + i)).ToArray());

			ChipDescription split4 = Builtin(
				"4-1BIT",
				ChipType.Split_4To1Bit,
				new[] { P("IN", 0, PinBitCount.Bit4) },
				Enumerable.Range(0, 4).Select(i => P("OUT " + (char)('D' - i), 1 + i)).ToArray());

			ChipDescription merge8 = Builtin(
				"1-8BIT",
				ChipType.Merge_1To8Bit,
				Enumerable.Range(0, 8).Select(i => P("IN " + (char)('H' - i), i)).ToArray(),
				new[] { P("OUT", 8, PinBitCount.Bit8) });

			ChipDescription merge4 = Builtin(
				"1-4BIT",
				ChipType.Merge_1To4Bit,
				Enumerable.Range(0, 4).Select(i => P("IN " + (char)('D' - i), i)).ToArray(),
				new[] { P("OUT", 4, PinBitCount.Bit4) });

			ChipDescription Bus(string name, ChipType type, PinBitCount bits) =>
				Builtin(
					name,
					type,
					new[] { P(name + " (Hidden)", 0, bits) },
					new[] { P(name, 1, bits) });

			List<ChipDescription> descriptions = new()
			{
				nand,
				ram,
				rom,
				split8,
				split4,
				merge8,
				merge4,
				Bus("BUS-1", ChipType.Bus_1Bit, PinBitCount.Bit1),
				Bus("BUS-4", ChipType.Bus_4Bit, PinBitCount.Bit4),
				Bus("BUS-8", ChipType.Bus_8Bit, PinBitCount.Bit8)
			};

			string[] sources =
			{
				"Examples/RHDL/Basics/00_AND.rhdl",
				"Examples/RHDL/Basics/01_BUS_ALU.rhdl",
				"Examples/RHDL/Basics/02_STRUCTURAL_NAND.rhdl",
				"Examples/RHDL/CPU4/00_CPU4.rhdl",
				"Examples/RHDL/Rewired8/00_RW8_REG8.rhdl",
				"Examples/RHDL/Rewired8/01_RW8_REG16.rhdl",
				"Examples/RHDL/Rewired8/02_RW8_REGFILE8.rhdl",
				"Examples/RHDL/Rewired8/03_RW8_ALU8.rhdl",
				"Examples/RHDL/Rewired8/04_RW8_SHIFTBIT8.rhdl",
				"Examples/RHDL/Rewired8/05_RW8_CORE.rhdl"
			};

			ChipDescription cpu4 = null;
			ChipDescription core = null;
			foreach (string sourcePath in sources)
			{
				Assert(File.Exists(sourcePath), "Missing RHDL source: " + sourcePath);
				ChipLibrary library = new(descriptions.ToArray());
				RhdlCompileResult result = RhdlCompiler.Compile(File.ReadAllText(sourcePath), library);
				Assert(result.Success,
					sourcePath + " failed: " + string.Join(" | ", result.Diagnostics.Select(d => d.ToString())));
				Assert(result.Description != null, sourcePath + " returned no chip description");

				// This test validates RHDL compilation/execution, not the cache builder.
				// Keep the large 21-bit RW8_ALU8 from queueing a 2,097,152-entry
				// background FULL LUT that can starve the dedicated cache regression.
				result.Description.CacheMode = ChipCacheMode.Normal;

				descriptions.Add(result.Description);
				if (result.Description.Name == "CPU4") cpu4 = result.Description;
				if (result.Description.Name == "RW8_CORE") core = result.Description;
			}

			Assert(cpu4 != null, "CPU4 source did not compile into a CPU4 chip");
			Assert(cpu4.InputPins.Any(p => p.Name == "CLK"), "CPU4 missing CLK input");
			Assert(cpu4.OutputPins.Any(p => p.Name == "ACC" && p.BitCount == PinBitCount.Bit4), "CPU4 missing 4-bit ACC");
			Assert(cpu4.OutputPins.Any(p => p.Name == "OUT_PORT" && p.BitCount == PinBitCount.Bit4), "CPU4 missing 4-bit OUT_PORT");
			Assert(cpu4.OutputPins.Any(p => p.Name == "HALTED"), "CPU4 missing HALTED output");

			Assert(core != null && core.Name == "RW8_CORE", "Rewired-8 source pack did not finish at RW8_CORE");
			Assert(core.InputPins.Any(p => p.Name == "IMEM_HI" && p.BitCount == PinBitCount.Bit8), "RW8_CORE missing IMEM_HI");
			Assert(core.OutputPins.Any(p => p.Name == "DMEM_ADDR_HI" && p.BitCount == PinBitCount.Bit8), "RW8_CORE missing 16-bit data address high byte");
			Assert(core.OutputPins.Any(p => p.Name == "HALTED"), "RW8_CORE missing HALTED output");
			Assert(core.SubChips.Length > 100, "RW8_CORE unexpectedly small; high-level logic was not lowered");

			// Execute a real program on CPU4 using its internal ROM:
			//   LDI 3
			//   STA 0
			//   LDI 2
			//   ADD 0
			//   OUT
			//   HLT
			ChipLibrary cpu4Library = new(descriptions.ToArray());
			Simulator.Reset();
			DeterministicSimulator.Reset();
			SimChip cpu4Root = Simulator.BuildSimChip(cpu4, cpu4Library);

			SimChip cpu4Rom = cpu4Root.SubChips.FirstOrDefault(c => c.ChipType == ChipType.Rom_256x16);
			Assert(cpu4Rom != null, "CPU4 has no internal program ROM");
			uint[] cpu4Program = { 0x0013u, 0x0030u, 0x0012u, 0x0040u, 0x00E0u, 0x00F0u };
			for (int i = 0; i < cpu4Program.Length; i++) cpu4Rom.InternalState[i] = cpu4Program[i];

			DevPinInstance[] cpu4Inputs = new DevPinInstance[cpu4.InputPins.Length];
			Dictionary<string, int> cpu4InputIndex = new(StringComparer.OrdinalIgnoreCase);
			for (int i = 0; i < cpu4.InputPins.Length; i++)
			{
				cpu4Inputs[i] = new DevPinInstance();
				cpu4Inputs[i].Pin.Address = new PinAddress(cpu4.InputPins[i].ID, 0);
				cpu4InputIndex[cpu4.InputPins[i].Name] = i;
			}

			Dictionary<string, int> cpu4OutputIndex = new(StringComparer.OrdinalIgnoreCase);
			for (int i = 0; i < cpu4.OutputPins.Length; i++)
				cpu4OutputIndex[cpu4.OutputPins[i].Name] = i;

			void SetCpu4Input(string name, uint value) =>
				cpu4Inputs[cpu4InputIndex[name]].Pin.PlayerInputState = value;

			uint ReadCpu4(string name, uint mask = 0xFu) =>
				PinState.GetBitStates(cpu4Root.OutputPins[cpu4OutputIndex[name]].State) & mask;

			void StepCpu4(uint clock, uint reset)
			{
				SetCpu4Input("CLK", clock);
				SetCpu4Input("RESET", reset);
				DeterministicSimulator.RunSimulationStep(cpu4Root, cpu4Inputs, new SimAudio());
			}

			StepCpu4(0, 1);
			StepCpu4(1, 1);
			StepCpu4(0, 1);
			StepCpu4(0, 0);

			for (int edge = 0; edge < 10 && ReadCpu4("HALTED", 1) == 0; edge++)
			{
				StepCpu4(1, 0);
				StepCpu4(0, 0);
			}

			Assert(ReadCpu4("HALTED", 1) == 1, "CPU4 did not reach HLT");
			Assert(ReadCpu4("ACC") == 5, $"CPU4 ACC expected 5, got {ReadCpu4("ACC")}");
			Assert(ReadCpu4("OUT_PORT") == 5, $"CPU4 OUT_PORT expected 5, got {ReadCpu4("OUT_PORT")}");
			Assert(ReadCpu4("PC") == 5, $"CPU4 PC should remain on HLT at address 5, got {ReadCpu4("PC")}");

			DeterministicSimulator.Reset();
			Simulator.Reset();

			// Execute a minimal real program through the generated CPU:
			//   LDI0 R1,5
			//   LDI0 R2,3
			//   ADD  R1,R2
			//   HALT
			ChipLibrary finalLibrary = new(descriptions.ToArray());
			Simulator.Reset();
			DeterministicSimulator.Reset();
			SimChip root = Simulator.BuildSimChip(core, finalLibrary);

			DevPinInstance[] inputs = new DevPinInstance[core.InputPins.Length];
			Dictionary<string, int> inputIndex = new(StringComparer.OrdinalIgnoreCase);
			for (int i = 0; i < core.InputPins.Length; i++)
			{
				inputs[i] = new DevPinInstance();
				inputs[i].Pin.Address = new PinAddress(core.InputPins[i].ID, 0);
				inputIndex[core.InputPins[i].Name] = i;
			}

			Dictionary<string, int> outputIndex = new(StringComparer.OrdinalIgnoreCase);
			for (int i = 0; i < core.OutputPins.Length; i++)
				outputIndex[core.OutputPins[i].Name] = i;

			ushort EncodeR(int opcode, int rd, int rs, int aux = 0) =>
				(ushort)(((opcode & 0x3F) << 10) | ((rd & 7) << 7) | ((rs & 7) << 4) | (aux & 0xF));

			ushort EncodeI(int opcode, int rd, int imm7) =>
				(ushort)(((opcode & 0x3F) << 10) | ((rd & 7) << 7) | (imm7 & 0x7F));

			ushort[] program =
			{
				EncodeI(0x02, 1, 5),
				EncodeI(0x02, 2, 3),
				EncodeR(0x10, 1, 2),
				(ushort)(0x3F << 10)
			};

			void SetInput(string name, uint value) =>
				inputs[inputIndex[name]].Pin.PlayerInputState = value;

			uint ReadOutput(string name, uint mask = 0xFFu) =>
				PinState.GetBitStates(root.OutputPins[outputIndex[name]].State) & mask;

			void DriveInstructionForCurrentPc()
			{
				int pc = (int)((ReadOutput("DBG_PC_HI") << 8) | ReadOutput("DBG_PC_LO"));
				ushort word = pc >= 0 && pc < program.Length ? program[pc] : (ushort)(0x3F << 10);
				SetInput("IMEM_HI", (uint)(word >> 8));
				SetInput("IMEM_LO", (uint)(word & 0xFF));
				SetInput("DMEM_IN", 0);
			}

			void Step(uint clock, uint reset)
			{
				DriveInstructionForCurrentPc();
				SetInput("CLK", clock);
				SetInput("RESET", reset);
				DeterministicSimulator.RunSimulationStep(root, inputs, new SimAudio());
			}

			// Synchronous reset needs a real rising edge.
			Step(0, 1);
			Step(1, 1);
			Step(0, 1);
			Step(0, 0);

			for (int edge = 0; edge < 12 && ReadOutput("HALTED", 1) == 0; edge++)
			{
				Step(1, 0);
				Step(0, 0);
			}

			Assert(ReadOutput("HALTED", 1) == 1, "RW8_CORE did not reach HALT");
			Assert(ReadOutput("DBG_R1") == 8, $"RW8_CORE R1 expected 8, got {ReadOutput("DBG_R1")}");
			Assert(ReadOutput("DBG_R2") == 3, $"RW8_CORE R2 expected 3, got {ReadOutput("DBG_R2")}");
			Assert(ReadOutput("DBG_PC_HI") == 0 && ReadOutput("DBG_PC_LO") == 4,
				$"RW8_CORE PC expected 0004 after HALT fetch, got {ReadOutput("DBG_PC_HI"):X2}{ReadOutput("DBG_PC_LO"):X2}");

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
