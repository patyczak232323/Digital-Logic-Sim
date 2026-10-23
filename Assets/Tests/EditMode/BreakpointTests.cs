using System.Linq;
using DLS.Description;
using DLS.Simulation;
using NUnit.Framework;
using static DLS.Tests.ChipBuilder;

namespace DLS.Tests
{
	public class BreakpointTests
	{
		[TearDown]
		public void TearDown()
		{
			SimulationBreakpoints.Clear();
			RewiredEngine.Reset();
		}

		// Y = NOT A, built from one NAND with both inputs tied to A.
		static TestCircuit Inverter()
		{
			ChipBuilder b = new("Inverter");
			PinAddress a = b.Input("A");
			PinAddress y = b.Output("Y");
			b.Wire(NandOutput(b.Nand(a, a)), y);
			return new TestCircuit(b.Build(), TestLibrary.Create());
		}

		static SimPin Output(TestCircuit c, string name)
		{
			int id = c.Description.OutputPins.First(p => p.Name == name).ID;
			return c.Root.OutputPins.First(p => p.ID == id);
		}

		// Sets A, steps once and reports whether a breakpoint fired on that step.
		static bool StepWith(TestCircuit c, uint a)
		{
			c.Set("A", a);
			c.Step();
			return SimulationBreakpoints.Check(c.Root) != null;
		}

		[Test]
		public void RisingFiresOnlyOnZeroToOne()
		{
			TestCircuit c = Inverter();
			StepWith(c, 1); // Y = 0
			SimulationBreakpoints.Set(new Breakpoint("Y", Output(c, "Y"), c.Root, 1, BreakCondition.Rising, 0));

			Assert.IsFalse(StepWith(c, 1), "first check only records the value");
			Assert.IsTrue(StepWith(c, 0), "Y 0 -> 1");
			Assert.IsFalse(StepWith(c, 0), "Y stays 1");
			Assert.IsFalse(StepWith(c, 1), "Y 1 -> 0 is a falling edge");
			Assert.IsTrue(StepWith(c, 0), "Y 0 -> 1 again");
		}

		[Test]
		public void FallingAndChangeFireOnTheRightSteps()
		{
			TestCircuit c = Inverter();
			StepWith(c, 0); // Y = 1
			SimulationBreakpoints.Set(new Breakpoint("Y", Output(c, "Y"), c.Root, 1, BreakCondition.Falling, 0));
			StepWith(c, 0);
			Assert.IsTrue(StepWith(c, 1), "Y 1 -> 0");
			Assert.IsFalse(StepWith(c, 0), "rising is not falling");

			SimulationBreakpoints.Set(new Breakpoint("Y", Output(c, "Y"), c.Root, 1, BreakCondition.Change, 0));
			Assert.AreEqual(1, SimulationBreakpoints.Count, "setting a pin again replaces its breakpoint");
			StepWith(c, 0);
			Assert.IsTrue(StepWith(c, 1));
			Assert.IsTrue(StepWith(c, 0));
			Assert.IsFalse(StepWith(c, 0));
		}

		[Test]
		public void EqualsFiresWhenTheValueIsReachedNotWhileItHolds()
		{
			TestCircuit c = Inverter();
			StepWith(c, 1); // Y = 0
			SimulationBreakpoints.Set(new Breakpoint("Y", Output(c, "Y"), c.Root, 1, BreakCondition.Equals, 1));
			StepWith(c, 1);

			Assert.IsTrue(StepWith(c, 0), "Y reaches 1");
			Assert.IsFalse(StepWith(c, 0), "resuming while Y stays 1 must not pause again");
			StepWith(c, 1);
			Assert.IsTrue(StepWith(c, 0), "Y reaches 1 again");
		}

		[Test]
		public void BreakpointsOfAnOldSimulationAreDropped()
		{
			TestCircuit c = Inverter();
			SimulationBreakpoints.Set(new Breakpoint("Y", Output(c, "Y"), c.Root, 1, BreakCondition.Change, 0));
			Assert.IsNull(SimulationBreakpoints.Check(new SimChip()));
			Assert.AreEqual(0, SimulationBreakpoints.Count);
		}
	}
}
