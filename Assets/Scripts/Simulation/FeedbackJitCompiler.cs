using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using DLS.Description;
using DLS.Game;

namespace DLS.Simulation
{
	// Native accelerator for gate-feedback custom chips.
	//
	// Unlike CombinationalJitCompiler this compiler deliberately accepts cycles. Each
	// native sweep reads only the previous delta-cycle buffer and writes the next one,
	// preserving the deterministic solver's simultaneous-update semantics. The normal
	// deterministic engine still performs power-on settling; only after that fixed point
	// is reached is the native executor synchronized and allowed to replace the expanded
	// gate hierarchy.
	public static class FeedbackJitCompiler
	{
		const uint DisconnectedState = 0xFFFF0000u;
		const uint SingleBitMask = PinState.SingleBitMask;
		const int NodesPerNativeMethod = 256;
		const int MinPrimitiveNodeCount = 2;

		internal delegate void NativeFeedbackBlock(uint[] current, uint[] next);

		sealed class ProgramHolder
		{
			public readonly CompiledFeedbackProgram Program;
			public ProgramHolder(CompiledFeedbackProgram program) => Program = program;
		}

		static readonly object programCacheLock = new();
		static ConditionalWeakTable<ChipDescription, ProgramHolder> programs = new();

		public static bool DynamicCodeAvailable { get; private set; } = true;
		public static string LastFailureReason { get; private set; } = string.Empty;

		internal static void NotifyDescriptionsChanged()
		{
			lock (programCacheLock)
			{
				programs = new ConditionalWeakTable<ChipDescription, ProgramHolder>();
			}
		}

		internal static void Attach(SimChip simChip, ChipDescription description, ChipLibrary library)
		{
			if (simChip == null) return;
			simChip.FeedbackExecutor = null;

			if (!DynamicCodeAvailable ||
			    simChip.CompiledExecutor != null ||
			    description == null ||
			    library == null ||
			    description.ChipType != ChipType.Custom)
			{
				return;
			}

			CompiledFeedbackProgram program = GetOrCompileProgram(simChip, description);
			if (program == null) return;

			CompiledFeedbackExecutor executor = program.CreateExecutor(simChip);
			if (executor != null) simChip.FeedbackExecutor = executor;
		}

		static CompiledFeedbackProgram GetOrCompileProgram(SimChip representative, ChipDescription description)
		{
			lock (programCacheLock)
			{
				if (programs.TryGetValue(description, out ProgramHolder holder))
				{
					return holder.Program;
				}
			}

			CompiledFeedbackProgram program;
			try
			{
				program = CompileProgram(representative, description.Name);
			}
			catch (PlatformNotSupportedException e)
			{
				DynamicCodeAvailable = false;
				LastFailureReason = e.Message;
				return null;
			}
			catch (NotSupportedException e)
			{
				DynamicCodeAvailable = false;
				LastFailureReason = e.Message;
				return null;
			}
			catch (Exception e)
			{
				LastFailureReason = e.GetType().Name + ": " + e.Message;
				return null;
			}

			if (program == null) return null;

			lock (programCacheLock)
			{
				if (programs.TryGetValue(description, out ProgramHolder existing)) return existing.Program;
				programs.Add(description, new ProgramHolder(program));
			}

			return program;
		}

