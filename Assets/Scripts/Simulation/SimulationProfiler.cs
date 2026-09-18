using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace DLS.Simulation
{
	public enum SimulationExecutionPath
	{
		Live,
		FullLut,
		NativeJit,
		FeedbackJit
	}

	public readonly struct SimulationHotChip
	{
		public readonly string Path;
		public readonly SimulationExecutionPath ExecutionPath;
		public readonly long Evaluations;
		public readonly double TotalMilliseconds;
		public readonly double AverageMicroseconds;
		public readonly double MaxMicroseconds;

		public SimulationHotChip(
			string path,
			SimulationExecutionPath executionPath,
			long evaluations,
			long totalTicks,
			long maxTicks)
		{
			Path = path;
			ExecutionPath = executionPath;
			Evaluations = evaluations;
			TotalMilliseconds = TicksToMilliseconds(totalTicks);
			AverageMicroseconds = evaluations > 0 ? TicksToMicroseconds(totalTicks) / evaluations : 0;
			MaxMicroseconds = TicksToMicroseconds(maxTicks);
		}

		static double TicksToMilliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
		static double TicksToMicroseconds(long ticks) => ticks * 1_000_000.0 / Stopwatch.Frequency;
	}

	public readonly struct SimulationStepProfile
	{
		public readonly int Frame;
		public readonly double StepMilliseconds;
		public readonly int DeltaCycles;
		public readonly int GateEvaluations;
		public readonly int SignalPropagations;
		public readonly int TargetResolutions;
		public readonly int CacheHits;
		public readonly int CacheMisses;
		public readonly int JitHits;
		public readonly int FeedbackJitHits;
		public readonly int FeedbackJitSweeps;
		public readonly int FeedbackJitFallbacks;
		public readonly bool Converged;

		public SimulationStepProfile(
			int frame,
			long elapsedTicks,
			int deltaCycles,
			int gateEvaluations,
			int signalPropagations,
			int targetResolutions,
			int cacheHits,
			int cacheMisses,
			int jitHits,
			int feedbackJitHits,
			int feedbackJitSweeps,
			int feedbackJitFallbacks,
			bool converged)
		{
			Frame = frame;
			StepMilliseconds = elapsedTicks * 1000.0 / Stopwatch.Frequency;
			DeltaCycles = deltaCycles;
			GateEvaluations = gateEvaluations;
			SignalPropagations = signalPropagations;
			TargetResolutions = targetResolutions;
			CacheHits = cacheHits;
			CacheMisses = cacheMisses;
			JitHits = jitHits;
			FeedbackJitHits = feedbackJitHits;
			FeedbackJitSweeps = feedbackJitSweeps;
			FeedbackJitFallbacks = feedbackJitFallbacks;
			Converged = converged;
		}
	}

	// Optional low-overhead-at-rest profiler. When disabled, the deterministic hot path
	// performs only one boolean check per evaluated chip. Timing and dictionary work are
	// done only while profiling is explicitly enabled.
	public static class SimulationProfiler
	{
		sealed class MutableChipStats
		{
			public string Path;
			public SimulationExecutionPath ExecutionPath;
			public long Evaluations;
			public long TotalTicks;
			public long MaxTicks;
		}

		static readonly object sync = new();
		static readonly Dictionary<SimChip, MutableChipStats> chipStats = new();
		static volatile bool enabled;
		static long stepStartTimestamp;
		static SimulationStepProfile lastStep;

		public static bool Enabled
		{
			get => enabled;
			set => enabled = value;
		}

		public static SimulationStepProfile LastStep
		{
			get
			{
				lock (sync) return lastStep;
			}
		}

		internal static void BeginStep()
		{
			if (!enabled) return;
			stepStartTimestamp = Stopwatch.GetTimestamp();
		}

		internal static void RecordChip(
			SimChip chip,
			string path,
			SimulationExecutionPath executionPath,
			long elapsedTicks)
		{
			if (!enabled || chip == null) return;

			lock (sync)
			{
				if (!chipStats.TryGetValue(chip, out MutableChipStats stats))
				{
					stats = new MutableChipStats();
					chipStats.Add(chip, stats);
				}

				stats.Path = path ?? chip.ChipType.ToString();
				stats.ExecutionPath = executionPath;
				stats.Evaluations++;
				stats.TotalTicks += elapsedTicks;
				if (elapsedTicks > stats.MaxTicks) stats.MaxTicks = elapsedTicks;
			}
		}

		internal static void EndStep(
			int frame,
			int deltaCycles,
			int gateEvaluations,
			int signalPropagations,
			int targetResolutions,
			int cacheHits,
			int cacheMisses,
			int jitHits,
			int feedbackJitHits,
			int feedbackJitSweeps,
			int feedbackJitFallbacks,
			bool converged)
		{
			if (!enabled) return;

			long elapsedTicks = Stopwatch.GetTimestamp() - stepStartTimestamp;
			SimulationStepProfile snapshot = new(
				frame,
				elapsedTicks,
				deltaCycles,
				gateEvaluations,
				signalPropagations,
				targetResolutions,
				cacheHits,
				cacheMisses,
				jitHits,
				feedbackJitHits,
				feedbackJitSweeps,
				feedbackJitFallbacks,
				converged);

			lock (sync) lastStep = snapshot;
		}

		public static SimulationHotChip[] GetHotChips(int maxCount = 16)
		{
			if (maxCount <= 0) return Array.Empty<SimulationHotChip>();

			lock (sync)
			{
				List<SimulationHotChip> snapshots = new(chipStats.Count);
				foreach (MutableChipStats stats in chipStats.Values)
				{
					snapshots.Add(new SimulationHotChip(
						stats.Path,
						stats.ExecutionPath,
						stats.Evaluations,
						stats.TotalTicks,
						stats.MaxTicks));
				}

				snapshots.Sort((a, b) => b.TotalMilliseconds.CompareTo(a.TotalMilliseconds));
				if (snapshots.Count > maxCount) snapshots.RemoveRange(maxCount, snapshots.Count - maxCount);
				return snapshots.ToArray();
			}
		}

		public static void Reset()
		{
			lock (sync)
			{
				chipStats.Clear();
				lastStep = default;
			}
			stepStartTimestamp = 0;
		}
	}
}
