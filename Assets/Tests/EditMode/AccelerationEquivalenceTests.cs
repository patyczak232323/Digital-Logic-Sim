using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DLS.Description;
using DLS.Game;
using DLS.RHDL;
using DLS.Simulation;
using NUnit.Framework;
using UnityEngine;

namespace DLS.Tests
{
	// The LUT and JIT accelerators replace a custom chip's expanded gates with a table or
	// native code. They must be invisible: every path has to produce exactly the outputs
	// of the plain deterministic simulator, step by step.
	public class AccelerationEquivalenceTests
	{
		const string Alu4Source = @"
circuit Alu4(A: u4, B: u4, OP0, OP1) -> (Y: u4, EQ, LT):
    Y = ((A - B) if OP0 else (A + B)) if OP1 else ((A ^ B) if OP0 else (A & B))
    EQ = A == B
    LT = A < B
";

		// Same pins as Alu4 but different logic, so its LUT fingerprint differs.
		const string Alu4VariantSource = @"
circuit Alu4(A: u4, B: u4, OP0, OP1) -> (Y: u4, EQ, LT):
    Y = ((A - B) if OP0 else (A + B)) if OP1 else ((A | B) if OP0 else (A & B))
    EQ = A == B
    LT = B < A
";

		// The root chip is never collapsed, so the chip under test sits one level down.
		const string AluHarnessSource = @"
circuit AluHarness(A: u4, B: u4, OP0, OP1) -> (Y: u4, EQ, LT):
    Y, EQ, LT = Alu4(A, B, OP0, OP1)
";

		const string Reg4HarnessSource = @"
circuit Reg4Harness(D: u4, WE, CLK, RESET) -> Q: u4:
    Q = Reg4(D, WE, CLK, RESET)
";

		static readonly string[] AluInputs = { "A", "B", "OP0", "OP1" };
		static readonly string[] AluOutputs = { "Y", "EQ", "LT" };
		static readonly string[] Reg4Inputs = { "D", "WE", "CLK", "RESET" };
		static readonly string[] Reg4Outputs = { "Q" };
		const int Reg4ResetSteps = 4;

		static string ExamplesRoot => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Examples", "RHDL"));

		ChipLibrary library;
		string tempDirectory;

		[SetUp]
		public void SetUp() => library = TestLibrary.Create();

		[TearDown]
		public void TearDown()
		{
			RewiredEngine.Reset();
			if (tempDirectory != null && Directory.Exists(tempDirectory)) Directory.Delete(tempDirectory, true);
			tempDirectory = null;
		}

		[Test]
		public void NativeJitMatchesReferenceOnEveryAluInput()
		{
			ChipDescription harness = BuildAluHarness();
			uint[][] stimulus = AluStimulus();

			Trace reference = Run(harness, EnginePath.Reference, AluInputs, AluOutputs, stimulus);
			Trace jit = Run(harness, EnginePath.NativeJit, AluInputs, AluOutputs, stimulus);

			Assert.AreEqual(0, reference.JitHits + reference.CacheHits + reference.FeedbackJitHits, "reference run used an accelerator");
			Assert.Greater(jit.JitHits, 0, "native JIT never evaluated the ALU");
			Assert.AreEqual(0, jit.CacheHits, "LUT was used on the JIT path");
			AssertSameOutputs(reference, jit, AluInputs, AluOutputs, stimulus);
		}

		[Test]
		public void FullLutMatchesReferenceOnEveryAluInput()
		{
			ChipDescription harness = BuildAluHarness();
			uint[][] stimulus = AluStimulus();

			Trace reference = Run(harness, EnginePath.Reference, AluInputs, AluOutputs, stimulus);
			Trace lut = Run(harness, EnginePath.FullLut, AluInputs, AluOutputs, stimulus);

			Assert.Greater(lut.CacheHits, 0, "FULL LUT never evaluated the ALU");
			Assert.AreEqual(0, lut.CacheMisses, "binary inputs fell outside the LUT");
			Assert.AreEqual(0, lut.JitHits, "native JIT was used on the LUT path");
			AssertSameOutputs(reference, lut, AluInputs, AluOutputs, stimulus);
		}

