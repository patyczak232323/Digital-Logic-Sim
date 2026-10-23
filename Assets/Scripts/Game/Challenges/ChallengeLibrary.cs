using System;
using System.Collections.Generic;
using System.Linq;
using DLS.Description;
using DLS.Simulation;

namespace DLS.Game
{
	public sealed class Challenge
	{
		public readonly string ChipName;
		public readonly string Goal;
		public readonly string Hint;
		public readonly (string name, int bits)[] Inputs;
		public readonly (string name, int bits)[] Outputs;

		// Built-in logic chips the solution may contain (directly or inside its own custom
		// chips). Wiring chips (split, merge, bus) are always allowed.
		public readonly ChipType[] AllowedBuiltins;

		// Expected outputs for one row of input values, in Inputs/Outputs order.
		public readonly Func<uint[], uint[]> Expected;

		public Challenge(string chipName, string goal, string hint, (string, int)[] inputs, (string, int)[] outputs, Func<uint[], uint[]> expected, params ChipType[] allowedBuiltins)
		{
			ChipName = chipName;
			Goal = goal;
			Hint = hint;
			Inputs = inputs;
			Outputs = outputs;
			Expected = expected;
			AllowedBuiltins = allowedBuiltins.Length == 0 ? new[] { ChipType.Nand } : allowedBuiltins;
		}

		public string PinSummary =>
			"IN: " + string.Join(", ", Inputs.Select(Pin)) + "   OUT: " + string.Join(", ", Outputs.Select(Pin));

		static string Pin((string name, int bits) p) => p.bits == 1 ? p.name : $"{p.name}[{p.bits}]";
	}

	public readonly struct ChallengeResult
	{
		public readonly bool Passed;
		public readonly string Message;

		public ChallengeResult(bool passed, string message)
		{
			Passed = passed;
			Message = message;
		}
	}

	// A guided path from a NAND gate to a 4-bit adder. Each step is checked by building the
	// user's chip in isolation and comparing its full truth table with the goal.
	public static class ChallengeLibrary
	{
		public static readonly Challenge[] All =
		{
			new("NOT", "Make OUT the opposite of IN, using only NAND.",
				"A NAND with both inputs joined inverts its input.",
				new[] { ("IN", 1) }, new[] { ("OUT", 1) },
				v => new[] { v[0] ^ 1 }),
			new("AND", "OUT is on only when A and B are both on.",
				"AND is NAND followed by NOT.",
				new[] { ("A", 1), ("B", 1) }, new[] { ("OUT", 1) },
				v => new[] { v[0] & v[1] }),
			new("OR", "OUT is on when A or B (or both) is on.",
				"Invert both inputs, then NAND them.",
				new[] { ("A", 1), ("B", 1) }, new[] { ("OUT", 1) },
				v => new[] { v[0] | v[1] }),
			new("XOR", "OUT is on when exactly one of A and B is on.",
				"Four NANDs are enough; or combine OR, NAND and AND.",
				new[] { ("A", 1), ("B", 1) }, new[] { ("OUT", 1) },
				v => new[] { v[0] ^ v[1] }),
			new("MUX", "OUT follows A when SEL is on, and B when SEL is off.",
				"(A AND SEL) OR (B AND NOT SEL).",
				new[] { ("A", 1), ("B", 1), ("SEL", 1) }, new[] { ("OUT", 1) },
				v => new[] { v[2] == 1 ? v[0] : v[1] }),
			new("HALF ADDER", "Add two bits: SUM is the low bit, CARRY the high bit.",
				"SUM is XOR, CARRY is AND.",
				new[] { ("A", 1), ("B", 1) }, new[] { ("SUM", 1), ("CARRY", 1) },
				v => new[] { (v[0] + v[1]) & 1, (v[0] + v[1]) >> 1 }),
			new("FULL ADDER", "Add A, B and a carry-in CIN: SUM is the low bit, COUT the carry.",
				"Two half adders, and OR their carries.",
				new[] { ("A", 1), ("B", 1), ("CIN", 1) }, new[] { ("SUM", 1), ("COUT", 1) },
				v => new[] { (v[0] + v[1] + v[2]) & 1, (v[0] + v[1] + v[2]) >> 1 }),
			new("DECODER", "Turn on exactly one of Y0..Y3: the one numbered by A1 A0.",
				"Each output is an AND of A1 or NOT A1 with A0 or NOT A0.",
				new[] { ("A1", 1), ("A0", 1) }, new[] { ("Y0", 1), ("Y1", 1), ("Y2", 1), ("Y3", 1) },
				v =>
				{
					uint n = v[0] * 2 + v[1];
					return new[] { n == 0 ? 1u : 0, n == 1 ? 1u : 0, n == 2 ? 1u : 0, n == 3 ? 1u : 0 };
				}),
			new("ADDER 4", "Add two 4-bit numbers: SUM is the low 4 bits, COUT the carry.",
				"Split A and B into bits, chain four full adders, merge the sums.",
				new[] { ("A", 4), ("B", 4) }, new[] { ("SUM", 4), ("COUT", 1) },
				v => new[] { (v[0] + v[1]) & 15, (v[0] + v[1]) >> 4 })
		};

