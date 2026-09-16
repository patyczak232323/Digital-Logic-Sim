using System;
using System.Collections.Generic;
using System.Diagnostics;
using DLS.Description;
using DLS.Game;

namespace DLS.Simulation
{
	public static class DeterministicSimulator
	{
		const int Address8BitMask = 0xFF;

		public static bool DiagnosticsEnabled;
		public static Action<string> DiagnosticSink;

		public static int LastDeltaCycles { get; private set; }
		public static int LastGateEvaluations { get; private set; }
		public static int LastSignalPropagations { get; private set; }
		public static int LastTargetResolutions { get; private set; }
		public static bool LastSettleConverged { get; private set; } = true;

		static readonly Stopwatch stopwatch = Stopwatch.StartNew();

		static readonly List<RuntimeChip> combinationalChips = new();
		static readonly List<RuntimeChip> sourceChips = new();
		static readonly List<RuntimeChip> sequentialChips = new();
		static readonly List<RuntimeChip> buzzerChips = new();
		static readonly List<SimPin> allPins = new();
		static readonly List<int> pinOwnerCombinationalIndexBuild = new();

		static readonly Dictionary<SimPin, int> pinIndices = new();
		// Compressed sparse row (CSR) adjacency. Flat arrays avoid one managed
		// allocation per pin and improve cache locality while propagating signals.
		static int[] targetOffsetsBySource = Array.Empty<int>();
		static int[] targetIndicesBySource = Array.Empty<int>();
		static int[] sourceOffsetsByTarget = Array.Empty<int>();
		static int[] sourceIndicesByTarget = Array.Empty<int>();
		static int[] targetOwnerCombinationalIndex = Array.Empty<int>();
		static bool[] targetIsCustomBoundary = Array.Empty<bool>();

		static readonly Dictionary<SimChip, string> registeredDiagnosticPaths = new();
		static readonly Dictionary<SimChip, string> runtimeDiagnosticPaths = new();

		static Queue<int> targetQueue = new();
		static bool[] queuedTargets = Array.Empty<bool>();
		static int[] triggeringSourceByTarget = Array.Empty<int>();

		static readonly List<int> dirtyChips = new();
		static bool[] dirtyChipFlags = Array.Empty<bool>();
		static readonly List<int> evaluationBatch = new();
		static readonly List<int> initializationOrder = new();
		static readonly List<PendingPinState> pendingPinStates = new();
		static DevPinInstance[] boundInputPins;
		static DevPinInstance[] boundInputPinSnapshot = Array.Empty<DevPinInstance>();
		static int[] boundInputPinIndices = Array.Empty<int>();

		static SimChip topologyRoot;
		static bool topologyDirty = true;
		static bool needsInitialPropagation = true;

		static SimAudio audioState;
		static double elapsedSecondsOld;
		static double deltaTime;

		readonly struct RuntimeChip
		{
			public readonly SimChip Chip;
			public readonly int InputStart;
			public readonly int OutputStart;
			public readonly int CombinationalIndex;

			public RuntimeChip(SimChip chip, int inputStart, int outputStart, int combinationalIndex)
			{
				Chip = chip;
				InputStart = inputStart;
				OutputStart = outputStart;
				CombinationalIndex = combinationalIndex;
			}
		}

		readonly struct PendingPinState
		{
			public readonly int PinIndex;
			public readonly uint State;

			public PendingPinState(int pinIndex, uint state)
			{
				PinIndex = pinIndex;
				State = state;
			}
		}

		public static void RunSimulationStep(SimChip rootSimChip, DevPinInstance[] inputPins, SimAudio newAudioState)
		{
			inputPins ??= Array.Empty<DevPinInstance>();
			audioState = newAudioState;
			audioState?.InitFrame();

			EnsureTopology(rootSimChip, inputPins);

			Simulator.simulationFrame++;

			LastDeltaCycles = 0;
			LastGateEvaluations = 0;
			LastSignalPropagations = 0;
			LastTargetResolutions = 0;
			LastSettleConverged = true;

			ApplyExternalInputs(inputPins);
			UpdateFrameSources();

			if (needsInitialPropagation)
			{
				PrimeInitialCombinationalState();
				QueueAllExistingSignals();
				for (int i = 0; i < combinationalChips.Count; i++)
				{
					MarkDirty(i);
				}

				needsInitialPropagation = false;
			}

			(bool preConverged, int preDelta) = SettleCombinational();
			LastDeltaCycles += preDelta;
			LastSettleConverged &= preConverged;

			AdvanceSequentialComponents();

			(bool postConverged, int postDelta) = SettleCombinational();
			LastDeltaCycles += postDelta;
			LastSettleConverged &= postConverged;

			UpdateBuzzers();
			UpdateAudioState();
		}

		public static void UpdateInPausedState()
		{
			if (audioState == null) return;

			audioState.InitFrame();
			UpdateAudioState();
		}

