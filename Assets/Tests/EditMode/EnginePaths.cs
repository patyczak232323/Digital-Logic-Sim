using System;
using System.Diagnostics;
using System.Threading;
using DLS.Simulation;
using NUnit.Framework;

namespace DLS.Tests
{
	public enum EnginePath
	{
		// Plain deterministic gate simulation: every accelerator detached.
		Reference,
		NativeJit,
		FullLut,
		FeedbackJit
	}

	// Detaches every accelerator except the requested one, so a test can run the same
	// circuit through each execution path and compare the results with the reference.
	public static class EnginePaths
	{
		static readonly TimeSpan LutTimeout = TimeSpan.FromSeconds(60);

		public static Action<SimChip> Force(EnginePath path) => root => Apply(root, path);

		static void Apply(SimChip chip, EnginePath path)
		{
			for (int i = 0; i < chip.SubChips.Length; i++) Apply(chip.SubChips[i], path);

			if (path != EnginePath.FullLut) chip.MemoCache = null;
			if (path != EnginePath.NativeJit) chip.CompiledExecutor = null;
			if (path != EnginePath.FeedbackJit) chip.FeedbackExecutor = null;

			if (chip.MemoCache != null) WaitUntilBuilt(chip.MemoCache);
		}

		// LUTs are built on a background worker; block until this one is published.
		internal static void WaitUntilBuilt(CombinationalChipMemoCache cache)
		{
			Stopwatch timer = Stopwatch.StartNew();
			while (cache.Working || !cache.Ready)
			{
				if (!cache.Working && !cache.Ready)
				{
					Assert.Fail("FULL LUT build stopped without a table: " + cache.BuildFailureReason);
				}

				if (timer.Elapsed > LutTimeout) Assert.Fail("FULL LUT build timed out: " + cache.PersistenceMessage);
				Thread.Sleep(5);
			}
		}
	}
}
