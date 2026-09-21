using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using DLS.Description;
using DLS.Game;

namespace DLS.Simulation
{
	public readonly struct CompatibilityStep
	{
		public readonly string Name;
		public readonly uint[] Inputs;
		public readonly uint[] ExpectedOutputs;
		public readonly uint[] OutputMasks;

		public CompatibilityStep(
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

	public readonly struct CompatibilitySample
	{
		public readonly int StepIndex;
		public readonly string StepName;
		public readonly uint[] Inputs;
		public readonly uint[] Outputs;
		public readonly RewiredEngine.DiagnosticsSnapshot Diagnostics;

		public CompatibilitySample(
			int stepIndex,
			string stepName,
			uint[] inputs,
			uint[] outputs,
			RewiredEngine.DiagnosticsSnapshot diagnostics)
		{
			StepIndex = stepIndex;
			StepName = stepName ?? string.Empty;
			Inputs = inputs ?? Array.Empty<uint>();
			Outputs = outputs ?? Array.Empty<uint>();
			Diagnostics = diagnostics;
		}
	}

	public readonly struct CompatibilityFailure
	{
		public readonly int StepIndex;
		public readonly string StepName;
		public readonly int OutputIndex;
		public readonly uint Expected;
		public readonly uint Actual;
		public readonly uint Mask;
		public readonly string Reason;

		public CompatibilityFailure(
			int stepIndex,
			string stepName,
			int outputIndex,
			uint expected,
			uint actual,
			uint mask,
			string reason)
		{
			StepIndex = stepIndex;
			StepName = stepName ?? string.Empty;
			OutputIndex = outputIndex;
			Expected = expected;
			Actual = actual;
			Mask = mask;
			Reason = reason ?? string.Empty;
		}
	}

	public sealed class CompatibilityRunResult
	{
		public readonly bool Supported;
		public readonly string UnsupportedReason;
		public readonly CompatibilitySample[] Samples;
		public readonly CompatibilityFailure[] Failures;

		public bool Passed => Supported && Failures.Length == 0;

		public CompatibilityRunResult(
			bool supported,
			string unsupportedReason,
			CompatibilitySample[] samples,
			CompatibilityFailure[] failures)
		{
			Supported = supported;
			UnsupportedReason = unsupportedReason ?? string.Empty;
			Samples = samples ?? Array.Empty<CompatibilitySample>();
			Failures = failures ?? Array.Empty<CompatibilityFailure>();
		}
	}

	public sealed class CompatibilityParityResult
	{
		public readonly bool Supported;
		public readonly string UnsupportedReason;
		public readonly CompatibilitySample[] LiveSamples;
		public readonly CompatibilitySample[] AcceleratedSamples;
		public readonly CompatibilityFailure[] Failures;
		public readonly bool AccelerationObserved;
		public readonly bool CacheObserved;
		public readonly bool JitObserved;
		public readonly bool FeedbackJitObserved;

		public bool Passed => Supported && Failures.Length == 0;

		public CompatibilityParityResult(
			bool supported,
			string unsupportedReason,
			CompatibilitySample[] liveSamples,
			CompatibilitySample[] acceleratedSamples,
			CompatibilityFailure[] failures,
			bool accelerationObserved,
			bool cacheObserved = false,
			bool jitObserved = false,
			bool feedbackJitObserved = false)
		{
			Supported = supported;
			UnsupportedReason = unsupportedReason ?? string.Empty;
			LiveSamples = liveSamples ?? Array.Empty<CompatibilitySample>();
			AcceleratedSamples = acceleratedSamples ?? Array.Empty<CompatibilitySample>();
			Failures = failures ?? Array.Empty<CompatibilityFailure>();
			AccelerationObserved = accelerationObserved;
			CacheObserved = cacheObserved;
			JitObserved = jitObserved;
			FeedbackJitObserved = feedbackJitObserved;
		}
	}

	/// <summary>
	/// Step-by-step compatibility harness for Rewired.
	///
	/// It is intentionally separate from CombinationalChipTestRunner:
	/// - sequential state is preserved between vectors,
	/// - every vector advances one real simulation step,
	/// - Clock/Pulse/RAM/ROM and feedback circuits can be tested,
	/// - the same vector stream can be compared against a golden trace,
	/// - accelerated execution can be compared against the fully expanded live solver.
	///
	/// A golden trace can be recorded from a known-good/reference build and then encoded as
	/// CompatibilityStep.ExpectedOutputs. The runner compares every output after every step,
	/// which catches ordering/timing regressions that final-state-only tests miss.
	/// </summary>
	public static class CompatibilityTestRunner
	{
		static readonly object runLock = new();

		public static CompatibilityRunResult RunGolden(
			ChipDescription description,
			ChipLibrary library,
			IReadOnlyList<CompatibilityStep> steps,
			int stepsPerClockTransition = 1,
			int maxFailures = 256)
		{
			if (description == null) return Unsupported("missing chip description");
			if (library == null) return Unsupported("missing chip library");
			steps ??= Array.Empty<CompatibilityStep>();
			maxFailures = Math.Max(1, maxFailures);

			uint[][] vectors = new uint[steps.Count][];
			for (int i = 0; i < steps.Count; i++) vectors[i] = steps[i].Inputs;

			CompatibilitySample[] samples;
			string error = CaptureInternal(
				description,
				library,
				vectors,
				stepsPerClockTransition,
				disableAcceleration: false,
				waitForFullLut: false,
				out samples);

			if (!string.IsNullOrEmpty(error))
			{
				return new CompatibilityRunResult(
					false,
					error,
					samples ?? Array.Empty<CompatibilitySample>(),
					Array.Empty<CompatibilityFailure>());
			}

			List<CompatibilityFailure> failures = new();
			for (int stepIndex = 0; stepIndex < steps.Count && failures.Count < maxFailures; stepIndex++)
			{
				CompatibilityStep step = steps[stepIndex];
				CompatibilitySample sample = samples[stepIndex];

				if (step.ExpectedOutputs.Length != sample.Outputs.Length)
				{
					failures.Add(new CompatibilityFailure(
						stepIndex,
						step.Name,
						-1,
						(uint)sample.Outputs.Length,
						(uint)step.ExpectedOutputs.Length,
						uint.MaxValue,
						"output count mismatch"));
					continue;
				}

				if (step.OutputMasks != null && step.OutputMasks.Length != sample.Outputs.Length)
				{
					failures.Add(new CompatibilityFailure(
						stepIndex,
						step.Name,
						-1,
						(uint)sample.Outputs.Length,
						(uint)step.OutputMasks.Length,
						uint.MaxValue,
						"output mask count mismatch"));
					continue;
				}

				for (int outputIndex = 0; outputIndex < sample.Outputs.Length; outputIndex++)
				{
					uint expected = step.ExpectedOutputs[outputIndex];
					uint actual = sample.Outputs[outputIndex];
					uint mask = step.OutputMasks == null ? uint.MaxValue : step.OutputMasks[outputIndex];

					if ((expected & mask) == (actual & mask)) continue;

					failures.Add(new CompatibilityFailure(
						stepIndex,
						step.Name,
						outputIndex,
						expected,
						actual,
						mask,
						"golden trace mismatch"));

					if (failures.Count >= maxFailures) break;
				}
			}

			return new CompatibilityRunResult(true, string.Empty, samples, failures.ToArray());

			CompatibilityRunResult Unsupported(string reason) =>
				new(false, reason, Array.Empty<CompatibilitySample>(), Array.Empty<CompatibilityFailure>());
		}

		public static CompatibilitySample[] CaptureTrace(
			ChipDescription description,
			ChipLibrary library,
			IReadOnlyList<uint[]> inputVectors,
			int stepsPerClockTransition = 1)
		{
			if (description == null) throw new ArgumentNullException(nameof(description));
			if (library == null) throw new ArgumentNullException(nameof(library));

			inputVectors ??= Array.Empty<uint[]>();
			string error = CaptureInternal(
				description,
				library,
				inputVectors,
				stepsPerClockTransition,
				disableAcceleration: false,
				waitForFullLut: false,
				out CompatibilitySample[] samples);

			if (!string.IsNullOrEmpty(error)) throw new InvalidOperationException(error);
			return samples;
		}

		public static CompatibilityParityResult RunAccelerationParity(
			ChipDescription description,
			ChipLibrary library,
			IReadOnlyList<uint[]> inputVectors,
			int stepsPerClockTransition = 1,
			int maxFailures = 256,
			bool waitForFullLut = false)
		{
			if (description == null) return Unsupported("missing chip description");
			if (library == null) return Unsupported("missing chip library");
			inputVectors ??= Array.Empty<uint[]>();
			maxFailures = Math.Max(1, maxFailures);

			string liveError = CaptureInternal(
				description,
				library,
				inputVectors,
				stepsPerClockTransition,
				disableAcceleration: true,
				waitForFullLut: false,
				out CompatibilitySample[] live);

			if (!string.IsNullOrEmpty(liveError))
			{
				return new CompatibilityParityResult(
					false,
					"live solver: " + liveError,
					live ?? Array.Empty<CompatibilitySample>(),
					Array.Empty<CompatibilitySample>(),
					Array.Empty<CompatibilityFailure>(),
					false);
			}

			string acceleratedError = CaptureInternal(
				description,
				library,
				inputVectors,
				stepsPerClockTransition,
				disableAcceleration: false,
				waitForFullLut,
				out CompatibilitySample[] accelerated);

			if (!string.IsNullOrEmpty(acceleratedError))
			{
				return new CompatibilityParityResult(
					false,
					"accelerated solver: " + acceleratedError,
					live,
					accelerated ?? Array.Empty<CompatibilitySample>(),
					Array.Empty<CompatibilityFailure>(),
					false);
			}

			List<CompatibilityFailure> failures = new();
			bool cacheObserved = false;
			bool jitObserved = false;
			bool feedbackJitObserved = false;
			bool accelerationObserved = false;

			for (int stepIndex = 0; stepIndex < accelerated.Length && failures.Count < maxFailures; stepIndex++)
			{
				RewiredEngine.DiagnosticsSnapshot diagnostics = accelerated[stepIndex].Diagnostics;
				cacheObserved |= diagnostics.CacheHits > 0;
				jitObserved |= diagnostics.JitHits > 0;
				feedbackJitObserved |= diagnostics.FeedbackJitHits > 0;
				accelerationObserved |= cacheObserved || jitObserved || feedbackJitObserved;

				uint[] expected = live[stepIndex].Outputs;
				uint[] actual = accelerated[stepIndex].Outputs;

				if (expected.Length != actual.Length)
				{
					failures.Add(new CompatibilityFailure(
						stepIndex,
						accelerated[stepIndex].StepName,
						-1,
						(uint)expected.Length,
						(uint)actual.Length,
						uint.MaxValue,
						"live/accelerated output count mismatch"));
					continue;
				}

				for (int outputIndex = 0; outputIndex < expected.Length; outputIndex++)
				{
					if (expected[outputIndex] == actual[outputIndex]) continue;

					failures.Add(new CompatibilityFailure(
						stepIndex,
						accelerated[stepIndex].StepName,
						outputIndex,
						expected[outputIndex],
						actual[outputIndex],
						uint.MaxValue,
						"live/accelerated mismatch"));

					if (failures.Count >= maxFailures) break;
				}
			}

			return new CompatibilityParityResult(
				true,
				string.Empty,
				live,
				accelerated,
				failures.ToArray(),
				accelerationObserved,
				cacheObserved,
				jitObserved,
				feedbackJitObserved);

			CompatibilityParityResult Unsupported(string reason) =>
				new(
					false,
					reason,
					Array.Empty<CompatibilitySample>(),
					Array.Empty<CompatibilitySample>(),
					Array.Empty<CompatibilityFailure>(),
					false);
		}

		static string CaptureInternal(
			ChipDescription description,
			ChipLibrary library,
			IReadOnlyList<uint[]> vectors,
			int stepsPerClockTransition,
			bool disableAcceleration,
			bool waitForFullLut,
			out CompatibilitySample[] samples)
		{
			samples = Array.Empty<CompatibilitySample>();
			vectors ??= Array.Empty<uint[]>();

			lock (runLock)
			{
				int oldClockSteps = RewiredEngine.StepsPerClockTransition;
				try
				{
					RewiredEngine.Reset();
					RewiredEngine.StepsPerClockTransition = Math.Max(0, stepsPerClockTransition);

					SimChip root = RewiredEngine.BuildChip(description, library);
					if (disableAcceleration)
					{
						DisableAccelerationRecursive(root);
						RewiredEngine.InvalidateTopology();
					}
					else if (waitForFullLut)
					{
						string cacheError = WaitForFullLutReady(description, library, 5000);
						if (cacheError != null) return cacheError;
						RewiredEngine.InvalidateTopology();
					}

					DevPinInstance[] inputPins = CreateInputPins(description);
					SimAudio audio = new();

					if (vectors.Count > 0)
					{
						string vectorError = ApplyInputs(inputPins, vectors[0], 0);
						if (vectorError != null) return vectorError;
					}

					RewiredEngine.EnsureInitialized(root, inputPins, audio);

					CompatibilitySample[] captured = new CompatibilitySample[vectors.Count];
					for (int stepIndex = 0; stepIndex < vectors.Count; stepIndex++)
					{
						uint[] vector = vectors[stepIndex] ?? Array.Empty<uint>();
						string vectorError = ApplyInputs(inputPins, vector, stepIndex);
						if (vectorError != null) return vectorError;

						RewiredEngine.RunStep(root, inputPins, audio);

						uint[] outputs = new uint[root.OutputPins.Length];
						for (int outputIndex = 0; outputIndex < outputs.Length; outputIndex++)
						{
							outputs[outputIndex] = root.OutputPins[outputIndex].State;
						}

						captured[stepIndex] = new CompatibilitySample(
							stepIndex,
							"step " + stepIndex,
							(uint[])vector.Clone(),
							outputs,
							RewiredEngine.Diagnostics);
					}

					samples = captured;
					return string.Empty;
				}
				catch (Exception ex)
				{
					return ex.GetType().Name + ": " + ex.Message;
				}
				finally
				{
					RewiredEngine.Reset();
					RewiredEngine.StepsPerClockTransition = oldClockSteps;
				}
			}
		}


		static string WaitForFullLutReady(
			ChipDescription root,
			ChipLibrary library,
			int timeoutMilliseconds)
		{
			List<ChipDescription> expectedCaches = new();
			HashSet<string> visited = new(ChipDescription.NameComparer);

			void Visit(ChipDescription description)
			{
				if (description == null || description.ChipType != ChipType.Custom) return;
				if (!visited.Add(description.Name ?? string.Empty)) return;

				if (description.CacheMode != ChipCacheMode.Normal)
				{
					ChipCacheAnalysis analysis = CombinationalChipCacheManager.Analyze(description, library);
					if (CombinationalChipCacheManager.CanBuildFullCache(description, analysis, out _))
					{
						expectedCaches.Add(description);
					}
				}

				SubChipDescription[] subChips = description.SubChips ?? Array.Empty<SubChipDescription>();
				for (int i = 0; i < subChips.Length; i++)
				{
					if (library.TryGetChipDescription(subChips[i].Name, out ChipDescription child)) Visit(child);
				}
			}

			Visit(root);
			if (expectedCaches.Count == 0) return "FULL LUT requested but no cacheable Cached/Auto Custom Chip exists in the test graph";

			Stopwatch timer = Stopwatch.StartNew();
			while (timer.ElapsedMilliseconds < timeoutMilliseconds)
			{
				bool allReady = true;
				for (int i = 0; i < expectedCaches.Count; i++)
				{
					ChipDescription description = expectedCaches[i];
					if (!CombinationalChipCacheManager.TryGetBuildInfo(
						    description,
						    out bool ready,
						    out _,
						    out _,
						    out string failureReason))
					{
						allReady = false;
						continue;
					}

					if (!string.IsNullOrWhiteSpace(failureReason))
					{
						return $"FULL LUT failed for {description.Name}: {failureReason}";
					}

					if (!ready) allReady = false;
				}

				if (allReady) return null;
				Thread.Sleep(5);
			}

			return $"FULL LUT did not become ready within {timeoutMilliseconds} ms";
		}

		static DevPinInstance[] CreateInputPins(ChipDescription description)
		{
			PinDescription[] descriptions = description.InputPins ?? Array.Empty<PinDescription>();
			DevPinInstance[] result = new DevPinInstance[descriptions.Length];
			for (int i = 0; i < descriptions.Length; i++)
			{
				result[i] = new DevPinInstance(descriptions[i], true);
			}
			return result;
		}

		static string ApplyInputs(DevPinInstance[] inputs, uint[] vector, int stepIndex)
		{
			vector ??= Array.Empty<uint>();
			if (vector.Length != inputs.Length)
			{
				return $"step {stepIndex}: input count mismatch; expected {inputs.Length}, got {vector.Length}";
			}

			for (int i = 0; i < inputs.Length; i++)
			{
				inputs[i].Pin.PlayerInputState = vector[i];
			}
			return null;
		}

		static void DisableAccelerationRecursive(SimChip chip)
		{
			if (chip == null) return;

			chip.MemoCache = null;
			chip.CompiledExecutor = null;
			if (chip.FeedbackExecutor != null)
			{
				chip.FeedbackExecutor.SetRuntimeActive(false);
				chip.FeedbackExecutor = null;
			}

			for (int i = 0; i < chip.SubChips.Length; i++)
			{
				DisableAccelerationRecursive(chip.SubChips[i]);
			}
		}
	}
}
