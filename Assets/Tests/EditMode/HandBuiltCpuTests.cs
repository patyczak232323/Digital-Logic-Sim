using System;
using System.Collections.Generic;
using System.Linq;
using DLS.Description;
using DLS.Simulation;
using NUnit.Framework;
using static DLS.Tests.ChipBuilder;

namespace DLS.Tests
{
	// An 8-bit datapath built the way people build one in the editor: every level is its own
	// custom chip (NOT, AND, OR, XOR, MUX, full adder, 8-bit adder, D latch, D flip-flop,
	// 8-bit register). With every accelerator attached, as in the game, the registers must run
	// as whole feedback programs and compute exactly what the plain gate simulation computes.
	public class HandBuiltCpuTests
	{
		readonly List<ChipDescription> chips = new();

		[TearDown]
		public void TearDown() => RewiredEngine.Reset();

		ChipDescription Define(string name, string[] inputs, string[] outputs, Func<ChipBuilder, PinAddress[], PinAddress[]> body)
		{
			ChipBuilder b = new(name);
			PinAddress[] ins = inputs.Select(n => b.Input(n)).ToArray();
			PinAddress[] outs = outputs.Select(n => b.Output(n)).ToArray();
			PinAddress[] results = body(b, ins);
			for (int i = 0; i < outs.Length; i++) b.Wire(results[i], outs[i]);
			ChipDescription chip = b.Build();
			chips.Add(chip);
			return chip;
		}

		static PinAddress[] Use(ChipBuilder b, ChipDescription chip, params PinAddress[] inputs)
		{
			int id = b.Chip(chip.Name);
			for (int i = 0; i < inputs.Length; i++) b.Wire(inputs[i], Pin(id, chip.InputPins[i].ID));
			return chip.OutputPins.Select(p => Pin(id, p.ID)).ToArray();
		}

		static string[] Names(string prefix, int count) => Enumerable.Range(0, count).Select(i => prefix + i).ToArray();

		// ACC <- ACC + IN while EN is high, PC <- PC + 1, on every rising CLK edge.
		ChipDescription BuildDatapath()
		{
			ChipDescription not = Define("NOT", new[] { "A" }, new[] { "Y" }, (b, i) => new[] { NandOutput(b.Nand(i[0], i[0])) });
			ChipDescription and = Define("AND", new[] { "A", "B" }, new[] { "Y" }, (b, i) => Use(b, not, NandOutput(b.Nand(i[0], i[1]))));
			ChipDescription or = Define("OR", new[] { "A", "B" }, new[] { "Y" }, (b, i) =>
				new[] { NandOutput(b.Nand(Use(b, not, i[0])[0], Use(b, not, i[1])[0])) });
			ChipDescription xor = Define("XOR", new[] { "A", "B" }, new[] { "Y" }, (b, i) =>
				Use(b, and, Use(b, or, i[0], i[1])[0], NandOutput(b.Nand(i[0], i[1]))));
			ChipDescription mux = Define("MUX", new[] { "A", "B", "S" }, new[] { "Y" }, (b, i) =>
				Use(b, or, Use(b, and, i[0], i[2])[0], Use(b, and, i[1], Use(b, not, i[2])[0])[0]));
			ChipDescription fullAdder = Define("FULL ADDER", new[] { "A", "B", "C" }, new[] { "S", "CO" }, (b, i) =>
			{
				PinAddress x = Use(b, xor, i[0], i[1])[0];
				return new[] { Use(b, xor, x, i[2])[0], Use(b, or, Use(b, and, i[0], i[1])[0], Use(b, and, x, i[2])[0])[0] };
			});
			ChipDescription adder = Define("ADD8", Names("A", 8).Concat(Names("B", 8)).Append("CIN").ToArray(), Names("S", 8).Append("COUT").ToArray(), (b, i) =>
			{
				PinAddress[] outs = new PinAddress[9];
				PinAddress carry = i[16];
				for (int k = 0; k < 8; k++)
				{
					PinAddress[] r = Use(b, fullAdder, i[k], i[8 + k], carry);
					outs[k] = r[0];
					carry = r[1];
				}

				outs[8] = carry;
				return outs;
			});
			ChipDescription latch = Define("D LATCH", new[] { "D", "E" }, new[] { "Q" }, (b, i) =>
			{
				int set = b.Nand(i[0], i[1]);
				int reset = b.Nand(Use(b, not, i[0])[0], i[1]);
				int q = b.Chip("NAND");
				int qn = b.Chip("NAND");
				b.Wire(NandOutput(set), Pin(q, NandInA));
				b.Wire(NandOutput(qn), Pin(q, NandInB));
				b.Wire(NandOutput(reset), Pin(qn, NandInA));
				b.Wire(NandOutput(q), Pin(qn, NandInB));
				return new[] { NandOutput(q) };
			});
			ChipDescription flipFlop = Define("D FLIP FLOP", new[] { "D", "CLK" }, new[] { "Q" }, (b, i) =>
				Use(b, latch, Use(b, latch, i[0], Use(b, not, i[1])[0])[0], i[1]));
			ChipDescription register = Define("REG8", Names("D", 8).Append("EN").Append("CLK").ToArray(), Names("Q", 8), (b, i) =>
			{
				PinAddress[] outs = new PinAddress[8];
				for (int k = 0; k < 8; k++)
				{
					int ff = b.Chip(flipFlop.Name);
					outs[k] = Pin(ff, flipFlop.OutputPins[0].ID);
					b.Wire(Use(b, mux, i[k], outs[k], i[8])[0], Pin(ff, flipFlop.InputPins[0].ID));
					b.Wire(i[9], Pin(ff, flipFlop.InputPins[1].ID));
				}

				return outs;
			});

			return Define("CPU8", Names("IN", 8).Append("EN").Append("CLK").ToArray(), Names("ACC", 8).Concat(Names("PC", 8)).ToArray(), (b, i) =>
			{
				int acc = b.Chip(register.Name);
				int pc = b.Chip(register.Name);
				int add = b.Chip(adder.Name);
				int increment = b.Chip(adder.Name);
				int one = b.Nand(i[9], Use(b, not, i[9])[0]);
				int zero = b.Nand(NandOutput(one), NandOutput(one));
				PinAddress Q(int reg, int k) => Pin(reg, register.OutputPins[k].ID);
				for (int k = 0; k < 8; k++)
				{
					b.Wire(Q(acc, k), Pin(add, adder.InputPins[k].ID));
					b.Wire(i[k], Pin(add, adder.InputPins[8 + k].ID));
					b.Wire(Pin(add, adder.OutputPins[k].ID), Pin(acc, register.InputPins[k].ID));
					b.Wire(Q(pc, k), Pin(increment, adder.InputPins[k].ID));
					b.Wire(NandOutput(zero), Pin(increment, adder.InputPins[8 + k].ID));
					b.Wire(Pin(increment, adder.OutputPins[k].ID), Pin(pc, register.InputPins[k].ID));
				}

				b.Wire(NandOutput(zero), Pin(add, adder.InputPins[16].ID));
				b.Wire(NandOutput(one), Pin(increment, adder.InputPins[16].ID));
				b.Wire(i[8], Pin(acc, register.InputPins[8].ID));
				b.Wire(i[9], Pin(acc, register.InputPins[9].ID));
				b.Wire(NandOutput(one), Pin(pc, register.InputPins[8].ID));
				b.Wire(i[9], Pin(pc, register.InputPins[9].ID));
				return Enumerable.Range(0, 8).Select(k => Q(acc, k)).Concat(Enumerable.Range(0, 8).Select(k => Q(pc, k))).ToArray();
			});
		}

