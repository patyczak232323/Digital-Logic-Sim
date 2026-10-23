using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using DLS.Description;
using DLS.Game;
using DLS.RHDL;
using DLS.Simulation;
using DLS.Tests;
using Newtonsoft.Json;
using NUnit.Framework;

namespace DLS.Benchmarks
{
	// Engine throughput on fixed workloads, through each execution path.
	//
	// Wall-clock speed depends on the machine, so it is only reported. Regressions are
	// caught with work counters (gate evaluations and signal propagations per step), which
	// do not depend on the machine: if an accelerator stops engaging or the scheduler
	// starts doing redundant work, they jump. Limits live in benchmark-baseline.json.
	[TestFixture, Category("Benchmark"), NonParallelizable]
	public class EngineBenchmarks
	{
		static string HarnessDirectory => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
		static string BaselinePath => Path.Combine(HarnessDirectory, "benchmark-baseline.json");
		static string ResultsPath => Path.Combine(HarnessDirectory, "benchmark-results.json");
		static string ExamplesRoot => Path.Combine(HarnessDirectory, "..", "..", "Examples", "RHDL");

		// Allowed growth of a work counter over its baseline before the benchmark fails.
		const double WorkTolerance = 1.25;

		static readonly List<Measurement> results = new();

		sealed class Measurement
		{
			public string Name;
			public int Steps;
			public double StepsPerSecond;
			public double GateEvaluationsPerStep;
			public double SignalPropagationsPerStep;
		}

		sealed class Baseline
		{
			[JsonProperty("_comment")] public string Comment;
			public Dictionary<string, double> MaxGateEvaluationsPerStep = new();
			public Dictionary<string, double> MaxSignalPropagationsPerStep = new();
		}

		ChipLibrary library;

		[SetUp]
		public void SetUp() => library = TestLibrary.Create();

		[TearDown]
		public void TearDown() => RewiredEngine.Reset();

		[OneTimeTearDown]
		public void WriteReport()
		{
			if (results.Count == 0) return;

			File.WriteAllText(ResultsPath, JsonConvert.SerializeObject(results, Formatting.Indented));

			StringBuilder md = new();
			md.AppendLine("### Engine benchmarks");
			md.AppendLine();
			md.AppendLine("| Workload | Steps/s | Gate evals/step | Propagations/step |");
			md.AppendLine("| --- | ---: | ---: | ---: |");
			foreach (Measurement m in results)
			{
				md.AppendLine($"| {m.Name} | {m.StepsPerSecond.ToString("N0", CultureInfo.InvariantCulture)} | {m.GateEvaluationsPerStep:F1} | {m.SignalPropagationsPerStep:F1} |");
			}

			TestContext.Progress.WriteLine(md.ToString());
			string summary = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
			if (!string.IsNullOrEmpty(summary)) File.AppendAllText(summary, md + Environment.NewLine);
		}

		[Test]
		public void Cpu4CounterProgram()
		{
			Compile(File.ReadAllText(Path.Combine(ExamplesRoot, "Basics", "04_DFF.rhdl")));
			Compile(File.ReadAllText(Path.Combine(ExamplesRoot, "Basics", "05_REG4.rhdl")));
			ChipDescription cpu = Compile(File.ReadAllText(Path.Combine(ExamplesRoot, "CPU4", "00_CPU4.rhdl")));

			// LDI 0; loop: INC; OUT; JMP loop. OUT_PORT counts up forever.
			uint[] rom = new uint[256];
			rom[0] = 0x0010;
			rom[1] = 0x00B0;
			rom[2] = 0x00E0;
			rom[3] = 0x0071;
			string romName = ChipTypeHelper.GetName(ChipType.Rom_256x16);
			cpu.SubChips[Array.FindIndex(cpu.SubChips, s => ChipDescription.NameMatch(s.Name, romName))].InternalData = rom;

			foreach (EnginePath path in new[] { EnginePath.Reference, EnginePath.FeedbackJit })
			{
				TestCircuit c = new(cpu, library, EnginePaths.Force(path));
				c.Set("RESET", 1);
				c.Tick();
				c.Set("RESET", 0);
				for (int i = 0; i < 30; i++) c.Tick();

				const int cycles = 600;
				Measure($"CPU4 counter ({path})", c, () =>
				{
					for (int i = 0; i < cycles; i++) c.Tick();
				});

				// Each loop iteration is three instructions, so the counter must keep advancing.
				uint before = c.Get("OUT_PORT");
				for (int i = 0; i < 3; i++) c.Tick();
				Assert.AreEqual((before + 1) & 15, c.Get("OUT_PORT"), $"CPU4 stopped counting on the {path} path");
				RewiredEngine.Reset();
			}
		}

