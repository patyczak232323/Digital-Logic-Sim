using System;
using System.Collections.Generic;
using DLS.Description;
using DLS.Game;

namespace DLS.Simulation
{
	public sealed class TruthTable
	{
		public readonly bool Supported;
		public readonly string UnsupportedReason;
		public readonly string[] InputNames;
		public readonly int[] InputBits;
		public readonly string[] OutputNames;
		public readonly int[] OutputBits;
		public readonly int TotalInputBits;

		// True when the chip has too many input bits for every row, so the table holds a
		// fixed pseudo-random sample instead.
		public readonly bool Sampled;

		// One row per input combination. Values are raw pin states, so an output that is
		// disconnected keeps its tristate flags.
		public readonly uint[][] Inputs;
		public readonly uint[][] Outputs;

		public int RowCount => Inputs.Length;

		public TruthTable(
			bool supported,
			string unsupportedReason,
			string[] inputNames,
			int[] inputBits,
			string[] outputNames,
			int[] outputBits,
			bool sampled,
			uint[][] inputs,
			uint[][] outputs)
		{
			Supported = supported;
			UnsupportedReason = unsupportedReason ?? string.Empty;
			InputNames = inputNames ?? Array.Empty<string>();
			InputBits = inputBits ?? Array.Empty<int>();
			OutputNames = outputNames ?? Array.Empty<string>();
			OutputBits = outputBits ?? Array.Empty<int>();
			Sampled = sampled;
			Inputs = inputs ?? Array.Empty<uint[]>();
			Outputs = outputs ?? Array.Empty<uint[]>();

			int total = 0;
			for (int i = 0; i < InputBits.Length; i++) total += InputBits[i];
			TotalInputBits = total;
		}
	}

	// Builds truth tables for pure combinational chips and records/runs them as tests.
	// Rows are evaluated on an isolated gate-level copy of the chip (the same primitive the
	// FULL LUT builder uses), so the live simulation is never touched.
	public static class TruthTableGenerator
	{
		public const int MaxExhaustiveInputBits = 12;
		public const int SampledRowCount = 4096;
		const int SampleSeed = 0x7AB1E;

		public static TruthTable Generate(ChipDescription description, ChipLibrary library)
		{
			if (description == null) return Unsupported(null, "missing chip description");
			if (library == null) return Unsupported(description, "missing chip library");

			ChipCacheAnalysis analysis = CombinationalChipCacheManager.Analyze(description, library);
			if (!analysis.CanCache) return Unsupported(description, analysis.Reason);

			PinDescription[] inputPins = description.InputPins ?? Array.Empty<PinDescription>();
			int[] widths = new int[inputPins.Length];
			int totalBits = 0;
			for (int i = 0; i < inputPins.Length; i++)
			{
				widths[i] = (int)inputPins[i].BitCount;
				totalBits += widths[i];
			}

			bool sampled = totalBits > MaxExhaustiveInputBits;
			uint[][] inputs = sampled ? SampleInputs(widths) : EnumerateInputs(widths, totalBits);

			uint[][] outputs = Evaluate(description, library, inputs, out string error);
			if (outputs == null) return Unsupported(description, error);

			return new TruthTable(
				true,
				string.Empty,
				Names(inputPins),
				widths,
				Names(description.OutputPins),
				Widths(description.OutputPins),
				sampled,
				inputs,
				outputs);
		}

		public static ChipTestSuite RecordTests(TruthTable table, string chipName)
		{
			if (table == null || !table.Supported) throw new ArgumentException("Only a supported truth table can be recorded.");

			return new ChipTestSuite
			{
				ChipName = chipName,
				InputNames = (string[])table.InputNames.Clone(),
				OutputNames = (string[])table.OutputNames.Clone(),
				Inputs = table.Inputs,
				Outputs = table.Outputs
			};
		}

