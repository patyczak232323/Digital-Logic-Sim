using System;
using System.Collections.Generic;
using System.Diagnostics;
using DLS.Description;
using DLS.Game;

namespace DLS.Simulation
{
	internal static class DeterministicSimulator
	{
		const int Address8BitMask = 0xFF;
		const int MaxEvaluationsPerChipPerSettle = 256;
		const int MinPowerOnEvaluationBudget = 4096;
		const int PowerOnEvaluationsPerChipBudget = 512;

		public static bool DiagnosticsEnabled;
		public static Action<string> DiagnosticSink;

		public static int LastDeltaCycles { get; private set; }
		public static int LastGateEvaluations { get; private set; }
		public static int LastSignalPropagations { get; private set; }
		public static int LastTargetResolutions { get; private set; }
		public static int LastCacheHits { get; private set; }
		public static int LastCacheMisses { get; private set; }
		public static int LastJitHits { get; private set; }
		public static int LastFeedbackJitHits { get; private set; }
		public static int LastFeedbackJitSweeps { get; private set; }
		public static int LastFeedbackJitFallbacks { get; private set; }
		public static bool LastSettleConverged { get; private set; } = true;
		public static string LastNonConvergenceDetails { get; private set; } = string.Empty;

		static readonly Stopwatch stopwatch = Stopwatch.StartNew();

		static readonly List<RuntimeChip> combinationalChips = new();
		static readonly List<RuntimeChip> sourceChips = new();
		static readonly List<RuntimeChip> sequentialChips = new();
		static readonly List<RuntimeChip> buzzerChips = new();
		static readonly List<SimPin> allPins = new();
		static readonly List<int> pinOwnerCombinationalIndexBuild = new();

		static readonly Dictionary<SimPin, int> pinIndices = new();
		// Compressed sparse row (CSR) adjacency keeps all netlist edges in two
		// contiguous arrays. This avoids one managed array per pin and gives the
		// propagation hot path predictable, cache-friendly memory access.
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
		static int[] evaluationEpochByChip = Array.Empty<int>();
		static ushort[] evaluationsInEpoch = Array.Empty<ushort>();
		static int evaluationEpoch;
		static readonly List<int> evaluationBatch = new();
		static readonly List<int> initializationOrder = new();
		static readonly List<PendingPinState> pendingPinStates = new();
		static DevPinInstance[] boundInputPins;
		static DevPinInstance[] boundInputPinSnapshot = Array.Empty<DevPinInstance>();
		static int[] boundInputPinIndices = Array.Empty<int>();

		static SimChip topologyRoot;
		static SimChip inspectionChip;
		static readonly HashSet<SimChip> inspectionPath = new();
		static bool topologyDirty = true;
		static int observedCacheGeneration;
		static bool needsInitialPropagation = true;
		static bool needsPowerOnSettle = true;
		static bool feedbackActivationPending;
		static ulong stateChangeSerial;

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

