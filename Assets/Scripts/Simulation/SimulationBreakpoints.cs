using System;
using System.Collections.Generic;

namespace DLS.Simulation
{
	public enum BreakCondition
	{
		Change,
		Rising,
		Falling,
		Equals
	}

	public sealed class Breakpoint
	{
		public readonly string Label;
		public readonly SimPin Pin;
		public readonly BreakCondition Condition;
		public readonly uint Value;
		public readonly int BitCount;

		// The sim tree the pin belongs to. A rebuilt simulation has new pins, so a breakpoint
		// from an older tree is dropped instead of watching a pin nothing drives any more.
		internal readonly SimChip Root;
		internal uint lastBits;
		internal bool primed;

		public Breakpoint(string label, SimPin pin, SimChip root, int bitCount, BreakCondition condition, uint value)
		{
			Label = label ?? string.Empty;
			Pin = pin;
			Root = root;
			BitCount = Math.Clamp(bitCount, 1, 16);
			Condition = condition;
			Value = value & Mask;
		}

		uint Mask => BitCount >= 16 ? 0xFFFFu : (1u << BitCount) - 1;

		public string Describe() => Condition switch
		{
			BreakCondition.Change => $"{Label} changes",
			BreakCondition.Rising => $"{Label} rises",
			BreakCondition.Falling => $"{Label} falls",
			_ => $"{Label} = {Value}"
		};

		// Edges compare against the value after the previous step; "equals" fires when the
		// pin reaches the value, not on every step it stays there, so resuming continues.
		internal bool Evaluate()
		{
			uint bits = PinState.GetBitStates(Pin.State) & Mask;
			if (!primed)
			{
				primed = true;
				lastBits = bits;
				return false;
			}

			uint previous = lastBits;
			lastBits = bits;
			return Condition switch
			{
				BreakCondition.Change => bits != previous,
				BreakCondition.Rising => (previous & 1) == 0 && (bits & 1) == 1,
				BreakCondition.Falling => (previous & 1) == 1 && (bits & 1) == 0,
				_ => bits == Value && previous != Value
			};
		}
	}

	// Breakpoints are set from the main thread and checked on the simulation thread after
	// every step, so a condition that only holds between two frames still pauses the sim.
	public static class SimulationBreakpoints
	{
		static readonly object sync = new();
		static readonly List<Breakpoint> breakpoints = new();

		public static int Count
		{
			get
			{
				lock (sync) return breakpoints.Count;
			}
		}

		public static void Set(Breakpoint breakpoint)
		{
			lock (sync)
			{
				breakpoints.RemoveAll(b => b.Pin == breakpoint.Pin);
				breakpoints.Add(breakpoint);
			}
		}

		public static bool Remove(SimPin pin)
		{
			lock (sync) return breakpoints.RemoveAll(b => b.Pin == pin) > 0;
		}

		public static Breakpoint Find(SimPin pin)
		{
			lock (sync) return breakpoints.Find(b => b.Pin == pin);
		}

		public static void Clear()
		{
			lock (sync) breakpoints.Clear();
		}

		public static Breakpoint[] Snapshot()
		{
			lock (sync) return breakpoints.ToArray();
		}

		// Returns the first breakpoint whose condition became true in the step that just ran.
		// Every breakpoint is evaluated so all of them track the latest value.
		public static Breakpoint Check(SimChip currentRoot)
		{
			lock (sync)
			{
				if (breakpoints.Count == 0) return null;
				breakpoints.RemoveAll(b => b.Root != currentRoot);

				Breakpoint hit = null;
				foreach (Breakpoint b in breakpoints)
				{
					if (b.Evaluate() && hit == null) hit = b;
				}

				return hit;
			}
		}
	}
}
