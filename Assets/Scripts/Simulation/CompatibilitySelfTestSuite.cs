using System;
using System.Collections.Generic;
using DLS.Description;
using DLS.Game;
using UnityEngine;

namespace DLS.Simulation
{
	public readonly struct CompatibilityCaseResult
	{
		public readonly string Name;
		public readonly bool Passed;
		public readonly string Details;

		public CompatibilityCaseResult(string name, bool passed, string details)
		{
			Name = name ?? string.Empty;
			Passed = passed;
			Details = details ?? string.Empty;
		}
	}

	public sealed class CompatibilitySuiteResult
	{
		public readonly CompatibilityCaseResult[] Cases;

		public CompatibilitySuiteResult(CompatibilityCaseResult[] cases)
		{
			Cases = cases ?? Array.Empty<CompatibilityCaseResult>();
		}

		public int PassedCount
		{
			get
			{
				int count = 0;
				for (int i = 0; i < Cases.Length; i++) if (Cases[i].Passed) count++;
				return count;
			}
		}

		public int FailedCount => Cases.Length - PassedCount;
		public bool Passed => FailedCount == 0;
	}

	/// <summary>
	/// High-value live-engine compatibility smoke suite.
	///
	/// The Python solver regression remains the large stress/oracle suite. This class is the
	/// complementary in-engine layer: it builds real SimChip graphs and drives the exact
	/// RewiredEngine path used by projects.
	/// </summary>
	public static class CompatibilitySelfTestSuite
	{
		const uint Disconnected = 0xFFFF0000u;

