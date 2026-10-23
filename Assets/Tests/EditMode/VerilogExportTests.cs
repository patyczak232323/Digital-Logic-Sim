using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using DLS.Description;
using DLS.Export;
using DLS.Game;
using DLS.RHDL;
using DLS.Simulation;
using NUnit.Framework;
using UnityEngine;

namespace DLS.Tests
{
	public class VerilogExportTests
	{
		const string Alu4Source = @"
circuit Alu4
    input A: 4
    input B: 4
    input OP0, OP1
    output Y: 4
    output EQ, LT

    Y = choose(OP1, choose(OP0, A xor B, A and B), choose(OP0, A - B, A + B))
    EQ = A == B
    LT = A < B
end";

		const string Add8Source = @"
circuit Add8
    input A: 8
    input B: 8
    output Y: 8

    Y = A + B
end";


		static string ExamplesRoot => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Examples", "RHDL"));

		ChipLibrary library;
		string tempDirectory;

		[SetUp]
		public void SetUp() => library = TestLibrary.Create();

		[TearDown]
		public void TearDown()
		{
			if (tempDirectory != null && Directory.Exists(tempDirectory)) Directory.Delete(tempDirectory, true);
			tempDirectory = null;
		}

		[Test]
		public void ExportsOneModulePerCustomChipWithDependenciesFirst()
		{
			BuildExample("Basics/01_HALF_ADDER.rhdl");
			ChipDescription fullAdder = BuildExample("Basics/02_FULL_ADDER.rhdl");
			string verilog = VerilogExporter.Export(fullAdder, library, out string top);

			Assert.AreEqual("FullAdder", top);
			StringAssert.Contains("module FullAdder (", verilog);
			StringAssert.Contains("endmodule", verilog);

			int moduleCount = verilog.Split('\n').Count(line => line.StartsWith("module "));
			int customChips = 1 + fullAdder.SubChips.Select(s => s.Name).Distinct().Count(n => !library.IsBuiltinChip(n));
			Assert.AreEqual(customChips, moduleCount, verilog);
			Assert.Less(verilog.IndexOf("module HalfAdder (", StringComparison.Ordinal), verilog.IndexOf("module FullAdder (", StringComparison.Ordinal), "a dependency must be declared before it is used");
		}

		[Test]
		public void KeywordPinNamesBecomeValidIdentifiers()
		{
			// The editor allows any pin name, including spaces, duplicates and Verilog keywords.
			ChipBuilder b = new("Awkward");
			PinAddress first = b.Input("module");
			PinAddress second = b.Input("IN A");
			PinAddress third = b.Input("IN A");
			PinAddress y = b.Output("wire");
			b.Wire(ChipBuilder.NandOutput(b.Nand(first, second)), y);
			b.Wire(third, b.Output("3 out"));
			ChipDescription chip = b.Build();

			CollectionAssert.AreEqual(new[] { "module_", "IN_A", "IN_A_2" }, VerilogExporter.InputPortNames(chip));
			CollectionAssert.AreEqual(new[] { "wire_", "n3_out" }, VerilogExporter.OutputPortNames(chip));
			StringAssert.Contains("input module_", VerilogExporter.Export(chip, library));
		}

		[Test]
		public void ExportedAluMatchesTruthTableInIcarusVerilog() => AssertMatchesTruthTable(Compile(Alu4Source));

		[Test]
		public void ExportedEightBitAdderMatchesTruthTableInIcarusVerilog() => AssertMatchesTruthTable(Compile(Add8Source));

		[Test]
		public void ExportedNestedFullAdderMatchesTruthTableInIcarusVerilog()
		{
			BuildExample("Basics/01_HALF_ADDER.rhdl");
			AssertMatchesTruthTable(BuildExample("Basics/02_FULL_ADDER.rhdl"));
		}

		[Test]
		public void ExportedMuxMatchesTruthTableInIcarusVerilog() => AssertMatchesTruthTable(BuildExample("Basics/03_MUX4.rhdl"));