		// Three 4-bit operands and two select bits give 16384 LUT entries, enough for the
		// build to be split across threads on a machine with four or more cores.
		const string Mix3Source = @"
circuit Mix3(A: u4, B: u4, C: u4, OP0, OP1) -> (Y: u4, EQ, LT):
    Y = ((A - B) if OP0 else (B + C)) if OP1 else ((A ^ B) if OP0 else (A & C))
    EQ = A == C
    LT = B < C
";

		const string Mix3HarnessSource = @"
circuit Mix3Harness(A: u4, B: u4, C: u4, OP0, OP1) -> (Y: u4, EQ, LT):
    Y, EQ, LT = Mix3(A, B, C, OP0, OP1)
";

		[Test]
		public void LargeFullLutBuiltOnSeveralThreadsMatchesReference()
		{
			Compile(Mix3Source);
			ChipDescription harness = Compile(Mix3HarnessSource);
			string[] inputs = { "A", "B", "C", "OP0", "OP1" };

			System.Random random = new(4321);
			uint[][] stimulus = new uint[3000][];
			for (int i = 0; i < stimulus.Length; i++)
			{
				stimulus[i] = new[] { (uint)random.Next(16), (uint)random.Next(16), (uint)random.Next(16), (uint)random.Next(2), (uint)random.Next(2) };
			}

			Trace reference = Run(harness, EnginePath.Reference, inputs, AluOutputs, stimulus);
			Trace lut = Run(harness, EnginePath.FullLut, inputs, AluOutputs, stimulus);

			Assert.Greater(lut.CacheHits, 0, "FULL LUT never evaluated the chip");
			Assert.AreEqual(0, lut.CacheMisses, "binary inputs fell outside the LUT");
			AssertSameOutputs(reference, lut, inputs, AluOutputs, stimulus);
		}

		[Test]
		public void FeedbackJitMatchesReferenceOnRegisterSequence()
		{
			BuildExample("Basics/04_DFF.rhdl");
			BuildExample("Basics/05_REG4.rhdl");
			ChipDescription harness = Compile(Reg4HarnessSource);
			uint[][] stimulus = Reg4Stimulus(400);

			Trace reference = Run(harness, EnginePath.Reference, Reg4Inputs, Reg4Outputs, stimulus);
			Trace feedback = Run(harness, EnginePath.FeedbackJit, Reg4Inputs, Reg4Outputs, stimulus);

			Assert.AreEqual(0, reference.JitHits + reference.CacheHits + reference.FeedbackJitHits, "reference run used an accelerator");
			Assert.Greater(feedback.FeedbackJitHits, 0, "feedback JIT never evaluated the register");
			Assert.AreEqual(0, feedback.FeedbackJitFallbacks, "feedback JIT fell back to the gate simulator");
			Assert.IsTrue(reference.AllConverged && feedback.AllConverged, "a step did not settle");
			// Power-on deliberately resolves latch races at random, so the register only has a
			// defined value once the initial reset has been clocked in.
			AssertSameOutputs(reference, feedback, Reg4Inputs, Reg4Outputs, stimulus, Reg4ResetSteps);
		}

		[Test]
		public void FingerprintFollowsNestedChipLogic()
		{
			ChipDescription alu = Compile(Alu4Source);
			ChipDescription harness = Compile(AluHarnessSource);
			byte[] original = CombinationalChipCacheManager.GetLogicFingerprint(harness, library);

			Compile(Alu4VariantSource);
			CombinationalChipCacheManager.NotifyProjectDescriptionsChanged();
			byte[] changed = CombinationalChipCacheManager.GetLogicFingerprint(harness, library);

			library.NotifyChipSaved(alu, false);
			CombinationalChipCacheManager.NotifyProjectDescriptionsChanged();
			byte[] restored = CombinationalChipCacheManager.GetLogicFingerprint(harness, library);

			Assert.IsFalse(CombinationalChipCacheManager.FingerprintsEqual(original, changed), "parent fingerprint ignored a nested logic change");
			Assert.IsTrue(CombinationalChipCacheManager.FingerprintsEqual(original, restored), "fingerprint is not a pure function of the logic");
		}