		public static void InvalidateTopology()
		{
			topologyDirty = true;
		}

		public static void Reset()
		{
			topologyRoot = null;
			topologyDirty = true;
			needsInitialPropagation = true;

			combinationalChips.Clear();
			sourceChips.Clear();
			sequentialChips.Clear();
			buzzerChips.Clear();
			allPins.Clear();
			pinOwnerCombinationalIndexBuild.Clear();
			pinIndices.Clear();
			targetOffsetsBySource = Array.Empty<int>();
			targetIndicesBySource = Array.Empty<int>();
			sourceOffsetsByTarget = Array.Empty<int>();
			sourceIndicesByTarget = Array.Empty<int>();
			targetOwnerCombinationalIndex = Array.Empty<int>();
			targetIsCustomBoundary = Array.Empty<bool>();
			queuedTargets = Array.Empty<bool>();
			triggeringSourceByTarget = Array.Empty<int>();
			dirtyChipFlags = Array.Empty<bool>();
			initializationOrder.Clear();
			boundInputPins = null;
			boundInputPinSnapshot = Array.Empty<DevPinInstance>();
			boundInputPinIndices = Array.Empty<int>();
			runtimeDiagnosticPaths.Clear();
			registeredDiagnosticPaths.Clear();

			ClearWorkQueues();

			audioState = null;
			stopwatch.Restart();
			elapsedSecondsOld = 0;
			deltaTime = 0;

			LastDeltaCycles = 0;
			LastGateEvaluations = 0;
			LastSignalPropagations = 0;
			LastTargetResolutions = 0;
			LastSettleConverged = true;
		}

		public static void RegisterDiagnosticPaths(SimChip root, ChipDescription rootDescription, ChipLibrary library)
		{
			if (root == null || rootDescription == null) return;

			string rootName = string.IsNullOrWhiteSpace(rootDescription.Name) ? "ROOT" : rootDescription.Name;
			RegisterDiagnosticPathsRecursive(root, rootDescription, library, rootName);
		}

		static void RegisterDiagnosticPathsRecursive(SimChip chip, ChipDescription description, ChipLibrary library, string path)
		{
			registeredDiagnosticPaths[chip] = path;

			int count = Math.Min(chip.SubChips.Length, description.SubChips?.Length ?? 0);
			for (int i = 0; i < count; i++)
			{
				SubChipDescription subDescription = description.SubChips[i];
				SimChip subChip = chip.SubChips[i];

				string name = string.IsNullOrWhiteSpace(subDescription.Name) ? subChip.ChipType.ToString() : subDescription.Name;
				string childPath = $"{path}/{name}[{subDescription.ID}]";
				registeredDiagnosticPaths[subChip] = childPath;

				if (subChip.ChipType == ChipType.Custom)
				{
					ChipDescription fullDescription = library.GetChipDescription(subDescription.Name);
					RegisterDiagnosticPathsRecursive(subChip, fullDescription, library, childPath);
				}
			}
		}

		static void EnsureTopology(SimChip root, DevPinInstance[] inputPins)
		{
			if (!topologyDirty && topologyRoot == root)
			{
				if (!ExternalInputBindingsMatch(inputPins)) BindExternalInputs(root, inputPins);
				return;
			}

			topologyRoot = root;
			topologyDirty = false;
			needsInitialPropagation = true;

			combinationalChips.Clear();
			sourceChips.Clear();
			sequentialChips.Clear();
			buzzerChips.Clear();
			allPins.Clear();
			pinOwnerCombinationalIndexBuild.Clear();
			pinIndices.Clear();
			initializationOrder.Clear();
			runtimeDiagnosticPaths.Clear();
			ClearWorkQueues();

			if (root == null)
			{
				ResetCompiledArrays();
				BindExternalInputs(root, inputPins);
				return;
			}

			string rootPath = registeredDiagnosticPaths.TryGetValue(root, out string registeredRootPath)
				? registeredRootPath
				: "ROOT";

			CollectTopologyRecursive(root, rootPath);

			for (int i = 0; i < allPins.Count; i++) pinIndices.Add(allPins[i], i);

			List<int>[] targetBuild = new List<int>[allPins.Count];
			List<int>[] sourceBuild = new List<int>[allPins.Count];

			for (int i = 0; i < allPins.Count; i++)
			{
				SimPin source = allPins[i];
				for (int j = 0; j < source.ConnectedTargetPins.Length; j++)
				{
					SimPin target = source.ConnectedTargetPins[j];
					if (!pinIndices.TryGetValue(target, out int targetIndex))
					{
						continue;
					}

					(targetBuild[i] ??= new List<int>()).Add(targetIndex);
					(sourceBuild[targetIndex] ??= new List<int>()).Add(i);
				}
			}

			BuildAdjacency(targetBuild, out targetOffsetsBySource, out targetIndicesBySource);
			BuildAdjacency(sourceBuild, out sourceOffsetsByTarget, out sourceIndicesByTarget);
			targetOwnerCombinationalIndex = pinOwnerCombinationalIndexBuild.ToArray();
			targetIsCustomBoundary = new bool[allPins.Count];

			for (int i = 0; i < allPins.Count; i++)
			{
				targetIsCustomBoundary[i] = allPins[i].parentChip.ChipType == ChipType.Custom;
			}

			targetQueue = new Queue<int>(Math.Max(64, allPins.Count));
			queuedTargets = new bool[allPins.Count];
			triggeringSourceByTarget = new int[allPins.Count];
			dirtyChipFlags = new bool[combinationalChips.Count];
			EnsureListCapacity(dirtyChips, combinationalChips.Count);
			EnsureListCapacity(evaluationBatch, combinationalChips.Count);
			EnsureListCapacity(initializationOrder, combinationalChips.Count);
			EnsureListCapacity(pendingPinStates, allPins.Count);

			for (int i = 0; i < combinationalChips.Count; i++) initializationOrder.Add(i);
			initializationOrder.Sort((a, b) => string.CompareOrdinal(
				GetChipPath(combinationalChips[a].Chip),
				GetChipPath(combinationalChips[b].Chip)));

			BindExternalInputs(root, inputPins);
		}

