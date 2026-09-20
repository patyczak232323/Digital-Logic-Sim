using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DLS.Description;
using DLS.Game;
using DLS.SaveSystem;

namespace DLS.Simulation
{
	public readonly struct ChipCacheAnalysis
	{
		public readonly bool CanCache;
		public readonly string Reason;
		public readonly int InputBitCount;
		public readonly ulong BinaryCombinationCount;

		public ChipCacheAnalysis(bool canCache, string reason, int inputBitCount, ulong binaryCombinationCount)
		{
			CanCache = canCache;
			Reason = reason;
			InputBitCount = inputBitCount;
			BinaryCombinationCount = binaryCombinationCount;
		}
	}

	public readonly struct PersistentCacheInfo
	{
		public readonly bool Supported;
		public readonly bool Exists;
		public readonly bool Valid;
		public readonly string Status;
		public readonly long FileBytes;
		public readonly string FilePath;

		public PersistentCacheInfo(bool supported, bool exists, bool valid, string status, long fileBytes, string filePath)
		{
			Supported = supported;
			Exists = exists;
			Valid = valid;
			Status = status;
			FileBytes = fileBytes;
			FilePath = filePath;
		}
	}

	public static class CombinationalChipCacheManager
	{
		public const int FullCacheMaxInputBits = 24;
		public const long FullCacheMaxBytes = 512L * 1024L * 1024L;

		// Kept for compatibility with the first cache patch/UI.
		public const int AutoMaxInputBits = FullCacheMaxInputBits;
		public const int ForcedMaxInputBits = FullCacheMaxInputBits;
		public const int AutoMaxEntries = 1 << FullCacheMaxInputBits;
		public const int ForcedMaxEntries = 1 << FullCacheMaxInputBits;

		const uint CacheMagic = 0x544C5344; // "DSLT" in little-endian bytes
		const int CacheFileVersion = 3;
		const int FingerprintLength = 32;
		const int IoBufferBytes = 1024 * 1024;

		sealed class FingerprintHolder
		{
			public readonly int Revision;
			public readonly byte[] Fingerprint;

			public FingerprintHolder(int revision, byte[] fingerprint)
			{
				Revision = revision;
				Fingerprint = fingerprint;
			}
		}

		internal sealed class CacheBuildSnapshot
		{
			public readonly ChipDescription Root;
			public readonly Dictionary<string, ChipDescription> Descriptions;

			public CacheBuildSnapshot(ChipDescription root, Dictionary<string, ChipDescription> descriptions)
			{
				Root = root;
				Descriptions = descriptions;
			}
		}

		static readonly object cacheTableLock = new();
		static readonly object fingerprintTableLock = new();
		static ConditionalWeakTable<ChipDescription, CombinationalChipMemoCache> caches = new();
		static ConditionalWeakTable<ChipDescription, FingerprintHolder> fingerprints = new();
		static int descriptionRevision;

		// Full LUT generation/loading is intentionally serialized on a background worker.
		// Building several large LUTs in parallel can easily starve Unity's main/simulation
		// threads and was the source of the multi-second UI freezes seen with the first
		// persistent-cache implementation.
		sealed class BackgroundCacheWorkItem
		{
			public readonly string JobKey;
			public readonly CancellationTokenSource Cancellation;
			public readonly Action<CancellationToken> Work;

			public BackgroundCacheWorkItem(string jobKey, CancellationTokenSource cancellation, Action<CancellationToken> work)
			{
				JobKey = jobKey;
				Cancellation = cancellation;
				Work = work;
			}
		}

		static readonly object backgroundQueueLock = new();
		static readonly Queue<BackgroundCacheWorkItem> backgroundQueue = new();
		static readonly Dictionary<string, CancellationTokenSource> backgroundCancellations = new(ChipDescription.NameComparer);
		static Thread backgroundWorker;
		static int readyGeneration;

		internal static int ReadyGeneration => Volatile.Read(ref readyGeneration);

		internal static void LogFullLut(string message)
		{
			string line = "[FULL LUT] " + message;
#if UNITY_5_3_OR_NEWER
			UnityEngine.Debug.Log(line);
#else
			Console.WriteLine(line);
#endif
		}

		internal static void LogFullLutError(string message, Exception ex = null)
		{
			string details = ex == null ? message : message + Environment.NewLine + ex;
			string line = "[FULL LUT] " + details;
#if UNITY_5_3_OR_NEWER
			UnityEngine.Debug.LogError(line);
#else
			Console.Error.WriteLine(line);
#endif
		}

		public static ChipCacheAnalysis Analyze(ChipDescription description, ChipLibrary library)
		{
			if (description == null)
			{
				return new ChipCacheAnalysis(false, "missing chip description", 0, 0);
			}

			HashSet<string> active = new(ChipDescription.NameComparer);
			return AnalyzeRecursive(description, library, active, true);
		}

		// A stateful/feedback chip cannot be represented by an input-only full LUT, but
		// combinational custom chips nested inside it are still safe to cache. This count
		// is used by the UI to report that hybrid behaviour instead of presenting a
		// stateful chip as a cache "error". Each custom description is counted once.
		public static int CountCacheableNestedDescriptions(ChipDescription description, ChipLibrary library)
		{
			if (description == null || library == null) return 0;

			HashSet<string> visited = new(ChipDescription.NameComparer);
			string rootName = description.Name ?? string.Empty;
			visited.Add(rootName);
			int count = 0;

			void Visit(ChipDescription current)
			{
				SubChipDescription[] subChips = current.SubChips ?? Array.Empty<SubChipDescription>();
				for (int i = 0; i < subChips.Length; i++)
				{
					if (!library.TryGetChipDescription(subChips[i].Name, out ChipDescription child)) continue;
					if (child.ChipType != ChipType.Custom) continue;

					string childName = child.Name ?? string.Empty;
					if (!visited.Add(childName)) continue;

					if (child.CacheMode != ChipCacheMode.Normal)
					{
						ChipCacheAnalysis childAnalysis = Analyze(child, library);
						if (CanBuildFullCache(child, childAnalysis, out _)) count++;
					}

					Visit(child);
				}
			}

			Visit(description);
			return count;
		}

		public static int GetInputBitLimit(ChipCacheMode mode) => FullCacheMaxInputBits;
		public static int GetEntryLimit(ChipCacheMode mode) => AutoMaxEntries;

		internal static int GetFullCacheEntryCount(int inputBitCount)
		{
			if (inputBitCount < 0 || inputBitCount > FullCacheMaxInputBits)
			{
				throw new ArgumentOutOfRangeException(nameof(inputBitCount), inputBitCount, "FULL LUT input bit count is outside supported range.");
			}

			long entries = 1L << inputBitCount;
			if (entries > int.MaxValue)
			{
				throw new OverflowException("FULL LUT entry count exceeds Int32 capacity.");
			}

			return checked((int)entries);
		}