		public static void EnsureInitialized(SimChip rootSimChip, DevPinInstance[] inputPins, SimAudio newAudioState)
		{
			if (inputPins == null) inputPins = Array.Empty<DevPinInstance>();
			audioState = newAudioState;

			EnsureTopology(rootSimChip, inputPins);
			if (!needsInitialPropagation || rootSimChip == null) return;

			// Initialization is deliberately separated from normal simulation time.
			// It must never manufacture a clock edge, RAM write or Pulse event.
			LastDeltaCycles = 0;
			LastGateEvaluations = 0;
			LastSignalPropagations = 0;
			LastTargetResolutions = 0;
			LastCacheHits = 0;
			LastCacheMisses = 0;
			LastJitHits = 0;
			LastFeedbackJitHits = 0;
			LastFeedbackJitSweeps = 0;
			LastFeedbackJitFallbacks = 0;
			LastSettleConverged = true;
			LastNonConvergenceDetails = string.Empty;

			ApplyExternalInputs(inputPins);
			UpdateFrameSources();

			bool initConverged;
			int initDeltaCycles = 0;

			if (needsPowerOnSettle)
			{
				// Real gate networks with feedback do not power up in a perfectly
				// synchronous state. During power-on only, evaluate dirty gates one
				// at a time in a random order and commit each *correct* logic result
				// immediately. This breaks symmetric SR/D-latch startup without ever
				// randomizing NAND truth tables or user inputs.
				initConverged = PowerOnAsynchronousSettle();

				// Once a fixed point is found, verify it with the normal deterministic
				// delta-cycle solver. A plain NAND (including a NAND3 made from NANDs)
				// therefore always ends in its mathematically correct state.
				ulong serialBeforeVerify = stateChangeSerial;
				(bool verifyConverged, int verifyDelta) = FullDeterministicResettle();
				initDeltaCycles += verifyDelta;
				initConverged &= verifyConverged;

				if (stateChangeSerial != serialBeforeVerify)
				{
					TracePowerOnVerificationAdjustment(serialBeforeVerify, stateChangeSerial);
				}

				// Preserve the original Simulator startup semantics. Initialization itself
				// does not advance sequential components, but it must not consume the
				// current input level as the "previous" clock/input state either.
				// On the first normal simulation tick, RAM/Display/Pulse therefore see
				// the same rising edge the legacy engine sees when the signal starts high.

				// Feedback JIT stays dormant for the first normal tick as well. That tick
				// can legitimately change a latch/register after power-on. We seed the
				// native state only after the normal pre/edge/post settle sequence finishes.
				feedbackActivationPending = true;
				needsPowerOnSettle = false;

				// The first initialization deliberately expanded every Custom Chip and
				// evaluated it through the live solver. Rebuild topology on the next
				// simulation step so JIT/FULL LUT acceleration can take over only after
				// this cold-start compatibility pass. This is especially important for
				// imported/nested chips that have never been opened in the editor.
				topologyDirty = true;
			}
			else
			{
				// Editing/rebinding an already running graph must preserve latch/RAM
				// state. Re-propagate everything deterministically, with no startup noise.
				(initConverged, initDeltaCycles) = FullDeterministicResettle();
			}

			LastDeltaCycles += initDeltaCycles;
			LastSettleConverged &= initConverged;
			needsInitialPropagation = false;
		}

