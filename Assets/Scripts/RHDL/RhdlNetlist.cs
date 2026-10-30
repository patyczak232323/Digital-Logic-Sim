using System.Collections.Generic;
using DLS.Description;

namespace DLS.RHDL
{
	// Bit-level netlist produced by elaboration. Every signal bit is a Net. A Net is either
	// driven by something (a NAND gate, a bit of an input pin or component output, a
	// constant) or is an alias of another net, created when a named signal is assigned.

	internal enum NetKind
	{
		// A named signal bit that has not been assigned yet, or an alias once it has been.
		Placeholder,
		Const0,
		Const1,
		Nand,
		// Bit of a root input pin.
		PortBit,
		// Bit of a component output pin.
		PinBit,
		// A wire with several tri-state drivers.
		Junction
	}

	internal sealed class Net
	{
		public readonly int Id;
		public NetKind Kind;
		public Net Alias;

		public NandCell Nand;
		public ComponentCell Component;
		public PortSignal Port;
		public int Pin;
		public int Bit;

		// Placeholders: the signal bit this stands for, for messages.
		public string Label;
		public int Line;
		public readonly List<Net> Drivers = new();

		public Net(int id, NetKind kind)
		{
			Id = id;
			Kind = kind;
		}
	}

	internal abstract class Cell
	{
		// Index of the statement that created the cell, after loops are unrolled. The layout
		// keeps the gates of one statement together, in the order they were written.
		public int Statement;
		public int Line;
		// Bit of the statement's result this gate belongs to, or -1. Gates of one bit of a
		// bus operation are kept on one row.
		public int Slice = -1;
		public bool Alive = true;
	}

	internal sealed class NandCell : Cell
	{
		public Net A;
		public Net B;
		public Net Out;
		// Written by the user as NAND(...). Never folded or merged with other gates, so
		// latches and other circuits that rely on individual gates stay exactly as written.
		public bool Explicit;
	}

	internal sealed class ComponentCell : Cell
	{
		public string Name;
		public ChipDescription Description;
		public Net[][] Inputs;
		public int[] InputLines;
		public Net[][] Outputs;
	}

	internal sealed class PortSignal
	{
		public string Name;
		public int Width;
		public bool IsInput;
		public int Line;
		public Net[] Bits;
	}

	internal sealed class Netlist
	{
		public readonly List<Net> Nets = new();
		public readonly List<NandCell> Nands = new();
		public readonly List<ComponentCell> Components = new();
		public readonly List<PortSignal> Inputs = new();
		public readonly List<PortSignal> Outputs = new();
		public readonly Net Const0;
		public readonly Net Const1;

		public Netlist()
		{
			Const0 = NewNet(NetKind.Const0);
			Const1 = NewNet(NetKind.Const1);
		}

		public Net NewNet(NetKind kind)
		{
			Net net = new(Nets.Count, kind);
			Nets.Add(net);
			return net;
		}

		// Follows aliases to the net that actually drives this one.
		public static Net Find(Net net)
		{
			Net root = net;
			while (root.Alias != null) root = root.Alias;

			while (net.Alias != null)
			{
				Net next = net.Alias;
				net.Alias = root;
				net = next;
			}

			return root;
		}
	}
}