		static CompiledFeedbackProgram CompileProgram(SimChip root, string chipName)
		{
			if (root == null || root.ChipType != ChipType.Custom) return null;

			Stopwatch timer = Stopwatch.StartNew();

			List<SimPin> localPins = new();
			HashSet<SimPin> localPinSet = new();
			List<SimChip> primitiveChips = new();
			CollectLocalGraph(root, localPins, localPinSet, primitiveChips);

			// Reject side-effecting/clocked/source primitives before removing sink-only
			// terminators. A buzzer, key, clock, RAM or display must never disappear merely
			// because it has no output pin used by the compiled dataflow.
			for (int i = 0; i < primitiveChips.Count; i++)
			{
				if (!IsSupportedPrimitive(primitiveChips[i].ChipType)) return null;
			}

			primitiveChips.RemoveAll(c => c.OutputPins.Length == 0);
			if (primitiveChips.Count < MinPrimitiveNodeCount) return null;

			Dictionary<SimPin, SimPin> incomingSource = BuildIncomingSourceMap(localPins, localPinSet);
			Dictionary<SimPin, int> rootInputSlots = new();
			for (int i = 0; i < root.InputPins.Length; i++) rootInputSlots[root.InputPins[i]] = i;

			Dictionary<SimPin, SignalRef> primitiveOutputSignals = new();
			List<SimPin> orderedPrimitiveOutputs = new();
			int nextScratchSlot = root.InputPins.Length;

			for (int nodeIndex = 0; nodeIndex < primitiveChips.Count; nodeIndex++)
			{
				SimChip chip = primitiveChips[nodeIndex];
				for (int outputIndex = 0; outputIndex < chip.OutputPins.Length; outputIndex++)
				{
					SimPin output = chip.OutputPins[outputIndex];
					primitiveOutputSignals.Add(output, SignalRef.FromSlot(nextScratchSlot++, nodeIndex));
					orderedPrimitiveOutputs.Add(output);
				}
			}

			SignalResolver resolver = new(root, incomingSource, rootInputSlots, primitiveOutputSignals);
			CompiledNode[] nodes = new CompiledNode[primitiveChips.Count];
			List<int>[] outgoing = new List<int>[primitiveChips.Count];
			int[] indegree = new int[primitiveChips.Count];

			for (int nodeIndex = 0; nodeIndex < primitiveChips.Count; nodeIndex++)
			{
				SimChip chip = primitiveChips[nodeIndex];
				SignalRef[] inputs = new SignalRef[chip.InputPins.Length];

				for (int inputIndex = 0; inputIndex < chip.InputPins.Length; inputIndex++)
				{
					SignalRef signal = resolver.Resolve(chip.InputPins[inputIndex]);
					inputs[inputIndex] = signal;

					int producer = signal.ProducerNode;
					if (producer >= 0)
					{
						(outgoing[producer] ??= new List<int>()).Add(nodeIndex);
						indegree[nodeIndex]++;
					}
				}

				SignalRef[] outputs = new SignalRef[chip.OutputPins.Length];
				for (int outputIndex = 0; outputIndex < chip.OutputPins.Length; outputIndex++)
				{
					outputs[outputIndex] = primitiveOutputSignals[chip.OutputPins[outputIndex]];
				}

				nodes[nodeIndex] = new CompiledNode(chip.ChipType, inputs, outputs);
			}

			// This accelerator is intentionally reserved for graphs that really contain a
			// cycle. Acyclic custom chips are handled more efficiently by the existing JIT.
			if (!ContainsCycle(indegree, outgoing)) return null;

			SignalRef[] rootOutputs = new SignalRef[root.OutputPins.Length];
			for (int i = 0; i < root.OutputPins.Length; i++) rootOutputs[i] = resolver.Resolve(root.OutputPins[i]);

			List<NativeFeedbackBlock> blocks = new();
			for (int start = 0; start < nodes.Length; start += NodesPerNativeMethod)
			{
				int count = Math.Min(NodesPerNativeMethod, nodes.Length - start);
				NativeFeedbackBlock block = EmitNativeBlock(nodes, start, count, chipName, blocks.Count);
				if (block == null) return null;
				blocks.Add(block);
			}

			int[] outputSlots = new int[rootOutputs.Length];
			uint[] outputConstants = new uint[rootOutputs.Length];
			for (int i = 0; i < rootOutputs.Length; i++)
			{
				outputSlots[i] = rootOutputs[i].IsConstant ? -1 : rootOutputs[i].Slot;
				outputConstants[i] = rootOutputs[i].IsConstant ? rootOutputs[i].Constant : 0;
			}

			timer.Stop();
			return new CompiledFeedbackProgram(
				blocks.ToArray(),
				nextScratchSlot,
				root.InputPins.Length,
				root.OutputPins.Length,
				primitiveChips.Count,
				outputSlots,
				outputConstants,
				orderedPrimitiveOutputs.Count,
				timer.Elapsed.TotalMilliseconds);
		}