		static void EnsureListCapacity<T>(List<T> list, int requiredCapacity)
		{
			if (list.Capacity < requiredCapacity) list.Capacity = requiredCapacity;
		}

		static void BuildAdjacency(List<int>[] rows, out int[] offsets, out int[] indices)
		{
			offsets = new int[rows.Length + 1];
			int edgeCount = 0;
			for (int i = 0; i < rows.Length; i++)
			{
				edgeCount += rows[i]?.Count ?? 0;
				offsets[i + 1] = edgeCount;
			}

			indices = new int[edgeCount];
			int writeIndex = 0;
			for (int i = 0; i < rows.Length; i++)
			{
				List<int> row = rows[i];
				if (row == null) continue;
				for (int j = 0; j < row.Count; j++) indices[writeIndex++] = row[j];
			}
		}

		static void ResetCompiledArrays()
		{
			targetOffsetsBySource = Array.Empty<int>();
			targetIndicesBySource = Array.Empty<int>();
			sourceOffsetsByTarget = Array.Empty<int>();
			sourceIndicesByTarget = Array.Empty<int>();
			targetOwnerCombinationalIndex = Array.Empty<int>();
			targetIsCustomBoundary = Array.Empty<bool>();
			queuedTargets = Array.Empty<bool>();
			triggeringSourceByTarget = Array.Empty<int>();
			dirtyChipFlags = Array.Empty<bool>();
			targetQueue = new Queue<int>();
		}

		static void CollectTopologyRecursive(SimChip chip, string fallbackPath)
		{
			string path = registeredDiagnosticPaths.TryGetValue(chip, out string registeredPath)
				? registeredPath
				: fallbackPath;

			runtimeDiagnosticPaths[chip] = path;
			bool isCombinational = chip.ChipType != ChipType.Custom && IsCombinationalChipType(chip.ChipType);
			int combinationalIndex = isCombinational ? combinationalChips.Count : -1;
			int inputStart = allPins.Count;

			for (int i = 0; i < chip.InputPins.Length; i++)
			{
				allPins.Add(chip.InputPins[i]);
				pinOwnerCombinationalIndexBuild.Add(combinationalIndex);
			}

			int outputStart = allPins.Count;
			for (int i = 0; i < chip.OutputPins.Length; i++)
			{
				allPins.Add(chip.OutputPins[i]);
				pinOwnerCombinationalIndexBuild.Add(-1);
			}

			if (chip.ChipType == ChipType.Custom)
			{
				for (int i = 0; i < chip.SubChips.Length; i++)
				{
					SimChip child = chip.SubChips[i];
					string childPath = $"{path}/{child.ChipType}[{child.ID}]";
					CollectTopologyRecursive(child, childPath);
				}

				return;
			}

			RuntimeChip runtimeChip = new(chip, inputStart, outputStart, combinationalIndex);

			if (isCombinational)
			{
				combinationalChips.Add(runtimeChip);
			}

			switch (chip.ChipType)
			{
				case ChipType.Clock:
				case ChipType.Key:
					sourceChips.Add(runtimeChip);
					break;

				case ChipType.Pulse:
				case ChipType.dev_Ram_8Bit:
				case ChipType.DisplayRGB:
				case ChipType.DisplayDot:
					sequentialChips.Add(runtimeChip);
					break;

				case ChipType.Buzzer:
					buzzerChips.Add(runtimeChip);
					break;
			}
		}

		static bool IsCombinationalChipType(ChipType type)
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
				ChipType.Rom_256x16 or
				ChipType.dev_Ram_8Bit or
				ChipType.DisplayRGB or
				ChipType.DisplayDot or
				ChipType.Bus_1Bit or
				ChipType.Bus_4Bit or
				ChipType.Bus_8Bit;
		}

