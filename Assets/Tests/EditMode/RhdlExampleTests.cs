using System.IO;
using System.Linq;
using DLS.Description;
using DLS.Game;
using DLS.RHDL;
using DLS.Simulation;
using NUnit.Framework;
using UnityEngine;

namespace DLS.Tests
{
	// Compiles the curated examples from Examples/RHDL and simulates them on the real engine,
	// so both the RHDL frontend and the documented example behaviour stay covered.
	public class RhdlExampleTests
	{
		static string ExamplesRoot => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Examples", "RHDL"));

		ChipLibrary library;

		[SetUp]
		public void SetUp() => library = TestLibrary.Create();

		[TearDown]
		public void TearDown() => RewiredEngine.Reset();

		ChipDescription Build(string relativePath)
		{
			string source = File.ReadAllText(Path.Combine(ExamplesRoot, relativePath));
			RhdlCompileResult result = RhdlCompiler.Compile(source, library);
			Assert.IsTrue(result.Success, $"{relativePath} failed to compile:\n" + string.Join("\n", result.Diagnostics.Select(d => d.ToString())));

			library.NotifyChipSaved(result.Description, false);
			return result.Description;
		}

		[Test]
		public void And2()
		{
			TestCircuit c = new(Build("Basics/00_AND.rhdl"), library);
			for (uint i = 0; i < 4; i++)
			{
				c.Set("A", i & 1);
				c.Set("B", i >> 1);
				c.Step();
				Assert.AreEqual(i == 3 ? 1u : 0u, c.Get("Y"), $"A={i & 1} B={i >> 1}");
			}
		}

		[Test]
		public void HalfAdder()
		{
			TestCircuit c = new(Build("Basics/01_HALF_ADDER.rhdl"), library);
			for (uint i = 0; i < 4; i++)
			{
				uint a = i & 1, b = i >> 1;
				c.Set("A", a);
				c.Set("B", b);
				c.Step();
				Assert.AreEqual((a + b) & 1, c.Get("SUM"), $"SUM A={a} B={b}");
				Assert.AreEqual((a + b) >> 1, c.Get("CARRY"), $"CARRY A={a} B={b}");
			}
		}

		[Test]
		public void FullAdder()
		{
			TestCircuit c = new(Build("Basics/02_FULL_ADDER.rhdl"), library);
			for (uint i = 0; i < 8; i++)
			{
				uint a = i & 1, b = (i >> 1) & 1, cin = i >> 2;
				c.Set("A", a);
				c.Set("B", b);
				c.Set("CIN", cin);
				c.Step();
				uint sum = a + b + cin;
				Assert.AreEqual(sum & 1, c.Get("SUM"), $"SUM {a}+{b}+{cin}");
				Assert.AreEqual(sum >> 1, c.Get("COUT"), $"COUT {a}+{b}+{cin}");
			}
		}

		[Test]
		public void Mux4()
		{
			TestCircuit c = new(Build("Basics/03_MUX4.rhdl"), library);
			c.Set("A", 0b1010);
			c.Set("B", 0b0101);

			c.Set("SEL", 1);
			c.Step();
			Assert.AreEqual(0b1010u, c.Get("Y"), "SEL=1 selects A");

			c.Set("SEL", 0);
			c.Step();
			Assert.AreEqual(0b0101u, c.Get("Y"), "SEL=0 selects B");
		}

		[Test]
		public void DffCapturesOnlyOnRisingEdge()
		{
			TestCircuit c = new(Build("Basics/04_DFF.rhdl"), library);

			c.Set("D", 1);
			c.Tick();
			Assert.AreEqual(1u, c.Get("Q"), "captured 1 on rising edge");
			Assert.AreEqual(0u, c.Get("NOT_Q"));

			c.Set("D", 0);
			c.Step(3);
			Assert.AreEqual(1u, c.Get("Q"), "D change while CLK high is ignored");

			c.Set("CLK", 0);
			c.Step(3);
			Assert.AreEqual(1u, c.Get("Q"), "falling edge does not capture");

			c.Set("CLK", 1);
			c.Step();
			Assert.AreEqual(0u, c.Get("Q"), "captured 0 on next rising edge");
			Assert.AreEqual(1u, c.Get("NOT_Q"));
		}

		[Test]
		public void Reg4WriteEnableAndReset()
		{
			Build("Basics/04_DFF.rhdl");
			TestCircuit c = new(Build("Basics/05_REG4.rhdl"), library);

			c.Set("RESET", 1);
			c.Tick();
			c.Set("RESET", 0);
			Assert.AreEqual(0u, c.Get("Q"), "reset clears");

			c.Set("D", 0b1011);
			c.Set("WE", 1);
			c.Tick();
			Assert.AreEqual(0b1011u, c.Get("Q"), "write");

			c.Set("D", 0b0110);
			c.Set("WE", 0);
			c.Tick();
			Assert.AreEqual(0b1011u, c.Get("Q"), "WE=0 holds value");

			c.Set("RESET", 1);
			c.Tick();
			Assert.AreEqual(0u, c.Get("Q"), "synchronous reset");
		}

		[Test]
		public void Ram4x4StoresEveryWord()
		{
			Build("Basics/04_DFF.rhdl");
			Build("Basics/05_REG4.rhdl");
			TestCircuit c = new(Build("Memory/00_RAM4x4.rhdl"), library);

			c.Set("RESET", 1);
			c.Tick();
			c.Set("RESET", 0);

			uint[] words = { 0x3, 0xA, 0x5, 0xC };
			c.Set("WE", 1);
			for (uint address = 0; address < 4; address++)
			{
				c.Set("A0", address & 1);
				c.Set("A1", address >> 1);
				c.Set("D", words[address]);
				c.Tick();
			}
			c.Set("WE", 0);

			// Reads are combinational: no clock edge between address changes.
			for (uint address = 0; address < 4; address++)
			{
				c.Set("A0", address & 1);
				c.Set("A1", address >> 1);
				c.Step();
				Assert.AreEqual(words[address], c.Get("Q"), $"word {address}");
			}
		}

