using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using DLS.Description;
using DLS.Game;

namespace DLS.Simulation
{
	// Native JIT accelerator for pure custom chips.
	//
	// A custom chip is flattened to primitive combinational operations once, emitted as
	// DynamicMethod IL, and then compiled by Mono's JIT to native machine code. Runtime
	// evaluation therefore avoids recursive Custom Chip traversal, gate-type switches,
	// queues and per-wire propagation inside the compiled block.
	//
	// This is intentionally conservative: any state, feedback or ambiguous/multi-driver
	// net falls back to the deterministic simulator. The current Standalone project uses
	// the Mono scripting backend, where Reflection.Emit/DynamicMethod is supported. If a
	// platform does not support dynamic code (for example IL2CPP), compilation simply
	// declines and the normal deterministic engine remains active.
	public static class CombinationalJitCompiler
	{
		const uint DisconnectedState = 0xFFFF0000u;
		const uint SingleBitMask = PinState.SingleBitMask;
		const int NodesPerNativeMethod = 256;
		const int MinPrimitiveNodeCount = 3;

		internal delegate void NativeBlock(uint[] scratch);
		internal delegate void NativeOutputWriter(uint[] scratch, uint[] outputs);

		sealed class ProgramHolder
		{
			public readonly CompiledCombinationalProgram Program;
			public ProgramHolder(CompiledCombinationalProgram program) => Program = program;
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
			simChip.CompiledExecutor = null;

			if (!DynamicCodeAvailable ||
			    description == null ||
			    library == null ||
			    description.ChipType != ChipType.Custom)
			{
				return;
			}

			// Reuse the exact same safety analysis as the LUT cache. This guarantees that
			// only pure, acyclic, single-driver logic is collapsed into a native block.
			ChipCacheAnalysis analysis = CombinationalChipCacheManager.Analyze(description, library);
			if (!analysis.CanCache) return;

			CompiledCombinationalProgram program = GetOrCompileProgram(simChip, description);
			if (program == null) return;

			simChip.CompiledExecutor = new CompiledChipExecutor(program);
		}