		static void BindExternalInputs(SimChip rootSimChip, DevPinInstance[] inputPins)
		{
			boundInputPins = inputPins;
			boundInputPinSnapshot = (DevPinInstance[])inputPins.Clone();
			boundInputPinIndices = new int[inputPins.Length];
			Array.Fill(boundInputPinIndices, -1);

			if (rootSimChip == null) return;

			for (int i = 0; i < inputPins.Length; i++)
			{
				DevPinInstance input = inputPins[i];
				if (input?.Pin == null) continue;

				try
				{
					SimPin simPin = rootSimChip.GetSimPinFromAddress(input.Pin.Address);
					if (pinIndices.TryGetValue(simPin, out int pinIndex)) boundInputPinIndices[i] = pinIndex;
				}
				catch (Exception)
				{
					// A project edit can briefly expose the new editor pin list before the
					// corresponding simulation-thread topology modification is applied.
				}
			}
		}

		static bool ExternalInputBindingsMatch(DevPinInstance[] inputPins)
		{
			if (!ReferenceEquals(boundInputPins, inputPins) || boundInputPinSnapshot.Length != inputPins.Length)
			{
				return false;
			}

			for (int i = 0; i < inputPins.Length; i++)
			{
				if (!ReferenceEquals(boundInputPinSnapshot[i], inputPins[i])) return false;
			}

			return true;
		}

		static void ApplyExternalInputs(DevPinInstance[] inputPins)
		{
			for (int i = 0; i < inputPins.Length; i++)
			{
				DevPinInstance input = inputPins[i];
				if (input?.Pin == null) continue;

				int pinIndex = i < boundInputPinIndices.Length ? boundInputPinIndices[i] : -1;
				if (pinIndex >= 0)
				{
					SimPin simPin = allPins[pinIndex];
					uint newState = input.Pin.PlayerInputState;

					if (simPin.State != newState)
					{
						simPin.State = newState;
						QueueFanout(pinIndex);
					}
				}

				input.Pin.State = input.Pin.PlayerInputState;
			}
		}

		static void UpdateFrameSources()
		{
			pendingPinStates.Clear();

			for (int i = 0; i < sourceChips.Count; i++)
			{
				RuntimeChip runtimeChip = sourceChips[i];
				SimChip chip = runtimeChip.Chip;

				switch (chip.ChipType)
				{
					case ChipType.Clock:
					{
						bool high =
							Simulator.stepsPerClockTransition != 0 &&
							((Simulator.simulationFrame / Simulator.stepsPerClockTransition) & 1) == 0;

						StageOutput(runtimeChip.OutputStart, high ? PinState.LogicHigh : PinState.LogicLow);
						break;
					}

					case ChipType.Key:
					{
						bool isHeld = SimKeyboardHelper.KeyIsHeld((char)chip.InternalState[0]);
						StageOutput(runtimeChip.OutputStart, isHeld ? PinState.LogicHigh : PinState.LogicLow);
						break;
					}
				}
			}

			CommitPendingOutputs();
		}

		static void AdvanceSequentialComponents()
		{
			pendingPinStates.Clear();

			for (int i = 0; i < sequentialChips.Count; i++)
			{
				RuntimeChip runtimeChip = sequentialChips[i];
				SimChip chip = runtimeChip.Chip;

				switch (chip.ChipType)
				{
					case ChipType.Pulse:
						AdvancePulse(runtimeChip);
						break;

					case ChipType.dev_Ram_8Bit:
						if (AdvanceRam(chip)) MarkDirty(runtimeChip.CombinationalIndex);
						break;

					case ChipType.DisplayRGB:
						if (AdvanceDisplayRgb(chip)) MarkDirty(runtimeChip.CombinationalIndex);
						break;

					case ChipType.DisplayDot:
						if (AdvanceDisplayDot(chip)) MarkDirty(runtimeChip.CombinationalIndex);
						break;
				}
			}

			CommitPendingOutputs();
		}

		static void AdvancePulse(RuntimeChip runtimeChip)
		{
			SimChip chip = runtimeChip.Chip;
			const int pulseDurationIndex = 0;
			const int pulseTicksRemainingIndex = 1;
			const int pulseInputOldIndex = 2;

			uint inputState = chip.InputPins[0].State;
			bool pulseInputHigh = PinState.FirstBitHigh(inputState);
			uint pulseTicksRemaining = chip.InternalState[pulseTicksRemainingIndex];

			if (pulseTicksRemaining == 0)
			{
				bool isRisingEdge = pulseInputHigh && chip.InternalState[pulseInputOldIndex] == 0;
				if (isRisingEdge)
				{
					pulseTicksRemaining = chip.InternalState[pulseDurationIndex];
					chip.InternalState[pulseTicksRemainingIndex] = pulseTicksRemaining;
				}
			}

			uint outputState = PinState.LogicLow;
			if (pulseTicksRemaining > 0)
			{
				chip.InternalState[pulseTicksRemainingIndex]--;
				outputState = PinState.LogicHigh;
			}
			else if (PinState.GetTristateFlags(inputState) != 0)
			{
				PinState.SetAllDisconnected(ref outputState);
			}

			chip.InternalState[pulseInputOldIndex] = pulseInputHigh ? 1u : 0;
			StageOutput(runtimeChip.OutputStart, outputState);
		}