		public static ChallengeResult Check(Challenge challenge, ChipLibrary library)
		{
			if (!library.TryGetChipDescription(challenge.ChipName, out ChipDescription chip) || chip.ChipType != ChipType.Custom)
			{
				return new ChallengeResult(false, $"Build and save a chip named {challenge.ChipName}.");
			}

			// Pins are matched by name, so their order on the chip does not matter.
			int[] inputOrder = MatchPins(challenge.Inputs, chip.InputPins, out string inputProblem);
			if (inputOrder == null) return new ChallengeResult(false, inputProblem);
			int[] outputOrder = MatchPins(challenge.Outputs, chip.OutputPins, out string outputProblem);
			if (outputOrder == null) return new ChallengeResult(false, outputProblem);

			string forbidden = FindForbiddenBuiltin(chip, library, challenge.AllowedBuiltins, new HashSet<string>(ChipDescription.NameComparer));
			if (forbidden != null) return new ChallengeResult(false, $"Use only {string.Join(", ", challenge.AllowedBuiltins.Select(ChipTypeHelper.GetName))} gates here, not {forbidden}.");

			TruthTable table = TruthTableGenerator.Generate(chip, library);
			if (!table.Supported) return new ChallengeResult(false, "The chip must not store state: " + table.UnsupportedReason);

			for (int row = 0; row < table.RowCount; row++)
			{
				uint[] inputs = new uint[challenge.Inputs.Length];
				for (int i = 0; i < inputs.Length; i++) inputs[i] = table.Inputs[row][inputOrder[i]];
				uint[] expected = challenge.Expected(inputs);

				for (int o = 0; o < challenge.Outputs.Length; o++)
				{
					uint state = table.Outputs[row][outputOrder[o]];
					uint mask = (1u << challenge.Outputs[o].bits) - 1;
					bool disconnected = (PinState.GetTristateFlags(state) & mask) != 0;
					uint actual = PinState.GetBitStates(state) & mask;
					if (!disconnected && actual == (expected[o] & mask)) continue;

					string given = string.Join(" ", challenge.Inputs.Select((p, i) => $"{p.name}={inputs[i]}"));
					string got = disconnected ? "not connected" : actual.ToString();
					return new ChallengeResult(false, $"Not yet: with {given}, {challenge.Outputs[o].name} should be {expected[o] & mask} but is {got}.");
				}
			}

			return new ChallengeResult(true, $"{challenge.ChipName} works for all {table.RowCount} input combinations.");
		}

		static int[] MatchPins((string name, int bits)[] wanted, PinDescription[] pins, out string problem)
		{
			pins ??= Array.Empty<PinDescription>();
			int[] order = new int[wanted.Length];
			for (int i = 0; i < wanted.Length; i++)
			{
				order[i] = Array.FindIndex(pins, p => string.Equals(p.Name?.Trim(), wanted[i].name, StringComparison.OrdinalIgnoreCase));
				if (order[i] < 0)
				{
					problem = $"Add a pin named {wanted[i].name}.";
					return null;
				}

				if ((int)pins[order[i]].BitCount != wanted[i].bits)
				{
					problem = $"Pin {wanted[i].name} must be {wanted[i].bits}-bit.";
					return null;
				}
			}

			if (pins.Length != wanted.Length)
			{
				problem = $"The chip needs exactly the pins {string.Join(", ", wanted.Select(p => p.name))}.";
				return null;
			}

			problem = null;
			return order;
		}

		static bool IsWiring(ChipType type) => type is
			ChipType.Split_4To1Bit or ChipType.Split_8To1Bit or ChipType.Split_8To4Bit or
			ChipType.Merge_1To4Bit or ChipType.Merge_1To8Bit or ChipType.Merge_4To8Bit or
			ChipType.Bus_1Bit or ChipType.Bus_4Bit or ChipType.Bus_8Bit or
			ChipType.BusTerminus_1Bit or ChipType.BusTerminus_4Bit or ChipType.BusTerminus_8Bit;

		static string FindForbiddenBuiltin(ChipDescription chip, ChipLibrary library, ChipType[] allowed, HashSet<string> visited)
		{
			if (!visited.Add(chip.Name)) return null;

			foreach (SubChipDescription sub in chip.SubChips ?? Array.Empty<SubChipDescription>())
			{
				if (!library.TryGetChipDescription(sub.Name, out ChipDescription child)) return sub.Name;
				if (child.ChipType == ChipType.Custom)
				{
					string inner = FindForbiddenBuiltin(child, library, allowed, visited);
					if (inner != null) return inner;
				}
				else if (!IsWiring(child.ChipType) && Array.IndexOf(allowed, child.ChipType) < 0)
				{
					return child.Name;
				}
			}

			return null;
		}
	}
}
