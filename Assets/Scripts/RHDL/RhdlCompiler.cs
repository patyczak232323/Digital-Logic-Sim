using System;
using System.Collections.Generic;
using System.Linq;
using DLS.Description;
using DLS.Game;
using UnityEngine;

namespace DLS.RHDL
{
	public sealed class RhdlDiagnostic
	{
		public readonly int Line;
		public readonly int Column;
		public readonly string Message;

		public RhdlDiagnostic(int line, string message) : this(line, 0, message) { }

		public RhdlDiagnostic(int line, int column, string message)
		{
			Line = line;
			Column = column;
			Message = message;
		}

		public override string ToString()
		{
			if (Line <= 0) return Message;
			return Column > 0 ? $"line {Line}, col {Column}: {Message}" : $"line {Line}: {Message}";
		}
	}

	public sealed class RhdlCompileResult
	{
		public readonly ChipDescription Description;
		public readonly RhdlDiagnostic[] Diagnostics;
		public bool Success => Description != null && Diagnostics.Length == 0;

		public RhdlCompileResult(ChipDescription description, List<RhdlDiagnostic> diagnostics)
		{
			Description = description;
			Diagnostics = diagnostics.ToArray();
		}
	}

	// Compiles RHDL source into a chip made of NAND gates and other chips:
	// parse (RhdlSyntax) -> netlist of bits (RhdlElaborator) -> chip parts and wires
	// (this file) -> schematic layout (RhdlLayout).
	public static class RhdlCompiler
	{
		public const string LanguageVersion = "1.0";
		public const int MaxExpressionNesting = RhdlParser.MaxNesting;

		// Parsing and elaboration recurse over expressions and over wires used before the line
		// that assigns them. A stack overflow cannot be caught and would close Rewired, so the
		// compiler runs on its own thread with a large stack. WebGL has no threads.
		const int CompilerStackBytes = 64 * 1024 * 1024;

		public static RhdlCompileResult Compile(string source, ChipLibrary library)
		{
#if UNITY_WEBGL && !UNITY_EDITOR
			return CompileOnThisThread(source, library);
#else
			RhdlCompileResult result = null;
			Exception failure = null;
			System.Threading.Thread thread = new(() =>
			{
				try
				{
					result = CompileOnThisThread(source, library);
				}
				catch (Exception e)
				{
					failure = e;
				}
			}, CompilerStackBytes) { Name = "RHDL compiler", IsBackground = true };
			thread.Start();
			thread.Join();
			if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
			return result;
#endif
		}

