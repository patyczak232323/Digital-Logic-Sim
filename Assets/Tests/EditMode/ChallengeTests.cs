using System.Linq;
using DLS.Description;
using DLS.Game;
using DLS.RHDL;
using NUnit.Framework;

namespace DLS.Tests
{
	public class ChallengeTests
	{
		// Reference solutions. RHDL lowers logic to NAND gates (and split/merge for buses),
		// so each one also respects its challenge's "only NAND" rule.
		static readonly (string chip, string source)[] Solutions =
		{
			("NOT", "circuit SolutionNOT(IN) -> OUT:\n    OUT = ~IN\n"),
			("AND", "circuit SolutionAND(A, B) -> OUT:\n    OUT = A & B\n"),
			("OR", "circuit SolutionOR(A, B) -> OUT:\n    OUT = A | B\n"),
			("XOR", "circuit SolutionXOR(A, B) -> OUT:\n    OUT = A ^ B\n"),
			("MUX", "circuit SolutionMUX(A, B, SEL) -> OUT:\n    OUT = A if SEL else B\n"),
			("HALF ADDER", "circuit HalfAdder(A, B) -> (SUM, CARRY):\n    SUM = A ^ B\n    CARRY = A & B\n"),
			("FULL ADDER", "circuit FullAdder(A, B, CIN) -> (SUM, COUT):\n    SUM = A ^ B ^ CIN\n    COUT = (A & B) | (CIN & (A ^ B))\n"),
			("DECODER", "circuit Decoder(A1, A0) -> (Y0, Y1, Y2, Y3):\n    Y0 = ~A1 & ~A0\n    Y1 = ~A1 & A0\n    Y2 = A1 & ~A0\n    Y3 = A1 & A0\n"),
			("ADDER 4", "circuit Adder4(A: u4, B: u4) -> (SUM: u4, COUT):\n    wide: u5 = A + B\n    SUM = wide[0:4]\n    COUT = wide[4]\n")
		};

		ChipLibrary library;

		[SetUp]
		public void SetUp() => library = TestLibrary.Create();

		[Test]
		public void EveryChallengeHasAReferenceSolution()
		{
			CollectionAssert.AreEquivalent(ChallengeLibrary.All.Select(c => c.ChipName), Solutions.Select(s => s.chip));
		}

		[TestCaseSource(nameof(ChallengeNames))]
		public void ReferenceSolutionPasses(string chipName)
		{
			Save(chipName, Solutions.First(s => s.chip == chipName).source);
			ChallengeResult result = ChallengeLibrary.Check(Find(chipName), library);
			Assert.IsTrue(result.Passed, result.Message);
		}

		static string[] ChallengeNames() => ChallengeLibrary.All.Select(c => c.ChipName).ToArray();

		[Test]
		public void MissingChipAsksToBuildIt()
		{
			ChallengeResult result = ChallengeLibrary.Check(Find("NOT"), library);
			Assert.IsFalse(result.Passed);
			StringAssert.Contains("named NOT", result.Message);
		}

		[Test]
		public void WrongLogicNamesTheFailingRow()
		{
			Save("XOR", "circuit SolutionXOR(A, B) -> OUT:\n    OUT = A | B\n");
			ChallengeResult result = ChallengeLibrary.Check(Find("XOR"), library);
			Assert.IsFalse(result.Passed);
			StringAssert.Contains("A=1 B=1", result.Message);
			StringAssert.Contains("should be 0 but is 1", result.Message);
		}

		[Test]
		public void PinsAreMatchedByNameInAnyOrder()
		{
			Save("MUX", "circuit SolutionMUX(SEL, B, A) -> OUT:\n    OUT = A if SEL else B\n");
			Assert.IsTrue(ChallengeLibrary.Check(Find("MUX"), library).Passed);
		}

		[Test]
		public void MissingPinIsReported()
		{
			Save("AND", "circuit SolutionAND(A, C) -> OUT:\n    OUT = A & C\n");
			ChallengeResult result = ChallengeLibrary.Check(Find("AND"), library);
			Assert.IsFalse(result.Passed);
			StringAssert.Contains("pin named B", result.Message);
		}

		[Test]
		public void ForbiddenBuiltinIsReported()
		{
			// A tri-state buffer is not allowed in the NAND-only challenges.
			ChipBuilder b = new("NOT");
			PinAddress input = b.Input("IN");
			PinAddress output = b.Output("OUT");
			int inverter = b.Nand(input, input);
			int buffer = b.Chip(ChipTypeHelper.GetName(ChipType.TriStateBuffer));
			b.Wire(ChipBuilder.NandOutput(inverter), ChipBuilder.Pin(buffer, 0));
			b.Wire(input, ChipBuilder.Pin(buffer, 1));
			b.Wire(ChipBuilder.Pin(buffer, 2), output);
			library.NotifyChipSaved(b.Build(), false);
			ChallengeResult result = ChallengeLibrary.Check(Find("NOT"), library);
			Assert.IsFalse(result.Passed, result.Message);
			StringAssert.Contains("Use only NAND", result.Message);
		}

		static Challenge Find(string name) => ChallengeLibrary.All.First(c => c.ChipName == name);

		void Save(string chipName, string source)
		{
			RhdlCompileResult result = RhdlCompiler.Compile(source, library);
			Assert.IsTrue(result.Success, "RHDL failed to compile:\n" + string.Join("\n", result.Diagnostics.Select(d => d.ToString())));
			result.Description.Name = chipName;
			library.NotifyChipSaved(result.Description, false);
		}
	}
}