		internal static void QueueBackgroundCacheWork(string jobKey, Action<CancellationToken> work)
		{
			if (work == null) return;
			jobKey ??= string.Empty;

			CancellationTokenSource cancellation = new();
			int queueDepth;

			lock (backgroundQueueLock)
			{
				if (backgroundCancellations.TryGetValue(jobKey, out CancellationTokenSource previous))
				{
					LogFullLut($"Superseding older queued/running job: chip={jobKey}");
					try { previous.Cancel(); } catch { }
				}

				backgroundCancellations[jobKey] = cancellation;
				backgroundQueue.Enqueue(new BackgroundCacheWorkItem(jobKey, cancellation, work));
				queueDepth = backgroundQueue.Count;

				if (backgroundWorker == null)
				{
					backgroundWorker = new Thread(BackgroundCacheWorkerLoop)
					{
						IsBackground = true,
						Name = "Rewired FULL LUT worker"
					};
					backgroundWorker.Start();
				}

				Monitor.PulseAll(backgroundQueueLock);
			}

			LogFullLut($"Job queued: chip={jobKey} depth={queueDepth}");
		}

		static void BackgroundCacheWorkerLoop()
		{
			try { Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; } catch { }

			while (true)
			{
				BackgroundCacheWorkItem item;
				lock (backgroundQueueLock)
				{
					while (backgroundQueue.Count == 0)
					{
						Monitor.Wait(backgroundQueueLock);
					}

					item = backgroundQueue.Dequeue();
				}

				RunBackgroundCacheWork(item);
			}
		}

		static void RunBackgroundCacheWork(BackgroundCacheWorkItem item)
		{
			try
			{
				item.Work(item.Cancellation.Token);
			}
			catch (Exception ex)
			{
				LogFullLutError($"UNHANDLED WORKER ERROR: chip={item.JobKey}", ex);
			}
			finally
			{
				lock (backgroundQueueLock)
				{
					if (backgroundCancellations.TryGetValue(item.JobKey, out CancellationTokenSource current) &&
					    ReferenceEquals(current, item.Cancellation))
					{
						backgroundCancellations.Remove(item.JobKey);
					}
				}

				try { item.Cancellation.Dispose(); } catch { }
			}
		}

		internal static void NotifyCacheReady()
		{
			Interlocked.Increment(ref readyGeneration);
		}

		public static bool CanBuildFullCache(ChipDescription description, ChipCacheAnalysis analysis, out string reason)
		{
			if (!analysis.CanCache)
			{
				reason = analysis.Reason;
				return false;
			}

			if (analysis.InputBitCount > FullCacheMaxInputBits)
			{
				reason = $"too many input bits ({analysis.InputBitCount} > {FullCacheMaxInputBits})";
				return false;
			}

			long bytes = GetEstimatedMemoryBytes(description, analysis);
			if (bytes < 0 || bytes > FullCacheMaxBytes)
			{
				reason = $"LUT too large ({FormatBytes(bytes)} > {FormatBytes(FullCacheMaxBytes)})";
				return false;
			}

			reason = "ready";
			return true;
		}

		public static long GetEstimatedMemoryBytes(ChipDescription description, ChipCacheAnalysis analysis)
		{
			if (description == null || analysis.BinaryCombinationCount == ulong.MaxValue)
			{
				return -1;
			}

			int outputCount = description.OutputPins?.Length ?? 0;
			ulong values = analysis.BinaryCombinationCount * (ulong)Math.Max(1, outputCount);
			if (values > long.MaxValue / sizeof(uint)) return -1;
			return (long)values * sizeof(uint);
		}

		public static bool TryGetStats(ChipDescription description, out int entryCount, out int targetEntryCount)
		{
			entryCount = 0;
			targetEntryCount = 0;

			if (description == null || !caches.TryGetValue(description, out CombinationalChipMemoCache cache))
			{
				return false;
			}

			entryCount = cache.EntryCount;
			targetEntryCount = cache.TargetEntryCount;
			return true;
		}

		public static bool TryGetBuildInfo(
			ChipDescription description,
			out bool ready,
			out double buildMilliseconds,
			out long memoryBytes,
			out string failureReason)
		{
			ready = false;
			buildMilliseconds = 0;
			memoryBytes = 0;
			failureReason = string.Empty;

			if (description == null || !caches.TryGetValue(description, out CombinationalChipMemoCache cache))
			{
				return false;
			}

			ready = cache.Ready;
			buildMilliseconds = cache.BuildMilliseconds;
			memoryBytes = cache.MemoryBytes;
			failureReason = cache.BuildFailureReason;
			return true;
		}

		public static bool TryGetRuntimePersistenceInfo(
			ChipDescription description,
			out bool loadedFromDisk,
			out double loadMilliseconds,
			out string diskMessage)
		{
			loadedFromDisk = false;
			loadMilliseconds = 0;
			diskMessage = string.Empty;

			if (description == null || !caches.TryGetValue(description, out CombinationalChipMemoCache cache))
			{
				return false;
			}

			loadedFromDisk = cache.LoadedFromDisk;
			loadMilliseconds = cache.LoadMilliseconds;
			diskMessage = cache.PersistenceMessage;
			return true;
		}

		public static PersistentCacheInfo GetPersistentCacheInfo(ChipDescription description, ChipLibrary library)
		{
			if (description == null)
			{
				return new PersistentCacheInfo(false, false, false, "UNAVAILABLE", 0, string.Empty);
			}

			ChipCacheAnalysis analysis = Analyze(description, library);
			if (!CanBuildFullCache(description, analysis, out _))
			{
				return new PersistentCacheInfo(false, false, false, "UNAVAILABLE", 0, string.Empty);
			}

			if (!TryGetActiveProjectName(out string projectName))
			{
				return new PersistentCacheInfo(false, false, false, "NO PROJECT", 0, string.Empty);
			}

			byte[] fingerprint = GetLogicFingerprint(description, library);
			string path = GetPersistentCachePath(description.Name, projectName);
			return InspectPersistentCacheFile(path, fingerprint, analysis.InputBitCount, description.OutputPins?.Length ?? 0, GetFullCacheEntryCount(analysis.InputBitCount));
		}

		// Called after a chip description has changed in the library. This invalidates
		// fingerprint memoization so parent chips see changes in nested dependencies.
		public static void NotifyProjectDescriptionsChanged()
		{
			Interlocked.Increment(ref descriptionRevision);
			CombinationalJitCompiler.NotifyDescriptionsChanged();
			FeedbackJitCompiler.NotifyDescriptionsChanged();
		}

