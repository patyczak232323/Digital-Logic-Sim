using System;
using System.Collections.Generic;
using DLS.Description;

namespace DLS.Simulation
{
	public readonly struct WaveformSample
	{
		public readonly int Frame;
		public readonly uint State;

		public WaveformSample(int frame, uint state)
		{
			Frame = frame;
			State = state;
		}
	}

	public readonly struct WaveformProbeInfo
	{
		public readonly int Id;
		public readonly string Name;
		public readonly int SampleCount;
		public readonly int BitCount;

		public WaveformProbeInfo(int id, string name, int sampleCount, int bitCount)
		{
			Id = id;
			Name = name;
			SampleCount = sampleCount;
			BitCount = bitCount;
		}
	}

	// Ring-buffer waveform backend. UI can attach probes to any live SimPin without
	// changing simulation semantics. Recording is opt-in and allocation-free per sample.
	public static class SimulationWaveformRecorder
	{
		sealed class Probe
		{
			public readonly int Id;
			public readonly string Name;
			public readonly SimPin Pin;
			public readonly int BitCount;
			public WaveformSample[] Samples;
			public int WriteIndex;
			public int Count;

			public Probe(int id, string name, SimPin pin, int capacity)
			{
				Id = id;
				Name = name;
				Pin = pin;
				BitCount = ResolveBitCount(pin);
				Samples = new WaveformSample[capacity];
			}
		}

		static readonly object sync = new();
		static readonly Dictionary<int, Probe> probes = new();
		static volatile bool enabled;
		static int nextId = 1;
		static int capacity = 4096;

		public static bool Enabled
		{
			get => enabled;
			set => enabled = value;
		}

		public static int Capacity
		{
			get => capacity;
			set
			{
				int newCapacity = Math.Max(16, value);
				lock (sync)
				{
					if (newCapacity == capacity) return;
					capacity = newCapacity;
					foreach (Probe probe in probes.Values)
					{
						ResizeProbe(probe, newCapacity);
					}
				}
			}
		}

		public static int AddProbe(string name, SimPin pin)
		{
			if (pin == null) throw new ArgumentNullException(nameof(pin));

			lock (sync)
			{
				foreach (Probe existing in probes.Values)
				{
					if (ReferenceEquals(existing.Pin, pin)) return existing.Id;
				}

				int id = nextId++;
				probes.Add(id, new Probe(id, string.IsNullOrWhiteSpace(name) ? $"PIN {pin.ID}" : name, pin, capacity));
				DeterministicSimulator.InvalidateTopology();
				return id;
			}
		}

		public static bool TryGetProbeId(SimPin pin, out int id)
		{
			if (pin != null)
			{
				lock (sync)
				{
					foreach (Probe probe in probes.Values)
					{
						if (!ReferenceEquals(probe.Pin, pin)) continue;
						id = probe.Id;
						return true;
					}
				}
			}

			id = -1;
			return false;
		}

		public static int AddProbe(string name, SimChip root, PinAddress address)
		{
			if (root == null) throw new ArgumentNullException(nameof(root));
			return AddProbe(name, root.GetSimPinFromAddress(address));
		}

		public static bool RemoveProbe(int id)
		{
			lock (sync)
			{
				bool removed = probes.Remove(id);
				if (removed) DeterministicSimulator.InvalidateTopology();
				return removed;
			}
		}

		public static void ClearSamples()
		{
			lock (sync)
			{
				foreach (Probe probe in probes.Values)
				{
					probe.WriteIndex = 0;
					probe.Count = 0;
				}
			}
		}

		public static void ClearAll()
		{
			lock (sync)
			{
				bool hadProbes = probes.Count > 0;
				probes.Clear();
				nextId = 1;
				if (hadProbes) DeterministicSimulator.InvalidateTopology();
			}
		}

		internal static bool ContainsProbeInSubtree(SimChip chip)
		{
			if (chip == null) return false;

			lock (sync)
			{
				foreach (Probe probe in probes.Values)
				{
					for (SimChip owner = probe.Pin?.parentChip; owner != null; owner = owner.ParentChip)
					{
						if (ReferenceEquals(owner, chip)) return true;
					}
				}
			}

			return false;
		}

