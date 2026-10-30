using System;
using System.IO;
using System.Linq;
using DLS.Description;
using DLS.Game;
using DLS.RHDL;
using DLS.SaveSystem;
using DLS.Simulation;
using NUnit.Framework;

namespace DLS.Tests
{
	public class TruthTableTests
	{
		const string HalfAdderSource = @"
circuit HalfAdder(A, B) -> (SUM, CARRY):
    SUM = A ^ B
    CARRY = A & B
";

		// Same pins as HalfAdder, but CARRY is wrong.
		const string BrokenHalfAdderSource = @"
circuit HalfAdder(A, B) -> (SUM, CARRY):
    SUM = A ^ B
    CARRY = A | B
";

		const string RenamedPinsSource = @"
circuit HalfAdder(X, B) -> (SUM, CARRY):
    SUM = X ^ B
    CARRY = X & B
";

		const string Add8Source = @"
circuit Add8(A: u8, B: u8) -> Y: u8:
    Y = A + B
";

		const string LatchSource = @"
circuit Latch(S_N, R_N) -> (Q, QN):
    Q = NAND(S_N, QN)
    QN = NAND(R_N, Q)
";

		ChipLibrary library;
		string projectName;

		[SetUp]
		public void SetUp() => library = TestLibrary.Create();

		[TearDown]
		public void TearDown()
		{
			RewiredEngine.Reset();
			if (projectName != null)
			{
				string path = SavePaths.GetProjectPath(projectName);
				if (Directory.Exists(path)) Directory.Delete(path, true);
				projectName = null;
			}
		}

		[Test]
		public void HalfAdderTableListsEveryRowInCountingOrder()
		{
			TruthTable table = TruthTableGenerator.Generate(Compile(HalfAdderSource), library);

			Assert.IsTrue(table.Supported, table.UnsupportedReason);
			Assert.IsFalse(table.Sampled);
			CollectionAssert.AreEqual(new[] { "A", "B" }, table.InputNames);
			CollectionAssert.AreEqual(new[] { "SUM", "CARRY" }, table.OutputNames);
			Assert.AreEqual(4, table.RowCount);

			for (int row = 0; row < 4; row++)
			{
				uint a = (uint)row >> 1, b = (uint)row & 1;
				CollectionAssert.AreEqual(new[] { a, b }, table.Inputs[row], $"row {row} inputs");
				Assert.AreEqual(a ^ b, PinState.GetBitStates(table.Outputs[row][0]), $"row {row} SUM");
				Assert.AreEqual(a & b, PinState.GetBitStates(table.Outputs[row][1]), $"row {row} CARRY");
			}
		}

		[Test]
		public void WideChipIsSampledDeterministically()
		{
			ChipDescription add8 = Compile(Add8Source);
			TruthTable first = TruthTableGenerator.Generate(add8, library);
			TruthTable second = TruthTableGenerator.Generate(add8, library);

			Assert.IsTrue(first.Supported, first.UnsupportedReason);
			Assert.IsTrue(first.Sampled);
			Assert.AreEqual(16, first.TotalInputBits);
			Assert.AreEqual(TruthTableGenerator.SampledRowCount, first.RowCount);
			CollectionAssert.AreEqual(new uint[] { 0, 0 }, first.Inputs[0], "all-low row first");
			CollectionAssert.AreEqual(new uint[] { 255, 255 }, first.Inputs[1], "all-high row second");

			for (int row = 0; row < first.RowCount; row++)
			{
				CollectionAssert.AreEqual(first.Inputs[row], second.Inputs[row], $"row {row} differs between runs");
				uint expected = (first.Inputs[row][0] + first.Inputs[row][1]) & 255;
				Assert.AreEqual(expected, PinState.GetBitStates(first.Outputs[row][0]) & 255u, $"row {row}");
			}
		}