		static bool AdvanceRam(SimChip chip)
		{
			uint addressPin = chip.InputPins[0].State;
			uint dataPin = chip.InputPins[1].State;
			uint writeEnablePin = chip.InputPins[2].State;
			uint resetPin = chip.InputPins[3].State;
			uint clockPin = chip.InputPins[4].State;

			bool clockHigh = PinState.FirstBitHigh(clockPin);
			bool isRisingEdge = clockHigh && chip.InternalState[^1] == 0;
			chip.InternalState[^1] = clockHigh ? 1u : 0;

			if (!isRisingEdge) return false;

			if (PinState.FirstBitHigh(resetPin))
			{
				for (int i = 0; i < 256; i++) chip.InternalState[i] = 0;
				return true;
			}

			if (PinState.FirstBitHigh(writeEnablePin))
			{
				chip.InternalState[GetAddress8Bit(addressPin)] = PinState.GetBitStates(dataPin) & Address8BitMask;
				return true;
			}

			return false;
		}

		static bool AdvanceDisplayRgb(SimChip chip)
		{
			const int addressSpace = 256;

			uint addressPin = chip.InputPins[0].State;
			uint redPin = chip.InputPins[1].State;
			uint greenPin = chip.InputPins[2].State;
			uint bluePin = chip.InputPins[3].State;
			uint resetPin = chip.InputPins[4].State;
			uint writePin = chip.InputPins[5].State;
			uint refreshPin = chip.InputPins[6].State;
			uint clockPin = chip.InputPins[7].State;

			bool clockHigh = PinState.FirstBitHigh(clockPin);
			bool isRisingEdge = clockHigh && chip.InternalState[^1] == 0;
			chip.InternalState[^1] = clockHigh ? 1u : 0;

			if (!isRisingEdge) return false;

			if (PinState.FirstBitHigh(resetPin))
			{
				for (int i = 0; i < addressSpace; i++) chip.InternalState[i + addressSpace] = 0;
			}
			else if (PinState.FirstBitHigh(writePin))
			{
				int addressIndex = GetAddress8Bit(addressPin) + addressSpace;
				uint data = (uint)(
					(PinState.GetBitStates(redPin) & 0xF) |
					((PinState.GetBitStates(greenPin) & 0xF) << 4) |
					((PinState.GetBitStates(bluePin) & 0xF) << 8));

				chip.InternalState[addressIndex] = data;
			}

			if (PinState.FirstBitHigh(refreshPin))
			{
				for (int i = 0; i < addressSpace; i++)
				{
					chip.InternalState[i] = chip.InternalState[i + addressSpace];
				}

				return true;
			}

			return false;
		}

		static bool AdvanceDisplayDot(SimChip chip)
		{
			const int addressSpace = 256;

			uint addressPin = chip.InputPins[0].State;
			uint pixelInputPin = chip.InputPins[1].State;
			uint resetPin = chip.InputPins[2].State;
			uint writePin = chip.InputPins[3].State;
			uint refreshPin = chip.InputPins[4].State;
			uint clockPin = chip.InputPins[5].State;

			bool clockHigh = PinState.FirstBitHigh(clockPin);
			bool isRisingEdge = clockHigh && chip.InternalState[^1] == 0;
			chip.InternalState[^1] = clockHigh ? 1u : 0;

			if (!isRisingEdge) return false;

			if (PinState.FirstBitHigh(resetPin))
			{
				for (int i = 0; i < addressSpace; i++) chip.InternalState[i + addressSpace] = 0;
			}
			else if (PinState.FirstBitHigh(writePin))
			{
				int addressIndex = GetAddress8Bit(addressPin) + addressSpace;
				chip.InternalState[addressIndex] = PinState.GetBitStates(pixelInputPin) & 1;
			}

			if (PinState.FirstBitHigh(refreshPin))
			{
				for (int i = 0; i < addressSpace; i++)
				{
					chip.InternalState[i] = chip.InternalState[i + addressSpace];
				}

				return true;
			}

			return false;
		}