		[Test]
		public void AluRandomInputs()
		{
			Compile(@"
circuit Alu8
    input A: 8
    input B: 8
    input OP0, OP1
    output Y: 8
    output EQ, LT

    Y = choose(OP1, choose(OP0, A xor B, A and B), choose(OP0, A - B, A + B))
    EQ = A == B
    LT = A < B
end");
			ChipDescription harness = Compile(@"
circuit Alu8Harness
    input A: 8
    input B: 8
    input OP0, OP1
    output Y: 8
    output EQ, LT

    component alu : Alu8(A=A, B=B, OP0=OP0, OP1=OP1, Y=Y, EQ=EQ, LT=LT)
end");

			foreach (EnginePath path in new[] { EnginePath.Reference, EnginePath.NativeJit })
			{
				TestCircuit c = new(harness, library, EnginePaths.Force(path));
				Random random = new(42);
				const int steps = 4000;
				Measure($"ALU8 random inputs ({path})", c, () =>
				{
					for (int i = 0; i < steps; i++)
					{
						c.Set("A", (uint)random.Next(256));
						c.Set("B", (uint)random.Next(256));
						c.Set("OP0", (uint)random.Next(2));
						c.Set("OP1", (uint)random.Next(2));
						c.Step();
					}
				});
				RewiredEngine.Reset();
			}
		}

		void Measure(string name, TestCircuit circuit, Action run)
		{
			long gatesBefore = circuit.TotalGateEvaluations;
			long propagationsBefore = circuit.TotalSignalPropagations;
			int stepsBefore = circuit.TotalSteps;
			Stopwatch timer = Stopwatch.StartNew();
			run();
			timer.Stop();

			int measuredSteps = Math.Max(1, circuit.TotalSteps - stepsBefore);
			long gates = circuit.TotalGateEvaluations - gatesBefore;
			long propagations = circuit.TotalSignalPropagations - propagationsBefore;
			Measurement m = new()
			{
				Name = name,
				Steps = measuredSteps,
				StepsPerSecond = measuredSteps / timer.Elapsed.TotalSeconds,
				GateEvaluationsPerStep = gates / (double)measuredSteps,
				SignalPropagationsPerStep = propagations / (double)measuredSteps
			};
			results.Add(m);
			CheckBaseline(m);
		}

		static void CheckBaseline(Measurement m)
		{
			if (!File.Exists(BaselinePath)) return;
			Baseline baseline = JsonConvert.DeserializeObject<Baseline>(File.ReadAllText(BaselinePath));

			if (baseline.MaxGateEvaluationsPerStep.TryGetValue(m.Name, out double gates))
			{
				Assert.LessOrEqual(m.GateEvaluationsPerStep, gates * WorkTolerance, $"{m.Name}: gate evaluations per step regressed (baseline {gates:F1})");
			}

			if (baseline.MaxSignalPropagationsPerStep.TryGetValue(m.Name, out double propagations))
			{
				Assert.LessOrEqual(m.SignalPropagationsPerStep, propagations * WorkTolerance, $"{m.Name}: signal propagations per step regressed (baseline {propagations:F1})");
			}
		}

		ChipDescription Compile(string source)
		{
			RhdlCompileResult result = RhdlCompiler.Compile(source, library);
			Assert.IsTrue(result.Success, "RHDL failed to compile:\n" + string.Join("\n", result.Diagnostics.Select(d => d.ToString())));
			library.NotifyChipSaved(result.Description, false);
			return result.Description;
		}
	}
}