		static uint Read(TestCircuit c, string prefix)
		{
			uint value = 0;
			for (int k = 0; k < 8; k++) value |= c.Get(prefix + k) << k;
			return value;
		}

		[Test]
		public void RegistersRunAsFeedbackProgramsAndCountCorrectly()
		{
			ChipDescription cpu = BuildDatapath();
			TestCircuit c = new(cpu, TestLibrary.Create(chips.ToArray()));

			const uint input = 0b0101_1011;
			for (int k = 0; k < 8; k++) c.Set("IN" + k, (input >> k) & 1);
			c.Set("EN", 1);

			int feedbackHits = 0;
			int fallbacks = 0;

			// Power-on leaves the registers random; the first ticks are when the feedback
			// programs take over from the gates, so their fallbacks count too.
			for (int i = 0; i < 4; i++)
			{
				c.Set("CLK", (uint)(i & 1));
				c.Step();
				fallbacks += c.Diagnostics.FeedbackJitFallbacks;
			}

			for (int i = 0; i < 300; i++)
			{
				// Let the background FULL LUT builds finish halfway through: the topology is
				// then rebuilt while the registers hold state, which is when stale gate pins
				// used to corrupt them.
				if (i == 150) System.Threading.Thread.Sleep(1500);

				uint pc = Read(c, "PC");
				uint acc = Read(c, "ACC");
				c.Set("CLK", 0);
				c.Step();
				feedbackHits += c.Diagnostics.FeedbackJitHits;
				fallbacks += c.Diagnostics.FeedbackJitFallbacks;
				c.Set("CLK", 1);
				c.Step();
				feedbackHits += c.Diagnostics.FeedbackJitHits;
				fallbacks += c.Diagnostics.FeedbackJitFallbacks;

				Assert.AreEqual((pc + 1) & 255, Read(c, "PC"), $"PC after tick {i}");
				Assert.AreEqual((acc + input) & 255, Read(c, "ACC"), $"ACC after tick {i}");
			}

			Assert.Greater(feedbackHits, 0, "no register ran as a feedback program");
			Assert.AreEqual(0, fallbacks, "a register's feedback program fell back to the gates");
		}
	}
}