		static CompiledCombinationalProgram GetOrCompileProgram(SimChip representative, ChipDescription description)
		{
			lock (programCacheLock)
			{
				if (programs.TryGetValue(description, out ProgramHolder holder))
				{
					return holder.Program;
				}
			}

			CompiledCombinationalProgram program;
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
				// Never make a project unloadable because the optional accelerator rejected
				// an unusual graph. The deterministic engine is the correctness fallback.
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

		static CompiledCombinationalProgram CompileProgram(SimChip root, string chipName)
		{
			if (root == null || root.ChipType != ChipType.Custom) return null;

			Stopwatch timer = Stopwatch.StartNew();

			List<SimPin> localPins = new();
			HashSet<SimPin> localPinSet = new();
			List<SimChip> primitiveChips = new();
			CollectLocalGraph(root, localPins, localPinSet, primitiveChips);

			// Terminus chips are sinks only; there is nothing to execute for them.
			primitiveChips.RemoveAll(c => c.OutputPins.Length == 0);
			if (primitiveChips.Count < MinPrimitiveNodeCount) return null;

			Dictionary<SimPin, SimPin> incomingSource = BuildIncomingSourceMap(localPins, localPinSet);
			Dictionary<SimPin, int> rootInputSlots = new();
			for (int i = 0; i < root.InputPins.Length; i++) rootInputSlots[root.InputPins[i]] = i;

			Dictionary<SimChip, int> nodeIndexByChip = new();
			for (int i = 0; i < primitiveChips.Count; i++) nodeIndexByChip.Add(primitiveChips[i], i);

			// Every primitive output receives one scratch slot. Root inputs occupy the first
			// slots so Evaluate() can copy live pin states into a compact contiguous array.
			Dictionary<SimPin, SignalRef> primitiveOutputSignals = new();
			int nextScratchSlot = root.InputPins.Length;
			for (int nodeIndex = 0; nodeIndex < primitiveChips.Count; nodeIndex++)
			{
				SimChip chip = primitiveChips[nodeIndex];
				for (int outputIndex = 0; outputIndex < chip.OutputPins.Length; outputIndex++)
				{
					primitiveOutputSignals.Add(
						chip.OutputPins[outputIndex],
						SignalRef.FromSlot(nextScratchSlot++, nodeIndex));
				}
			}

			SignalResolver resolver = new(root, incomingSource, rootInputSlots, primitiveOutputSignals);
			CompiledNode[] nodes = new CompiledNode[primitiveChips.Count];
			List<int>[] outgoing = new List<int>[primitiveChips.Count];
			int[] indegree = new int[primitiveChips.Count];

			for (int nodeIndex = 0; nodeIndex < primitiveChips.Count; nodeIndex++)
			{
				SimChip chip = primitiveChips[nodeIndex];
				if (!IsSupportedPrimitive(chip.ChipType)) return null;

				SignalRef[] inputs = new SignalRef[chip.InputPins.Length];
				for (int inputIndex = 0; inputIndex < chip.InputPins.Length; inputIndex++)
				{
					SignalRef signal = resolver.Resolve(chip.InputPins[inputIndex]);
					inputs[inputIndex] = signal;

					int producer = signal.ProducerNode;
					if (producer >= 0 && producer != nodeIndex)
					{
						(outgoing[producer] ??= new List<int>()).Add(nodeIndex);
						indegree[nodeIndex]++;
					}
					else if (producer == nodeIndex)
					{
						return null;
					}
				}

				SignalRef[] outputs = new SignalRef[chip.OutputPins.Length];
				for (int outputIndex = 0; outputIndex < chip.OutputPins.Length; outputIndex++)
				{
					outputs[outputIndex] = primitiveOutputSignals[chip.OutputPins[outputIndex]];
				}

				nodes[nodeIndex] = new CompiledNode(chip.ChipType, inputs, outputs);
			}

			int[] topologicalOrder = TopologicalSort(indegree, outgoing);
			if (topologicalOrder == null || topologicalOrder.Length != nodes.Length) return null;

			SignalRef[] rootOutputs = new SignalRef[root.OutputPins.Length];
			for (int i = 0; i < root.OutputPins.Length; i++) rootOutputs[i] = resolver.Resolve(root.OutputPins[i]);

			List<NativeBlock> blocks = new();
			for (int start = 0; start < topologicalOrder.Length; start += NodesPerNativeMethod)
			{
				int count = Math.Min(NodesPerNativeMethod, topologicalOrder.Length - start);
				NativeBlock block = EmitNativeBlock(nodes, topologicalOrder, start, count, chipName, blocks.Count);
				if (block == null) return null;
				blocks.Add(block);
			}

			NativeOutputWriter outputWriter = EmitOutputWriter(rootOutputs, chipName);
			if (outputWriter == null) return null;

			// Optional C backend consumes the exact same verified acyclic program.
			// The DynamicMethod implementation is always retained as a bit-exact fallback.
			NativeCombinationalProgram nativeProgram = NativeCombinationalBackend.TryCreate(
				nodes,
				topologicalOrder,
				rootOutputs,
				nextScratchSlot);

			timer.Stop();
			return new CompiledCombinationalProgram(
				blocks.ToArray(),
				outputWriter,
				nativeProgram,
				nextScratchSlot,
				root.InputPins.Length,
				root.OutputPins.Length,
				primitiveChips.Count,
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
							throw new InvalidOperationException("multiple drivers reached JIT compiler");
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

		static int[] TopologicalSort(int[] indegreeSource, List<int>[] outgoing)
		{
			int[] indegree = (int[])indegreeSource.Clone();
			Queue<int> ready = new();
			for (int i = 0; i < indegree.Length; i++)
			{
				if (indegree[i] == 0) ready.Enqueue(i);
			}

			int[] result = new int[indegree.Length];
			int write = 0;
			while (ready.Count > 0)
			{
				int node = ready.Dequeue();
				result[write++] = node;
				List<int> targets = outgoing[node];
				if (targets == null) continue;

				for (int i = 0; i < targets.Count; i++)
				{
					int target = targets[i];
					if (--indegree[target] == 0) ready.Enqueue(target);
				}
			}

			return write == result.Length ? result : null;
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

		static NativeBlock EmitNativeBlock(
			CompiledNode[] nodes,
			int[] order,
			int start,
			int count,
			string chipName,
			int blockIndex)
		{
			string safeName = string.IsNullOrWhiteSpace(chipName) ? "Unnamed" : chipName.Replace(' ', '_');
			DynamicMethod method = new(
				$"DLS_JIT_{safeName}_{blockIndex}",
				typeof(void),
				new[] { typeof(uint[]) },
				typeof(CombinationalJitCompiler).Module,
				true);

			ILGenerator il = method.GetILGenerator();
			for (int i = 0; i < count; i++) EmitNode(il, nodes[order[start + i]]);
			il.Emit(OpCodes.Ret);

			return (NativeBlock)method.CreateDelegate(typeof(NativeBlock));
		}

		static NativeOutputWriter EmitOutputWriter(SignalRef[] outputs, string chipName)
		{
			string safeName = string.IsNullOrWhiteSpace(chipName) ? "Unnamed" : chipName.Replace(' ', '_');
			DynamicMethod method = new(
				$"DLS_JIT_{safeName}_Outputs",
				typeof(void),
				new[] { typeof(uint[]), typeof(uint[]) },
				typeof(CombinationalJitCompiler).Module,
				true);

			ILGenerator il = method.GetILGenerator();
			for (int i = 0; i < outputs.Length; i++)
			{
				il.Emit(OpCodes.Ldarg_1);
				EmitInt(il, i);
				EmitLoadSignal(il, outputs[i]);
				il.Emit(OpCodes.Stelem_I4);
			}
			il.Emit(OpCodes.Ret);

			return (NativeOutputWriter)method.CreateDelegate(typeof(NativeOutputWriter));
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
					// Sink-only built-in. No generated operation is required.
					break;

				default:
					throw new NotSupportedException("Unsupported JIT primitive: " + node.Type);
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
				}
				if (bit != 0) il.Emit(OpCodes.Or);
			}
			il.Emit(OpCodes.Stelem_I4);
		}

		static void EmitMerge4To8(ILGenerator il, CompiledNode node)
		{
			// Exact equivalent of PinState.Set8BitFrom4BitSources(ref state, input[1], input[0]).
			EmitStorePrefix(il, node.Outputs[0].Slot);

			// bit states
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

			// tristate flags, moved back to high 16 bits
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
			// output[0] = upper nibble, output[1] = lower nibble (matches the existing engine).
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
			il.Emit(OpCodes.Ldarg_0);
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

		internal readonly struct SignalRef
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

		internal readonly struct CompiledNode
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
				if (!active.Add(pin)) throw new InvalidOperationException("signal cycle reached JIT compiler");

				try
				{
					SignalRef result;

					// The root custom-chip inputs are the JIT function arguments. Do not follow
					// an external parent connection even when this instance is nested.
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
						// SimPin initializes every disconnected input to all-tristated.
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

	internal sealed class CompiledCombinationalProgram
	{
		readonly CombinationalJitCompiler.NativeBlock[] blocks;
		readonly CombinationalJitCompiler.NativeOutputWriter outputWriter;
		readonly NativeCombinationalProgram nativeProgram;
		readonly uint[] validationOutputs;

		public readonly int ScratchCount;
		public readonly int InputCount;
		public readonly int OutputCount;
		public readonly int PrimitiveNodeCount;
		public readonly double CompileMilliseconds;

		public CompiledCombinationalProgram(
			CombinationalJitCompiler.NativeBlock[] blocks,
			CombinationalJitCompiler.NativeOutputWriter outputWriter,
			NativeCombinationalProgram nativeProgram,
			int scratchCount,
			int inputCount,
			int outputCount,
			int primitiveNodeCount,
			double compileMilliseconds)
		{
			this.blocks = blocks;
			this.outputWriter = outputWriter;
			this.nativeProgram = nativeProgram;
			validationOutputs = new uint[outputCount];
			ScratchCount = scratchCount;
			InputCount = inputCount;
			OutputCount = outputCount;
			PrimitiveNodeCount = primitiveNodeCount;
			CompileMilliseconds = compileMilliseconds;
		}

		public bool UsesNativeC => nativeProgram != null;

		public void Run(uint[] scratch, uint[] outputs)
		{
			if (nativeProgram != null &&
			    NativeCombinationalBackend.ShouldUseNative(nativeProgram.IsNandOnly) &&
			    nativeProgram.TryRun(scratch, outputs))
			{
				NativeCombinationalBackend.RecordNativeEvaluation();

				if (!NativeCombinationalBackend.ValidationEnabled) return;

				Array.Copy(outputs, validationOutputs, Math.Min(outputs.Length, validationOutputs.Length));
				NativeCombinationalBackend.RecordDynamicJitEvaluation();
				for (int i = 0; i < blocks.Length; i++) blocks[i](scratch);
				outputWriter(scratch, outputs);

				int compareCount = Math.Min(outputs.Length, validationOutputs.Length);
				for (int i = 0; i < compareCount; i++)
				{
					if (outputs[i] == validationOutputs[i]) continue;
					EngineDiagnostics.Record(
						$"event=native-c-jit-mismatch\noutput={i}\nnative={validationOutputs[i]}\njit={outputs[i]}");
					break;
				}
				return;
			}

			NativeCombinationalBackend.RecordDynamicJitEvaluation();
			for (int i = 0; i < blocks.Length; i++) blocks[i](scratch);
			outputWriter(scratch, outputs);
		}
	}

	internal sealed class CompiledChipExecutor
	{
		readonly CompiledCombinationalProgram program;
		readonly uint[] scratch;
		readonly uint[] outputs;

		public int PrimitiveNodeCount => program.PrimitiveNodeCount;
		public double CompileMilliseconds => program.CompileMilliseconds;
		public bool UsesNativeC => program.UsesNativeC;

		public CompiledChipExecutor(CompiledCombinationalProgram program)
		{
			this.program = program;
			scratch = new uint[program.ScratchCount];
			outputs = new uint[program.OutputCount];
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public uint[] Evaluate(SimChip chip)
		{
			int count = Math.Min(program.InputCount, chip.InputPins.Length);
			for (int i = 0; i < count; i++) scratch[i] = chip.InputPins[i].State;
			program.Run(scratch, outputs);
			return outputs;
		}
	}
}