		static (bool converged, int deltaCycles) SettleCombinational()
		{
			int deltaCycles = 0;
			int maxDeltaCycles = Math.Max(256, combinationalChips.Count + 64);

			while (targetQueue.Count > 0 || dirtyChips.Count > 0)
			{
				DrainTargetQueue();

				if (dirtyChips.Count == 0) continue;

				if (deltaCycles >= maxDeltaCycles)
				{
					TraceNonConvergence(maxDeltaCycles);
					ClearWorkQueues();
					return (false, deltaCycles);
				}

				evaluationBatch.Clear();
				evaluationBatch.AddRange(dirtyChips);
				for (int i = 0; i < dirtyChips.Count; i++) dirtyChipFlags[dirtyChips[i]] = false;
				dirtyChips.Clear();

				pendingPinStates.Clear();

				for (int i = 0; i < evaluationBatch.Count; i++)
				{
					EvaluateCombinationalChip(evaluationBatch[i]);
					LastGateEvaluations++;
				}

				CommitPendingOutputs();
				deltaCycles++;
			}

			return (true, deltaCycles);
		}

		static void DrainTargetQueue()
		{
			while (targetQueue.Count > 0)
			{
				int targetIndex = targetQueue.Dequeue();
				queuedTargets[targetIndex] = false;
				ResolveTargetPin(targetIndex, triggeringSourceByTarget[targetIndex]);
				LastTargetResolutions++;
			}
		}

		static void ResolveTargetPin(int targetIndex, int triggeringSourceIndex)
		{
			int sourceStart = sourceOffsetsByTarget[targetIndex];
			int sourceEnd = sourceOffsetsByTarget[targetIndex + 1];
			int sourceCount = sourceEnd - sourceStart;
			if (sourceCount == 0) return;

			SimPin target = allPins[targetIndex];
			uint newState;
			ushort contentionMask;

			if (sourceCount == 1)
			{
				newState = allPins[sourceIndicesByTarget[sourceStart]].State;
				contentionMask = 0;
			}
			else
			{
				newState = ResolveDrivenState(sourceStart, sourceEnd, out contentionMask);
			}

			target.lastUpdatedFrameIndex = Simulator.simulationFrame;
			target.numInputsReceivedThisFrame = sourceIndices.Length;

			if (target.State == newState)
			{
				if (contentionMask != 0) TraceContention(target, contentionMask);
				return;
			}

			uint oldState = target.State;
			target.State = newState;
			SimPin triggeringSource = allPins[triggeringSourceIndex];
			target.latestSourceID = triggeringSource.ID;
			target.latestSourceParentChipID = triggeringSource.parentChip.ID;

			if (DiagnosticsEnabled && DiagnosticSink != null)
			{
				TracePropagation(triggeringSource, target, oldState, newState, sourceCount);
			}
			if (contentionMask != 0) TraceContention(target, contentionMask);

			if (targetIsCustomBoundary[targetIndex])
			{
				QueueFanout(targetIndex);
			}
			else
			{
				MarkDirty(targetOwnerCombinationalIndex[targetIndex]);
			}
		}

		static uint ResolveDrivenState(int sourceStart, int sourceEnd, out ushort contentionMask)
		{
			ushort connectedMask = 0;
			ushort highMask = 0;
			ushort lowMask = 0;

			for (int i = sourceStart; i < sourceEnd; i++)
			{
				uint state = allPins[sourceIndicesByTarget[i]].State;
				ushort bits = PinState.GetBitStates(state);
				ushort tristate = PinState.GetTristateFlags(state);
				ushort activeMask = (ushort)~tristate;

				connectedMask |= activeMask;
				highMask |= (ushort)(bits & activeMask);
				lowMask |= (ushort)((ushort)~bits & activeMask);
			}

			contentionMask = (ushort)(highMask & lowMask);

			// Conflicting active drivers are invalid digital wiring. Resolve them
			// deterministically low instead of letting RNG choose a result.
			ushort resolvedBits = (ushort)(highMask & (ushort)~lowMask);
			ushort resolvedTristate = (ushort)~connectedMask;

			return (uint)(resolvedBits | (resolvedTristate << 16));
		}

