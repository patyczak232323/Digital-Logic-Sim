using System;
using System.Collections.Generic;
using DLS.Description;
using DLS.Game;

namespace DLS.Simulation
{
	public readonly struct ChipTestVector
	{
		public readonly string Name;
		public readonly uint[] Inputs;
		public readonly uint[] ExpectedOutputs;
		public readonly uint[] OutputMasks;

		public ChipTestVector(
			string name,
			uint[] inputs,
			uint[] expectedOutputs,
			uint[] outputMasks = null)
		{
			Name = name ?? string.Empty;
			Inputs = inputs ?? Array.Empty<uint>();
			ExpectedOutputs = expectedOutputs ?? Array.Empty<uint>();
			OutputMasks = outputMasks;
		}
	}

	public readonly struct ChipTestFailure
	{
		public readonly int VectorIndex;
		public readonly string VectorName;
		public readonly int OutputIndex;
		public readonly uint Expected;
		public readonly uint Actual;
		public readonly uint Mask;
		public readonly string Reason;

		public ChipTestFailure(
			int vectorIndex,
			string vectorName,
			int outputIndex,
			uint expected,
			uint actual,
			uint mask,
			string reason)
		{
			VectorIndex = vectorIndex;
			VectorName = vectorName ?? string.Empty;
			OutputIndex = outputIndex;
			Expected = expected;
			Actual = actual;
			Mask = mask;
			Reason = reason ?? string.Empty;
		}
	}

	public sealed class ChipTestRunResult
	{
		public readonly bool Supported;
		public readonly string UnsupportedReason;
		public readonly int TotalVectors;
		public readonly int PassedVectors;
		public readonly ChipTestFailure[] Failures;

		public int FailedVectors => TotalVectors - PassedVectors;
		public bool Passed => Supported && Failures.Length == 0;

		public ChipTestRunResult(
			bool supported,
			string unsupportedReason,
			int totalVectors,
			int passedVectors,
			ChipTestFailure[] failures)
		{
			Supported = supported;
			UnsupportedReason = unsupportedReason ?? string.Empty;
			TotalVectors = totalVectors;
			PassedVectors = passedVectors;
			Failures = failures ?? Array.Empty<ChipTestFailure>();
		}
	}

	// Deterministic, side-effect-free test-vector runner for pure combinational chips.
	// It intentionally uses the same isolated evaluation primitive as full-LUT generation,
	// so tests validate gate-level truth-table behaviour rather than a cache/JIT shortcut.
	public static class CombinationalChipTestRunner
	{
		public static ChipTestRunResult Run(
			ChipDescription description,
			ChipLibrary library,
			IReadOnlyList<ChipTestVector> vectors,
			int maxFailures = 256)
		{
			if (description == null)
			{
				return Unsupported("missing chip description");
			}

			if (library == null)
			{
				return Unsupported("missing chip library");
			}

			ChipCacheAnalysis analysis = CombinationalChipCacheManager.Analyze(description, library);
			if (!analysis.CanCache)
			{
				return Unsupported("chip is not pure combinational: " + analysis.Reason);
			}

			vectors ??= Array.Empty<ChipTestVector>();
			maxFailures = Math.Max(1, maxFailures);

			SimChip chip;
			try
			{
				chip = Simulator.BuildSimChip(description, library);
			}
			catch (Exception ex)
			{
				return Unsupported("could not build chip: " + ex.GetType().Name + ": " + ex.Message);
			}

			List<ChipTestFailure> failures = new();
			int passedVectors = 0;

			for (int vectorIndex = 0; vectorIndex < vectors.Count; vectorIndex++)
			{
				ChipTestVector vector = vectors[vectorIndex];

				if (vector.Inputs.Length != chip.InputPins.Length)
				{
					failures.Add(new ChipTestFailure(
						vectorIndex,
						vector.Name,
						-1,
						(uint)chip.InputPins.Length,
						(uint)vector.Inputs.Length,
						uint.MaxValue,
						"input count mismatch"));
					if (failures.Count >= maxFailures) break;
					continue;
				}

				if (vector.ExpectedOutputs.Length != chip.OutputPins.Length)
				{
					failures.Add(new ChipTestFailure(
						vectorIndex,
						vector.Name,
						-1,
						(uint)chip.OutputPins.Length,
						(uint)vector.ExpectedOutputs.Length,
						uint.MaxValue,
						"output count mismatch"));
					if (failures.Count >= maxFailures) break;
					continue;
				}

				if (vector.OutputMasks != null && vector.OutputMasks.Length != chip.OutputPins.Length)
				{
					failures.Add(new ChipTestFailure(
						vectorIndex,
						vector.Name,
						-1,
						(uint)chip.OutputPins.Length,
						(uint)vector.OutputMasks.Length,
						uint.MaxValue,
						"output mask count mismatch"));
					if (failures.Count >= maxFailures) break;
					continue;
				}

				for (int input = 0; input < chip.InputPins.Length; input++)
				{
					chip.InputPins[input].State = vector.Inputs[input];
				}

				Simulator.EvaluatePureCombinationalForMemo(chip);

				bool vectorPassed = true;
				for (int output = 0; output < chip.OutputPins.Length; output++)
				{
					uint expected = vector.ExpectedOutputs[output];
					uint actual = chip.OutputPins[output].State;
					uint mask = vector.OutputMasks == null ? uint.MaxValue : vector.OutputMasks[output];

					if ((expected & mask) == (actual & mask)) continue;

					vectorPassed = false;
					failures.Add(new ChipTestFailure(
						vectorIndex,
						vector.Name,
						output,
						expected,
						actual,
						mask,
						"output mismatch"));

					if (failures.Count >= maxFailures) break;
				}

				if (vectorPassed) passedVectors++;
				if (failures.Count >= maxFailures) break;
			}

			return new ChipTestRunResult(
				true,
				string.Empty,
				vectors.Count,
				passedVectors,
				failures.ToArray());

			ChipTestRunResult Unsupported(string reason) =>
				new(false, reason, vectors?.Count ?? 0, 0, Array.Empty<ChipTestFailure>());
		}
	}
}