		public static void RunSimulationStep(SimChip rootSimChip, DevPinInstance[] inputPins, SimAudio newAudioState)
		{
			if (inputPins == null) inputPins = Array.Empty<DevPinInstance>();
			audioState = newAudioState;
			audioState?.InitFrame();
			SimulationProfiler.BeginStep();
			SimulationBenchmark.BeginStep();

			LastDeltaCycles = 0;
			LastGateEvaluations = 0;
			LastSignalPropagations = 0;
			LastTargetResolutions = 0;
			LastCacheHits = 0;
			LastCacheMisses = 0;
			LastJitHits = 0;
			LastFeedbackJitHits = 0;
			LastFeedbackJitSweeps = 0;
			LastFeedbackJitFallbacks = 0;
			LastSettleConverged = true;
			LastNonConvergenceDetails = string.Empty;

			EnsureInitialized(rootSimChip, inputPins, newAudioState);
			if (rootSimChip != null) SimulationReplayRecorder.CaptureStepStart(rootSimChip, inputPins);
			if (rootSimChip == null)
			{
				UpdateAudioState();
				EndProfilingStep();
				return;
			}

			Simulator.simulationFrame++;

			ApplyExternalInputs(inputPins);
			UpdateFrameSources();

			(bool preConverged, int preDelta) = SettleCombinational();
			LastDeltaCycles += preDelta;
			LastSettleConverged &= preConverged;

			AdvanceSequentialComponents();

			(bool postConverged, int postDelta) = SettleCombinational();
			LastDeltaCycles += postDelta;
			LastSettleConverged &= postConverged;

			UpdateBuzzers();

			if (feedbackActivationPending)
			{
				if (SynchronizeFeedbackExecutors(rootSimChip)) topologyDirty = true;
				feedbackActivationPending = false;
			}

			SimulationWaveformRecorder.Capture(Simulator.simulationFrame);
			SimulationReplayRecorder.CaptureStepEnd(rootSimChip);
			UpdateAudioState();
			EndProfilingStep();
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

		public static void SetInspectionChip(SimChip chip)
		{
			if (ReferenceEquals(inspectionChip, chip)) return;

			// A collapsed feedback executor owns the authoritative internal gate state.
			// Materialize it before expanding a hierarchy for editor inspection.
			MaterializeOutermostFeedbackState(topologyRoot);
			inspectionChip = chip;
			topologyDirty = true;
		}

		public static void Reset()
		{
			topologyRoot = null;
			topologyDirty = true;
			observedCacheGeneration = CombinationalChipCacheManager.ReadyGeneration;
			needsInitialPropagation = true;
			needsPowerOnSettle = true;
			feedbackActivationPending = false;

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
			evaluationEpochByChip = Array.Empty<int>();
			evaluationsInEpoch = Array.Empty<ushort>();
			evaluationEpoch = 0;
			stateChangeSerial = 0;
			initializationOrder.Clear();
			boundInputPins = null;
			boundInputPinSnapshot = Array.Empty<DevPinInstance>();
			boundInputPinIndices = Array.Empty<int>();
			runtimeDiagnosticPaths.Clear();
			registeredDiagnosticPaths.Clear();
			inspectionChip = null;
			inspectionPath.Clear();

			ClearWorkQueues();

			audioState = null;
			stopwatch.Restart();
			elapsedSecondsOld = 0;
			deltaTime = 0;

			LastDeltaCycles = 0;
			LastGateEvaluations = 0;
			LastSignalPropagations = 0;
			LastTargetResolutions = 0;
			LastCacheHits = 0;
			LastCacheMisses = 0;
			LastJitHits = 0;
			LastFeedbackJitHits = 0;
			LastFeedbackJitSweeps = 0;
			LastFeedbackJitFallbacks = 0;
			LastSettleConverged = true;
			LastNonConvergenceDetails = string.Empty;
			SimulationProfiler.Reset();
			SimulationWaveformRecorder.ClearAll();
			SimulationReplayRecorder.Reset();
			SimulationBenchmark.Reset();
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
			int cacheGeneration = CombinationalChipCacheManager.ReadyGeneration;
			if (cacheGeneration != observedCacheGeneration)
			{
				observedCacheGeneration = cacheGeneration;
				topologyDirty = true;
			}

			bool rootChanged = !ReferenceEquals(topologyRoot, root);

			if (!topologyDirty && topologyRoot == root)
			{
				if (!ExternalInputBindingsMatch(inputPins))
				{
					BindExternalInputs(root, inputPins);
					// A changed editor input list needs a full propagation pass, but it is
					// not a new power-on and must not randomize existing storage.
					needsInitialPropagation = true;
				}
				return;
			}

			// Only executors selected by the previous runtime topology may own state
			// newer than the primitive pins. Materialize those before changing ownership.
			if (topologyRoot != null)
			{
				MaterializeOutermostFeedbackState(topologyRoot);
			}

			topologyRoot = root;
			topologyDirty = false;
			needsInitialPropagation = true;
			if (rootChanged && root != null) needsPowerOnSettle = true;

			SetFeedbackRuntimeActiveRecursive(root, false);

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

			SimulationWaveformRecorder.PruneToRoot(root);

			inspectionPath.Clear();
			if (inspectionChip != null) FindInspectionPath(root, inspectionChip);
			CollectTopologyRecursive(root, rootPath, false);

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
				targetIsCustomBoundary[i] =
					allPins[i].parentChip.ChipType == ChipType.Custom &&
					targetOwnerCombinationalIndex[i] < 0;
			}

			targetQueue = new Queue<int>(Math.Max(64, allPins.Count));
			queuedTargets = new bool[allPins.Count];
			triggeringSourceByTarget = new int[allPins.Count];
			dirtyChipFlags = new bool[combinationalChips.Count];
			evaluationEpochByChip = new int[combinationalChips.Count];
			evaluationsInEpoch = new ushort[combinationalChips.Count];
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
			evaluationEpochByChip = Array.Empty<int>();
			evaluationsInEpoch = Array.Empty<ushort>();
			evaluationEpoch = 0;
			targetQueue = new Queue<int>();
		}