		// Builds (or loads, if already valid) persistent caches for the saved chip and
		// every custom chip that depends on it. This keeps startup from doing the work.
		public static void RefreshPersistentCachesAfterSave(string changedChipName, ChipLibrary library, string projectName)
		{
			if (library == null || string.IsNullOrWhiteSpace(changedChipName) || string.IsNullOrWhiteSpace(projectName)) return;

			NotifyProjectDescriptionsChanged();

			Queue<string> pending = new();
			HashSet<string> visited = new(ChipDescription.NameComparer);
			pending.Enqueue(changedChipName);

			while (pending.Count > 0)
			{
				string chipName = pending.Dequeue();
				if (!visited.Add(chipName)) continue;

				if (library.TryGetChipDescription(chipName, out ChipDescription description))
				{
					EnsurePersistentCacheForDescription(description, library, projectName);
				}

				ChipDescription[] parents = library.GetDirectParentChips(chipName);
				for (int i = 0; i < parents.Length; i++)
				{
					pending.Enqueue(parents[i].Name);
				}
			}
		}

		public static void DeletePersistentCache(string chipName, string projectName)
		{
			if (string.IsNullOrWhiteSpace(chipName) || string.IsNullOrWhiteSpace(projectName)) return;

			try
			{
				string path = GetPersistentCachePath(chipName, projectName);
				if (File.Exists(path)) File.Delete(path);
				string temp = path + ".tmp";
				if (File.Exists(temp)) File.Delete(temp);
			}
			catch
			{
				// Disk cache is disposable. Failure to delete must never break project saving.
			}
		}

		static CacheBuildSnapshot CreateBuildSnapshot(ChipDescription root, ChipLibrary library)
		{
			if (root == null || library == null) return null;

			Dictionary<string, ChipDescription> descriptions = new(ChipDescription.NameComparer);
			HashSet<string> active = new(ChipDescription.NameComparer);

			ChipDescription Capture(ChipDescription source)
			{
				string name = source?.Name ?? string.Empty;
				if (descriptions.TryGetValue(name, out ChipDescription existing)) return existing;
				if (source == null || !active.Add(name)) return null;

				try
				{
					ChipDescription clone = CloneDescriptionForBuild(source);
					descriptions[name] = clone;

					SubChipDescription[] subChips = source.SubChips ?? Array.Empty<SubChipDescription>();
					for (int i = 0; i < subChips.Length; i++)
					{
						if (library.TryGetChipDescription(subChips[i].Name, out ChipDescription child)) Capture(child);
					}

					return clone;
				}
				finally
				{
					active.Remove(name);
				}
			}

			ChipDescription rootClone = Capture(root);
			return rootClone == null ? null : new CacheBuildSnapshot(rootClone, descriptions);
		}

		static ChipDescription CloneDescriptionForBuild(ChipDescription source)
		{
			PinDescription[] inputPins = source.InputPins == null ? Array.Empty<PinDescription>() : (PinDescription[])source.InputPins.Clone();
			PinDescription[] outputPins = source.OutputPins == null ? Array.Empty<PinDescription>() : (PinDescription[])source.OutputPins.Clone();
			WireDescription[] wires = source.Wires == null ? Array.Empty<WireDescription>() : (WireDescription[])source.Wires.Clone();

			SubChipDescription[] sourceSubs = source.SubChips ?? Array.Empty<SubChipDescription>();
			SubChipDescription[] subChips = new SubChipDescription[sourceSubs.Length];
			for (int i = 0; i < sourceSubs.Length; i++)
			{
				subChips[i] = sourceSubs[i];
				if (sourceSubs[i].InternalData != null) subChips[i].InternalData = (uint[])sourceSubs[i].InternalData.Clone();
			}

			return new ChipDescription
			{
				DLSVersion = source.DLSVersion,
				Name = source.Name,
				NameLocation = source.NameLocation,
				ChipType = source.ChipType,
				CacheMode = ChipCacheMode.Normal, // prevent nested memo attachment in isolated build
				Size = source.Size,
				Colour = source.Colour,
				InputPins = inputPins,
				OutputPins = outputPins,
				SubChips = subChips,
				Wires = wires,
				Displays = Array.Empty<DisplayDescription>()
			};
		}

		internal static void Attach(SimChip simChip, ChipDescription description, ChipLibrary library)
		{
			if (simChip == null) return;

			simChip.MemoCache = null;

			if (description == null ||
			    description.ChipType != ChipType.Custom ||
			    description.CacheMode == ChipCacheMode.Normal)
			{
				return;
			}

			ChipCacheAnalysis analysis = Analyze(description, library);
			if (!CanBuildFullCache(description, analysis, out _)) return;

			byte[] fingerprint = GetLogicFingerprint(description, library);
			string cachePath = TryGetActiveProjectName(out string projectName)
				? GetPersistentCachePath(description.Name, projectName)
				: string.Empty;

			CombinationalChipMemoCache cache = GetOrCreateCache(description, analysis, fingerprint, cachePath);

			// Attach the cache object immediately, but the deterministic engine only collapses
			// this custom chip once cache.Ready becomes true. Until then the normal flattened
			// topology remains active, so cache construction never stalls or slows simulation.
			simChip.MemoCache = cache;

			// Many instances share one cache per ChipDescription. Never clone the whole
			// dependency tree again while the first instance is already loading/building it.
			if (cache.Ready || cache.Working) return;

			PersistentCacheInfo info = string.IsNullOrWhiteSpace(cachePath)
				? new PersistentCacheInfo(false, false, false, "RAM ONLY", 0, string.Empty)
				: InspectPersistentCacheFile(
					cachePath,
					fingerprint,
					analysis.InputBitCount,
					description.OutputPins?.Length ?? 0,
					GetFullCacheEntryCount(analysis.InputBitCount));

			CacheBuildSnapshot snapshot = CreateBuildSnapshot(description, library);
			cache.EnsureReadyAsync(snapshot);
		}

		static CombinationalChipMemoCache GetOrCreateCache(
			ChipDescription description,
			ChipCacheAnalysis analysis,
			byte[] fingerprint,
			string cachePath)
		{
			CombinationalChipMemoCache cache;
			lock (cacheTableLock)
			{
				if (caches.TryGetValue(description, out cache) && !cache.MatchesFingerprint(fingerprint))
				{
					caches.Remove(description);
					cache = null;
				}

				if (cache == null)
				{
					cache = new CombinationalChipMemoCache(description, analysis, fingerprint, cachePath);
					caches.Add(description, cache);
				}
			}

			return cache;
		}

		static void EnsurePersistentCacheForDescription(ChipDescription description, ChipLibrary library, string projectName)
		{
			if (description == null || description.ChipType != ChipType.Custom) return;

			if (description.CacheMode == ChipCacheMode.Normal)
			{
				DeletePersistentCache(description.Name, projectName);
				return;
			}

			ChipCacheAnalysis analysis = Analyze(description, library);
			if (!CanBuildFullCache(description, analysis, out _))
			{
				// A stateful/feedback parent stays on the normal deterministic path. Its
				// combinational descendants are attached and cached independently when used.
				DeletePersistentCache(description.Name, projectName);
				return;
			}

			byte[] fingerprint = GetLogicFingerprint(description, library);
			string path = GetPersistentCachePath(description.Name, projectName);
			PersistentCacheInfo info = InspectPersistentCacheFile(
				path,
				fingerprint,
				analysis.InputBitCount,
				description.OutputPins?.Length ?? 0,
				GetFullCacheEntryCount(analysis.InputBitCount));

			if (info.Valid) return;

			CombinationalChipMemoCache cache = GetOrCreateCache(description, analysis, fingerprint, path);
			if (cache.Ready || cache.Working) return;

			CacheBuildSnapshot snapshot = CreateBuildSnapshot(description, library);
			cache.EnsureReadyAsync(snapshot);
		}

