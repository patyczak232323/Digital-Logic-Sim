using System;
using System.Linq;
using DLS.Description;
using DLS.Game;
using DLS.RHDL;
using DLS.Simulation;
using NUnit.Framework;

namespace DLS.Tests
{
	// The RHDL language itself: every construct is compiled and then simulated on the real
	// engine, and mistakes must produce a message that points at the right line.
	public class RhdlLanguageTests
	{
		ChipLibrary library;

		[SetUp]
		public void SetUp() => library = TestLibrary.Create();

		[TearDown]
		public void TearDown() => RewiredEngine.Reset();

		ChipDescription Build(string source)
		{
			RhdlCompileResult result = RhdlCompiler.Compile(source, library);
			Assert.IsTrue(result.Success, "RHDL failed to compile:\n" + string.Join("\n", result.Diagnostics.Select(d => d.ToString())));
			library.NotifyChipSaved(result.Description, false);
			return result.Description;
		}

		RhdlDiagnostic[] Errors(string source)
		{
			RhdlCompileResult result = RhdlCompiler.Compile(source, library);
			Assert.IsFalse(result.Success, "expected the source to be rejected");
			Assert.IsNotEmpty(result.Diagnostics);
			return result.Diagnostics;
		}

		static void AssertMessage(RhdlDiagnostic[] diagnostics, int line, string fragment)
		{
			string all = string.Join("\n", diagnostics.Select(d => d.ToString()));
			Assert.IsTrue(diagnostics.Any(d => d.Line == line && d.Message.Contains(fragment)),
				$"expected a message on line {line} containing '{fragment}', got:\n{all}");
		}

		static int NandCount(ChipDescription chip) =>
			chip.SubChips.Count(s => ChipDescription.NameMatch(s.Name, ChipTypeHelper.GetName(ChipType.Nand)));

		// ---- Behaviour ----