		static void CollectLocalGraph(
			SimChip chip,
			List<SimPin> pins,
			HashSet<SimPin> pinSet,
			List<SimChip> primitiveChips)
		{
			for (int i = 0; i < chip.InputPins.Length; i++)
			{
				pins.Add(chip.InputPins[i]);
				pinSet.Add(chip.InputPins[i]);
			}
			for (int i = 0; i < chip.OutputPins.Length; i++)
			{
				pins.Add(chip.OutputPins[i]);
				pinSet.Add(chip.OutputPins[i]);
			}

			if (chip.ChipType == ChipType.Custom)
			{
				for (int i = 0; i < chip.SubChips.Length; i++)
				{
					CollectLocalGraph(chip.SubChips[i], pins, pinSet, primitiveChips);
				}
			}
			else
			{
				primitiveChips.Add(chip);
			}
		}

		static Dictionary<SimPin, SimPin> BuildIncomingSourceMap(List<SimPin> pins, HashSet<SimPin> localPins)
		{
			Dictionary<SimPin, SimPin> incoming = new();

			for (int i = 0; i < pins.Count; i++)
			{
				SimPin source = pins[i];
				for (int j = 0; j < source.ConnectedTargetPins.Length; j++)
				{
					SimPin target = source.ConnectedTargetPins[j];
					if (!localPins.Contains(target)) continue;

					if (incoming.TryGetValue(target, out SimPin previous))
					{
						if (!ReferenceEquals(previous, source))
						{
							throw new InvalidOperationException("multiple drivers reached feedback JIT compiler");
						}
					}
					else
					{
						incoming.Add(target, source);
					}
				}
			}

			return incoming;
		}

		static bool ContainsCycle(int[] indegreeSource, List<int>[] outgoing)
		{
			int[] indegree = (int[])indegreeSource.Clone();
			Queue<int> ready = new();
			for (int i = 0; i < indegree.Length; i++)
			{
				if (indegree[i] == 0) ready.Enqueue(i);
			}

			int visited = 0;
			while (ready.Count > 0)
			{
				int node = ready.Dequeue();
				visited++;
				List<int> targets = outgoing[node];
				if (targets == null) continue;

				for (int i = 0; i < targets.Count; i++)
				{
					int target = targets[i];
					if (--indegree[target] == 0) ready.Enqueue(target);
				}
			}

			return visited != indegree.Length;
		}

		static bool IsSupportedPrimitive(ChipType type)
		{
			return type is
				ChipType.Nand or
				ChipType.TriStateBuffer or
				ChipType.Split_4To1Bit or
				ChipType.Split_8To1Bit or
				ChipType.Split_8To4Bit or
				ChipType.Merge_1To4Bit or
				ChipType.Merge_1To8Bit or
				ChipType.Merge_4To8Bit or
				ChipType.Bus_1Bit or
				ChipType.Bus_4Bit or
				ChipType.Bus_8Bit or
				ChipType.BusTerminus_1Bit or
				ChipType.BusTerminus_4Bit or
				ChipType.BusTerminus_8Bit;
		}

		static NativeFeedbackBlock EmitNativeBlock(
			CompiledNode[] nodes,
			int start,
			int count,
			string chipName,
			int blockIndex)
		{
			string safeName = string.IsNullOrWhiteSpace(chipName) ? "Unnamed" : chipName.Replace(' ', '_');
			DynamicMethod method = new(
				$"DLS_FB_JIT_{safeName}_{blockIndex}",
				typeof(void),
				new[] { typeof(uint[]), typeof(uint[]) },
				typeof(FeedbackJitCompiler).Module,
				true);

			ILGenerator il = method.GetILGenerator();
			for (int i = 0; i < count; i++) EmitNode(il, nodes[start + i]);
			il.Emit(OpCodes.Ret);
			return (NativeFeedbackBlock)method.CreateDelegate(typeof(NativeFeedbackBlock));
		}