		[Test]
		public void PersistentLutIsReusedAndMatchesReference()
		{
			ChipDescription alu = Compile(Alu4Source);
			ChipDescription harness = Compile(AluHarnessSource);
			string path = TempCachePath();

			CombinationalChipMemoCache built = BuildPersistentCache(alu, path);
			Assert.IsFalse(built.LoadedFromDisk, "first build cannot come from disk");
			Assert.IsTrue(File.Exists(path), "LUT was not written to disk: " + built.PersistenceMessage);

			CombinationalChipMemoCache loaded = BuildPersistentCache(alu, path);
			Assert.IsTrue(loaded.LoadedFromDisk, "valid LUT file was not reused: " + loaded.PersistenceMessage);

			AssertCacheMatchesReference(harness, loaded);
		}

		[Test]
		public void CorruptLutFileIsRebuilt()
		{
			ChipDescription alu = Compile(Alu4Source);
			ChipDescription harness = Compile(AluHarnessSource);
			string path = TempCachePath();
			BuildPersistentCache(alu, path);

			// Drop the last value so the header no longer matches the data length.
			using (FileStream stream = new(path, FileMode.Open, FileAccess.Write))
			{
				stream.SetLength(stream.Length - sizeof(uint));
			}

			CombinationalChipMemoCache rebuilt = BuildPersistentCache(alu, path);
			Assert.IsFalse(rebuilt.LoadedFromDisk, "truncated LUT file was loaded");
			AssertCacheMatchesReference(harness, rebuilt);

			CombinationalChipMemoCache reloaded = BuildPersistentCache(alu, path);
			Assert.IsTrue(reloaded.LoadedFromDisk, "rebuilt LUT was not saved back: " + reloaded.PersistenceMessage);
		}

		[Test]
		public void StaleLutFileIsNotUsedAfterLogicChange()
		{
			ChipDescription original = Compile(Alu4Source);
			string path = TempCachePath();
			BuildPersistentCache(original, path);

			ChipDescription variant = Compile(Alu4VariantSource);
			ChipDescription harness = Compile(AluHarnessSource);
			CombinationalChipCacheManager.NotifyProjectDescriptionsChanged();

			CombinationalChipMemoCache rebuilt = BuildPersistentCache(variant, path);
			Assert.IsFalse(rebuilt.LoadedFromDisk, "LUT of the old logic was loaded for the new logic");
			AssertCacheMatchesReference(harness, rebuilt);
		}

		// ---- helpers ----

		sealed class Trace
		{
			public readonly List<uint[]> Outputs = new();
			public int CacheHits, CacheMisses, JitHits, FeedbackJitHits, FeedbackJitFallbacks;
			public bool AllConverged = true;
		}

		Trace Run(ChipDescription harness, EnginePath path, string[] inputs, string[] outputs, uint[][] stimulus) =>
			Run(harness, EnginePaths.Force(path), inputs, outputs, stimulus);

		Trace Run(ChipDescription harness, Action<SimChip> prepare, string[] inputs, string[] outputs, uint[][] stimulus)
		{
			TestCircuit circuit = new(harness, library, prepare);
			Trace trace = new();

			foreach (uint[] values in stimulus)
			{
				for (int i = 0; i < inputs.Length; i++) circuit.Set(inputs[i], values[i]);
				circuit.Step();

				RewiredEngine.DiagnosticsSnapshot d = circuit.Diagnostics;
				trace.CacheHits += d.CacheHits;
				trace.CacheMisses += d.CacheMisses;
				trace.JitHits += d.JitHits;
				trace.FeedbackJitHits += d.FeedbackJitHits;
				trace.FeedbackJitFallbacks += d.FeedbackJitFallbacks;
				trace.AllConverged &= d.SettleConverged;
				trace.Outputs.Add(outputs.Select(circuit.Get).ToArray());
			}

			RewiredEngine.Reset();
			return trace;
		}