		public static CompatibilitySuiteResult RunAll()
		{
			List<CompatibilityCaseResult> cases = new();

			ChipDescription[] builtins = BuiltinChipCreator.CreateAllBuiltinChipDescriptions();
			Dictionary<ChipType, ChipDescription> builtinByType = new();
			for (int i = 0; i < builtins.Length; i++) builtinByType[builtins[i].ChipType] = builtins[i];

			ChipDescription nandWrapper = WrapBuiltin(builtinByType[ChipType.Nand], "COMPAT_NAND");
			ChipDescription triWrapper = WrapBuiltin(builtinByType[ChipType.TriStateBuffer], "COMPAT_TRI");
			ChipDescription pulseWrapper = WrapBuiltin(
				builtinByType[ChipType.Pulse],
				"COMPAT_PULSE",
				new uint[] { 3, 0, 0 });
			ChipDescription clockWrapper = WrapBuiltin(builtinByType[ChipType.Clock], "COMPAT_CLOCK");

			uint[] romData = new uint[256];
			romData[0x12] = 0xABCD;
			romData[0xFE] = 0x1234;
			ChipDescription romWrapper = WrapBuiltin(
				builtinByType[ChipType.Rom_256x16],
				"COMPAT_ROM",
				romData);

			ChipDescription ramWrapper = WrapBuiltin(builtinByType[ChipType.dev_Ram_8Bit], "COMPAT_RAM");
			ChipDescription srLatch = CreateSrLatch(builtinByType[ChipType.Nand]);
			ChipDescription srRoot = WrapCustom(srLatch, "COMPAT_SR_ROOT");
			ChipDescription comb4 = CreateFourInverterIdentity(builtinByType[ChipType.Nand]);
			List<ChipDescription> nested = CreateNestedWrappers(comb4, 8);
			ChipDescription cachedComb4 = CreateFourInverterIdentity(builtinByType[ChipType.Nand]);
			cachedComb4.Name = "COMPAT_CACHE4";
			cachedComb4.CacheMode = ChipCacheMode.Auto;
			ChipDescription cacheRoot = WrapCustom(cachedComb4, "COMPAT_CACHE_ROOT");

			List<ChipDescription> customs = new()
			{
				nandWrapper,
				triWrapper,
				pulseWrapper,
				clockWrapper,
				romWrapper,
				ramWrapper,
				srLatch,
				srRoot,
				comb4,
				cachedComb4,
				cacheRoot
			};
			customs.AddRange(nested);

			ChipLibrary library = new(customs.ToArray(), builtins);

			RunGolden(
				cases,
				"NAND truth table / propagation",
				nandWrapper,
				library,
				new[]
				{
					Step("00", new uint[] { 0, 0 }, 1),
					Step("01", new uint[] { 0, 1 }, 1),
					Step("10", new uint[] { 1, 0 }, 1),
					Step("11", new uint[] { 1, 1 }, 0),
				});

			RunGolden(
				cases,
				"Tri-state disconnect / reconnect",
				triWrapper,
				library,
				new[]
				{
					Step("disabled-high", new uint[] { 1, 0 }, Disconnected),
					Step("enabled-high", new uint[] { 1, 1 }, 1),
					Step("enabled-low", new uint[] { 0, 1 }, 0),
					Step("disabled-low", new uint[] { 0, 0 }, Disconnected),
				});

			RunGolden(
				cases,
				"Pulse rising-edge width and retrigger",
				pulseWrapper,
				library,
				new[]
				{
					Step("idle", new uint[] { 0 }, 0),
					Step("rise", new uint[] { 1 }, 1),
					Step("hold-1", new uint[] { 1 }, 1),
					Step("hold-2", new uint[] { 1 }, 1),
					Step("expired-while-high", new uint[] { 1 }, 0),
					Step("fall", new uint[] { 0 }, 0),
					Step("rise-again", new uint[] { 1 }, 1),
				});

			RunGolden(
				cases,
				"Clock source cadence",
				clockWrapper,
				library,
				new[]
				{
					Step("frame-1", Array.Empty<uint>(), 1),
					Step("frame-2", Array.Empty<uint>(), 0),
					Step("frame-3", Array.Empty<uint>(), 0),
					Step("frame-4", Array.Empty<uint>(), 1),
				},
				stepsPerClockTransition: 2);

			RunGolden(
				cases,
				"ROM address/read ordering",
				romWrapper,
				library,
				new[]
				{
					Step2("read-12", new uint[] { 0x12 }, 0xAB, 0xCD),
					Step2("read-empty", new uint[] { 0x13 }, 0x00, 0x00),
					Step2("read-fe", new uint[] { 0xFE }, 0x12, 0x34),
				});

			RunGolden(
				cases,
				"RAM reset / rising-edge write / hold",
				ramWrapper,
				library,
				new[]
				{
					Step("reset-rise", new uint[] { 0x2A, 0x00, 0, 1, 1 }, 0x00),
					Step("write-armed-clock-low", new uint[] { 0x2A, 0xA5, 1, 0, 0 }, 0x00),
					Step("write-rise-a5", new uint[] { 0x2A, 0xA5, 1, 0, 1 }, 0xA5),
					Step("clock-stays-high-no-rewrite", new uint[] { 0x2A, 0x5A, 1, 0, 1 }, 0xA5),
					Step("clock-low", new uint[] { 0x2A, 0x5A, 1, 0, 0 }, 0xA5),
					Step("write-rise-5a", new uint[] { 0x2A, 0x5A, 1, 0, 1 }, 0x5A),
					Step("other-address-cleared", new uint[] { 0x2B, 0x00, 0, 0, 1 }, 0x00),
				});

			RunGolden(
				cases,
				"Feedback SR latch SET/HOLD/RESET/HOLD",
				srLatch,
				library,
				new[]
				{
					Step2("set", new uint[] { 0, 1 }, 1, 0),
					Step2("hold-set", new uint[] { 1, 1 }, 1, 0),
					Step2("reset", new uint[] { 1, 0 }, 0, 1),
					Step2("hold-reset", new uint[] { 1, 1 }, 0, 1),
				});

			ChipDescription deepest = nested[^1];
			RunGolden(
				cases,
				"Deep custom-chip nesting",
				deepest,
				library,
				new[]
				{
					Step("low", new uint[] { 0 }, 0),
					Step("high", new uint[] { 1 }, 1),
					Step("low-again", new uint[] { 0 }, 0),
					Step("high-again", new uint[] { 1 }, 1),
				});

			RunParity(
				cases,
				"Live solver vs native JIT parity",
				deepest,
				library,
				new uint[][]
				{
					new uint[] { 0 },
					new uint[] { 1 },
					new uint[] { 0 },
					new uint[] { 1 },
					new uint[] { 1 },
					new uint[] { 0 },
				},
				requiredPath: CompatibilityAccelerationPath.Jit);

			RunParity(
				cases,
				"FULL LUT cache vs live solver parity",
				cacheRoot,
				library,
				new uint[][]
				{
					new uint[] { 0 },
					new uint[] { 1 },
					new uint[] { 0 },
					new uint[] { 1 },
				},
				requiredPath: CompatibilityAccelerationPath.FullLut,
				waitForFullLut: true);

			RunParity(
				cases,
				"Feedback live solver vs feedback-JIT parity",
				srRoot,
				library,
				new uint[][]
				{
					new uint[] { 0, 1 },
					new uint[] { 1, 1 },
					new uint[] { 1, 0 },
					new uint[] { 1, 1 },
					new uint[] { 0, 1 },
					new uint[] { 1, 1 },
				},
				requiredPath: CompatibilityAccelerationPath.FeedbackJit);

			return new CompatibilitySuiteResult(cases.ToArray());
		}