		static void EmitNode(ILGenerator il, CompiledNode node)
		{
			switch (node.Type)
			{
				case ChipType.Nand:
					EmitStorePrefix(il, node.Outputs[0].Slot);
					EmitLoadSignal(il, node.Inputs[0]);
					EmitLoadSignal(il, node.Inputs[1]);
					il.Emit(OpCodes.And);
					EmitInt(il, 1);
					il.Emit(OpCodes.Xor);
					EmitInt(il, 1);
					il.Emit(OpCodes.And);
					il.Emit(OpCodes.Stelem_I4);
					break;

				case ChipType.TriStateBuffer:
				{
					Label disconnected = il.DefineLabel();
					Label done = il.DefineLabel();

					EmitStorePrefix(il, node.Outputs[0].Slot);
					EmitLoadSignal(il, node.Inputs[1]);
					EmitInt(il, 1);
					il.Emit(OpCodes.And);
					il.Emit(OpCodes.Brfalse, disconnected);
					EmitLoadSignal(il, node.Inputs[0]);
					il.Emit(OpCodes.Br, done);
					il.MarkLabel(disconnected);
					EmitUInt(il, DisconnectedState);
					il.MarkLabel(done);
					il.Emit(OpCodes.Stelem_I4);
					break;
				}

				case ChipType.Split_4To1Bit:
					EmitSplitBits(il, node, new[] { 3, 2, 1, 0 });
					break;

				case ChipType.Split_8To1Bit:
					EmitSplitBits(il, node, new[] { 7, 6, 5, 4, 3, 2, 1, 0 });
					break;

				case ChipType.Merge_1To4Bit:
					EmitMergeSingleBits(il, node, new[] { 3, 2, 1, 0 });
					break;

				case ChipType.Merge_1To8Bit:
					EmitMergeSingleBits(il, node, new[] { 7, 6, 5, 4, 3, 2, 1, 0 });
					break;

				case ChipType.Merge_4To8Bit:
					EmitMerge4To8(il, node);
					break;

				case ChipType.Split_8To4Bit:
					EmitSplit8To4(il, node);
					break;

				case ChipType.Bus_1Bit:
				case ChipType.Bus_4Bit:
				case ChipType.Bus_8Bit:
					EmitStorePrefix(il, node.Outputs[0].Slot);
					EmitLoadSignal(il, node.Inputs[0]);
					il.Emit(OpCodes.Stelem_I4);
					break;

				case ChipType.BusTerminus_1Bit:
				case ChipType.BusTerminus_4Bit:
				case ChipType.BusTerminus_8Bit:
					break;

				default:
					throw new NotSupportedException("Unsupported feedback JIT primitive: " + node.Type);
			}
		}

		static void EmitSplitBits(ILGenerator il, CompiledNode node, int[] shifts)
		{
			for (int i = 0; i < shifts.Length; i++)
			{
				EmitStorePrefix(il, node.Outputs[i].Slot);
				EmitLoadSignal(il, node.Inputs[0]);
				if (shifts[i] != 0)
				{
					EmitInt(il, shifts[i]);
					il.Emit(OpCodes.Shr_Un);
				}
				EmitUInt(il, SingleBitMask);
				il.Emit(OpCodes.And);
				il.Emit(OpCodes.Stelem_I4);
			}
		}

		static void EmitMergeSingleBits(ILGenerator il, CompiledNode node, int[] inputIndices)
		{
			EmitStorePrefix(il, node.Outputs[0].Slot);
			for (int bit = 0; bit < inputIndices.Length; bit++)
			{
				EmitLoadSignal(il, node.Inputs[inputIndices[bit]]);
				EmitUInt(il, SingleBitMask);
				il.Emit(OpCodes.And);
				if (bit != 0)
				{
					EmitInt(il, bit);
					il.Emit(OpCodes.Shl);
					il.Emit(OpCodes.Or);
				}
			}
			il.Emit(OpCodes.Stelem_I4);
		}

		static void EmitMerge4To8(ILGenerator il, CompiledNode node)
		{
			EmitStorePrefix(il, node.Outputs[0].Slot);

			EmitLoadSignal(il, node.Inputs[1]);
			EmitInt(il, 0xFFFF);
			il.Emit(OpCodes.And);
			EmitLoadSignal(il, node.Inputs[0]);
			EmitInt(il, 0xFFFF);
			il.Emit(OpCodes.And);
			EmitInt(il, 4);
			il.Emit(OpCodes.Shl);
			il.Emit(OpCodes.Or);
			EmitInt(il, 0xFFFF);
			il.Emit(OpCodes.And);

			EmitLoadSignal(il, node.Inputs[1]);
			EmitInt(il, 16);
			il.Emit(OpCodes.Shr_Un);
			EmitInt(il, 0xF);
			il.Emit(OpCodes.And);
			EmitLoadSignal(il, node.Inputs[0]);
			EmitInt(il, 16);
			il.Emit(OpCodes.Shr_Un);
			EmitInt(il, 0xF);
			il.Emit(OpCodes.And);
			EmitInt(il, 4);
			il.Emit(OpCodes.Shl);
			il.Emit(OpCodes.Or);
			EmitInt(il, 16);
			il.Emit(OpCodes.Shl);
			il.Emit(OpCodes.Or);
			il.Emit(OpCodes.Stelem_I4);
		}