		static ChipCacheAnalysis AnalyzeRecursive(
			ChipDescription description,
			ChipLibrary library,
			HashSet<string> activeDescriptions,
			bool includeInputCount)
		{
			if (description == null)
			{
				return new ChipCacheAnalysis(false, "missing chip description", 0, 0);
			}

			if (description.ChipType != ChipType.Custom)
			{
				bool pure = IsPureBuiltin(description.ChipType);
				return new ChipCacheAnalysis(
					pure,
					pure ? "cacheable" : $"contains state/source chip: {description.ChipType}",
					0,
					1);
			}

			string name = string.IsNullOrWhiteSpace(description.Name) ? "<unnamed>" : description.Name;
			if (!activeDescriptions.Add(name))
			{
				return new ChipCacheAnalysis(false, "recursive Custom Chip dependency", 0, 0);
			}

			try
			{
				SubChipDescription[] subChips = description.SubChips ?? Array.Empty<SubChipDescription>();
				WireDescription[] wires = description.Wires ?? Array.Empty<WireDescription>();

				Dictionary<(int owner, int pin), (int owner, int pin)> driverByTarget = new();
				for (int i = 0; i < wires.Length; i++)
				{
					WireDescription wire = wires[i];
					var target = (wire.TargetPinAddress.PinOwnerID, wire.TargetPinAddress.PinID);
					var source = (wire.SourcePinAddress.PinOwnerID, wire.SourcePinAddress.PinID);

					if (driverByTarget.TryGetValue(target, out var oldSource))
					{
						if (oldSource != source)
						{
							return MakeFailure(description, includeInputCount, "multiple drivers on one net");
						}
					}
					else
					{
						driverByTarget.Add(target, source);
					}
				}

				for (int i = 0; i < subChips.Length; i++)
				{
					SubChipDescription sub = subChips[i];
					if (!library.TryGetChipDescription(sub.Name, out ChipDescription subDescription))
					{
						return MakeFailure(description, includeInputCount, $"missing subchip: {sub.Name}");
					}

					if (subDescription.ChipType == ChipType.Custom)
					{
						ChipCacheAnalysis nested = AnalyzeRecursive(subDescription, library, activeDescriptions, false);
						if (!nested.CanCache)
						{
							return MakeFailure(description, includeInputCount, $"{sub.Name}: {nested.Reason}");
						}
					}
					else if (!IsPureBuiltin(subDescription.ChipType))
					{
						return MakeFailure(
							description,
							includeInputCount,
							$"contains state/source chip: {subDescription.ChipType}");
					}
				}

				if (HasFeedback(subChips, wires))
				{
					return MakeFailure(description, includeInputCount, "feedback loop detected");
				}

				int inputBits = includeInputCount ? CountInputBits(description) : 0;
				ulong binaryCombinations = includeInputCount ? Pow2Saturated(inputBits) : 1;
				return new ChipCacheAnalysis(true, "cacheable", inputBits, binaryCombinations);
			}
			finally
			{
				activeDescriptions.Remove(name);
			}
		}

		static ChipCacheAnalysis MakeFailure(ChipDescription description, bool includeInputCount, string reason)
		{
			int bits = includeInputCount ? CountInputBits(description) : 0;
			return new ChipCacheAnalysis(false, reason, bits, includeInputCount ? Pow2Saturated(bits) : 0);
		}

		static int CountInputBits(ChipDescription description)
		{
			int count = 0;
			PinDescription[] inputs = description.InputPins ?? Array.Empty<PinDescription>();
			for (int i = 0; i < inputs.Length; i++) count += (int)inputs[i].BitCount;
			return count;
		}

		static ulong Pow2Saturated(int bits)
		{
			if (bits <= 0) return 1;
			if (bits >= 63) return ulong.MaxValue;
			return 1UL << bits;
		}

		static bool HasFeedback(SubChipDescription[] subChips, WireDescription[] wires)
		{
			if (subChips.Length == 0) return false;

			Dictionary<int, int> nodeFromID = new();
			for (int i = 0; i < subChips.Length; i++) nodeFromID[subChips[i].ID] = i;

			List<int>[] outgoing = new List<int>[subChips.Length];
			int[] inDegree = new int[subChips.Length];
			HashSet<long> uniqueEdges = new();

			for (int i = 0; i < wires.Length; i++)
			{
				int sourceOwner = wires[i].SourcePinAddress.PinOwnerID;
				int targetOwner = wires[i].TargetPinAddress.PinOwnerID;

				if (!nodeFromID.TryGetValue(sourceOwner, out int sourceNode) ||
				    !nodeFromID.TryGetValue(targetOwner, out int targetNode))
				{
					continue;
				}

				if (sourceNode == targetNode) return true;

				long edgeKey = ((long)sourceNode << 32) | (uint)targetNode;
				if (!uniqueEdges.Add(edgeKey)) continue;

				(outgoing[sourceNode] ??= new List<int>()).Add(targetNode);
				inDegree[targetNode]++;
			}

			Queue<int> ready = new();
			for (int i = 0; i < inDegree.Length; i++)
			{
				if (inDegree[i] == 0) ready.Enqueue(i);
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
					inDegree[target]--;
					if (inDegree[target] == 0) ready.Enqueue(target);
				}
			}

			return visited != subChips.Length;
		}