		static CompatibilityStep Step(string name, uint[] inputs, uint output) =>
			new(name, inputs, new uint[] { output });

		static CompatibilityStep Step2(string name, uint[] inputs, uint output0, uint output1) =>
			new(name, inputs, new uint[] { output0, output1 });

		static void RunGolden(
			List<CompatibilityCaseResult> cases,
			string name,
			ChipDescription description,
			ChipLibrary library,
			IReadOnlyList<CompatibilityStep> steps,
			int stepsPerClockTransition = 1)
		{
			try
			{
				CompatibilityRunResult result = CompatibilityTestRunner.RunGolden(
					description,
					library,
					steps,
					stepsPerClockTransition);

				if (!result.Supported)
				{
					cases.Add(new CompatibilityCaseResult(name, false, result.UnsupportedReason));
					return;
				}

				if (result.Passed)
				{
					cases.Add(new CompatibilityCaseResult(name, true, $"{result.Samples.Length} steps"));
					return;
				}

				CompatibilityFailure failure = result.Failures[0];
				cases.Add(new CompatibilityCaseResult(
					name,
					false,
					$"step {failure.StepIndex} ({failure.StepName}), output {failure.OutputIndex}: expected 0x{failure.Expected:X8}, actual 0x{failure.Actual:X8}; {failure.Reason}"));
			}
			catch (Exception ex)
			{
				cases.Add(new CompatibilityCaseResult(name, false, ex.GetType().Name + ": " + ex.Message));
			}
		}

		enum CompatibilityAccelerationPath
		{
			Any,
			Jit,
			FullLut,
			FeedbackJit
		}

		static void RunParity(
			List<CompatibilityCaseResult> cases,
			string name,
			ChipDescription description,
			ChipLibrary library,
			IReadOnlyList<uint[]> vectors,
			CompatibilityAccelerationPath requiredPath = CompatibilityAccelerationPath.Any,
			bool waitForFullLut = false)
		{
			try
			{
				CompatibilityParityResult result = CompatibilityTestRunner.RunAccelerationParity(
					description,
					library,
					vectors,
					waitForFullLut: waitForFullLut);

				if (!result.Supported)
				{
					cases.Add(new CompatibilityCaseResult(name, false, result.UnsupportedReason));
					return;
				}

				if (!result.Passed)
				{
					CompatibilityFailure failure = result.Failures[0];
					cases.Add(new CompatibilityCaseResult(
						name,
						false,
						$"step {failure.StepIndex}, output {failure.OutputIndex}: live 0x{failure.Expected:X8}, accelerated 0x{failure.Actual:X8}"));
					return;
				}

				bool requiredObserved = requiredPath switch
				{
					CompatibilityAccelerationPath.Any => result.AccelerationObserved,
					CompatibilityAccelerationPath.Jit => result.JitObserved,
					CompatibilityAccelerationPath.FullLut => result.CacheObserved,
					CompatibilityAccelerationPath.FeedbackJit => result.FeedbackJitObserved,
					_ => false,
				};

				if (!requiredObserved)
				{
					cases.Add(new CompatibilityCaseResult(
						name,
						false,
						$"output parity passed, but required acceleration path {requiredPath} was not observed"));
					return;
				}

				cases.Add(new CompatibilityCaseResult(
					name,
					true,
					$"{result.AcceleratedSamples.Length} steps; {requiredPath} path observed"));
			}
			catch (Exception ex)
			{
				cases.Add(new CompatibilityCaseResult(name, false, ex.GetType().Name + ": " + ex.Message));
			}
		}