		static void EmitSplit8To4(ILGenerator il, CompiledNode node)
		{
			EmitSplitNibble(il, node.Outputs[0].Slot, node.Inputs[0], 4);
			EmitSplitNibble(il, node.Outputs[1].Slot, node.Inputs[0], 0);
		}

		static void EmitSplitNibble(ILGenerator il, int outputSlot, SignalRef input, int shift)
		{
			EmitStorePrefix(il, outputSlot);
			EmitLoadSignal(il, input);
			if (shift != 0)
			{
				EmitInt(il, shift);
				il.Emit(OpCodes.Shr_Un);
			}
			EmitInt(il, 0xF);
			il.Emit(OpCodes.And);

			EmitLoadSignal(il, input);
			EmitInt(il, 16 + shift);
			il.Emit(OpCodes.Shr_Un);
			EmitInt(il, 0xF);
			il.Emit(OpCodes.And);
			EmitInt(il, 16);
			il.Emit(OpCodes.Shl);
			il.Emit(OpCodes.Or);
			il.Emit(OpCodes.Stelem_I4);
		}

		static void EmitStorePrefix(ILGenerator il, int slot)
		{
			il.Emit(OpCodes.Ldarg_1);
			EmitInt(il, slot);
		}

		static void EmitLoadSignal(ILGenerator il, SignalRef signal)
		{
			if (signal.IsConstant)
			{
				EmitUInt(il, signal.Constant);
				return;
			}

			il.Emit(OpCodes.Ldarg_0);
			EmitInt(il, signal.Slot);
			il.Emit(OpCodes.Ldelem_U4);
		}

		static void EmitUInt(ILGenerator il, uint value) => EmitInt(il, unchecked((int)value));

		static void EmitInt(ILGenerator il, int value)
		{
			switch (value)
			{
				case -1: il.Emit(OpCodes.Ldc_I4_M1); break;
				case 0: il.Emit(OpCodes.Ldc_I4_0); break;
				case 1: il.Emit(OpCodes.Ldc_I4_1); break;
				case 2: il.Emit(OpCodes.Ldc_I4_2); break;
				case 3: il.Emit(OpCodes.Ldc_I4_3); break;
				case 4: il.Emit(OpCodes.Ldc_I4_4); break;
				case 5: il.Emit(OpCodes.Ldc_I4_5); break;
				case 6: il.Emit(OpCodes.Ldc_I4_6); break;
				case 7: il.Emit(OpCodes.Ldc_I4_7); break;
				case 8: il.Emit(OpCodes.Ldc_I4_8); break;
				default:
					if (value >= sbyte.MinValue && value <= sbyte.MaxValue) il.Emit(OpCodes.Ldc_I4_S, (sbyte)value);
					else il.Emit(OpCodes.Ldc_I4, value);
					break;
			}
		}

		readonly struct SignalRef
		{
			public readonly bool IsConstant;
			public readonly uint Constant;
			public readonly int Slot;
			public readonly int ProducerNode;

			SignalRef(bool isConstant, uint constant, int slot, int producerNode)
			{
				IsConstant = isConstant;
				Constant = constant;
				Slot = slot;
				ProducerNode = producerNode;
			}

			public static SignalRef ConstantValue(uint value) => new(true, value, -1, -1);
			public static SignalRef FromSlot(int slot, int producerNode = -1) => new(false, 0, slot, producerNode);
		}

		readonly struct CompiledNode
		{
			public readonly ChipType Type;
			public readonly SignalRef[] Inputs;
			public readonly SignalRef[] Outputs;

			public CompiledNode(ChipType type, SignalRef[] inputs, SignalRef[] outputs)
			{
				Type = type;
				Inputs = inputs;
				Outputs = outputs;
			}
		}

		sealed class SignalResolver
		{
			readonly SimChip root;
			readonly Dictionary<SimPin, SimPin> incomingSource;
			readonly Dictionary<SimPin, int> rootInputSlots;
			readonly Dictionary<SimPin, SignalRef> primitiveOutputs;
			readonly Dictionary<SimPin, SignalRef> memo = new();
			readonly HashSet<SimPin> active = new();