		static RhdlCompileResult CompileOnThisThread(string source, ChipLibrary library)
		{
			List<RhdlDiagnostic> diagnostics = new();
			if (library == null)
			{
				diagnostics.Add(new RhdlDiagnostic(0, "No active chip library."));
				return new RhdlCompileResult(null, diagnostics);
			}

			ModuleSyntax module;
			try
			{
				module = RhdlParser.Parse(source);
			}
			catch (RhdlSyntaxException e)
			{
				diagnostics.Add(new RhdlDiagnostic(e.Line, e.Column, e.Message));
				if (LooksLikeVersion06(source))
				{
					diagnostics.Add(new RhdlDiagnostic(0, "This source uses the RHDL 0.6 syntax. RHDL 1.0 uses Python syntax: " +
					                                      "see 'Coming from RHDL 0.6' in Docs/RHDL_GUIDE.md."));
				}

				return new RhdlCompileResult(null, diagnostics);
			}

			if (module.Circuits.Count == 0)
			{
				diagnostics.Add(new RhdlDiagnostic(1, "No circuit found. Start with a line such as 'circuit Name(A, B) -> Y:'."));
				return new RhdlCompileResult(null, diagnostics);
			}

			if (module.Circuits.Count > 1)
			{
				CircuitSyntax second = module.Circuits[1];
				diagnostics.Add(new RhdlDiagnostic(second.Line, second.Column, "Write one circuit per source. Build this one first, then use it by name from the next."));
				return new RhdlCompileResult(null, diagnostics);
			}

			Dictionary<string, long> constants = new(StringComparer.Ordinal);
			foreach (AssignStatement constant in module.Constants)
			{
				string name = ((NameExpr)constant.Targets[0]).Name;
				if (constants.ContainsKey(name))
				{
					diagnostics.Add(new RhdlDiagnostic(constant.Line, constant.Column, $"Constant '{name}' is defined twice."));
					continue;
				}

				if (!RhdlElaborator.TryEvaluateModuleConstant(constant.Value, constants, out long value, out string error))
				{
					diagnostics.Add(new RhdlDiagnostic(constant.Line, constant.Column, error));
					continue;
				}

				constants[name] = value;
			}

			if (diagnostics.Count > 0) return new RhdlCompileResult(null, diagnostics);

			CircuitSyntax circuit = module.Circuits[0];
			if (library.TryGetChipDescription(circuit.Name, out ChipDescription existing) && library.IsBuiltinChip(existing.Name))
			{
				diagnostics.Add(new RhdlDiagnostic(circuit.Line, circuit.Column, $"'{circuit.Name}' is the name of a built-in chip. Choose another name."));
				return new RhdlCompileResult(null, diagnostics);
			}

			Netlist netlist = RhdlElaborator.Elaborate(circuit, constants, library, diagnostics);
			if (netlist == null || diagnostics.Count > 0) return new RhdlCompileResult(null, diagnostics);

			try
			{
				ChipDescription description = new Emitter(netlist, library).Emit(circuit.Name);
				return new RhdlCompileResult(description, diagnostics);
			}
			catch (Exception e)
			{
				diagnostics.Add(new RhdlDiagnostic(0, $"Internal RHDL error while drawing the chip ({e.GetType().Name}: {e.Message} at {FirstFrame(e)}). Please report this source as a bug."));
				return new RhdlCompileResult(null, diagnostics);
			}
		}

		// Where an unexpected exception was thrown, for bug reports.
		internal static string FirstFrame(Exception e)
		{
			string trace = e.StackTrace ?? string.Empty;
			foreach (string line in trace.Split('\n'))
			{
				if (line.Contains("RHDL")) return line.Trim();
			}

			return trace.Split('\n')[0].Trim();
		}

		static readonly System.Text.RegularExpressions.Regex Version06Line = new(
			@"^\s*(input|output|signal|constant|component|connect)\s|^\s*end\s*$",
			System.Text.RegularExpressions.RegexOptions.Multiline);

		static bool LooksLikeVersion06(string source) => Version06Line.IsMatch(source ?? string.Empty);

		// Turns the bit netlist into chip parts and wires: multi-bit pins are split into bits
		// or merged from bits only where the logic needs single bits.
		sealed class Emitter
		{
			readonly Netlist netlist;
			readonly ChipLibrary library;
			readonly List<LayoutNode> nodes = new();
			readonly List<LayoutEdge> edges = new();
			readonly HashSet<(LayoutNode, int, LayoutNode, int)> edgeSet = new();
			readonly List<Part> parts = new();
			readonly Dictionary<PortSignal, LayoutNode> portNodes = new();
			readonly Dictionary<NandCell, LayoutNode> nandNodes = new();
			readonly Dictionary<ComponentCell, LayoutNode> componentNodes = new();
			readonly Dictionary<(LayoutNode, int), LayoutNode> splits = new();
			readonly Dictionary<(LayoutNode, int), LayoutNode> nibbleSplits = new();
			readonly Dictionary<string, LayoutNode> merges = new();
			readonly Dictionary<LayoutNode, Part> partOf = new();
			LayoutNode constOne;
			LayoutNode constZero;

			sealed class Part
			{
				public LayoutNode Node;
				public ChipDescription Description;
			}

			readonly struct Endpoint
			{
				public readonly LayoutNode Node;
				public readonly int Pin;

				public Endpoint(LayoutNode node, int pin)
				{
					Node = node;
					Pin = pin;
				}
			}