		[Test]
		public void Cpu4RunsDocumentedTestProgram()
		{
			Build("Basics/04_DFF.rhdl");
			Build("Basics/05_REG4.rhdl");
			ChipDescription cpu = Build("CPU4/00_CPU4.rhdl");

			// Program from Examples/RHDL/CPU4/README.md: LDI 3, ADDI 2, OUT, HLT.
			uint[] rom = new uint[256];
			rom[0] = 0x0013;
			rom[1] = 0x0022;
			rom[2] = 0x00E0;
			rom[3] = 0x00F0;

			string romName = ChipTypeHelper.GetName(ChipType.Rom_256x16);
			int romIndex = System.Array.FindIndex(cpu.SubChips, s => ChipDescription.NameMatch(s.Name, romName));
			Assert.GreaterOrEqual(romIndex, 0, "CPU4 should contain the stock ROM");
			cpu.SubChips[romIndex].InternalData = rom;

			TestCircuit c = new(cpu, library);
			c.Set("RESET", 1);
			c.Tick();
			c.Set("RESET", 0);

			for (int i = 0; i < 12; i++) c.Tick();

			Assert.AreEqual(5u, c.Get("ACC"), "ACC");
			Assert.AreEqual(5u, c.Get("OUT_PORT"), "OUT_PORT");
			Assert.AreEqual(1u, c.Get("HALTED"), "HALTED");
			Assert.AreEqual(0u, c.Get("Z"), "Z");
		}

		// Every accumulator instruction from Examples/RHDL/CPU4/README.md, starting from A = 5.
		[TestCase(0x0, 0x0u, 5u, TestName = "Cpu4_NOP")]
		[TestCase(0x2, 0x2u, 7u, TestName = "Cpu4_ADDI")]
		[TestCase(0x3, 0x2u, 3u, TestName = "Cpu4_SUBI")]
		[TestCase(0x4, 0x6u, 4u, TestName = "Cpu4_ANDI")]
		[TestCase(0x5, 0x2u, 7u, TestName = "Cpu4_ORI")]
		[TestCase(0x6, 0x1u, 4u, TestName = "Cpu4_XORI")]
		[TestCase(0xA, 0x0u, 10u, TestName = "Cpu4_NOT")]
		[TestCase(0xB, 0x0u, 6u, TestName = "Cpu4_INC")]
		[TestCase(0xC, 0x0u, 4u, TestName = "Cpu4_DEC")]
		[TestCase(0xD, 0x0u, 5u, TestName = "Cpu4_Reserved")]
		public void Cpu4AccumulatorInstruction(int opcode, uint operand, uint expected)
		{
			Build("Basics/04_DFF.rhdl");
			Build("Basics/05_REG4.rhdl");
			ChipDescription cpu = Build("CPU4/00_CPU4.rhdl");

			// LDI 5; <instruction>; OUT; HLT
			uint[] rom = new uint[256];
			rom[0] = 0x0015;
			rom[1] = (uint)(opcode << 4) | operand;
			rom[2] = 0x00E0;
			rom[3] = 0x00F0;
			string romName = ChipTypeHelper.GetName(ChipType.Rom_256x16);
			cpu.SubChips[System.Array.FindIndex(cpu.SubChips, s => ChipDescription.NameMatch(s.Name, romName))].InternalData = rom;

			TestCircuit c = new(cpu, library);
			c.Set("RESET", 1);
			c.Tick();
			c.Set("RESET", 0);
			for (int i = 0; i < 8; i++) c.Tick();

			Assert.AreEqual(expected, c.Get("ACC"), "ACC");
			Assert.AreEqual(expected, c.Get("OUT_PORT"), "OUT_PORT");
		}

		// "not X" used to invert the cached bits of X in place, so every later use of X in the
		// same circuit saw ~X (CPU4's INC and DEC computed ~A + 1 and ~A - 1).
		[Test]
		public void NotDoesNotChangeLaterUsesOfItsOperand()
		{
			RhdlCompileResult result = RhdlCompiler.Compile(@"
circuit NotThenUse
    input A: 4
    output N: 4
    output Y: 4

    N = not A
    Y = A + 0x1
end", library);
			Assert.IsTrue(result.Success);
			library.NotifyChipSaved(result.Description, false);

			TestCircuit c = new(result.Description, library);
			for (uint a = 0; a < 16; a++)
			{
				c.Set("A", a);
				c.Step();
				Assert.AreEqual(~a & 15, c.Get("N"), $"N for A={a}");
				Assert.AreEqual((a + 1) & 15, c.Get("Y"), $"Y for A={a}");
			}
		}

		[Test]
		public void InvalidSourceReportsDiagnosticsWithLine()
		{
			const string source = "circuit Broken\n    input A\n    output Y\n    Y = A and MISSING\nend\n";
			RhdlCompileResult result = RhdlCompiler.Compile(source, library);

			Assert.IsFalse(result.Success);
			Assert.IsNotEmpty(result.Diagnostics);
			Assert.IsTrue(result.Diagnostics.Any(d => d.Line == 4), "diagnostic should point at the offending line:\n" + string.Join("\n", result.Diagnostics.Select(d => d.ToString())));
		}
	}
}
