using System.Diagnostics;
using DLS.Description;
using DLS.Simulation;
using NUnit.Framework;
using static DLS.Tests.ChipBuilder;

namespace DLS.Tests
{
	// Gate-level tests that run the real C# runtime (DeterministicSimulator, JIT, LUT cache)
	// through RewiredEngine. Tools/SimulationRegression models the same rules in Python;
	// these tests check that the shipped engine actually follows them.
	public class RewiredEngineTests
	{
		[TearDown]
		public void TearDown() => RewiredEngine.Reset();

		[Test]
		public void NandTruthTable()
		{
			ChipBuilder b = new("NandTest");
			PinAddress a = b.Input("A");
			PinAddress bIn = b.Input("B");
			PinAddress y = b.Output("Y");
			b.Wire(NandOutput(b.Nand(a, bIn)), y);

			TestCircuit circuit = new(b.Build(), TestLibrary.Create());

			for (uint av = 0; av <= 1; av++)
			{
				for (uint bv = 0; bv <= 1; bv++)
				{
					circuit.Set("A", av);
					circuit.Set("B", bv);
					circuit.Step();
					Assert.AreEqual((av & bv) == 1 ? 0u : 1u, circuit.Get("Y"), $"NAND({av}, {bv})");
				}
			}

			Assert.IsTrue(circuit.Diagnostics.SettleConverged);
		}

		[Test]
		public void SrLatchSetsResetsAndHolds()
		{
			// Active-low SR latch made from two cross-coupled NANDs.
			ChipBuilder b = new("SrLatch");
			PinAddress setN = b.Input("S_N");
			PinAddress resetN = b.Input("R_N");
			PinAddress q = b.Output("Q");
			PinAddress qn = b.Output("QN");

			int top = b.Chip("NAND");
			int bottom = b.Chip("NAND");
			b.Wire(setN, Pin(top, NandInA));
			b.Wire(NandOutput(bottom), Pin(top, NandInB));
			b.Wire(resetN, Pin(bottom, NandInA));
			b.Wire(NandOutput(top), Pin(bottom, NandInB));
			b.Wire(NandOutput(top), q);
			b.Wire(NandOutput(bottom), qn);

			TestCircuit latch = new(b.Build(), TestLibrary.Create());

			latch.Set("S_N", 0);
			latch.Set("R_N", 1);
			latch.Step();
			Assert.AreEqual(1u, latch.Get("Q"), "set");
			Assert.AreEqual(0u, latch.Get("QN"), "set");

			latch.Set("S_N", 1);
			latch.Step(5);
			Assert.AreEqual(1u, latch.Get("Q"), "hold after set");

			latch.Set("R_N", 0);
			latch.Step();
			Assert.AreEqual(0u, latch.Get("Q"), "reset");
			Assert.AreEqual(1u, latch.Get("QN"), "reset");

			latch.Set("R_N", 1);
			latch.Step(5);
			Assert.AreEqual(0u, latch.Get("Q"), "hold after reset");
			Assert.IsTrue(latch.Diagnostics.SettleConverged);
		}

		[TestCase(1)]
		[TestCase(4)]
		[TestCase(8)]
		public void NestedCustomChipsPropagateWithinOneStep(int depth)
		{
			// Not = NAND(x, x); Buf0 = Not(Not(x)); BufN = Buf(N-1)(Buf(N-1)(x)) ...
			ChipBuilder notBuilder = new("Not");
			PinAddress notIn = notBuilder.Input("IN");
			PinAddress notOut = notBuilder.Output("OUT");
			notBuilder.Wire(NandOutput(notBuilder.Nand(notIn, notIn)), notOut);
			ChipDescription not = notBuilder.Build();

			ChipDescription[] chips = new ChipDescription[depth + 1];
			chips[0] = not;
			for (int level = 1; level <= depth; level++)
			{
				ChipDescription child = chips[level - 1];
				ChipBuilder lb = new("Level" + level);
				PinAddress input = lb.Input("IN");
				PinAddress output = lb.Output("OUT");
				int first = lb.Chip(child.Name);
				int second = lb.Chip(child.Name);
				lb.Wire(input, Pin(first, child.InputPins[0].ID));
				lb.Wire(Pin(first, child.OutputPins[0].ID), Pin(second, child.InputPins[0].ID));
				lb.Wire(Pin(second, child.OutputPins[0].ID), output);
				chips[level] = lb.Build();
			}

			ChipDescription top = chips[depth];
			ChipBuilder rb = new("Root");
			PinAddress rIn = rb.Input("X");
			PinAddress rOut = rb.Output("Y");
			int dut = rb.Chip(top.Name);
			rb.Wire(rIn, Pin(dut, top.InputPins[0].ID));
			rb.Wire(Pin(dut, top.OutputPins[0].ID), rOut);

			TestCircuit circuit = new(rb.Build(), TestLibrary.Create(chips));

			// Every level contains an even number of inverters, so the chain is a buffer.
			foreach (uint value in new uint[] { 1, 0, 1, 1, 0 })
			{
				circuit.Set("X", value);
				circuit.Step();
				Assert.AreEqual(value, circuit.Get("Y"), $"depth {depth}, input {value}");
			}
		}

		[Test]
		public void SingleGateOscillatorIsBoundedAndReported()
		{
			ChipBuilder b = new("Oscillator");
			PinAddress y = b.Output("Y");
			int nand = b.Chip("NAND");
			b.Wire(NandOutput(nand), Pin(nand, NandInA));
			b.Wire(NandOutput(nand), Pin(nand, NandInB));
			b.Wire(NandOutput(nand), y);

			Stopwatch watch = Stopwatch.StartNew();
			TestCircuit circuit = new(b.Build(), TestLibrary.Create());
			circuit.Step(3);
			watch.Stop();

			Assert.IsFalse(circuit.Diagnostics.SettleConverged, "an oscillator must be reported as non-convergent");
			Assert.IsNotEmpty(circuit.Diagnostics.NonConvergenceDetails);
			Assert.Less(watch.ElapsedMilliseconds, 5000, "oscillation guard must bound the settle loop");
		}

		[Test]
		public void SameInputsGiveSameOutputsAfterReset()
		{
			ChipBuilder b = new("Xor");
			PinAddress a = b.Input("A");
			PinAddress bIn = b.Input("B");
			PinAddress y = b.Output("Y");
			int n1 = b.Nand(a, bIn);
			int n2 = b.Nand(a, NandOutput(n1));
			int n3 = b.Nand(bIn, NandOutput(n1));
			b.Wire(NandOutput(b.Nand(NandOutput(n2), NandOutput(n3))), y);
			ChipDescription xor = b.Build();

			uint[] Run()
			{
				TestCircuit circuit = new(xor, TestLibrary.Create());
				uint[] results = new uint[4];
				for (uint i = 0; i < 4; i++)
				{
					circuit.Set("A", i & 1);
					circuit.Set("B", i >> 1);
					circuit.Step();
					results[i] = circuit.Get("Y");
				}
				return results;
			}

			uint[] first = Run();
			CollectionAssert.AreEqual(new uint[] { 0, 1, 1, 0 }, first);
			CollectionAssert.AreEqual(first, Run());
		}
	}
}