			public Emitter(Netlist netlist, ChipLibrary library)
			{
				this.netlist = netlist;
				this.library = library;
			}

			public ChipDescription Emit(string chipName)
			{
				foreach (PortSignal port in netlist.Inputs) portNodes[port] = PortNode(port);
				foreach (PortSignal port in netlist.Outputs) portNodes[port] = PortNode(port);

				foreach (NandCell cell in netlist.Nands)
				{
					LayoutNode node = ChipNode(Builtin(ChipType.Nand), cell.Statement, cell.Slice);
					nandNodes[cell] = node;
				}

				foreach (ComponentCell cell in netlist.Components)
				{
					componentNodes[cell] = ChipNode(cell.Description, cell.Statement, -1);
				}

				// NAND inputs: pin 0 is IN B, pin 1 is IN A.
				foreach (NandCell cell in netlist.Nands)
				{
					LayoutNode node = nandNodes[cell];
					ConnectBit(cell.A, node, 1, cell.Statement);
					ConnectBit(cell.B, node, 0, cell.Statement);
				}

				foreach (ComponentCell cell in netlist.Components)
				{
					LayoutNode node = componentNodes[cell];
					for (int pin = 0; pin < cell.Inputs.Length; pin++)
					{
						Connect(cell.Inputs[pin], node, pin, cell.Statement);
					}
				}

				foreach (PortSignal port in netlist.Outputs)
				{
					Connect(port.Bits, portNodes[port], 0, int.MaxValue);
				}

				RhdlLayout.Arrange(nodes, edges);
				return Build(chipName);
			}

			ChipDescription Builtin(ChipType type) => library.GetChipDescription(ChipTypeHelper.GetName(type));

			// ---- Nodes ----

			LayoutNode PortNode(PortSignal port)
			{
				// A pin's connection point sits to the side of its handle: 6 grid squares away
				// for 1 and 4 bits, 9 for 8 bits (DevPinInstance.PinPosition).
				float reach = PortReach(port.Width);
				LayoutNode node = new()
				{
					Width = reach + 0.25f,
					Height = 0.5f,
					IsInputPort = port.IsInput,
					IsOutputPort = !port.IsInput,
					Statement = -1
				};

				Vector2 pin = new(port.IsInput ? node.Width / 2 : -node.Width / 2, 0);
				if (port.IsInput) node.OutputPins = new[] { pin };
				else node.InputPins = new[] { pin };
				nodes.Add(node);
				return node;
			}

			static float PortReach(int width) => 0.125f * (width == 8 ? 9 : 6);

			LayoutNode ChipNode(ChipDescription description, int statement, int slice)
			{
				LayoutNode node = new()
				{
					Width = description.Size.x + 0.05f,
					Height = description.Size.y,
					InputPins = PinOffsets(description, description.InputPins, -1),
					OutputPins = PinOffsets(description, description.OutputPins, 1),
					Statement = statement,
					Slice = slice
				};

				nodes.Add(node);
				Part part = new() { Node = node, Description = description };
				parts.Add(part);
				partOf[node] = part;
				return node;
			}

			// Same rules as SubChipInstance.CalculatePinLayout and PinInstance.GetWorldPos.
			static Vector2[] PinOffsets(ChipDescription description, PinDescription[] pins, int side)
			{
				const float grid = 0.125f;
				const float outlineWidth = 0.05f;
				const float pinInset = 0.015f;
				Vector2[] result = new Vector2[pins.Length];
				float x = side * (description.Size.x / 2 + outlineWidth / 2 - pinInset);
				if (pins.Length == 1)
				{
					result[0] = new Vector2(x, 0);
					return result;
				}

				int gridY = 0;
				float[] pinGridY = new float[pins.Length];
				for (int i = 0; i < pins.Length; i++)
				{
					int height = pins[i].BitCount switch
					{
						PinBitCount.Bit1 => 2,
						PinBitCount.Bit4 => 3,
						_ => 4
					};

					pinGridY[i] = gridY - height / 2f;
					gridY -= height;
				}

				float chipHeight = Math.Abs(gridY) * grid;
				float spacing = description.Size.y > chipHeight ? (description.Size.y - chipHeight) / (pins.Length - 1) : 0;
				for (int i = 0; i < pins.Length; i++)
				{
					result[i] = new Vector2(x, description.Size.y / 2 + pinGridY[i] * grid - spacing * i);
				}

				return result;
			}