		[Test]
		public void NativeRowsMatchGateEvaluationExactly()
		{
			// Truth tables of pure chips are computed by native code; every row, including
			// the tri-state flag bits, must equal stepping the gates themselves.
			ChipDescription add8 = Compile(Add8Source);
			TruthTable table = TruthTableGenerator.Generate(add8, library);
			Assert.IsTrue(table.Supported, table.UnsupportedReason);

			SimChip chip = CombinationalChipTestRunner.BuildIsolatedChip(add8, library);
			for (int row = 0; row < table.RowCount; row++)
			{
				for (int i = 0; i < chip.InputPins.Length; i++) chip.InputPins[i].State = table.Inputs[row][i];
				Simulator.EvaluatePureCombinationalForMemo(chip);
				for (int o = 0; o < chip.OutputPins.Length; o++)
				{
					Assert.AreEqual(chip.OutputPins[o].State, table.Outputs[row][o], $"row {row} output {o}");
				}
			}
		}

		[Test]
		public void StatefulChipHasNoTruthTable()
		{
			TruthTable table = TruthTableGenerator.Generate(Compile(LatchSource), library);
			Assert.IsFalse(table.Supported);
			Assert.IsNotEmpty(table.UnsupportedReason);
		}

		[Test]
		public void RecordedTestPassesUntilTheLogicChanges()
		{
			ChipDescription original = Compile(HalfAdderSource);
			ChipTestSuite suite = TruthTableGenerator.RecordTests(TruthTableGenerator.Generate(original, library), "HalfAdder");

			ChipTestRunResult pass = TruthTableGenerator.RunTests(suite, original, library);
			Assert.IsTrue(pass.Passed, pass.UnsupportedReason);
			Assert.AreEqual(4, pass.PassedVectors);

			ChipDescription broken = Compile(BrokenHalfAdderSource);
			ChipTestRunResult fail = TruthTableGenerator.RunTests(suite, broken, library);
			Assert.IsTrue(fail.Supported);
			Assert.IsFalse(fail.Passed);
			// A or B differs from A and B on rows 01 and 10 only.
			CollectionAssert.AreEquivalent(new[] { 1, 2 }, fail.Failures.Select(f => f.VectorIndex).ToArray());
			Assert.IsTrue(fail.Failures.All(f => f.OutputIndex == 1), "only CARRY should fail");
		}

		[Test]
		public void RecordedTestRefusesToRunAfterPinsChange()
		{
			ChipTestSuite suite = TruthTableGenerator.RecordTests(TruthTableGenerator.Generate(Compile(HalfAdderSource), library), "HalfAdder");
			ChipTestRunResult result = TruthTableGenerator.RunTests(suite, Compile(RenamedPinsSource), library);

			Assert.IsFalse(result.Supported);
			StringAssert.Contains("pins changed", result.UnsupportedReason);
		}

		[Test]
		public void TestsSurviveSaveRenameAndDelete()
		{
			projectName = "RewiredTruthTableTest_" + Guid.NewGuid().ToString("N");
			ChipDescription chip = Compile(HalfAdderSource);
			ChipTestSuite suite = TruthTableGenerator.RecordTests(TruthTableGenerator.Generate(chip, library), "HalfAdder");

			Assert.IsNull(Loader.LoadChipTests(projectName, "HalfAdder"), "no tests before saving");
			Saver.SaveChipTests(suite, projectName);

			ChipTestSuite loaded = Loader.LoadChipTests(projectName, "HalfAdder");
			Assert.IsNotNull(loaded);
			Assert.IsTrue(TruthTableGenerator.RunTests(loaded, chip, library).Passed, "loaded test should pass");

			Saver.RenameChipTests("HalfAdder", "Adder1", projectName);
			Assert.IsNull(Loader.LoadChipTests(projectName, "HalfAdder"), "old name still has tests");
			Assert.AreEqual("Adder1", Loader.LoadChipTests(projectName, "Adder1")?.ChipName);

			Saver.DeleteChipTests("Adder1", projectName);
			Assert.IsNull(Loader.LoadChipTests(projectName, "Adder1"), "tests survived delete");
		}

		ChipDescription Compile(string source)
		{
			RhdlCompileResult result = RhdlCompiler.Compile(source, library);
			Assert.IsTrue(result.Success, "RHDL failed to compile:\n" + string.Join("\n", result.Diagnostics.Select(d => d.ToString())));

			library.NotifyChipSaved(result.Description, false);
			return result.Description;
		}
	}
}