		static void EvaluateCombinationalChip(int chipIndex)
		{
			RuntimeChip runtimeChip = combinationalChips[chipIndex];
			SimChip chip = runtimeChip.Chip;
			int outputStart = runtimeChip.OutputStart;

			switch (chip.ChipType)
			{
				case ChipType.Nand:
				{
					uint nandOp = 1 ^ (chip.InputPins[0].State & chip.InputPins[1].State);
					StageOutput(outputStart, nandOp & 1);
					break;
				}

				case ChipType.Split_4To1Bit:
				{
					uint input = chip.InputPins[0].State;
					StageOutput(outputStart, (input >> 3) & PinState.SingleBitMask);
					StageOutput(outputStart + 1, (input >> 2) & PinState.SingleBitMask);
					StageOutput(outputStart + 2, (input >> 1) & PinState.SingleBitMask);
					StageOutput(outputStart + 3, input & PinState.SingleBitMask);
					break;
				}

				case ChipType.Merge_1To4Bit:
				{
					uint a = chip.InputPins[3].State & PinState.SingleBitMask;
					uint b = chip.InputPins[2].State & PinState.SingleBitMask;
					uint c = chip.InputPins[1].State & PinState.SingleBitMask;
					uint d = chip.InputPins[0].State & PinState.SingleBitMask;
					StageOutput(outputStart, a | (b << 1) | (c << 2) | (d << 3));
					break;
				}

				case ChipType.Merge_1To8Bit:
				{
					uint a = chip.InputPins[7].State & PinState.SingleBitMask;
					uint b = chip.InputPins[6].State & PinState.SingleBitMask;
					uint c = chip.InputPins[5].State & PinState.SingleBitMask;
					uint d = chip.InputPins[4].State & PinState.SingleBitMask;
					uint e = chip.InputPins[3].State & PinState.SingleBitMask;
					uint f = chip.InputPins[2].State & PinState.SingleBitMask;
					uint g = chip.InputPins[1].State & PinState.SingleBitMask;
					uint h = chip.InputPins[0].State & PinState.SingleBitMask;

					StageOutput(
						outputStart,
						a | (b << 1) | (c << 2) | (d << 3) |
						(e << 4) | (f << 5) | (g << 6) | (h << 7));
					break;
				}

				case ChipType.Merge_4To8Bit:
				{
					uint state = chip.OutputPins[0].State;
					PinState.Set8BitFrom4BitSources(ref state, chip.InputPins[1].State, chip.InputPins[0].State);
					StageOutput(outputStart, state);
					break;
				}

				case ChipType.Split_8To4Bit:
				{
					uint low = chip.OutputPins[0].State;
					uint high = chip.OutputPins[1].State;

					PinState.Set4BitFrom8BitSource(ref low, chip.InputPins[0].State, false);
					PinState.Set4BitFrom8BitSource(ref high, chip.InputPins[0].State, true);

					StageOutput(outputStart, low);
					StageOutput(outputStart + 1, high);
					break;
				}

				case ChipType.Split_8To1Bit:
				{
					uint input = chip.InputPins[0].State;
					StageOutput(outputStart, (input >> 7) & PinState.SingleBitMask);
					StageOutput(outputStart + 1, (input >> 6) & PinState.SingleBitMask);
					StageOutput(outputStart + 2, (input >> 5) & PinState.SingleBitMask);
					StageOutput(outputStart + 3, (input >> 4) & PinState.SingleBitMask);
					StageOutput(outputStart + 4, (input >> 3) & PinState.SingleBitMask);
					StageOutput(outputStart + 5, (input >> 2) & PinState.SingleBitMask);
					StageOutput(outputStart + 6, (input >> 1) & PinState.SingleBitMask);
					StageOutput(outputStart + 7, input & PinState.SingleBitMask);
					break;
				}

				case ChipType.TriStateBuffer:
				{
					uint output = chip.InputPins[0].State;
					if (!PinState.FirstBitHigh(chip.InputPins[1].State))
					{
						PinState.SetAllDisconnected(ref output);
					}

					StageOutput(outputStart, output);
					break;
				}

				case ChipType.Rom_256x16:
				{
					const int byteMask = 0b11111111;
					int address = GetAddress8Bit(chip.InputPins[0].State);
					uint data = chip.InternalState[address];

					StageOutput(outputStart, (data >> 8) & byteMask);
					StageOutput(outputStart + 1, data & byteMask);
					break;
				}

				case ChipType.dev_Ram_8Bit:
				{
					int address = GetAddress8Bit(chip.InputPins[0].State);
					StageOutput(outputStart, chip.InternalState[address]);
					break;
				}

				case ChipType.DisplayRGB:
				{
					int address = GetAddress8Bit(chip.InputPins[0].State);
					uint data = chip.InternalState[address];
					StageOutput(outputStart, (data >> 0) & 0b1111);
					StageOutput(outputStart + 1, (data >> 4) & 0b1111);
					StageOutput(outputStart + 2, (data >> 8) & 0b1111);
					break;
				}

				case ChipType.DisplayDot:
				{
					int address = GetAddress8Bit(chip.InputPins[0].State);
					StageOutput(outputStart, chip.InternalState[address]);
					break;
				}

				default:
				{
					if (ChipTypeHelper.IsBusOriginType(chip.ChipType))
					{
						StageOutput(outputStart, chip.InputPins[0].State);
					}

					break;
				}
			}
		}

		static void UpdateBuzzers()
		{
			if (audioState == null) return;

			for (int i = 0; i < buzzerChips.Count; i++)
			{
				SimChip chip = buzzerChips[i].Chip;
				int frequencyIndex = GetAddress8Bit(chip.InputPins[0].State);
				int volumeIndex = PinState.GetBitStates(chip.InputPins[1].State);
				audioState.RegisterNote(frequencyIndex, (uint)volumeIndex);
			}
		}

		static int GetAddress8Bit(uint pinState) => PinState.GetBitStates(pinState) & Address8BitMask;