		// Simulates the exported Verilog with Icarus Verilog against every row of the chip's
		// truth table. Skipped where iverilog is not installed (e.g. the Unity CI image).
		void AssertMatchesTruthTable(ChipDescription chip)
		{
			string iverilog = FindExecutable("iverilog");
			string vvp = FindExecutable("vvp");
			if (iverilog == null || vvp == null) Assert.Ignore("Icarus Verilog is not installed.");

			TruthTable table = TruthTableGenerator.Generate(chip, library);
			Assert.IsTrue(table.Supported, table.UnsupportedReason);

			string design = VerilogExporter.Export(chip, library, out string top);
			tempDirectory ??= Path.Combine(Path.GetTempPath(), "RewiredVerilog_" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(tempDirectory);
			string designPath = Path.Combine(tempDirectory, "design.v");
			string benchPath = Path.Combine(tempDirectory, "bench.v");
			string simPath = Path.Combine(tempDirectory, "sim.vvp");
			File.WriteAllText(designPath, design);
			File.WriteAllText(benchPath, Testbench(chip, top, table));

			(int compileCode, string compileOutput) = RunProcess(iverilog, $"-g2005 -o \"{simPath}\" \"{designPath}\" \"{benchPath}\"");
			Assert.AreEqual(0, compileCode, "iverilog rejected the export:\n" + compileOutput + "\n" + design);

			(int _, string simOutput) = RunProcess(vvp, $"\"{simPath}\"");
			StringAssert.Contains("PASS", simOutput, simOutput);
		}

		static string Testbench(ChipDescription chip, string top, TruthTable table)
		{
			string[] inputs = VerilogExporter.InputPortNames(chip);
			string[] outputs = VerilogExporter.OutputPortNames(chip);
			StringBuilder sb = new();
			sb.AppendLine("`timescale 1ns / 1ps");
			sb.AppendLine("module bench;");
			for (int i = 0; i < inputs.Length; i++) sb.AppendLine($"\treg {Range(table.InputBits[i])}{inputs[i]};");
			for (int i = 0; i < outputs.Length; i++) sb.AppendLine($"\twire {Range(table.OutputBits[i])}{outputs[i]};");
			sb.AppendLine("\tinteger errors = 0;");
			string ports = string.Join(", ", inputs.Concat(outputs).Select(p => $".{p}({p})"));
			sb.AppendLine($"\t{top} dut ({ports});");
			sb.AppendLine("\tinitial begin");

			for (int row = 0; row < table.RowCount; row++)
			{
				for (int i = 0; i < inputs.Length; i++) sb.AppendLine($"\t\t{inputs[i]} = {table.InputBits[i]}'d{table.Inputs[row][i]};");
				sb.AppendLine("\t\t#1;");
				for (int o = 0; o < outputs.Length; o++)
				{
					string expected = Literal(table.Outputs[row][o], table.OutputBits[o]);
					sb.AppendLine($"\t\tif ({outputs[o]} !== {expected}) begin errors = errors + 1; $display(\"row {row}: {outputs[o]} = %b, expected {expected}\", {outputs[o]}); end");
				}
			}

			sb.AppendLine("\t\tif (errors == 0) $display(\"PASS\"); else $display(\"FAIL %0d\", errors);");
			sb.AppendLine("\t\t$finish;");
			sb.AppendLine("\tend");
			sb.AppendLine("endmodule");
			return sb.ToString();
		}

		// Binary literal with z for disconnected bits.
		static string Literal(uint state, int bits)
		{
			char[] digits = new char[bits];
			for (int b = 0; b < bits; b++)
			{
				bool z = ((PinState.GetTristateFlags(state) >> b) & 1) != 0;
				bool high = ((PinState.GetBitStates(state) >> b) & 1) != 0;
				digits[bits - 1 - b] = z ? 'z' : high ? '1' : '0';
			}

			return $"{bits}'b{new string(digits)}";
		}

		static string Range(int width) => width > 1 ? $"[{width - 1}:0] " : string.Empty;

		static string FindExecutable(string name)
		{
			string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
			foreach (string directory in path.Split(Path.PathSeparator))
			{
				foreach (string candidate in new[] { name, name + ".exe" })
				{
					string full = Path.Combine(directory, candidate);
					if (File.Exists(full)) return full;
				}
			}

			return null;
		}

		static (int, string) RunProcess(string file, string arguments)
		{
			ProcessStartInfo info = new(file, arguments)
			{
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true
			};

			using Process process = Process.Start(info);
			string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
			process.WaitForExit(120000);
			return (process.ExitCode, output);
		}

		ChipDescription BuildExample(string relativePath) =>
			Compile(File.ReadAllText(Path.Combine(ExamplesRoot, relativePath)));

		ChipDescription Compile(string source)
		{
			RhdlCompileResult result = RhdlCompiler.Compile(source, library);
			Assert.IsTrue(result.Success, "RHDL failed to compile:\n" + string.Join("\n", result.Diagnostics.Select(d => d.ToString())));

			library.NotifyChipSaved(result.Description, false);
			return result.Description;
		}
	}
}