			public SignalResolver(
				SimChip root,
				Dictionary<SimPin, SimPin> incomingSource,
				Dictionary<SimPin, int> rootInputSlots,
				Dictionary<SimPin, SignalRef> primitiveOutputs)
			{
				this.root = root;
				this.incomingSource = incomingSource;
				this.rootInputSlots = rootInputSlots;
				this.primitiveOutputs = primitiveOutputs;
			}

			public SignalRef Resolve(SimPin pin)
			{
				if (pin == null) return SignalRef.ConstantValue(DisconnectedState);
				if (memo.TryGetValue(pin, out SignalRef existing)) return existing;
				if (!active.Add(pin)) throw new InvalidOperationException("boundary signal cycle reached feedback JIT compiler");

				try
				{
					SignalRef result;
					if (ReferenceEquals(pin.parentChip, root) && pin.isInput && rootInputSlots.TryGetValue(pin, out int inputSlot))
					{
						result = SignalRef.FromSlot(inputSlot);
					}
					else if (incomingSource.TryGetValue(pin, out SimPin source))
					{
						result = Resolve(source);
					}
					else if (primitiveOutputs.TryGetValue(pin, out SignalRef primitive))
					{
						result = primitive;
					}
					else
					{
						result = SignalRef.ConstantValue(DisconnectedState);
					}

					memo[pin] = result;
					return result;
				}
				finally
				{
					active.Remove(pin);
				}
			}
		}
	}

	internal sealed class CompiledFeedbackProgram
	{
		readonly FeedbackJitCompiler.NativeFeedbackBlock[] blocks;
		readonly int[] outputSlots;
		readonly uint[] outputConstants;

		public readonly int ScratchCount;
		public readonly int InputCount;
		public readonly int OutputCount;
		public readonly int PrimitiveNodeCount;
		public readonly int PrimitiveOutputCount;
		public readonly double CompileMilliseconds;

		public CompiledFeedbackProgram(
			FeedbackJitCompiler.NativeFeedbackBlock[] blocks,
			int scratchCount,
			int inputCount,
			int outputCount,
			int primitiveNodeCount,
			int[] outputSlots,
			uint[] outputConstants,
			int primitiveOutputCount,
			double compileMilliseconds)
		{
			this.blocks = blocks;
			this.outputSlots = outputSlots;
			this.outputConstants = outputConstants;
			ScratchCount = scratchCount;
			InputCount = inputCount;
			OutputCount = outputCount;
			PrimitiveNodeCount = primitiveNodeCount;
			PrimitiveOutputCount = primitiveOutputCount;
			CompileMilliseconds = compileMilliseconds;
		}

		public void RunSweep(uint[] current, uint[] next)
		{
			for (int i = 0; i < blocks.Length; i++) blocks[i](current, next);
		}

		public void WriteOutputs(uint[] current, uint[] outputs)
		{
			for (int i = 0; i < outputSlots.Length; i++)
			{
				int slot = outputSlots[i];
				outputs[i] = slot < 0 ? outputConstants[i] : current[slot];
			}
		}

		public CompiledFeedbackExecutor CreateExecutor(SimChip root)
		{
			List<SimPin> primitiveOutputs = new(PrimitiveOutputCount);
			CollectPrimitiveOutputs(root, primitiveOutputs);
			if (primitiveOutputs.Count != PrimitiveOutputCount) return null;
			return new CompiledFeedbackExecutor(this, primitiveOutputs.ToArray());
		}

		static void CollectPrimitiveOutputs(SimChip chip, List<SimPin> outputs)
		{
			if (chip.ChipType == ChipType.Custom)
			{
				for (int i = 0; i < chip.SubChips.Length; i++) CollectPrimitiveOutputs(chip.SubChips[i], outputs);
				return;
			}

			if (!FeedbackJitCompilerIsSupported(chip.ChipType) || chip.OutputPins.Length == 0) return;
			for (int i = 0; i < chip.OutputPins.Length; i++) outputs.Add(chip.OutputPins[i]);
		}

