using System;
using System.Diagnostics;

namespace DLS.Simulation
{
	public readonly struct SimulationBenchmarkResult
	{
		public readonly bool Complete;
		public readonly int WarmupSteps;
		public readonly int SampledSteps;
		public readonly double RawStepsPerSecond;
		public readonly double AverageMicrosecondsPerStep;
		public readonly double MinMicrosecondsPerStep;
		public readonly double MaxMicrosecondsPerStep;
		public readonly long GateEvaluations;
		public readonly long SignalPropagations;
		public readonly long TargetResolutions;
		public readonly long CacheHits;
		public readonly long CacheMisses;
		public readonly long NativeJitHits;
		public readonly long FeedbackJitHits;
		public readonly long FeedbackJitSweeps;
		public readonly long FeedbackJitFallbacks;
		public readonly int NonConvergentSteps;

		public SimulationBenchmarkResult(
			bool complete,
			int warmupSteps,
			int sampledSteps,
			long totalTicks,
			long minTicks,
			long maxTicks,
			long gateEvaluations,
			long signalPropagations,
			long targetResolutions,
			long cacheHits,
			long cacheMisses,
			long nativeJitHits,
			long feedbackJitHits,
			long feedbackJitSweeps,
			long feedbackJitFallbacks,
			int nonConvergentSteps)
		{
			Complete = complete;
			WarmupSteps = warmupSteps;
			SampledSteps = sampledSteps;

			double seconds = totalTicks / (double)Stopwatch.Frequency;
			RawStepsPerSecond = sampledSteps > 0 && seconds > 0 ? sampledSteps / seconds : 0;
			AverageMicrosecondsPerStep = sampledSteps > 0
				? totalTicks * 1_000_000.0 / Stopwatch.Frequency / sampledSteps
				: 0;
			MinMicrosecondsPerStep = sampledSteps > 0
				? minTicks * 1_000_000.0 / Stopwatch.Frequency
				: 0;
			MaxMicrosecondsPerStep = sampledSteps > 0
				? maxTicks * 1_000_000.0 / Stopwatch.Frequency
				: 0;

			GateEvaluations = gateEvaluations;
			SignalPropagations = signalPropagations;
			TargetResolutions = targetResolutions;
			CacheHits = cacheHits;
			CacheMisses = cacheMisses;
			NativeJitHits = nativeJitHits;
			FeedbackJitHits = feedbackJitHits;
			FeedbackJitSweeps = feedbackJitSweeps;
			FeedbackJitFallbacks = feedbackJitFallbacks;
			NonConvergentSteps = nonConvergentSteps;
		}
	}

	// Measures raw engine compute throughput in the already-running simulation. Timing
	// starts/ends inside RunSimulationStep, so the project's target-rate limiter and
	// thread sleeps are excluded. The benchmark never advances extra simulation steps.
	public static class SimulationBenchmark
	{
		static readonly object sync = new();

		static volatile bool active;
		static int requestedWarmup;
		static int warmupRemaining;
		static int requestedSamples;
		static int sampledSteps;

		static long stepStart;
		static long totalTicks;
		static long minTicks;
		static long maxTicks;
		static long gateEvaluations;
		static long signalPropagations;
		static long targetResolutions;
		static long cacheHits;
		static long cacheMisses;
		static long nativeJitHits;
		static long feedbackJitHits;
		static long feedbackJitSweeps;
		static long feedbackJitFallbacks;
		static int nonConvergentSteps;

		static SimulationBenchmarkResult latest;

		public static bool Active => active;

		public static SimulationBenchmarkResult Latest
		{
			get
			{
				lock (sync) return latest;
			}
		}

		public static void Start(int sampleSteps = 10000, int warmupSteps = 128)
		{
			lock (sync)
			{
				requestedSamples = Math.Max(1, sampleSteps);
				requestedWarmup = Math.Max(0, warmupSteps);
				warmupRemaining = requestedWarmup;
				sampledSteps = 0;
				totalTicks = 0;
				minTicks = long.MaxValue;
				maxTicks = 0;
				gateEvaluations = 0;
				signalPropagations = 0;
				targetResolutions = 0;
				cacheHits = 0;
				cacheMisses = 0;
				nativeJitHits = 0;
				feedbackJitHits = 0;
				feedbackJitSweeps = 0;
				feedbackJitFallbacks = 0;
				nonConvergentSteps = 0;
				latest = default;
				stepStart = 0;
				active = true;
			}
		}

		public static SimulationBenchmarkResult Cancel()
		{
			lock (sync)
			{
				active = false;
				latest = CreateResult(false);
				return latest;
			}
		}

		internal static void BeginStep()
		{
			if (!active) return;
			stepStart = Stopwatch.GetTimestamp();
		}

		internal static void EndStep(
			int gateEvaluationCount,
			int signalPropagationCount,
			int targetResolutionCount,
			int cacheHitCount,
			int cacheMissCount,
			int nativeJitHitCount,
			int feedbackJitHitCount,
			int feedbackJitSweepCount,
			int feedbackJitFallbackCount,
			bool converged)
		{
			if (!active) return;

			long elapsed = Stopwatch.GetTimestamp() - stepStart;
			lock (sync)
			{
				if (!active) return;

				if (warmupRemaining > 0)
				{
					warmupRemaining--;
					return;
				}

				sampledSteps++;
				totalTicks += elapsed;
				if (elapsed < minTicks) minTicks = elapsed;
				if (elapsed > maxTicks) maxTicks = elapsed;

				gateEvaluations += gateEvaluationCount;
				signalPropagations += signalPropagationCount;
				targetResolutions += targetResolutionCount;
				cacheHits += cacheHitCount;
				cacheMisses += cacheMissCount;
				nativeJitHits += nativeJitHitCount;
				feedbackJitHits += feedbackJitHitCount;
				feedbackJitSweeps += feedbackJitSweepCount;
				feedbackJitFallbacks += feedbackJitFallbackCount;
				if (!converged) nonConvergentSteps++;

				if (sampledSteps >= requestedSamples)
				{
					active = false;
					latest = CreateResult(true);
				}
			}
		}

		static SimulationBenchmarkResult CreateResult(bool complete)
		{
			long effectiveMin = sampledSteps == 0 || minTicks == long.MaxValue ? 0 : minTicks;
			return new SimulationBenchmarkResult(
				complete,
				requestedWarmup,
				sampledSteps,
				totalTicks,
				effectiveMin,
				maxTicks,
				gateEvaluations,
				signalPropagations,
				targetResolutions,
				cacheHits,
				cacheMisses,
				nativeJitHits,
				feedbackJitHits,
				feedbackJitSweeps,
				feedbackJitFallbacks,
				nonConvergentSteps);
		}

		public static void Reset()
		{
			lock (sync)
			{
				active = false;
				requestedWarmup = 0;
				warmupRemaining = 0;
				requestedSamples = 0;
				sampledSteps = 0;
				stepStart = 0;
				totalTicks = 0;
				minTicks = long.MaxValue;
				maxTicks = 0;
				gateEvaluations = 0;
				signalPropagations = 0;
				targetResolutions = 0;
				cacheHits = 0;
				cacheMisses = 0;
				nativeJitHits = 0;
				feedbackJitHits = 0;
				feedbackJitSweeps = 0;
				feedbackJitFallbacks = 0;
				nonConvergentSteps = 0;
				latest = default;
			}
		}
	}
}
