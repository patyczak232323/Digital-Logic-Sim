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

		public WaveformProbeInfo(int id, string name, int sampleCount)
		{
			Id = id;
			Name = name;
			SampleCount = sampleCount;
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
			public WaveformSample[] Samples;
			public int WriteIndex;
			public int Count;

			public Probe(int id, string name, SimPin pin, int capacity)
			{
				Id = id;
				Name = name;
				Pin = pin;
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
			lock (sync) return probes.Remove(id);
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
				probes.Clear();
				nextId = 1;
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
					result[index++] = new WaveformProbeInfo(probe.Id, probe.Name, probe.Count);
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