			// ---- Connections ----

			void AddEdge(Endpoint source, LayoutNode target, int targetPin)
			{
				if (!edgeSet.Add((source.Node, source.Pin, target, targetPin))) return;
				edges.Add(new LayoutEdge { Source = source.Node, SourcePin = source.Pin, Target = target, TargetPin = targetPin });
			}

			void ConnectBit(Net net, LayoutNode target, int targetPin, int statement)
			{
				foreach (Endpoint source in BitSources(net, statement)) AddEdge(source, target, targetPin);
			}

			void Connect(Net[] bits, LayoutNode target, int targetPin, int statement)
			{
				if (bits.Length == 1)
				{
					ConnectBit(bits[0], target, targetPin, statement);
					return;
				}

				foreach (Endpoint source in BusSources(bits, statement)) AddEdge(source, target, targetPin);
			}

			List<Endpoint> BitSources(Net net, int statement)
			{
				Net root = Netlist.Find(net);
				switch (root.Kind)
				{
					case NetKind.Const1:
						return new List<Endpoint> { new(ConstOne(), 0) };

					case NetKind.Const0:
						return new List<Endpoint> { new(ConstZero(), 0) };

					case NetKind.Nand:
						return new List<Endpoint> { new(nandNodes[root.Nand], 0) };

					case NetKind.PortBit:
					{
						LayoutNode port = portNodes[root.Port];
						if (root.Port.Width == 1) return new List<Endpoint> { new(port, 0) };
						return new List<Endpoint> { SplitBit(new Endpoint(port, 0), root.Port.Width, root.Bit, statement) };
					}

					case NetKind.PinBit:
					{
						LayoutNode component = componentNodes[root.Component];
						int width = root.Component.Outputs[root.Pin].Length;
						if (width == 1) return new List<Endpoint> { new(component, root.Pin) };
						return new List<Endpoint> { SplitBit(new Endpoint(component, root.Pin), width, root.Bit, statement) };
					}

					case NetKind.Junction:
						return root.Drivers.SelectMany(d => BitSources(d, statement)).ToList();

					default:
						return new List<Endpoint> { new(ConstZero(), 0) };
				}
			}

			LayoutNode ConstOne()
			{
				// A NAND with nothing connected outputs 1.
				return constOne ??= ChipNode(Builtin(ChipType.Nand), -1, -1);
			}

			LayoutNode ConstZero()
			{
				if (constZero != null) return constZero;
				constZero = ChipNode(Builtin(ChipType.Nand), -1, -1);
				AddEdge(new Endpoint(ConstOne(), 0), constZero, 0);
				AddEdge(new Endpoint(ConstOne(), 0), constZero, 1);
				return constZero;
			}

			// Output pins of the split chips list the most significant bit first.
			Endpoint SplitBit(Endpoint bus, int width, int bit, int statement)
			{
				if (!splits.TryGetValue((bus.Node, bus.Pin), out LayoutNode split))
				{
					split = ChipNode(Builtin(width == 4 ? ChipType.Split_4To1Bit : ChipType.Split_8To1Bit), StatementOf(bus.Node, statement), -1);
					splits[(bus.Node, bus.Pin)] = split;
					AddEdge(bus, split, 0);
				}

				return new Endpoint(split, width - 1 - bit);
			}

			int StatementOf(LayoutNode node, int fallback) => node.IsInputPort ? Math.Min(fallback, 0) : node.Statement;