		static void AssertSameOutputs(Trace expected, Trace actual, string[] inputs, string[] outputs, uint[][] stimulus, int firstStep = 0)
		{
			for (int step = firstStep; step < stimulus.Length; step++)
			{
				for (int o = 0; o < outputs.Length; o++)
				{
					if (expected.Outputs[step][o] == actual.Outputs[step][o]) continue;

					string inputText = string.Join(" ", inputs.Select((name, i) => $"{name}={stimulus[step][i]}"));
					Assert.Fail($"step {step} ({inputText}): {outputs[o]} expected {expected.Outputs[step][o]} but was {actual.Outputs[step][o]}");
				}
			}
		}

		void AssertCacheMatchesReference(ChipDescription harness, CombinationalChipMemoCache cache)
		{
			uint[][] stimulus = AluStimulus();
			Trace reference = Run(harness, EnginePath.Reference, AluInputs, AluOutputs, stimulus);

			Action<SimChip> useCache = root =>
			{
				EnginePaths.Force(EnginePath.FullLut)(root);
				Array.Find(root.SubChips, c => c.ChipType == ChipType.Custom).MemoCache = cache;
			};

			Trace cached = Run(harness, useCache, AluInputs, AluOutputs, stimulus);
			Assert.Greater(cached.CacheHits, 0, "the given LUT was never evaluated");
			AssertSameOutputs(reference, cached, AluInputs, AluOutputs, stimulus);
		}

		CombinationalChipMemoCache BuildPersistentCache(ChipDescription description, string path)
		{
			ChipCacheAnalysis analysis = CombinationalChipCacheManager.Analyze(description, library);
			Assert.IsTrue(analysis.CanCache, "chip is not cacheable: " + analysis.Reason);

			byte[] fingerprint = CombinationalChipCacheManager.GetLogicFingerprint(description, library);
			CombinationalChipMemoCache cache = new(description, analysis, fingerprint, path);
			cache.EnsureReadyAsync(CombinationalChipCacheManager.CreateBuildSnapshot(description, library));
			EnginePaths.WaitUntilBuilt(cache);
			return cache;
		}

		string TempCachePath()
		{
			tempDirectory ??= Path.Combine(Path.GetTempPath(), "RewiredLutTests_" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(tempDirectory);
			return Path.Combine(tempDirectory, "Alu4.dlscache");
		}

		ChipDescription BuildAluHarness()
		{
			Compile(Alu4Source);
			return Compile(AluHarnessSource);
		}

		ChipDescription BuildExample(string relativePath) =>
			Compile(File.ReadAllText(Path.Combine(ExamplesRoot, relativePath)));

		ChipDescription Compile(string source)
		{
			RhdlCompileResult result = RhdlCompiler.Compile(source, library);
			Assert.IsTrue(result.Success, "RHDL failed to compile:\n" + string.Join("\n", result.Diagnostics.Select(d => d.ToString())));

			library.NotifyChipSaved(result.Description, false);
			return result.Description;
		}

		// Every input combination in counting order, then the same set in a fixed shuffled
		// order so the accelerators also see large jumps between consecutive inputs.
		static uint[][] AluStimulus()
		{
			List<uint[]> vectors = new();
			for (uint i = 0; i < 1024; i++) vectors.Add(new[] { i & 15, (i >> 4) & 15, (i >> 8) & 1, (i >> 9) & 1 });

			System.Random random = new(1234);
			vectors.AddRange(vectors.ToArray().OrderBy(_ => random.Next()));
			return vectors.ToArray();
		}

		// Two clock cycles with RESET held high, then random data and control with a clock
		// that toggles every step.
		static uint[][] Reg4Stimulus(int steps)
		{
			System.Random random = new(5678);
			uint[][] vectors = new uint[steps][];
			for (int i = 0; i < steps; i++)
			{
				uint reset = i < Reg4ResetSteps || random.Next(10) == 0 ? 1u : 0u;
				vectors[i] = new[] { (uint)random.Next(16), (uint)random.Next(2), (uint)(i & 1), reset };
			}

			return vectors;
		}
	}
}