		static ChipDescription WrapBuiltin(
			ChipDescription builtin,
			string name,
			uint[] internalData = null)
		{
			PinDescription[] rootInputs = new PinDescription[builtin.InputPins?.Length ?? 0];
			PinDescription[] rootOutputs = new PinDescription[builtin.OutputPins?.Length ?? 0];
			List<WireDescription> wires = new();

			for (int i = 0; i < rootInputs.Length; i++)
			{
				PinDescription source = builtin.InputPins[i];
				int rootId = 100 + i;
				rootInputs[i] = Pin(source.Name, rootId, source.BitCount);
				wires.Add(Wire(
					new PinAddress(rootId, 0),
					new PinAddress(1, source.ID)));
			}

			for (int i = 0; i < rootOutputs.Length; i++)
			{
				PinDescription source = builtin.OutputPins[i];
				int rootId = 200 + i;
				rootOutputs[i] = Pin(source.Name, rootId, source.BitCount);
				wires.Add(Wire(
					new PinAddress(1, source.ID),
					new PinAddress(rootId, 0)));
			}

			return Custom(
				name,
				rootInputs,
				rootOutputs,
				new[]
				{
					new SubChipDescription(
						builtin.Name,
						1,
						string.Empty,
						Vector2.zero,
						Array.Empty<OutputPinColourInfo>(),
						internalData)
				},
				wires.ToArray());
		}


		static ChipDescription WrapCustom(ChipDescription child, string name)
		{
			PinDescription[] inputs = new PinDescription[child.InputPins?.Length ?? 0];
			PinDescription[] outputs = new PinDescription[child.OutputPins?.Length ?? 0];
			List<WireDescription> wires = new();

			for (int i = 0; i < inputs.Length; i++)
			{
				PinDescription pin = child.InputPins[i];
				int rootId = 100 + i;
				inputs[i] = Pin(pin.Name, rootId, pin.BitCount);
				wires.Add(Wire(new PinAddress(rootId, 0), new PinAddress(1, pin.ID)));
			}

			for (int i = 0; i < outputs.Length; i++)
			{
				PinDescription pin = child.OutputPins[i];
				int rootId = 200 + i;
				outputs[i] = Pin(pin.Name, rootId, pin.BitCount);
				wires.Add(Wire(new PinAddress(1, pin.ID), new PinAddress(rootId, 0)));
			}

			return Custom(
				name,
				inputs,
				outputs,
				new[]
				{
					new SubChipDescription(
						child.Name,
						1,
						string.Empty,
						Vector2.zero,
						Array.Empty<OutputPinColourInfo>())
				},
				wires.ToArray());
		}

		static ChipDescription CreateFourInverterIdentity(ChipDescription nand)
		{
			PinDescription input = Pin("IN", 100, PinBitCount.Bit1);
			PinDescription output = Pin("OUT", 200, PinBitCount.Bit1);
			SubChipDescription[] subs = new SubChipDescription[4];

			for (int i = 0; i < subs.Length; i++)
			{
				subs[i] = new SubChipDescription(
					nand.Name,
					i + 1,
					string.Empty,
					Vector2.zero,
					Array.Empty<OutputPinColourInfo>());
			}

			List<WireDescription> wires = new();
			PinAddress previous = new(100, 0);
			for (int i = 0; i < subs.Length; i++)
			{
				int id = i + 1;
				wires.Add(Wire(previous, new PinAddress(id, 0)));
				wires.Add(Wire(previous, new PinAddress(id, 1)));
				previous = new PinAddress(id, 2);
			}
			wires.Add(Wire(previous, new PinAddress(200, 0)));

			return Custom(
				"COMPAT_COMB4",
				new[] { input },
				new[] { output },
				subs,
				wires.ToArray());
		}