			List<Endpoint> BusSources(Net[] bits, int statement)
			{
				Net[] roots = bits.Select(Netlist.Find).ToArray();
				if (TryWholeBus(roots, out Endpoint whole)) return new List<Endpoint> { whole };
				if (roots.Length == 4 && TryNibble(roots, statement, out Endpoint nibble)) return new List<Endpoint> { nibble };

				string key = string.Join(",", roots.Select(r => r.Id));
				if (merges.TryGetValue(key, out LayoutNode merge)) return new List<Endpoint> { new(merge, 0) };

				if (roots.Length == 8 &&
				    TryFourBits(roots.Take(4).ToArray(), statement, out Endpoint low) &&
				    TryFourBits(roots.Skip(4).ToArray(), statement, out Endpoint high))
				{
					merge = ChipNode(Builtin(ChipType.Merge_4To8Bit), statement, -1);
					AddEdge(high, merge, 0);
					AddEdge(low, merge, 1);
				}
				else
				{
					merge = ChipNode(Builtin(roots.Length == 4 ? ChipType.Merge_1To4Bit : ChipType.Merge_1To8Bit), statement, -1);
					for (int pin = 0; pin < roots.Length; pin++)
					{
						ConnectBit(roots[roots.Length - 1 - pin], merge, pin, statement);
					}
				}

				merges[key] = merge;
				return new List<Endpoint> { new(merge, 0) };
			}

			bool TryFourBits(Net[] roots, int statement, out Endpoint endpoint) =>
				TryWholeBus(roots, out endpoint) || TryNibble(roots, statement, out endpoint);

			bool TryWholeBus(Net[] roots, out Endpoint endpoint)
			{
				endpoint = default;
				if (!TryBusBits(roots, out LayoutNode node, out int pin, out int busWidth, out int offset)) return false;
				if (offset != 0 || busWidth != roots.Length) return false;
				endpoint = new Endpoint(node, pin);
				return true;
			}

			bool TryNibble(Net[] roots, int statement, out Endpoint endpoint)
			{
				endpoint = default;
				if (!TryBusBits(roots, out LayoutNode node, out int pin, out int busWidth, out int offset)) return false;
				if (busWidth != 8 || (offset != 0 && offset != 4)) return false;

				if (!nibbleSplits.TryGetValue((node, pin), out LayoutNode split))
				{
					split = ChipNode(Builtin(ChipType.Split_8To4Bit), StatementOf(node, statement), -1);
					nibbleSplits[(node, pin)] = split;
					AddEdge(new Endpoint(node, pin), split, 0);
				}

				// Output 0 is the high nibble.
				endpoint = new Endpoint(split, offset == 4 ? 0 : 1);
				return true;
			}

			// Whether these bits are consecutive bits of one multi-bit pin.
			bool TryBusBits(Net[] roots, out LayoutNode node, out int pin, out int busWidth, out int offset)
			{
				node = null;
				pin = 0;
				busWidth = 0;
				offset = 0;
				Net first = roots[0];
				if (first.Kind == NetKind.PortBit)
				{
					node = portNodes[first.Port];
					busWidth = first.Port.Width;
				}
				else if (first.Kind == NetKind.PinBit)
				{
					node = componentNodes[first.Component];
					pin = first.Pin;
					busWidth = first.Component.Outputs[first.Pin].Length;
				}
				else
				{
					return false;
				}

				offset = first.Bit;
				for (int i = 0; i < roots.Length; i++)
				{
					Net bit = roots[i];
					if (bit.Kind != first.Kind || bit.Bit != offset + i) return false;
					if (bit.Kind == NetKind.PortBit && bit.Port != first.Port) return false;
					if (bit.Kind == NetKind.PinBit && (bit.Component != first.Component || bit.Pin != first.Pin)) return false;
				}

				return busWidth > 1;
			}

			// ---- Description ----

