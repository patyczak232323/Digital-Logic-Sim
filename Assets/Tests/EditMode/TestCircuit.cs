using System;
using System.Collections.Generic;
using System.Linq;
using DLS.Description;
using DLS.Game;
using DLS.Simulation;
using UnityEngine;

namespace DLS.Tests
{
	// Drives a chip through the public RewiredEngine boundary exactly like the editor:
	// dev input pins feed the root SimChip and root output pins are read back after each step.
	public sealed class TestCircuit
	{
		public readonly ChipDescription Description;
		public readonly SimChip Root;
		readonly DevPinInstance[] inputs;

		public TestCircuit(ChipDescription description, ChipLibrary library)
		{
			Description = description;
			RewiredEngine.Reset();
			RewiredEngine.StepsPerClockTransition = 0;
			Root = RewiredEngine.BuildChip(description, library);
			inputs = description.InputPins.Select(p => new DevPinInstance(p, true)).ToArray();
			RewiredEngine.EnsureInitialized(Root, inputs, null);
		}

		public RewiredEngine.DiagnosticsSnapshot Diagnostics => RewiredEngine.Diagnostics;

		public void Set(string inputName, uint value)
		{
			DevPinInstance pin = inputs.FirstOrDefault(p => p.Name == inputName);
			if (pin == null) throw new ArgumentException($"No input pin named '{inputName}' on {Description.Name}");
			pin.Pin.PlayerInputState = value;
		}

		public uint Get(string outputName)
		{
			PinDescription[] outputs = Description.OutputPins;
			int index = Array.FindIndex(outputs, p => p.Name == outputName);
			if (index < 0) throw new ArgumentException($"No output pin named '{outputName}' on {Description.Name}");

			int id = outputs[index].ID;
			SimPin pin = Root.OutputPins.First(p => p.ID == id);
			return PinState.GetBitStates(pin.State);
		}

		public void Step(int count = 1)
		{
			for (int i = 0; i < count; i++)
			{
				RewiredEngine.RunStep(Root, inputs, null);
			}
		}

		// One full clock cycle: low phase, then the rising edge.
		public void Tick(string clockName = "CLK")
		{
			Set(clockName, 0);
			Step();
			Set(clockName, 1);
			Step();
		}
	}

	// Minimal programmatic chip author for gate-level engine tests.
	public sealed class ChipBuilder
	{
		public const int NandInB = 0;
		public const int NandInA = 1;
		public const int NandOut = 2;

		readonly string name;
		readonly List<PinDescription> inputPins = new();
		readonly List<PinDescription> outputPins = new();
		readonly List<SubChipDescription> subChips = new();
		readonly List<WireDescription> wires = new();
		int nextId = 1;

		public ChipBuilder(string name) => this.name = name;

		public PinAddress Input(string pinName, PinBitCount bits = PinBitCount.Bit1) =>
			AddPin(inputPins, pinName, bits);

		public PinAddress Output(string pinName, PinBitCount bits = PinBitCount.Bit1) =>
			AddPin(outputPins, pinName, bits);

		public int Chip(string chipName)
		{
			int id = nextId++;
			subChips.Add(new SubChipDescription(chipName, id, string.Empty, Vector2.zero, Array.Empty<OutputPinColourInfo>()));
			return id;
		}

		public int Nand(PinAddress a, PinAddress b)
		{
			int id = Chip(ChipTypeHelper.GetName(ChipType.Nand));
			Wire(a, new PinAddress(id, NandInA));
			Wire(b, new PinAddress(id, NandInB));
			return id;
		}

		public static PinAddress Pin(int chipId, int pinId) => new(chipId, pinId);
		public static PinAddress NandOutput(int nandId) => new(nandId, NandOut);

		public void Wire(PinAddress source, PinAddress target)
		{
			wires.Add(new WireDescription
			{
				SourcePinAddress = source,
				TargetPinAddress = target,
				ConnectionType = WireConnectionType.ToPins,
				ConnectedWireIndex = -1,
				ConnectedWireSegmentIndex = -1,
				Points = Array.Empty<Vector2>()
			});
		}

		public ChipDescription Build() => new()
		{
			Name = name,
			ChipType = ChipType.Custom,
			Size = Vector2.one,
			Colour = Color.white,
			InputPins = inputPins.ToArray(),
			OutputPins = outputPins.ToArray(),
			SubChips = subChips.ToArray(),
			Wires = wires.ToArray(),
			Displays = Array.Empty<DisplayDescription>()
		};

		PinAddress AddPin(List<PinDescription> list, string pinName, PinBitCount bits)
		{
			int id = nextId++;
			list.Add(new PinDescription(pinName, id, Vector2.zero, bits, PinColour.Red, PinValueDisplayMode.Off));
			return new PinAddress(id, 0);
		}
	}

	public static class TestLibrary
	{
		public static ChipLibrary Create(params ChipDescription[] customChips) =>
			new(customChips, Array.Empty<ChipDescription>(), BuiltinChipCreator.CreateAllBuiltinChipDescriptions());
	}
}