		static bool FindInspectionPath(SimChip chip, SimChip target)
		{
			if (chip == null || target == null) return false;

			if (ReferenceEquals(chip, target))
			{
				inspectionPath.Add(chip);
				return true;
			}

			for (int i = 0; i < chip.SubChips.Length; i++)
			{
				if (FindInspectionPath(chip.SubChips[i], target))
				{
					inspectionPath.Add(chip);
					return true;
				}
			}

			return false;
		}

		static void CollectTopologyRecursive(SimChip chip, string fallbackPath, bool allowMemoCache)
		{
			string path = registeredDiagnosticPaths.TryGetValue(chip, out string registeredPath)
				? registeredPath
				: fallbackPath;

			runtimeDiagnosticPaths[chip] = path;

			bool feedbackAccelerationAvailable =
				!needsPowerOnSettle &&
				chip.FeedbackExecutor != null &&
				chip.FeedbackExecutor.Ready &&
				!chip.FeedbackExecutor.Disabled;

			bool hasVisibleDisplaySurface = (chip.Description?.Displays?.Length ?? 0) > 0;

			bool acceleratedCustom =
				!needsPowerOnSettle &&
				allowMemoCache &&
				chip.ChipType == ChipType.Custom &&
				!hasVisibleDisplaySurface &&
				!inspectionPath.Contains(chip) &&
				!SimulationWaveformRecorder.ContainsProbeInSubtree(chip) &&
				((chip.MemoCache != null && chip.MemoCache.Ready) ||
				 chip.CompiledExecutor != null ||
				 feedbackAccelerationAvailable);

			bool feedbackAccelerationSelected =
				acceleratedCustom &&
				(chip.MemoCache == null || !chip.MemoCache.Ready) &&
				chip.CompiledExecutor == null &&
				feedbackAccelerationAvailable;

			if (feedbackAccelerationSelected)
			{
				// The previous authoritative executor was materialized before the rebuild.
				// Refresh this executor from live gate state before it becomes authoritative.
				chip.FeedbackExecutor.SynchronizeFromChipTree();
				chip.FeedbackExecutor.SetRuntimeActive(true);
			}

			bool isCombinational =
				acceleratedCustom ||
				(chip.ChipType != ChipType.Custom && IsCombinationalChipType(chip.ChipType));

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

			if (chip.ChipType == ChipType.Custom && !acceleratedCustom)
			{
				for (int i = 0; i < chip.SubChips.Length; i++)
				{
					SimChip child = chip.SubChips[i];
					string childPath = $"{path}/{child.ChipType}[{child.ID}]";
					CollectTopologyRecursive(child, childPath, true);
				}

				return;
			}

			RuntimeChip runtimeChip = new(chip, inputStart, outputStart, combinationalIndex);

			if (isCombinational)
			{
				combinationalChips.Add(runtimeChip);
			}

			if (acceleratedCustom)
			{
				return;
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
						stateChangeSerial++;
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
				chip.InternalState[GetAddress8Bit(addressPin)] = (uint)(PinState.GetBitStates(dataPin) & Address8BitMask);
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
				chip.InternalState[addressIndex] = (uint)(PinState.GetBitStates(pixelInputPin) & 1);
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
			int currentEvaluationEpoch = BeginEvaluationEpoch();

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
					int chipIndex = evaluationBatch[i];
					if (!TryBeginChipEvaluation(chipIndex, currentEvaluationEpoch))
					{
						TraceRepeatedEvaluation(chipIndex);
						ClearWorkQueues();
						return (false, deltaCycles);
					}

					if (SimulationProfiler.Enabled)
					{
						SimChip profiledChip = combinationalChips[chipIndex].Chip;
						SimulationExecutionPath executionPath = GetExecutionPath(profiledChip);
						long startTimestamp = Stopwatch.GetTimestamp();
						EvaluateCombinationalChip(chipIndex);
						long elapsedTicks = Stopwatch.GetTimestamp() - startTimestamp;
						SimulationProfiler.RecordChip(
							profiledChip,
							GetChipPath(profiledChip),
							executionPath,
							elapsedTicks);
					}
					else
					{
						EvaluateCombinationalChip(chipIndex);
					}
					LastGateEvaluations++;
				}

				CommitPendingOutputs();
				deltaCycles++;
			}