		public static ChipTestRunResult RunTests(ChipTestSuite suite, ChipDescription description, ChipLibrary library)
		{
			if (suite == null) return Fail("no recorded tests", 0);

			int count = suite.Inputs?.Length ?? 0;
			if (suite.Outputs == null || suite.Outputs.Length != count) return Fail("recorded tests are damaged", count);

			if (!SameNames(suite.InputNames, description?.InputPins) || !SameNames(suite.OutputNames, description?.OutputPins))
			{
				return Fail("the chip's pins changed since the tests were recorded", count);
			}

			ChipTestVector[] vectors = new ChipTestVector[count];
			for (int i = 0; i < count; i++) vectors[i] = new ChipTestVector($"row {i}", suite.Inputs[i], suite.Outputs[i]);
			return CombinationalChipTestRunner.Run(description, library, vectors, count);

			static ChipTestRunResult Fail(string reason, int vectorCount) =>
				new(false, reason, vectorCount, 0, Array.Empty<ChipTestFailure>());
		}

		// Evaluates each input row on an isolated copy of the chip. Returns null (with an
		// error) when the chip cannot be built.
		public static uint[][] Evaluate(ChipDescription description, ChipLibrary library, uint[][] inputs, out string error)
		{
			SimChip chip;
			try
			{
				chip = CombinationalChipTestRunner.BuildIsolatedChip(description, library);
			}
			catch (Exception ex)
			{
				error = "could not build chip: " + ex.Message;
				return null;
			}

			uint[][] outputs = new uint[inputs.Length][];
			for (int row = 0; row < inputs.Length; row++)
			{
				for (int i = 0; i < chip.InputPins.Length; i++) chip.InputPins[i].State = inputs[row][i];
				Simulator.EvaluatePureCombinationalForMemo(chip);

				uint[] values = new uint[chip.OutputPins.Length];
				for (int o = 0; o < values.Length; o++) values[o] = chip.OutputPins[o].State;
				outputs[row] = values;
			}

			error = string.Empty;
			return outputs;
		}

		// Counting order with the first input pin as the most significant digit, like a
		// hand-written truth table (A B: 00, 01, 10, 11).
		static uint[][] EnumerateInputs(int[] widths, int totalBits)
		{
			int rowCount = 1 << totalBits;
			uint[][] rows = new uint[rowCount][];
			for (int row = 0; row < rowCount; row++)
			{
				uint[] values = new uint[widths.Length];
				int shift = 0;
				for (int i = widths.Length - 1; i >= 0; i--)
				{
					values[i] = (uint)(row >> shift) & Mask(widths[i]);
					shift += widths[i];
				}

				rows[row] = values;
			}

			return rows;
		}

		// All-low and all-high first, then a fixed pseudo-random sample, so the same chip
		// always gives the same table and recorded tests stay comparable.
		static uint[][] SampleInputs(int[] widths)
		{
			Random random = new(SampleSeed);
			List<uint[]> rows = new(SampledRowCount);
			uint[] low = new uint[widths.Length];
			uint[] high = new uint[widths.Length];
			for (int i = 0; i < widths.Length; i++) high[i] = Mask(widths[i]);
			rows.Add(low);
			rows.Add(high);

			while (rows.Count < SampledRowCount)
			{
				uint[] values = new uint[widths.Length];
				for (int i = 0; i < widths.Length; i++) values[i] = (uint)random.Next(1 << widths[i]);
				rows.Add(values);
			}

			return rows.ToArray();
		}

		static uint Mask(int bits) => (1u << bits) - 1;

		static TruthTable Unsupported(ChipDescription description, string reason) =>
			new(
				false,
				reason,
				Names(description?.InputPins),
				Widths(description?.InputPins),
				Names(description?.OutputPins),
				Widths(description?.OutputPins),
				false,
				Array.Empty<uint[]>(),
				Array.Empty<uint[]>());

		static string[] Names(PinDescription[] pins)
		{
			if (pins == null) return Array.Empty<string>();
			string[] names = new string[pins.Length];
			for (int i = 0; i < pins.Length; i++) names[i] = pins[i].Name ?? string.Empty;
			return names;
		}

		static int[] Widths(PinDescription[] pins)
		{
			if (pins == null) return Array.Empty<int>();
			int[] widths = new int[pins.Length];
			for (int i = 0; i < pins.Length; i++) widths[i] = (int)pins[i].BitCount;
			return widths;
		}

		static bool SameNames(string[] names, PinDescription[] pins)
		{
			pins ??= Array.Empty<PinDescription>();
			if (names == null || names.Length != pins.Length) return false;
			for (int i = 0; i < names.Length; i++)
			{
				if (!string.Equals(names[i], pins[i].Name, StringComparison.Ordinal)) return false;
			}

			return true;
		}
	}
}