			ChipDescription Build(string chipName)
			{
				int nextId = 1;
				List<PinDescription> inputPins = new();
				List<PinDescription> outputPins = new();
				Dictionary<LayoutNode, int> ids = new();

				foreach (PortSignal port in netlist.Inputs.Concat(netlist.Outputs))
				{
					LayoutNode node = portNodes[port];
					int id = nextId++;
					ids[node] = id;
					float reach = PortReach(port.Width);
					float handleX = port.IsInput ? node.Centre.x + node.Width / 2 - reach : node.Centre.x - node.Width / 2 + reach;
					PinDescription pin = new(
						port.Name,
						id,
						new Vector2(handleX, node.Centre.y),
						(PinBitCount)port.Width,
						PinColour.Blue,
						PinValueDisplayMode.Off);
					(port.IsInput ? inputPins : outputPins).Add(pin);
				}

				List<SubChipDescription> subChips = new();
				foreach (Part part in parts)
				{
					int id = nextId++;
					ids[part.Node] = id;
					subChips.Add(new SubChipDescription(
						part.Description.Name,
						id,
						string.Empty,
						part.Node.Centre,
						Array.Empty<OutputPinColourInfo>()));
				}

				List<WireDescription> wires = new();
				foreach (LayoutEdge edge in edges)
				{
					PinAddress source = Address(edge.Source, edge.SourcePin, output: true, ids);
					PinAddress target = Address(edge.Target, edge.TargetPin, output: false, ids);
					Vector2[] points = new Vector2[edge.Bends.Count + 2];
					for (int i = 0; i < edge.Bends.Count; i++) points[i + 1] = edge.Bends[i];

					wires.Add(new WireDescription
					{
						SourcePinAddress = source,
						TargetPinAddress = target,
						ConnectionType = WireConnectionType.ToPins,
						ConnectedWireIndex = -1,
						ConnectedWireSegmentIndex = -1,
						Points = points
					});
				}

				PinDescription[] inputs = inputPins.ToArray();
				PinDescription[] outputs = outputPins.ToArray();
				Vector2 size = ChipSize(inputs, outputs, chipName);

				return new ChipDescription
				{
					DLSVersion = Main.DLSVersion.ToString(),
					Name = chipName,
					NameLocation = NameDisplayLocation.Centre,
					ChipType = ChipType.Custom,
					CacheMode = ChipCacheMode.Auto,
					Size = size,
					Colour = new Color(0.18f, 0.31f, 0.58f),
					InputPins = inputs,
					OutputPins = outputs,
					SubChips = subChips.ToArray(),
					Wires = wires.ToArray(),
					Displays = Array.Empty<DisplayDescription>()
				};
			}

			// The editor measures the chip name with its font, which is only loaded while the
			// game runs; without it (tests, tools) the size is worked out from the pins and an
			// estimate of the name's width.
			static Vector2 ChipSize(PinDescription[] inputs, PinDescription[] outputs, string chipName)
			{
				Vector2 size;
				try
				{
					size = SubChipInstance.CalculateMinChipSize(inputs, outputs, chipName);
				}
				catch (Exception)
				{
					static float PinsHeight(PinDescription[] pins) =>
						pins.Sum(p => p.BitCount == PinBitCount.Bit1 ? 2 : p.BitCount == PinBitCount.Bit4 ? 3 : 4) * 0.125f;

					float height = Mathf.Max(0.375f, Mathf.Max(PinsHeight(inputs), PinsHeight(outputs)));
					size = new Vector2(chipName.Length * 0.16f + 0.25f, height);
				}

				size.x = Mathf.Max(size.x, 1.5f);
				return size;
			}

			PinAddress Address(LayoutNode node, int pin, bool output, Dictionary<LayoutNode, int> ids)
			{
				int id = ids[node];
				if (node.IsInputPort || node.IsOutputPort) return new PinAddress(id, 0);
				ChipDescription description = partOf[node].Description;
				PinDescription[] pins = output ? description.OutputPins : description.InputPins;
				return new PinAddress(id, pins[pin].ID);
			}
		}
	}
}