		[Test]
		public void LoopVariableBuildsARippleCarryAdder()
		{
			// 'carry' is assigned in every iteration, so each use reads the value from the
			// line above it, as in Python.
			ChipDescription chip = Build(@"
circuit Add4(A: u4, B: u4, CIN) -> (S: u4, COUT):
    carry = CIN
    for i in range(4):
        S[i] = A[i] ^ B[i] ^ carry
        carry = (A[i] & B[i]) | (carry & (A[i] ^ B[i]))
    COUT = carry
");
			TestCircuit c = new(chip, library);
			for (uint a = 0; a < 16; a++)
			{
				for (uint b = 0; b < 16; b++)
				{
					for (uint cin = 0; cin < 2; cin++)
					{
						c.Set("A", a);
						c.Set("B", b);
						c.Set("CIN", cin);
						c.Step();
						uint sum = a + b + cin;
						Assert.AreEqual(sum & 15, c.Get("S"), $"S for {a}+{b}+{cin}");
						Assert.AreEqual(sum >> 4, c.Get("COUT"), $"COUT for {a}+{b}+{cin}");
					}
				}
			}
		}

		[Test]
		public void NamesUsedAboveTheirAssignmentFormFeedback()
		{
			ChipDescription chip = Build(@"
circuit SrLatch(S_N, R_N) -> (Q, QN):
    Q = NAND(S_N, QN)
    QN = NAND(R_N, Q)
");
			Assert.AreEqual(2, NandCount(chip), "each NAND(...) is exactly one gate");

			TestCircuit c = new(chip, library);
			c.Set("S_N", 0);
			c.Set("R_N", 1);
			c.Step();
			Assert.AreEqual(1u, c.Get("Q"), "set");
			c.Set("S_N", 1);
			c.Step(3);
			Assert.AreEqual(1u, c.Get("Q"), "hold after set");
			c.Set("R_N", 0);
			c.Step();
			Assert.AreEqual(0u, c.Get("Q"), "reset");
			Assert.AreEqual(1u, c.Get("QN"), "reset");
		}

		[Test]
		public void OperatorsSlicesAndBuiltinsMatchPythonOnIntegers()
		{
			ChipDescription chip = Build(@"
WIDTH = 4

circuit Ops(A: u4, B: u4, S) -> (
    AND_: u4, OR_: u4, XOR_: u4, NOT_: u4, ADD: u4, SUB: u4,
    SHL: u4, SHR: u4, MUX: u4, CAT: u8, LOW: u4, TOP, WIDE: u8,
    EQ, NE, LT, GT, LE, GE, LOGIC,
):
    AND_ = A & B
    OR_ = A | B
    XOR_ = A ^ B
    NOT_ = ~A
    ADD = A + B
    SUB = A - B
    SHL = A << 1
    SHR = A >> 2
    MUX = A if S else B
    CAT = cat(A, B)
    LOW = cat(B, A)[0:WIDTH]
    TOP = A[-1]
    WIDE = u8(A) + u8(B)
    EQ = A == B
    NE = A != B
    LT = A < B
    GT = A > B
    LE = A <= B
    GE = A >= B
    LOGIC = (A[0] and not B[0]) or S
");
			TestCircuit c = new(chip, library);
			for (uint a = 0; a < 16; a++)
			{
				for (uint b = 0; b < 16; b++)
				{
					uint s = (a + b) & 1;
					c.Set("A", a);
					c.Set("B", b);
					c.Set("S", s);
					c.Step();
					string at = $"A={a} B={b} S={s}";
					Assert.AreEqual(a & b, c.Get("AND_"), "& " + at);
					Assert.AreEqual(a | b, c.Get("OR_"), "| " + at);
					Assert.AreEqual(a ^ b, c.Get("XOR_"), "^ " + at);
					Assert.AreEqual(~a & 15, c.Get("NOT_"), "~ " + at);
					Assert.AreEqual((a + b) & 15, c.Get("ADD"), "+ " + at);
					Assert.AreEqual((a - b) & 15, c.Get("SUB"), "- " + at);
					Assert.AreEqual((a << 1) & 15, c.Get("SHL"), "<< " + at);
					Assert.AreEqual(a >> 2, c.Get("SHR"), ">> " + at);
					Assert.AreEqual(s == 1 ? a : b, c.Get("MUX"), "if " + at);
					Assert.AreEqual((a << 4) | b, c.Get("CAT"), "cat " + at);
					Assert.AreEqual(a, c.Get("LOW"), "slice " + at);
					Assert.AreEqual(a >> 3, c.Get("TOP"), "[-1] " + at);
					Assert.AreEqual(a + b, c.Get("WIDE"), "u8() " + at);
					Assert.AreEqual(a == b ? 1u : 0u, c.Get("EQ"), "== " + at);
					Assert.AreEqual(a != b ? 1u : 0u, c.Get("NE"), "!= " + at);
					Assert.AreEqual(a < b ? 1u : 0u, c.Get("LT"), "< " + at);
					Assert.AreEqual(a > b ? 1u : 0u, c.Get("GT"), "> " + at);
					Assert.AreEqual(a <= b ? 1u : 0u, c.Get("LE"), "<= " + at);
					Assert.AreEqual(a >= b ? 1u : 0u, c.Get("GE"), ">= " + at);
					bool logic = ((a & 1) == 1 && (b & 1) == 0) || s == 1;
					Assert.AreEqual(logic ? 1u : 0u, c.Get("LOGIC"), "and/or/not " + at);
				}
			}
		}

		[Test]
		public void AnnotatedWidthKeepsTheCarry()
		{
			ChipDescription chip = Build(@"
circuit Carry(A: u4, B: u4) -> (SUM: u4, COUT):
    wide: u5 = A + B
    SUM = wide[0:4]
    COUT = wide[4]
");
			TestCircuit c = new(chip, library);
			c.Set("A", 9);
			c.Set("B", 8);
			c.Step();
			Assert.AreEqual(1u, c.Get("SUM"));
			Assert.AreEqual(1u, c.Get("COUT"));
		}

		[Test]
		public void ComponentsAreCalledUnpackedAndWiredByPin()
		{
			Build(@"
circuit HalfAdder(A, B) -> (SUM, CARRY):
    SUM = A ^ B
    CARRY = A & B
");
			ChipDescription chip = Build(@"
circuit TwoHalves(X, Y, Z) -> (S, C1, C2):
    s1, C1 = HalfAdder(X, Y)
    second = HalfAdder()
    second.A = s1
    second.B = Z
    S = second.SUM
    C2 = second.CARRY
");
			TestCircuit c = new(chip, library);
			for (uint i = 0; i < 8; i++)
			{
				uint x = i & 1, y = (i >> 1) & 1, z = i >> 2;
				c.Set("X", x);
				c.Set("Y", y);
				c.Set("Z", z);
				c.Step();
				Assert.AreEqual(x ^ y ^ z, c.Get("S"), $"S for {x}{y}{z}");
				Assert.AreEqual(x & y, c.Get("C1"), $"C1 for {x}{y}{z}");
				Assert.AreEqual((x ^ y) & z, c.Get("C2"), $"C2 for {x}{y}{z}");
			}
		}

		[Test]
		public void TriStateBuffersShareOneSignal()
		{
			ChipDescription chip = Build(@"
circuit SharedBus(A, B, SEL) -> Y:
    bus = TRISTATE(A, SEL)
    bus = TRISTATE(B, ~SEL)
    Y = bus
");
			TestCircuit c = new(chip, library);
			for (uint i = 0; i < 8; i++)
			{
				uint a = i & 1, b = (i >> 1) & 1, sel = i >> 2;
				c.Set("A", a);
				c.Set("B", b);
				c.Set("SEL", sel);
				c.Step(2);
				Assert.AreEqual(sel == 1 ? a : b, c.Get("Y"), $"A={a} B={b} SEL={sel}");
			}
		}

		[Test]
		public void ConstantConditionsAreChosenWhenBuilding()
		{
			ChipDescription chip = Build(@"
INVERT = 1

circuit Maybe(A: u4) -> Y: u4:
    Y = ~A if INVERT else A
");
			Assert.AreEqual(4, NandCount(chip), "only the chosen branch is built");
		}

		// ---- Gate economy ----

		[Test]
		public void LogicUsesFewNandGates()
		{
			Assert.AreEqual(5, NandCount(Build("circuit H(A, B) -> (S, C):\n    S = A ^ B\n    C = A & B\n")), "half adder");
			Assert.AreEqual(9, NandCount(Build("circuit F(A, B, CI) -> (S, CO):\n    S = A ^ B ^ CI\n    CO = (A & B) | (CI & (A ^ B))\n")), "full adder");
			Assert.LessOrEqual(NandCount(Build("circuit Add4(A: u4, B: u4) -> Y: u4:\n    Y = A + B\n")), 4 * 9, "4-bit adder");
			Assert.AreEqual(4, NandCount(Build("circuit M(A, B, S) -> Y:\n    Y = A if S else B\n")), "2:1 multiplexer");
			Assert.AreEqual(0, NandCount(Build("circuit W(A: u4) -> Y: u4:\n    Y = A & 0b1111\n")), "AND with all ones is a wire");
		}

		// ---- Layout ----

		[TestCase("circuit Add8(A: u8, B: u8) -> Y: u8:\n    Y = A + B\n")]
		[TestCase("circuit Latch(S_N, R_N) -> (Q, QN):\n    Q = NAND(S_N, QN)\n    QN = NAND(R_N, Q)\n")]
		[TestCase("circuit Mix(A: u4, B: u4, S) -> (Y: u4, EQ):\n    Y = (A + B) if S else (A ^ B)\n    EQ = A == B\n")]
		public void LayoutHasNoOverlappingPartsAndOnlyStraightWires(string source)
		{
			ChipDescription chip = Build(source);

			for (int i = 0; i < chip.SubChips.Length; i++)
			{
				for (int j = i + 1; j < chip.SubChips.Length; j++)
				{
					SubChipDescription a = chip.SubChips[i];
					SubChipDescription b = chip.SubChips[j];
					UnityEngine.Vector2 sa = library.GetChipDescription(a.Name).Size;
					UnityEngine.Vector2 sb = library.GetChipDescription(b.Name).Size;
					bool overlap = Math.Abs(a.Position.x - b.Position.x) < (sa.x + sb.x) / 2 &&
					               Math.Abs(a.Position.y - b.Position.y) < (sa.y + sb.y) / 2;
					Assert.IsFalse(overlap, $"{a.Name}#{a.ID} overlaps {b.Name}#{b.ID}");
				}
			}

			foreach (WireDescription wire in chip.Wires)
			{
				// The first and last points are replaced by the pins; the bends in between
				// must form horizontal and vertical segments only.
				for (int p = 2; p < wire.Points.Length - 1; p++)
				{
					UnityEngine.Vector2 from = wire.Points[p - 1];
					UnityEngine.Vector2 to = wire.Points[p];
					bool straight = Math.Abs(from.x - to.x) < 0.001f || Math.Abs(from.y - to.y) < 0.001f;
					Assert.IsTrue(straight, $"diagonal wire segment {from} -> {to}");
				}
			}
		}

		// ---- Messages ----

		[Test]
		public void UnknownNamesSuggestTheClosestOne()
		{
			RhdlDiagnostic[] errors = Errors("circuit C(A, B) -> Y:\n    total = A & B\n    Y = totl\n");
			AssertMessage(errors, 3, "Did you mean 'total'");
		}

		[Test]
		public void PinsCannotBeAssignedTwice()
		{
			AssertMessage(Errors("circuit C(A, B) -> Y:\n    Y = A\n    Y = B\n"), 3, "assigned more than once");
		}

		[Test]
		public void VariablesMustBeAssignedBeforeUse()
		{
			RhdlDiagnostic[] errors = Errors("circuit C(A) -> Y:\n    Y = t\n    t = A\n    t = ~t\n");
			AssertMessage(errors, 2, "used before it is assigned");
		}

		[Test]
		public void WidthMismatchExplainsTheFix()
		{
			AssertMessage(Errors("circuit C(A: u4) -> Y: u8:\n    Y = A\n"), 2, "u8(...)");
			AssertMessage(Errors("circuit C(A: u8) -> Y: u4:\n    Y = A\n"), 2, "slice");
		}

		[Test]
		public void OldSyntaxGetsAHint()
		{
			AssertMessage(Errors("circuit C(A) -> Y:\n    Y = A\nend\n"), 3, "no longer uses 'end'");
			AssertMessage(Errors("circuit C(A, S) -> Y:\n    if S:\n        Y = A\n"), 2, "a if select else b");
		}

		[Test]
		public void OldVersionSourcePointsToTheMigrationGuide()
		{
			RhdlDiagnostic[] errors = Errors("circuit Old\n    input A\n    output Y\n    Y = not A\nend\n");
			Assert.IsTrue(errors.Any(d => d.Message.Contains("Coming from RHDL 0.6")), string.Join("\n", errors.Select(d => d.ToString())));
		}

		[Test]
		public void MissingOutputsAndInputsAreReported()
		{
			AssertMessage(Errors("circuit C(A) -> (Y, Z):\n    Y = A\n"), 1, "Output 'Z' is never assigned");
			AssertMessage(Errors("circuit C(A) -> Y:\n    Y = NAND(A)\n"), 2, "NAND needs both inputs");
		}

		[Test]
		public void PinWidthsMustExistOnChips()
		{
			AssertMessage(Errors("circuit C(A: u6) -> Y:\n    Y = A[0]\n"), 1, "1, 4 or 8 bits");
		}
	}
}