		static List<ChipDescription> CreateNestedWrappers(ChipDescription baseChip, int depth)
		{
			List<ChipDescription> result = new();
			ChipDescription child = baseChip;

			for (int level = 1; level <= depth; level++)
			{
				string name = $"COMPAT_NESTED_{level}";
				ChipDescription wrapper = Custom(
					name,
					new[] { Pin("IN", 100, PinBitCount.Bit1) },
					new[] { Pin("OUT", 200, PinBitCount.Bit1) },
					new[]
					{
						new SubChipDescription(
							child.Name,
							1,
							string.Empty,
							Vector2.zero,
							Array.Empty<OutputPinColourInfo>())
					},
					new[]
					{
						Wire(new PinAddress(100, 0), new PinAddress(1, child.InputPins[0].ID)),
						Wire(new PinAddress(1, child.OutputPins[0].ID), new PinAddress(200, 0)),
					});

				result.Add(wrapper);
				child = wrapper;
			}

			return result;
		}

		static ChipDescription CreateSrLatch(ChipDescription nand)
		{
			PinDescription setN = Pin("SET_N", 100, PinBitCount.Bit1);
			PinDescription resetN = Pin("RESET_N", 101, PinBitCount.Bit1);
			PinDescription q = Pin("Q", 200, PinBitCount.Bit1);
			PinDescription qb = Pin("QB", 201, PinBitCount.Bit1);

			SubChipDescription nQ = new(
				nand.Name,
				1,
				string.Empty,
				Vector2.zero,
				Array.Empty<OutputPinColourInfo>());
			SubChipDescription nQb = new(
				nand.Name,
				2,
				string.Empty,
				Vector2.zero,
				Array.Empty<OutputPinColourInfo>());

			return Custom(
				"COMPAT_SR_LATCH",
				new[] { setN, resetN },
				new[] { q, qb },
				new[] { nQ, nQb },
				new[]
				{
					// Q = NAND(SET_N, QB)
					Wire(new PinAddress(100, 0), new PinAddress(1, 1)),
					Wire(new PinAddress(2, 2), new PinAddress(1, 0)),
					// QB = NAND(RESET_N, Q)
					Wire(new PinAddress(101, 0), new PinAddress(2, 1)),
					Wire(new PinAddress(1, 2), new PinAddress(2, 0)),
					// expose outputs
					Wire(new PinAddress(1, 2), new PinAddress(200, 0)),
					Wire(new PinAddress(2, 2), new PinAddress(201, 0)),
				});
		}

		static ChipDescription Custom(
			string name,
			PinDescription[] inputs,
			PinDescription[] outputs,
			SubChipDescription[] subChips,
			WireDescription[] wires)
		{
			return new ChipDescription
			{
				Name = name,
				NameLocation = NameDisplayLocation.Centre,
				ChipType = ChipType.Custom,
				CacheMode = ChipCacheMode.Normal,
				Size = Vector2.one,
				Colour = Color.gray,
				InputPins = inputs ?? Array.Empty<PinDescription>(),
				OutputPins = outputs ?? Array.Empty<PinDescription>(),
				SubChips = subChips ?? Array.Empty<SubChipDescription>(),
				Wires = wires ?? Array.Empty<WireDescription>(),
				Displays = Array.Empty<DisplayDescription>(),
			};
		}

		static PinDescription Pin(string name, int id, PinBitCount bitCount)
		{
			return new PinDescription(
				name,
				id,
				Vector2.zero,
				bitCount,
				PinColour.Red,
				PinValueDisplayMode.Off);
		}

		static WireDescription Wire(PinAddress source, PinAddress target)
		{
			return new WireDescription
			{
				SourcePinAddress = source,
				TargetPinAddress = target,
				ConnectionType = WireConnectionType.ToPins,
				ConnectedWireIndex = -1,
				ConnectedWireSegmentIndex = -1,
				Points = Array.Empty<Vector2>(),
			};
		}
	}
}