			return (true, deltaCycles);
		}

		static SimulationExecutionPath GetExecutionPath(SimChip chip)
		{
			if (chip != null && chip.ChipType == ChipType.Custom)
			{
				if (chip.MemoCache != null && chip.MemoCache.Ready) return SimulationExecutionPath.FullLut;
				if (chip.CompiledExecutor != null) return SimulationExecutionPath.NativeJit;
				if (chip.FeedbackExecutor != null && chip.FeedbackExecutor.Ready) return SimulationExecutionPath.FeedbackJit;
			}

			return SimulationExecutionPath.Live;
		}

		static void EndProfilingStep()
		{
			SimulationProfiler.EndStep(
				Simulator.simulationFrame,
				LastDeltaCycles,
				LastGateEvaluations,
				LastSignalPropagations,
				LastTargetResolutions,
				LastCacheHits,
				LastCacheMisses,
				LastJitHits,
				LastFeedbackJitHits,
				LastFeedbackJitSweeps,
				LastFeedbackJitFallbacks,
				LastSettleConverged);

			SimulationBenchmark.EndStep(
				LastGateEvaluations,
				LastSignalPropagations,
				LastTargetResolutions,
				LastCacheHits,
				LastCacheMisses,
				LastJitHits,
				LastFeedbackJitHits,
				LastFeedbackJitSweeps,
				LastFeedbackJitFallbacks,
				LastSettleConverged);
		}

		static int BeginEvaluationEpoch()
		{
			if (evaluationEpoch == int.MaxValue)
			{
				Array.Clear(evaluationEpochByChip, 0, evaluationEpochByChip.Length);
				evaluationEpoch = 1;
			}
			else
			{
				evaluationEpoch++;
			}

			return evaluationEpoch;
		}