		// Keep the executor construction independent from compiler-private helpers.
		static bool FeedbackJitCompilerIsSupported(ChipType type)
		{
			return type is
				ChipType.Nand or
				ChipType.TriStateBuffer or
				ChipType.Split_4To1Bit or
				ChipType.Split_8To1Bit or
				ChipType.Split_8To4Bit or
				ChipType.Merge_1To4Bit or
				ChipType.Merge_1To8Bit or
				ChipType.Merge_4To8Bit or
				ChipType.Bus_1Bit or
				ChipType.Bus_4Bit or
				ChipType.Bus_8Bit or
				ChipType.BusTerminus_1Bit or
				ChipType.BusTerminus_4Bit or
				ChipType.BusTerminus_8Bit;
		}
	}

	internal sealed class CompiledFeedbackExecutor
	{
		readonly CompiledFeedbackProgram program;
		readonly SimPin[] primitiveOutputPins;
		uint[] current;
		uint[] next;
		readonly uint[] outputs;
		readonly uint[] lastStableInputs;
		bool hasStableInputSnapshot;
		bool disabled;

		public bool Ready { get; private set; }
		public bool Disabled => disabled;
		public int PrimitiveNodeCount => program.PrimitiveNodeCount;
		public double CompileMilliseconds => program.CompileMilliseconds;
		public int LastSweepCount { get; private set; }

		public CompiledFeedbackExecutor(CompiledFeedbackProgram program, SimPin[] primitiveOutputPins)
		{
			this.program = program;
			this.primitiveOutputPins = primitiveOutputPins;
			current = new uint[program.ScratchCount];
			next = new uint[program.ScratchCount];
			outputs = new uint[program.OutputCount];
			lastStableInputs = new uint[program.InputCount];
		}

		public void SynchronizeFromChipTree()
		{
			if (disabled) return;

			int slot = program.InputCount;
			for (int i = 0; i < primitiveOutputPins.Length && slot < current.Length; i++, slot++)
			{
				uint state = primitiveOutputPins[i].State;
				current[slot] = state;
				next[slot] = state;
			}

			hasStableInputSnapshot = false;
			Ready = true;
		}

		public void MaterializeState()
		{
			if (!Ready) return;

			int slot = program.InputCount;
			for (int i = 0; i < primitiveOutputPins.Length && slot < current.Length; i++, slot++)
			{
				primitiveOutputPins[i].State = current[slot];
			}
		}

		public void Disable()
		{
			MaterializeState();
			disabled = true;
			hasStableInputSnapshot = false;
			Ready = false;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public bool Evaluate(SimChip chip, out uint[] result, out int sweepCount)
		{
			result = outputs;
			sweepCount = 0;
			LastSweepCount = 0;
			if (!Ready || disabled) return false;

			int inputCount = Math.Min(program.InputCount, chip.InputPins.Length);

			if (hasStableInputSnapshot && inputCount == lastStableInputs.Length)
			{
				bool unchanged = true;
				for (int i = 0; i < inputCount; i++)
				{
					if (chip.InputPins[i].State == lastStableInputs[i]) continue;
					unchanged = false;
					break;
				}

				// Feedback-JIT programs reject clocks, keys, pulse generators, RAM,
				// displays and every other spontaneous/stateful primitive. Therefore a
				// previously converged region cannot change while all boundary inputs
				// remain identical.
				if (unchanged) return true;
			}

			for (int i = 0; i < inputCount; i++)
			{
				uint state = chip.InputPins[i].State;
				current[i] = state;
				next[i] = state;
			}

			int maxSweeps = Math.Max(256, program.PrimitiveNodeCount + 64);
			bool converged = false;

			for (int sweep = 0; sweep < maxSweeps; sweep++)
			{
				for (int i = 0; i < inputCount; i++) next[i] = current[i];
				program.RunSweep(current, next);

				bool changed = false;
				for (int slot = program.InputCount; slot < program.ScratchCount; slot++)
				{
					if (current[slot] != next[slot])
					{
						changed = true;
						break;
					}
				}

				sweepCount = sweep + 1;
				if (!changed)
				{
					converged = true;
					break;
				}

				uint[] swap = current;
				current = next;
				next = swap;
			}

			LastSweepCount = sweepCount;
			program.WriteOutputs(current, outputs);

			if (converged && inputCount == lastStableInputs.Length)
			{
				for (int i = 0; i < inputCount; i++) lastStableInputs[i] = chip.InputPins[i].State;
				hasStableInputSnapshot = true;
			}
			else
			{
				hasStableInputSnapshot = false;
			}

			return converged;
		}
	}
}