		static bool IsPureBuiltin(ChipType type)
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
				ChipType.BusTerminus_1Bit or
				ChipType.Bus_4Bit or
				ChipType.BusTerminus_4Bit or
				ChipType.Bus_8Bit or
				ChipType.BusTerminus_8Bit;
		}

		static byte[] GetLogicFingerprint(ChipDescription description, ChipLibrary library)
		{
			int revision = Volatile.Read(ref descriptionRevision);
			if (fingerprints.TryGetValue(description, out FingerprintHolder existing) && existing.Revision == revision)
			{
				return existing.Fingerprint;
			}

			lock (fingerprintTableLock)
			{
				if (fingerprints.TryGetValue(description, out existing) && existing.Revision == revision)
				{
					return existing.Fingerprint;
				}

				Dictionary<string, byte[]> memo = new(ChipDescription.NameComparer);
				HashSet<string> active = new(ChipDescription.NameComparer);
				byte[] fingerprint = ComputeLogicFingerprintRecursive(description, library, memo, active);

				fingerprints.Remove(description);
				fingerprints.Add(description, new FingerprintHolder(revision, fingerprint));
				return fingerprint;
			}
		}

		static byte[] ComputeLogicFingerprintRecursive(
			ChipDescription description,
			ChipLibrary library,
			Dictionary<string, byte[]> memo,
			HashSet<string> active)
		{
			string memoKey = description.Name ?? string.Empty;
			if (memo.TryGetValue(memoKey, out byte[] memoized)) return memoized;

			if (!active.Add(memoKey))
			{
				return HashUtf8("recursive-dependency");
			}

			try
			{
				StringBuilder b = new();
				b.Append("DLS-FULL-LUT|").Append(CacheFileVersion).Append('|');
				b.Append("TYPE:").Append((int)description.ChipType).Append('|');

				PinDescription[] inputs = description.InputPins ?? Array.Empty<PinDescription>();
				b.Append("IN:").Append(inputs.Length).Append('|');
				for (int i = 0; i < inputs.Length; i++)
				{
					b.Append(inputs[i].ID).Append(',').Append((int)inputs[i].BitCount).Append(';');
				}

				PinDescription[] outputs = description.OutputPins ?? Array.Empty<PinDescription>();
				b.Append("OUT:").Append(outputs.Length).Append('|');
				for (int i = 0; i < outputs.Length; i++)
				{
					b.Append(outputs[i].ID).Append(',').Append((int)outputs[i].BitCount).Append(';');
				}

				SubChipDescription[] subChips = description.SubChips ?? Array.Empty<SubChipDescription>();
				b.Append("SUB:").Append(subChips.Length).Append('|');
				for (int i = 0; i < subChips.Length; i++)
				{
					SubChipDescription sub = subChips[i];
					b.Append("ID:").Append(sub.ID).Append('|');

					uint[] internalData = sub.InternalData ?? Array.Empty<uint>();
					b.Append("DATA:").Append(internalData.Length).Append(':');
					for (int d = 0; d < internalData.Length; d++) b.Append(internalData[d]).Append(',');
					b.Append('|');

					if (library.TryGetChipDescription(sub.Name, out ChipDescription child))
					{
						b.Append("CHILDTYPE:").Append((int)child.ChipType).Append('|');
						if (child.ChipType == ChipType.Custom)
						{
							byte[] childFingerprint = ComputeLogicFingerprintRecursive(child, library, memo, active);
							b.Append("CHILDHASH:").Append(Convert.ToBase64String(childFingerprint)).Append('|');
						}
					}
					else
					{
						b.Append("MISSING:").Append(sub.Name).Append('|');
					}
				}

				WireDescription[] wires = description.Wires ?? Array.Empty<WireDescription>();
				b.Append("WIRES:").Append(wires.Length).Append('|');
				for (int i = 0; i < wires.Length; i++)
				{
					WireDescription w = wires[i];
					b.Append(w.SourcePinAddress.PinOwnerID).Append(',')
						.Append(w.SourcePinAddress.PinID).Append('>')
						.Append(w.TargetPinAddress.PinOwnerID).Append(',')
						.Append(w.TargetPinAddress.PinID).Append(',')
						.Append((int)w.ConnectionType).Append(',')
						.Append(w.ConnectedWireIndex).Append(',')
						.Append(w.ConnectedWireSegmentIndex).Append(';');
				}

				byte[] hash = HashUtf8(b.ToString());
				memo[memoKey] = hash;
				return hash;
			}
			finally
			{
				active.Remove(memoKey);
			}
		}

		static byte[] HashUtf8(string text)
		{
			using SHA256 sha = SHA256.Create();
			return sha.ComputeHash(Encoding.UTF8.GetBytes(text));
		}

		static bool TryGetActiveProjectName(out string projectName)
		{
			projectName = Project.ActiveProject == null ? null : Project.ActiveProject.description.ProjectName;
			return !string.IsNullOrWhiteSpace(projectName);
		}

		static string GetPersistentCachePath(string chipName, string projectName)
		{
			string directory = Path.Combine(SavePaths.GetProjectPath(projectName), "Cache", "FullLUT");
			string safeName = MakeSafeFileName(chipName);
			if (safeName.Length > 60) safeName = safeName.Substring(0, 60);

			byte[] nameHash = HashUtf8(chipName ?? string.Empty);
			string suffix = $"{nameHash[0]:X2}{nameHash[1]:X2}{nameHash[2]:X2}{nameHash[3]:X2}";
			return Path.Combine(directory, "chip_" + safeName + "_" + suffix + ".dlscache");
		}

		static string MakeSafeFileName(string name)
		{
			if (string.IsNullOrWhiteSpace(name)) return "unnamed";

			char[] chars = name.ToCharArray();
			char[] invalid = Path.GetInvalidFileNameChars();
			for (int i = 0; i < chars.Length; i++)
			{
				for (int j = 0; j < invalid.Length; j++)
				{
					if (chars[i] == invalid[j])
					{
						chars[i] = '_';
						break;
					}
				}
			}

			return new string(chars);
		}

		static PersistentCacheInfo InspectPersistentCacheFile(
			string path,
			byte[] expectedFingerprint,
			int expectedInputBits,
			int expectedOutputCount,
			int expectedEntryCount)
		{
			if (string.IsNullOrWhiteSpace(path))
			{
				return new PersistentCacheInfo(false, false, false, "NO PROJECT", 0, string.Empty);
			}

			if (!File.Exists(path))
			{
				return new PersistentCacheInfo(true, false, false, "MISSING", 0, path);
			}

			try
			{
				FileInfo fileInfo = new(path);
				using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
				using BinaryReader reader = new(stream);

				if (!TryReadAndValidateHeader(
					    reader,
					    expectedFingerprint,
					    expectedInputBits,
					    expectedOutputCount,
					    expectedEntryCount,
					    out int storedValueCount,
					    out bool fingerprintMatches))
				{
					return new PersistentCacheInfo(true, true, false, fingerprintMatches ? "CORRUPT" : "OUTDATED", fileInfo.Length, path);
				}

				long expectedBytes = (long)storedValueCount * sizeof(uint);
				long remaining = stream.Length - stream.Position;
				if (remaining != expectedBytes)
				{
					return new PersistentCacheInfo(true, true, false, "CORRUPT", fileInfo.Length, path);
				}

				return new PersistentCacheInfo(true, true, true, "VALID", fileInfo.Length, path);
			}
			catch
			{
				long size = 0;
				try { size = new FileInfo(path).Length; } catch { }
				return new PersistentCacheInfo(true, true, false, "CORRUPT", size, path);
			}
		}

		static bool TryReadAndValidateHeader(
			BinaryReader reader,
			byte[] expectedFingerprint,
			int expectedInputBits,
			int expectedOutputCount,
			int expectedEntryCount,
			out int storedValueCount,
			out bool fingerprintMatches)
		{
			storedValueCount = 0;
			fingerprintMatches = true;

			if (reader.ReadUInt32() != CacheMagic) return false;
			if (reader.ReadInt32() != CacheFileVersion) return false;

			int fingerprintLength = reader.ReadInt32();
			if (fingerprintLength != FingerprintLength) return false;

			byte[] storedFingerprint = reader.ReadBytes(fingerprintLength);
			if (storedFingerprint.Length != fingerprintLength) return false;
			fingerprintMatches = ByteArraysEqual(storedFingerprint, expectedFingerprint);
			if (!fingerprintMatches) return false;

			if (reader.ReadInt32() != expectedInputBits) return false;
			if (reader.ReadInt32() != expectedOutputCount) return false;
			if (reader.ReadInt32() != expectedEntryCount) return false;

			storedValueCount = reader.ReadInt32();
			long expectedValues = (long)expectedEntryCount * Math.Max(1, expectedOutputCount);
			if (storedValueCount < 0 || storedValueCount != expectedValues) return false;

			return true;
		}

		static bool ByteArraysEqual(byte[] a, byte[] b)
		{
			if (ReferenceEquals(a, b)) return true;
			if (a == null || b == null || a.Length != b.Length) return false;
			for (int i = 0; i < a.Length; i++)
			{
				if (a[i] != b[i]) return false;
			}
			return true;
		}

		internal static bool FingerprintsEqual(byte[] a, byte[] b) => ByteArraysEqual(a, b);
		internal static uint PersistentCacheMagic => CacheMagic;
		internal static int PersistentCacheVersion => CacheFileVersion;
		internal static int PersistentFingerprintLength => FingerprintLength;
		internal static int PersistentIoBufferBytes => IoBufferBytes;

		public static string FormatBytes(long bytes)
		{
			if (bytes < 0) return "unknown";
			if (bytes < 1024) return $"{bytes} B";
			if (bytes < 1024L * 1024L) return $"{bytes / 1024.0:0.0} KiB";
			if (bytes < 1024L * 1024L * 1024L) return $"{bytes / (1024.0 * 1024.0):0.0} MiB";
			return $"{bytes / (1024.0 * 1024.0 * 1024.0):0.00} GiB";
		}
	}

	internal sealed class CombinationalChipMemoCache
	{
		readonly string chipName;
		readonly int[] inputWidths;
		readonly int inputBitCount;
		readonly int outputCount;
		readonly int targetEntryCount;
		readonly byte[] fingerprint;
		readonly string persistentPath;

		volatile uint[] lut;
		int buildState; // 0 = idle, 1 = queued/building, 2 = ready, 3 = failed
		long completedEntries;
		double buildMilliseconds;
		double loadMilliseconds;
		volatile string buildFailureReason = string.Empty;
		volatile string persistenceMessage = string.Empty;
		int loadedFromDisk;

		[ThreadStatic] static uint[] threadOutputScratch;
		[ThreadStatic] static uint[] threadPreviousOutputs;

		public bool Ready => lut != null;
		public int EntryCount
		{
			get
			{
				if (Ready) return targetEntryCount;
				long completed = Interlocked.Read(ref completedEntries);
				return (int)Math.Min(targetEntryCount, Math.Max(0, completed));
			}
		}
		public int TargetEntryCount => targetEntryCount;
		public double BuildMilliseconds => buildMilliseconds;
		public double LoadMilliseconds => loadMilliseconds;
		public string BuildFailureReason => buildFailureReason ?? string.Empty;
		public string PersistenceMessage => persistenceMessage ?? string.Empty;
		public bool LoadedFromDisk => Volatile.Read(ref loadedFromDisk) != 0;
		public bool Working => Volatile.Read(ref buildState) == 1;
		public long MemoryBytes
		{
			get
			{
				uint[] data = lut;
				return data == null ? 0 : (long)data.LongLength * sizeof(uint);
			}
		}

		public CombinationalChipMemoCache(
			ChipDescription description,
			ChipCacheAnalysis analysis,
			byte[] fingerprint,
			string persistentPath)
		{
			chipName = string.IsNullOrWhiteSpace(description?.Name) ? "<unnamed>" : description.Name;
			this.fingerprint = fingerprint;
			this.persistentPath = persistentPath;
			inputBitCount = analysis.InputBitCount;
			outputCount = description.OutputPins?.Length ?? 0;
			targetEntryCount = CombinationalChipCacheManager.GetFullCacheEntryCount(analysis.InputBitCount);

			PinDescription[] inputs = description.InputPins ?? Array.Empty<PinDescription>();
			inputWidths = new int[inputs.Length];
			for (int i = 0; i < inputs.Length; i++) inputWidths[i] = (int)inputs[i].BitCount;
		}

		public bool MatchesFingerprint(byte[] other) => CombinationalChipCacheManager.FingerprintsEqual(fingerprint, other);

		public void EnsureReadyAsync(CombinationalChipCacheManager.CacheBuildSnapshot snapshot)
		{
			if (Ready) return;

			// A previous background attempt may have failed because a cache file disappeared,
			// was being replaced, or another transient I/O problem occurred. Allow a later
			// attach/save pass to retry instead of leaving the cache permanently disabled.
			while (true)
			{
				int state = Volatile.Read(ref buildState);
				if (state == 1 || state == 2 || Ready) return;
				if (state != 0 && state != 3) return;
				if (Interlocked.CompareExchange(ref buildState, 1, state) == state) break;
			}

			buildFailureReason = string.Empty;
			persistenceMessage = "QUEUED";
			CombinationalChipCacheManager.LogFullLut($"Requested: chip={chipName} inputBits={inputBitCount} entries={targetEntryCount}");
			CombinationalChipCacheManager.QueueBackgroundCacheWork(
				chipName,
				token => LoadOrBuildInBackground(snapshot, token));
		}

		void LoadOrBuildInBackground(CombinationalChipCacheManager.CacheBuildSnapshot snapshot, CancellationToken token)
		{
			CombinationalChipCacheManager.LogFullLut($"Worker picked job: chip={chipName}");

			try
			{
				token.ThrowIfCancellationRequested();

				if (TryLoadPersistent(token))
				{
					Volatile.Write(ref buildState, 2);
					return;
				}

				token.ThrowIfCancellationRequested();

				if (snapshot == null)
				{
					AbortBuild("persistent cache could not be loaded and no build snapshot is available");
					return;
				}

				SimChip isolatedChip = BuildIsolatedSimChip(snapshot);
				token.ThrowIfCancellationRequested();
				BuildFullLut(isolatedChip, token);

				if (Ready)
				{
					token.ThrowIfCancellationRequested();
					TrySavePersistent(token);
					Volatile.Write(ref buildState, 2);
				}
				else
				{
					AbortBuild("RAM LUT build completed without publishing a LUT");
				}
			}
			catch (OperationCanceledException)
			{
				AbortBuild("superseded by a newer FULL LUT request");
			}
			catch (Exception ex)
			{
				AbortBuild(ex.GetType().Name + ": " + ex.Message, ex);
			}
		}

		void AbortBuild(string reason, Exception ex = null)
		{
			buildFailureReason = reason ?? "unknown FULL LUT failure";
			persistenceMessage = "BUILD ABORTED";
			lut = null;
			Volatile.Write(ref buildState, 3);
			CombinationalChipCacheManager.LogFullLutError($"BUILD ABORTED: chip={chipName} reason={buildFailureReason}", ex);
		}

		static SimChip BuildIsolatedSimChip(CombinationalChipCacheManager.CacheBuildSnapshot snapshot)
		{
			if (snapshot?.Root == null) throw new InvalidDataException("Missing LUT build snapshot.");

			HashSet<string> active = new(ChipDescription.NameComparer);
			return BuildRecursive(snapshot.Root, -1, null);

			SimChip BuildRecursive(ChipDescription desc, int id, uint[] internalState)
			{
				string name = desc?.Name ?? string.Empty;
				if (desc == null) throw new InvalidDataException("Null chip in LUT build snapshot.");
				if (!active.Add(name)) throw new InvalidDataException("Recursive chip dependency in LUT build snapshot: " + name);

				try
				{
					SubChipDescription[] subDescriptions = desc.SubChips ?? Array.Empty<SubChipDescription>();
					SimChip[] children = subDescriptions.Length == 0 ? Array.Empty<SimChip>() : new SimChip[subDescriptions.Length];

					for (int i = 0; i < subDescriptions.Length; i++)
					{
						SubChipDescription sub = subDescriptions[i];
						if (!snapshot.Descriptions.TryGetValue(sub.Name ?? string.Empty, out ChipDescription childDesc))
						{
							throw new InvalidDataException("Missing chip in LUT build snapshot: " + sub.Name);
						}

						children[i] = BuildRecursive(childDesc, sub.ID, sub.InternalData);
					}

					SimChip chip = new(desc, id, internalState, children);
					WireDescription[] wires = desc.Wires ?? Array.Empty<WireDescription>();
					for (int i = 0; i < wires.Length; i++)
					{
						chip.AddConnection(wires[i].SourcePinAddress, wires[i].TargetPinAddress);
					}

					return chip;
				}
				finally
				{
					active.Remove(name);
				}
			}
		}

		void BuildFullLut(SimChip chip, CancellationToken token)
		{
			Stopwatch sw = Stopwatch.StartNew();
			Interlocked.Exchange(ref completedEntries, 0);
			persistenceMessage = "BUILDING IN BACKGROUND";

			try
			{
				token.ThrowIfCancellationRequested();

				int stride = Math.Max(1, outputCount);
				long valueCount = (long)targetEntryCount * stride;
				if (valueCount > int.MaxValue)
				{
					throw new InvalidOperationException("full LUT exceeds maximum .NET array length");
				}

				CombinationalChipCacheManager.LogFullLut($"Allocation started: chip={chipName} values={valueCount}");
				uint[] built = new uint[(int)valueCount];
				CombinationalChipCacheManager.LogFullLut($"Build started: chip={chipName} entries={targetEntryCount}");

				int progressMask = targetEntryCount <= 65536 ? 0 : 0xFFF;
				long nextProgressLog = 100000;

				for (int key = 0; key < targetEntryCount; key++)
				{
					if ((key & 0x3FF) == 0) token.ThrowIfCancellationRequested();

					SetBinaryInputs(chip, key);
					Simulator.EvaluatePureCombinationalForMemo(chip);

					for (int output = 0; output < outputCount; output++)
					{
						built[key * stride + output] = chip.OutputPins[output].State;
					}

					if ((key & progressMask) == 0 || key == targetEntryCount - 1)
					{
						long completed = key + 1L;
						Interlocked.Exchange(ref completedEntries, completed);

						if (completed == 1 || completed >= nextProgressLog)
						{
							CombinationalChipCacheManager.LogFullLut($"Progress {completed}/{targetEntryCount}: chip={chipName}");
							while (nextProgressLog <= completed) nextProgressLog += 100000;
						}
					}

					if ((key & 0x3FFF) == 0) Thread.Yield();
				}

				token.ThrowIfCancellationRequested();
				Interlocked.Exchange(ref completedEntries, targetEntryCount);
				Volatile.Write(ref loadedFromDisk, 0);
				lut = built;
				persistenceMessage = "BUILT";
				CombinationalChipCacheManager.NotifyCacheReady();
				CombinationalChipCacheManager.LogFullLut($"RAM build complete: chip={chipName} entries={targetEntryCount}");
			}
			finally
			{
				sw.Stop();
				buildMilliseconds = sw.Elapsed.TotalMilliseconds;
			}
		}

		bool TryLoadPersistent(CancellationToken token)
		{
			if (string.IsNullOrWhiteSpace(persistentPath) || !File.Exists(persistentPath))
			{
				persistenceMessage = string.IsNullOrWhiteSpace(persistentPath) ? "RAM ONLY" : "DISK MISSING";
				return false;
			}

			Stopwatch sw = Stopwatch.StartNew();
			persistenceMessage = "LOADING FROM DISK";
			CombinationalChipCacheManager.LogFullLut($"Disk load started: chip={chipName}");
			try
			{
				token.ThrowIfCancellationRequested();
				using FileStream stream = new(persistentPath, FileMode.Open, FileAccess.Read, FileShare.Read);
				using BinaryReader reader = new(stream);

				if (!ReadAndValidateHeader(reader, out int storedValueCount))
				{
					persistenceMessage = "DISK OUTDATED";
					return false;
				}

				long dataBytes = (long)storedValueCount * sizeof(uint);
				if (stream.Length - stream.Position != dataBytes)
				{
					persistenceMessage = "DISK CORRUPT";
					return false;
				}

				uint[] loaded = new uint[storedValueCount];
				ReadUIntArray(reader, loaded, token);
				token.ThrowIfCancellationRequested();
				Interlocked.Exchange(ref completedEntries, targetEntryCount);
				Volatile.Write(ref loadedFromDisk, 1);
				lut = loaded;
				persistenceMessage = "DISK VALID";
				CombinationalChipCacheManager.NotifyCacheReady();
				CombinationalChipCacheManager.LogFullLut($"Disk load complete: chip={chipName} entries={targetEntryCount}");
				return true;
			}
			catch (OperationCanceledException)
			{
				persistenceMessage = "CANCELLED";
				lut = null;
				throw;
			}
			catch (Exception ex)
			{
				persistenceMessage = "DISK CORRUPT";
				lut = null;
				CombinationalChipCacheManager.LogFullLutError($"Disk cache rejected; rebuilding: chip={chipName}", ex);
				return false;
			}
			finally
			{
				sw.Stop();
				loadMilliseconds = sw.Elapsed.TotalMilliseconds;
			}
		}

		bool ReadAndValidateHeader(BinaryReader reader, out int storedValueCount)
		{
			storedValueCount = 0;
			if (reader.ReadUInt32() != CombinationalChipCacheManager.PersistentCacheMagic) return false;
			if (reader.ReadInt32() != CombinationalChipCacheManager.PersistentCacheVersion) return false;

			int fingerprintLength = reader.ReadInt32();
			if (fingerprintLength != CombinationalChipCacheManager.PersistentFingerprintLength) return false;

			byte[] storedFingerprint = reader.ReadBytes(fingerprintLength);
			if (!CombinationalChipCacheManager.FingerprintsEqual(storedFingerprint, fingerprint)) return false;

			if (reader.ReadInt32() != inputBitCount) return false;
			if (reader.ReadInt32() != outputCount) return false;
			if (reader.ReadInt32() != targetEntryCount) return false;

			storedValueCount = reader.ReadInt32();
			long expectedValueCount = (long)targetEntryCount * Math.Max(1, outputCount);
			return storedValueCount >= 0 && storedValueCount == expectedValueCount;
		}

		void TrySavePersistent(CancellationToken token)
		{
			uint[] data = lut;
			if (data == null || string.IsNullOrWhiteSpace(persistentPath)) return;

			persistenceMessage = "SAVING TO DISK";
			string temporaryPath = persistentPath + ".tmp";
			CombinationalChipCacheManager.LogFullLut($"Disk save started: chip={chipName}");
			try
			{
				token.ThrowIfCancellationRequested();
				string directory = Path.GetDirectoryName(persistentPath);
				if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

				using (FileStream stream = new(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
				using (BinaryWriter writer = new(stream))
				{
					writer.Write(CombinationalChipCacheManager.PersistentCacheMagic);
					writer.Write(CombinationalChipCacheManager.PersistentCacheVersion);
					writer.Write(fingerprint.Length);
					writer.Write(fingerprint);
					writer.Write(inputBitCount);
					writer.Write(outputCount);
					writer.Write(targetEntryCount);
					writer.Write(data.Length);
					WriteUIntArray(stream, writer, data, token);
					token.ThrowIfCancellationRequested();
					writer.Flush();
					stream.Flush(true);
				}

				if (File.Exists(persistentPath)) File.Delete(persistentPath);
				File.Move(temporaryPath, persistentPath);
				persistenceMessage = "DISK VALID";
				CombinationalChipCacheManager.LogFullLut($"Disk save complete: chip={chipName}");
			}
			catch (OperationCanceledException)
			{
				persistenceMessage = "CANCELLED";
				try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
				throw;
			}
			catch (Exception ex)
			{
				persistenceMessage = "RAM ONLY: " + ex.GetType().Name;
				CombinationalChipCacheManager.LogFullLutError($"Disk save failed; RAM LUT remains usable: chip={chipName}", ex);
				try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
			}
		}

		static void ReadUIntArray(BinaryReader reader, uint[] destination, CancellationToken token)
		{
			int totalBytes = checked(destination.Length * sizeof(uint));
			byte[] buffer = new byte[Math.Min(CombinationalChipCacheManager.PersistentIoBufferBytes, Math.Max(sizeof(uint), totalBytes))];
			int destinationByteOffset = 0;

			while (destinationByteOffset < totalBytes)
			{
				token.ThrowIfCancellationRequested();
				int requested = Math.Min(buffer.Length, totalBytes - destinationByteOffset);
				int readTotal = 0;
				while (readTotal < requested)
				{
					int read = reader.Read(buffer, readTotal, requested - readTotal);
					if (read <= 0) throw new EndOfStreamException();
					readTotal += read;
				}

				Buffer.BlockCopy(buffer, 0, destination, destinationByteOffset, requested);
				destinationByteOffset += requested;
			}
		}

		static void WriteUIntArray(FileStream stream, BinaryWriter writer, uint[] source, CancellationToken token)
		{
			writer.Flush();
			int totalBytes = checked(source.Length * sizeof(uint));
			byte[] buffer = new byte[Math.Min(CombinationalChipCacheManager.PersistentIoBufferBytes, Math.Max(sizeof(uint), totalBytes))];
			int sourceByteOffset = 0;

			while (sourceByteOffset < totalBytes)
			{
				token.ThrowIfCancellationRequested();
				int count = Math.Min(buffer.Length, totalBytes - sourceByteOffset);
				Buffer.BlockCopy(source, sourceByteOffset, buffer, 0, count);
				stream.Write(buffer, 0, count);
				sourceByteOffset += count;
			}
		}

		public bool Evaluate(SimChip chip, out uint[] outputs)
		{
			if (Ready && TryBuildBinaryKey(chip, out int key))
			{
				if (outputCount == 0)
				{
					outputs = Array.Empty<uint>();
					return true;
				}

				if (threadOutputScratch == null || threadOutputScratch.Length < outputCount)
				{
					threadOutputScratch = new uint[outputCount];
				}

				int baseIndex = key * outputCount;
				for (int i = 0; i < outputCount; i++)
				{
					threadOutputScratch[i] = lut[baseIndex + i];
				}

				outputs = threadOutputScratch;
				return true;
			}

			// A tristated input is outside the binary LUT. Evaluate that single case normally.
			if (outputCount == 0)
			{
				Simulator.EvaluatePureCombinationalForMemo(chip);
				outputs = Array.Empty<uint>();
				return false;
			}

			if (threadPreviousOutputs == null || threadPreviousOutputs.Length < outputCount)
			{
				threadPreviousOutputs = new uint[outputCount];
			}
			if (threadOutputScratch == null || threadOutputScratch.Length < outputCount)
			{
				threadOutputScratch = new uint[outputCount];
			}

			for (int i = 0; i < outputCount; i++)
			{
				threadPreviousOutputs[i] = chip.OutputPins[i].State;
			}

			Simulator.EvaluatePureCombinationalForMemo(chip);

			for (int i = 0; i < outputCount; i++)
			{
				threadOutputScratch[i] = chip.OutputPins[i].State;
				chip.OutputPins[i].State = threadPreviousOutputs[i];
			}

			outputs = threadOutputScratch;
			return false;
		}

		void SetBinaryInputs(SimChip chip, int key)
		{
			int offset = 0;

			for (int i = 0; i < inputWidths.Length; i++)
			{
				int width = inputWidths[i];
				uint mask = (1u << width) - 1u;
				ushort bits = (ushort)(((uint)key >> offset) & mask);
				uint state = 0;
				PinState.Set(ref state, bits, 0);
				chip.InputPins[i].State = state;
				offset += width;
			}
		}

		bool TryBuildBinaryKey(SimChip chip, out int key)
		{
			key = 0;
			if (chip.InputPins.Length != inputWidths.Length) return false;

			int offset = 0;
			for (int i = 0; i < inputWidths.Length; i++)
			{
				int width = inputWidths[i];
				uint mask = (1u << width) - 1u;
				ushort tri = PinState.GetTristateFlags(chip.InputPins[i].State);
				if ((tri & mask) != 0) return false;

				uint bits = (uint)PinState.GetBitStates(chip.InputPins[i].State) & mask;
				key |= (int)(bits << offset);
				offset += width;
			}

			return true;
		}
	}
}