		static void StageOutput(int pinIndex, uint state)
		{
			if (allPins[pinIndex].State == state) return;
			pendingPinStates.Add(new PendingPinState(pinIndex, state));
		}

		static void CommitPendingOutputs()
		{
			for (int i = 0; i < pendingPinStates.Count; i++)
			{
				PendingPinState pending = pendingPinStates[i];
				SimPin pin = allPins[pending.PinIndex];
				if (pin.State == pending.State) continue;

				pin.State = pending.State;
				QueueFanout(pending.PinIndex);
			}

			pendingPinStates.Clear();
		}

		static void QueueFanout(int sourceIndex)
		{
			int targetStart = targetOffsetsBySource[sourceIndex];
			int targetEnd = targetOffsetsBySource[sourceIndex + 1];
			for (int i = targetStart; i < targetEnd; i++)
			{
				int targetIndex = targetIndicesBySource[i];
				LastSignalPropagations++;
				triggeringSourceByTarget[targetIndex] = sourceIndex;

				if (queuedTargets[targetIndex]) continue;
				queuedTargets[targetIndex] = true;
				targetQueue.Enqueue(targetIndex);
			}
		}

		static void QueueAllExistingSignals()
		{
			for (int i = 0; i < allPins.Count; i++)
			{
				QueueFanout(i);
			}
		}

		static void PrimeInitialCombinationalState()
		{
			// Feedback storage has no defined power-on state. Seed it once in a
			// deterministic path order, then use simultaneous delta-cycles only.
			QueueAllExistingSignals();
			DrainTargetQueue();

			for (int i = 0; i < initializationOrder.Count; i++)
			{
				pendingPinStates.Clear();
				EvaluateCombinationalChip(initializationOrder[i]);
				CommitPendingOutputs();
				DrainTargetQueue();
			}

			for (int i = 0; i < dirtyChips.Count; i++) dirtyChipFlags[dirtyChips[i]] = false;
			dirtyChips.Clear();
		}

		static void MarkDirty(int chipIndex)
		{
			if (chipIndex < 0 || dirtyChipFlags[chipIndex]) return;

			dirtyChipFlags[chipIndex] = true;
			dirtyChips.Add(chipIndex);
		}

		static void ClearWorkQueues()
		{
			targetQueue.Clear();
			if (queuedTargets.Length > 0) Array.Clear(queuedTargets, 0, queuedTargets.Length);
			dirtyChips.Clear();
			if (dirtyChipFlags.Length > 0) Array.Clear(dirtyChipFlags, 0, dirtyChipFlags.Length);
			evaluationBatch.Clear();
			pendingPinStates.Clear();
		}

		static void UpdateAudioState()
		{
			if (audioState == null) return;

			double elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
			if (Simulator.simulationFrame <= 1) deltaTime = 0;
			else deltaTime = elapsedSeconds - elapsedSecondsOld;

			elapsedSecondsOld = elapsedSeconds;
			audioState.NotifyAllNotesRegistered(deltaTime);
		}

		static string GetChipPath(SimChip chip)
		{
			return runtimeDiagnosticPaths.TryGetValue(chip, out string path)
				? path
				: $"{chip.ChipType}[{chip.ID}]";
		}

		static void TracePropagation(
			SimPin source,
			SimPin target,
			uint oldState,
			uint newState,
			int inputCount)
		{
			if (!DiagnosticsEnabled || DiagnosticSink == null) return;

			DiagnosticSink(
				$"frame={Simulator.simulationFrame}\n" +
				$"chipPath={GetChipPath(target.parentChip)}\n" +
				$"chipID={target.parentChip.ID}\n" +
				$"pinID={target.ID}\n" +
				$"sourceChip={GetChipPath(source.parentChip)}\n" +
				$"sourcePin={source.ID}\n" +
				$"oldState={oldState}\n" +
				$"newState={newState}\n" +
				$"inputsReceived={inputCount}/{target.numInputConnections}");
		}

		static void TraceContention(SimPin target, ushort contentionMask)
		{
			if (!DiagnosticsEnabled || DiagnosticSink == null) return;

			DiagnosticSink(
				$"frame={Simulator.simulationFrame}\n" +
				$"chipPath={GetChipPath(target.parentChip)}\n" +
				$"chipID={target.parentChip.ID}\n" +
				$"pinID={target.ID}\n" +
				$"contentionMask=0x{contentionMask:X4}\n" +
				"resolution=LOW");
		}

		static void TraceNonConvergence(int maxDeltaCycles)
		{
			if (!DiagnosticsEnabled || DiagnosticSink == null) return;

			DiagnosticSink(
				$"frame={Simulator.simulationFrame}\n" +
				$"event=non-convergent-combinational-network\n" +
				$"maxDeltaCycles={maxDeltaCycles}\n" +
				$"pendingSignals={targetQueue.Count}\n" +
				$"pendingChips={dirtyChips.Count}");
		}
	}
}