		internal static void PruneToRoot(SimChip root)
		{
			lock (sync)
			{
				if (probes.Count == 0) return;
				if (root == null)
				{
					probes.Clear();
					return;
				}

				HashSet<SimPin> livePins = new();
				CollectPins(root, livePins);

				List<int> removeIds = null;
				foreach (KeyValuePair<int, Probe> pair in probes)
				{
					if (livePins.Contains(pair.Value.Pin)) continue;
					(removeIds ??= new List<int>()).Add(pair.Key);
				}

				if (removeIds == null) return;
				for (int i = 0; i < removeIds.Count; i++) probes.Remove(removeIds[i]);
			}

			static void CollectPins(SimChip chip, HashSet<SimPin> pins)
			{
				for (int i = 0; i < chip.InputPins.Length; i++) pins.Add(chip.InputPins[i]);
				for (int i = 0; i < chip.OutputPins.Length; i++) pins.Add(chip.OutputPins[i]);
				for (int i = 0; i < chip.SubChips.Length; i++)
				{
					if (chip.SubChips[i] != null) CollectPins(chip.SubChips[i], pins);
				}
			}
		}

		internal static void Capture(int frame)
		{
			if (!enabled) return;

			lock (sync)
			{
				foreach (Probe probe in probes.Values)
				{
					WaveformSample[] samples = probe.Samples;
					samples[probe.WriteIndex] = new WaveformSample(frame, probe.Pin.State);
					probe.WriteIndex++;
					if (probe.WriteIndex == samples.Length) probe.WriteIndex = 0;
					if (probe.Count < samples.Length) probe.Count++;
				}
			}
		}

		public static WaveformProbeInfo[] GetProbes()
		{
			lock (sync)
			{
				WaveformProbeInfo[] result = new WaveformProbeInfo[probes.Count];
				int index = 0;
				foreach (Probe probe in probes.Values)
				{
					result[index++] = new WaveformProbeInfo(probe.Id, probe.Name, probe.Count, probe.BitCount);
				}
				Array.Sort(result, (a, b) => a.Id.CompareTo(b.Id));
				return result;
			}
		}

		public static WaveformSample[] GetSamples(int id)
		{
			lock (sync)
			{
				if (!probes.TryGetValue(id, out Probe probe) || probe.Count == 0)
				{
					return Array.Empty<WaveformSample>();
				}

				WaveformSample[] result = new WaveformSample[probe.Count];
				int start = probe.Count == probe.Samples.Length ? probe.WriteIndex : 0;
				for (int i = 0; i < probe.Count; i++)
				{
					result[i] = probe.Samples[(start + i) % probe.Samples.Length];
				}
				return result;
			}
		}

		static int ResolveBitCount(SimPin pin)
		{
			ChipDescription description = pin?.parentChip?.Description;
			if (description == null) return 1;

			PinDescription[] descriptions = pin.isInput
				? description.InputPins ?? Array.Empty<PinDescription>()
				: description.OutputPins ?? Array.Empty<PinDescription>();

			for (int i = 0; i < descriptions.Length; i++)
			{
				if (descriptions[i].ID == pin.ID) return Math.Max(1, (int)descriptions[i].BitCount);
			}

			return 1;
		}

		static void ResizeProbe(Probe probe, int newCapacity)
		{
			WaveformSample[] ordered = new WaveformSample[probe.Count];
			int start = probe.Count == probe.Samples.Length ? probe.WriteIndex : 0;
			for (int i = 0; i < probe.Count; i++)
			{
				ordered[i] = probe.Samples[(start + i) % probe.Samples.Length];
			}

			int keep = Math.Min(ordered.Length, newCapacity);
			WaveformSample[] resized = new WaveformSample[newCapacity];
			int sourceStart = ordered.Length - keep;
			for (int i = 0; i < keep; i++) resized[i] = ordered[sourceStart + i];

			probe.Samples = resized;
			probe.Count = keep;
			probe.WriteIndex = keep == newCapacity ? 0 : keep;
		}
	}
}
