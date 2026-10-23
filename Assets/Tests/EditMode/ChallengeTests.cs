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
			("NOT", "circuit SolutionNOT\n input IN\n output OUT\n OUT = not IN\nend"),
			("AND", "circuit SolutionAND\n input A, B\n output OUT\n OUT = A and B\nend"),
			("OR", "circuit SolutionOR\n input A, B\n output OUT\n OUT = A or B\nend"),
			("XOR", "circuit SolutionXOR\n input A, B\n output OUT\n OUT = A xor B\nend"),
			("MUX", "circuit SolutionMUX\n input A, B, SEL\n output OUT\n OUT = choose(SEL, A, B)\nend"),
			("HALF ADDER", "circuit HalfAdder\n input A, B\n output SUM, CARRY\n SUM = A xor B\n CARRY = A and B\nend"),
			("FULL ADDER", "circuit FullAdder\n input A, B, CIN\n output SUM, COUT\n SUM = A xor B xor CIN\n COUT = (A and B) or (CIN and (A xor B))\nend"),
			("DECODER", "circuit Decoder\n input A1, A0\n output Y0, Y1, Y2, Y3\n Y0 = (not A1) and (not A0)\n Y1 = (not A1) and A0\n Y2 = A1 and (not A0)\n Y3 = A1 and A0\nend"),
			("ADDER 4", "circuit Adder4\n input A: 4\n input B: 4\n output SUM: 4\n output COUT\n signal wide: 8\n wide = join(0x0, A) + join(0x0, B)\n SUM = wide[3:0]\n COUT = wide[4]\nend")
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
			Save("XOR", "circuit SolutionXOR\n input A, B\n output OUT\n OUT = A or B\nend");
			ChallengeResult result = ChallengeLibrary.Check(Find("XOR"), library);
			Assert.IsFalse(result.Passed);
			StringAssert.Contains("A=1 B=1", result.Message);
			StringAssert.Contains("should be 0 but is 1", result.Message);
		}

		[Test]
		public void PinsAreMatchedByNameInAnyOrder()
		{
			Save("MUX", "circuit SolutionMUX\n input SEL, B, A\n output OUT\n OUT = choose(SEL, A, B)\nend");
			Assert.IsTrue(ChallengeLibrary.Check(Find("MUX"), library).Passed);
		}

		[Test]
		public void MissingPinIsReported()
		{
			Save("AND", "circuit SolutionAND\n input A, C\n output OUT\n OUT = A and C\nend");
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