		static bool TryBeginChipEvaluation(int chipIndex, int currentEvaluationEpoch)
		{
			if (evaluationEpochByChip[chipIndex] != currentEvaluationEpoch)
			{
				evaluationEpochByChip[chipIndex] = currentEvaluationEpoch;
				evaluationsInEpoch[chipIndex] = 0;
			}

			if (evaluationsInEpoch[chipIndex] >= MaxEvaluationsPerChipPerSettle) return false;
			evaluationsInEpoch[chipIndex]++;
			return true;
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
			int resolvedSourceIndex = triggeringSourceIndex;

			if (sourceCount == 1)
			{
				resolvedSourceIndex = sourceIndicesByTarget[sourceStart];
				newState = allPins[resolvedSourceIndex].State;
				contentionMask = 0;
			}
			else
			{
				newState = ResolveDrivenState(sourceStart, sourceEnd, out contentionMask, out resolvedSourceIndex);
			}

			target.lastUpdatedFrameIndex = Simulator.simulationFrame;
			target.numInputsReceivedThisFrame = sourceCount;

			if (target.State == newState)
			{
				if (contentionMask != 0) TraceContention(target, contentionMask);
				return;
			}

			uint oldState = target.State;
			target.State = newState;
			stateChangeSerial++;
			SimPin triggeringSource = allPins[resolvedSourceIndex];
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

		static uint ResolveDrivenState(
			int sourceStart,
			int sourceEnd,
			out ushort contentionMask,
			out int acceptedSourceIndex)
		{
			// Legacy Simulator/SimPin compatibility path. Multiple active drivers are
			// uncommon, so keep the single-driver hot path above branch-free and pay
			// this extra work only for actual shared nets/buses.
			int firstSourceIndex = sourceIndicesByTarget[sourceStart];
			uint resolvedState = allPins[firstSourceIndex].State;
			acceptedSourceIndex = firstSourceIndex;

			ushort highMask = 0;
			ushort lowMask = 0;

			AccumulateDriverMasks(resolvedState, ref highMask, ref lowMask);

			for (int i = sourceStart + 1; i < sourceEnd; i++)
			{
				int sourceIndex = sourceIndicesByTarget[i];
				uint sourceState = allPins[sourceIndex].State;
				AccumulateDriverMasks(sourceState, ref highMask, ref lowMask);

				// Match SimPin.ReceiveInput(): combine the already-resolved state with
				// the next source, randomly accepting/rejecting conflicting driven bits
				// while always accepting bits whose previous source was tri-stated.
				uint orState = sourceState | resolvedState;
				uint andState = sourceState & resolvedState;
				ushort bitsNew = (ushort)(Simulator.RandomBool() ? orState : andState);
				ushort tristateMask = (ushort)(orState >> 16);
				bitsNew = (ushort)((bitsNew & ~tristateMask) | ((ushort)orState & tristateMask));
				ushort tristateNew = (ushort)(andState >> 16);
				uint stateNew = (uint)(bitsNew | (tristateNew << 16));

				if (stateNew != resolvedState) acceptedSourceIndex = sourceIndex;
				resolvedState = stateNew;
			}

			contentionMask = (ushort)(highMask & lowMask);
			return resolvedState;
		}

		static void AccumulateDriverMasks(uint state, ref ushort highMask, ref ushort lowMask)
		{
			ushort bits = PinState.GetBitStates(state);
			ushort tristate = PinState.GetTristateFlags(state);
			ushort activeMask = (ushort)~tristate;
			highMask |= (ushort)(bits & activeMask);
			lowMask |= (ushort)((ushort)~bits & activeMask);
		}

		static void EvaluateCombinationalChip(int chipIndex)
		{
			RuntimeChip runtimeChip = combinationalChips[chipIndex];
			SimChip chip = runtimeChip.Chip;
			int outputStart = runtimeChip.OutputStart;

			if (chip.ChipType == ChipType.Custom)
			{
				// A ready LUT is still the cheapest path. Native JIT acts as the immediate
				// accelerator while an async LUT is loading/building and as the permanent
				// path for chips that are intentionally not using a LUT.
				if (chip.MemoCache != null && chip.MemoCache.Ready)
				{
					bool cacheHit = chip.MemoCache.Evaluate(chip, out uint[] cachedOutputs);
					if (cacheHit) LastCacheHits++;
					else LastCacheMisses++;

					int count = Math.Min(cachedOutputs.Length, chip.OutputPins.Length);
					for (int i = 0; i < count; i++) StageOutput(outputStart + i, cachedOutputs[i]);
					return;
				}

				if (chip.CompiledExecutor != null)
				{
					uint[] compiledOutputs = chip.CompiledExecutor.Evaluate(chip);
					LastJitHits++;
					int count = Math.Min(compiledOutputs.Length, chip.OutputPins.Length);
					for (int i = 0; i < count; i++) StageOutput(outputStart + i, compiledOutputs[i]);
					return;
				}

				if (chip.FeedbackExecutor != null && chip.FeedbackExecutor.Ready)
				{
					bool converged = chip.FeedbackExecutor.Evaluate(
						chip,
						out uint[] feedbackOutputs,
						out int sweepCount);

					LastFeedbackJitHits++;
					LastFeedbackJitSweeps += sweepCount;

					int count = Math.Min(feedbackOutputs.Length, chip.OutputPins.Length);
					for (int i = 0; i < count; i++) StageOutput(outputStart + i, feedbackOutputs[i]);

					if (!converged)
					{
						LastFeedbackJitFallbacks++;
						LastSettleConverged = false;
						TraceFeedbackJitFallback(chip, sweepCount);
						chip.FeedbackExecutor.Disable();
						topologyDirty = true;
					}

					return;
				}
			}

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
				stateChangeSerial++;
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

		static bool PowerOnAsynchronousSettle()
		{
			// This is the "switch-on noise" phase used only for a genuinely new root
			// circuit. Gate *results* remain deterministic; only scheduling order is
			// randomized. Each gate commits immediately before the next gate runs.
			ClearWorkQueues();
			QueueAllExistingSignals();
			DrainTargetQueue();

			for (int i = 0; i < combinationalChips.Count; i++)
			{
				MarkDirty(i);
			}

			int evaluationBudget = Math.Max(
				MinPowerOnEvaluationBudget,
				Math.Max(1, combinationalChips.Count) * PowerOnEvaluationsPerChipBudget);

			int evaluations = 0;

			while (targetQueue.Count > 0 || dirtyChips.Count > 0)
			{
				DrainTargetQueue();
				if (dirtyChips.Count == 0) continue;

				if (evaluations >= evaluationBudget)
				{
					TracePowerOnNonConvergence(evaluationBudget, evaluations);
					ClearWorkQueues();
					return false;
				}

				// Remove one random dirty gate in O(1). This models tiny propagation
				// delay differences that decide which side of a feedback latch wins.
				int slot = Simulator.rng.Next(dirtyChips.Count);
				int lastSlot = dirtyChips.Count - 1;
				int chipIndex = dirtyChips[slot];
				dirtyChips[slot] = dirtyChips[lastSlot];
				dirtyChips.RemoveAt(lastSlot);
				dirtyChipFlags[chipIndex] = false;

				pendingPinStates.Clear();
				EvaluateCombinationalChip(chipIndex);
				LastGateEvaluations++;
				CommitPendingOutputs();
				evaluations++;
			}

			return true;
		}

		static (bool converged, int deltaCycles) FullDeterministicResettle()
		{
			QueueAllExistingSignals();
			for (int i = 0; i < combinationalChips.Count; i++)
			{
				MarkDirty(i);
			}

			return SettleCombinational();
		}

		static bool SynchronizeFeedbackExecutors(SimChip chip)
		{
			if (chip == null) return false;

			bool changed = false;
			for (int i = 0; i < chip.SubChips.Length; i++)
			{
				changed |= SynchronizeFeedbackExecutors(chip.SubChips[i]);
			}

			CompiledFeedbackExecutor executor = chip.FeedbackExecutor;
			if (executor != null && !executor.Disabled)
			{
				// Power-on and the first real tick have now settled. Refresh even a
				// previously-ready executor because a reused SimChip may contain an old
				// native buffer from an earlier runtime topology.
				executor.SynchronizeFromChipTree();
				changed = true;
			}

			return changed;
		}

		internal static void MaterializeStateForSnapshot(SimChip chip)
		{
			MaterializeOutermostFeedbackState(chip);
		}

		internal static void PrepareForSnapshotRestore(SimChip root)
		{
			DisableFeedbackExecutorsRecursive(root);
			topologyRoot = root;
			topologyDirty = true;
			needsInitialPropagation = true;
			needsPowerOnSettle = false;
			feedbackActivationPending = false;
			ClearWorkQueues();
		}

		static void DisableFeedbackExecutorsRecursive(SimChip chip)
		{
			if (chip == null) return;

			if (chip.FeedbackExecutor != null)
			{
				if (chip.FeedbackExecutor.RuntimeActive)
				{
					chip.FeedbackExecutor.MaterializeState();
				}
				chip.FeedbackExecutor.SetRuntimeActive(false);
				chip.FeedbackExecutor = null;
			}

			for (int i = 0; i < chip.SubChips.Length; i++)
			{
				DisableFeedbackExecutorsRecursive(chip.SubChips[i]);
			}
		}

		static void MaterializeOutermostFeedbackState(SimChip chip)
		{
			if (chip == null) return;

			CompiledFeedbackExecutor executor = chip.FeedbackExecutor;
			if (executor != null && executor.RuntimeActive)
			{
				executor.MaterializeState();
				return;
			}

			for (int i = 0; i < chip.SubChips.Length; i++)
			{
				MaterializeOutermostFeedbackState(chip.SubChips[i]);
			}
		}

		static void SetFeedbackRuntimeActiveRecursive(SimChip chip, bool active)
		{
			if (chip == null) return;

			chip.FeedbackExecutor?.SetRuntimeActive(active);
			for (int i = 0; i < chip.SubChips.Length; i++)
			{
				SetFeedbackRuntimeActiveRecursive(chip.SubChips[i], active);
			}
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
			string suspects = DescribePendingWork();
			LastNonConvergenceDetails =
				$"non-convergent combinational network; deltaLimit={maxDeltaCycles}; suspects={suspects}";

			if (!DiagnosticsEnabled || DiagnosticSink == null) return;

			DiagnosticSink(
				$"frame={Simulator.simulationFrame}\n" +
				$"event=non-convergent-combinational-network\n" +
				$"maxDeltaCycles={maxDeltaCycles}\n" +
				$"pendingSignals={targetQueue.Count}\n" +
				$"pendingChips={dirtyChips.Count}\n" +
				$"suspects={suspects}");
		}

		static void TracePowerOnNonConvergence(int evaluationBudget, int evaluations)
		{
			string suspects = DescribePendingWork();
			LastNonConvergenceDetails =
				$"power-on did not reach fixed point; evaluations={evaluations}/{evaluationBudget}; suspects={suspects}";

			if (!DiagnosticsEnabled || DiagnosticSink == null) return;

			DiagnosticSink(
				$"frame={Simulator.simulationFrame}\n" +
				$"event=power-on-did-not-reach-fixed-point\n" +
				$"evaluationBudget={evaluationBudget}\n" +
				$"evaluations={evaluations}\n" +
				$"pendingSignals={targetQueue.Count}\n" +
				$"pendingChips={dirtyChips.Count}\n" +
				$"suspects={suspects}");
		}

		static void TracePowerOnVerificationAdjustment(ulong serialBefore, ulong serialAfter)
		{
			if (!DiagnosticsEnabled || DiagnosticSink == null) return;

			DiagnosticSink(
				$"frame={Simulator.simulationFrame}\n" +
				$"event=power-on-verification-adjusted-state\n" +
				$"changes={serialAfter - serialBefore}");
		}

		static void TraceRepeatedEvaluation(int chipIndex)
		{
			SimChip chip = combinationalChips[chipIndex].Chip;
			LastNonConvergenceDetails =
				$"repeated evaluation limit; chip={GetChipPath(chip)}; evaluations={MaxEvaluationsPerChipPerSettle}";

			if (!DiagnosticsEnabled || DiagnosticSink == null) return;

			DiagnosticSink(
				$"frame={Simulator.simulationFrame}\n" +
				$"event=repeated-chip-evaluation-limit\n" +
				$"chipPath={GetChipPath(chip)}\n" +
				$"chipID={chip.ID}\n" +
				$"evaluations={MaxEvaluationsPerChipPerSettle}");
		}

		static string DescribePendingWork(int maxItems = 8)
		{
			List<string> items = new(maxItems);
			HashSet<string> seen = new(StringComparer.Ordinal);

			for (int i = 0; i < dirtyChips.Count && items.Count < maxItems; i++)
			{
				int chipIndex = dirtyChips[i];
				if (chipIndex < 0 || chipIndex >= combinationalChips.Count) continue;
				string path = GetChipPath(combinationalChips[chipIndex].Chip);
				if (seen.Add(path)) items.Add(path);
			}

			foreach (int targetIndex in targetQueue)
			{
				if (items.Count >= maxItems) break;
				if (targetIndex < 0 || targetIndex >= allPins.Count) continue;
				SimPin pin = allPins[targetIndex];
				string path = $"{GetChipPath(pin.parentChip)}:pin[{pin.ID}]";
				if (seen.Add(path)) items.Add(path);
			}

			return items.Count == 0 ? "none" : string.Join(", ", items);
		}

		static void TraceFeedbackJitFallback(SimChip chip, int sweeps)
		{
			LastNonConvergenceDetails =
				$"feedback JIT did not converge; chip={GetChipPath(chip)}; sweeps={sweeps}; fallback=live";

			if (!DiagnosticsEnabled || DiagnosticSink == null) return;

			DiagnosticSink(
				$"frame={Simulator.simulationFrame}\n" +
				$"event=feedback-jit-non-convergent\n" +
				$"chipPath={GetChipPath(chip)}\n" +
				$"chipID={chip.ID}\n" +
				$"sweeps={sweeps}\n" +
				"action=disable-jit-and-expand-next-step");
		}
	}
}
