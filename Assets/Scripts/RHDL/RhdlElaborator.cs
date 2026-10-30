using System;
using System.Collections.Generic;
using System.Linq;
using DLS.Description;
using DLS.Game;

namespace DLS.RHDL
{
	// Turns a parsed circuit into a bit-level netlist of NAND gates and components.
	//
	// Naming rules, which follow Python where hardware allows:
	// - A name assigned once is a wire. It can be used anywhere in the circuit, also above
	//   its assignment, which is how feedback (latches, registers, CPUs) is written.
	// - A name assigned several times is a variable: each use sees the latest assignment
	//   above it, so `carry = ...` inside a loop builds a ripple chain.
	// - Bits of a signal with a declared width (outputs, `x: u4`) may be assigned one at a
	//   time: `Y[i] = ...`.
	internal sealed class RhdlElaborator
	{
		const int MaxUnrolledStatements = 20000;
		const int MaxErrors = 40;

		readonly ChipLibrary library;
		readonly List<RhdlDiagnostic> diagnostics;
		readonly Netlist netlist = new();
		readonly Dictionary<string, Symbol> symbols = new(StringComparer.Ordinal);
		readonly List<Work> works = new();
		readonly Dictionary<(int, int), ComponentCell> precreated = new();
		CircuitSyntax circuit;

		// Tags given to the gates that are being created, used by the layout.
		int currentStatement;
		int currentSlice = -1;
		int currentLine;

		RhdlElaborator(ChipLibrary library, List<RhdlDiagnostic> diagnostics)
		{
			this.library = library;
			this.diagnostics = diagnostics;
		}

		public static Netlist Elaborate(CircuitSyntax circuit, Dictionary<string, long> constants, ChipLibrary library, List<RhdlDiagnostic> diagnostics)
		{
			RhdlElaborator elaborator = new(library, diagnostics) { circuit = circuit };
			try
			{
				elaborator.Run(constants);
			}
			catch (ElaborationStop)
			{
			}
			catch (Exception e)
			{
				// A bug in the compiler must not take the editor down with it.
				diagnostics.Add(new RhdlDiagnostic(elaborator.currentLine, 0,
					$"Internal RHDL error ({e.GetType().Name}: {e.Message} at {FirstFrame(e)}). Please report this source as a bug."));
			}

			return diagnostics.Count == 0 ? elaborator.netlist : null;
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

		// ---- Symbols ----

		abstract class Symbol
		{
			public string Name;
			public int Line;
		}

		sealed class ConstSymbol : Symbol
		{
			public long Value;
		}

		sealed class SignalSymbol : Symbol
		{
			public int Width;
			public Net[] Bits;
			public bool IsInput;
			public bool IsOutput;
			public bool Declared;
			public bool[] BitAssigned;
			public bool InferringWidth;
			// The one statement that assigns the whole signal, for width inference.
			public Work Definition;
			public int TargetIndex;
			// Several 3-state buffers drive this 1-bit signal.
			public bool IsJunction;
			public bool Used;
		}

		sealed class VariableSymbol : Symbol
		{
			public readonly List<(Work work, int target)> Assignments = new();
			public Value Current;
			public bool HasValue;
			public readonly Dictionary<int, int> InferredWidths = new();
			public readonly HashSet<int> Inferring = new();
		}

		sealed class InstanceSymbol : Symbol
		{
			public ComponentCell Cell;
		}

		sealed class Work
		{
			public Statement Statement;
			public Dictionary<string, long> Constants;
			public int Index;
		}

		readonly struct Value
		{
			public readonly Net[] Bits;
			public readonly long Constant;
			public readonly bool IsConstant;

			Value(Net[] bits, long constant, bool isConstant)
			{
				Bits = bits;
				Constant = constant;
				IsConstant = isConstant;
			}

			public int Width => Bits?.Length ?? 0;
			public static Value Of(Net[] bits) => new(bits, 0, false);
			public static Value Const(long value) => new(null, value, true);
		}

		sealed class ElaborationStop : Exception { }

		void Error(Node node, string message) => Error(node?.Line ?? 0, node?.Column ?? 0, message);

		void Error(int line, int column, string message)
		{
			diagnostics.Add(new RhdlDiagnostic(line, column, message));
			if (diagnostics.Count >= MaxErrors) throw new ElaborationStop();
		}

		// ---- Driver ----

		void Run(Dictionary<string, long> constants)
		{
			foreach ((string name, long value) in constants)
			{
				symbols[name] = new ConstSymbol { Name = name, Value = value };
			}

			DeclarePorts();
			if (diagnostics.Count > 0) return;

			Unroll(circuit.Body, new Dictionary<string, long>(StringComparer.Ordinal));
			if (diagnostics.Count > 0) return;

			Prescan();
			if (diagnostics.Count > 0) return;

			foreach (Work work in works)
			{
				currentStatement = work.Index;
				currentSlice = -1;
				currentLine = work.Statement.Line;
				Process(work);
			}

			CheckComplete();
			if (diagnostics.Count > 0) return;

			CheckAliasCycles();
			if (diagnostics.Count > 0) return;

			Optimize();
		}

		void DeclarePorts()
		{
			HashSet<string> seen = new(StringComparer.Ordinal);
			foreach (PortSyntax port in circuit.Inputs) DeclarePort(port, true, seen);
			foreach (PortSyntax port in circuit.Outputs) DeclarePort(port, false, seen);

			if (circuit.Outputs.Count == 0)
			{
				Error(circuit, $"'{circuit.Name}' has no outputs. Add them after '->', e.g. 'circuit {circuit.Name}(A, B) -> Y:'.");
			}
		}

		void DeclarePort(PortSyntax port, bool isInput, HashSet<string> seen)
		{
			int width = port.Width == 0 ? 1 : port.Width;
			if (width != 1 && width != 4 && width != 8)
			{
				Error(port, $"Pin '{port.Name}' is {width} bits, but chip pins can only be 1, 4 or 8 bits wide (bit, u4 or u8). " +
				            "Use several pins, or a wider signal inside the circuit.");
				return;
			}

			if (!seen.Add(port.Name))
			{
				Error(port, $"Pin name '{port.Name}' is used twice.");
				return;
			}

			if (IsBuiltinFunction(port.Name) || symbols.ContainsKey(port.Name))
			{
				Error(port, $"'{port.Name}' is already the name of a constant or a built-in; choose another pin name.");
				return;
			}

			PortSignal signal = new() { Name = port.Name, Width = width, IsInput = isInput, Line = port.Line };
			signal.Bits = new Net[width];
			for (int i = 0; i < width; i++)
			{
				Net net = netlist.NewNet(isInput ? NetKind.PortBit : NetKind.Placeholder);
				net.Port = signal;
				net.Bit = i;
				net.Label = BitLabel(port.Name, width, i);
				net.Line = port.Line;
				signal.Bits[i] = net;
			}

			(isInput ? netlist.Inputs : netlist.Outputs).Add(signal);
			symbols[port.Name] = new SignalSymbol
			{
				Name = port.Name,
				Line = port.Line,
				Width = width,
				Bits = signal.Bits,
				IsInput = isInput,
				IsOutput = !isInput,
				Declared = true,
				BitAssigned = new bool[width]
			};
		}

		static string BitLabel(string name, int width, int bit) => width == 1 ? name : $"{name}[{bit}]";

		void Unroll(List<Statement> body, Dictionary<string, long> loopConstants)
		{
			foreach (Statement statement in body)
			{
				if (statement is ForStatement loop)
				{
					if (symbols.ContainsKey(loop.Variable) && symbols[loop.Variable] is not ConstSymbol || loopConstants.ContainsKey(loop.Variable))
					{
						Error(loop, $"Loop variable '{loop.Variable}' already names something else.");
						continue;
					}

					Work rangeWork = new() { Statement = loop, Constants = loopConstants };
					if (!TryConstant(loop.Start, rangeWork, out long start) ||
					    !TryConstant(loop.Stop, rangeWork, out long stop))
					{
						continue;
					}

					long step = 1;
					if (loop.Step != null && !TryConstant(loop.Step, rangeWork, out step)) continue;
					if (step == 0)
					{
						Error(loop.Step, "range() step cannot be zero.");
						continue;
					}

					for (long i = start; step > 0 ? i < stop : i > stop; i += step)
					{
						Dictionary<string, long> inner = new(loopConstants, StringComparer.Ordinal) { [loop.Variable] = i };
						Unroll(loop.Body, inner);
						if (works.Count > MaxUnrolledStatements)
						{
							Error(loop, $"Loops expand to more than {MaxUnrolledStatements} statements.");
							throw new ElaborationStop();
						}
					}

					continue;
				}

				works.Add(new Work { Statement = statement, Constants = loopConstants, Index = works.Count });
			}
		}

		// Finds out what every name is before any logic is built, because a wire may be used
		// above the line that assigns it.
		void Prescan()
		{
			Dictionary<string, List<(Work work, int target)>> wholeAssignments = new(StringComparer.Ordinal);

			foreach (Work work in works)
			{
				switch (work.Statement)
				{
					case DeclareStatement declare:
						if (symbols.TryGetValue(declare.Name, out Symbol existing))
						{
							Error(declare, $"'{declare.Name}' is already {Describe(existing)}.");
							break;
						}

						if (IsBuiltinFunction(declare.Name) || work.Constants.ContainsKey(declare.Name))
						{
							Error(declare, $"'{declare.Name}' is the name of a built-in or a loop variable.");
							break;
						}

						symbols[declare.Name] = NewSignal(declare.Name, declare.Width, declare.Line, declared: true);
						break;

					case AssignStatement assign:
						for (int t = 0; t < assign.Targets.Count; t++)
						{
							if (assign.Targets[t] is NameExpr name)
							{
								if (name.Name == "_") continue;
								if (!wholeAssignments.TryGetValue(name.Name, out var list))
								{
									wholeAssignments[name.Name] = list = new List<(Work, int)>();
								}

								list.Add((work, t));
							}
						}

						break;
				}
			}

			foreach ((string name, List<(Work work, int target)> assignments) in wholeAssignments)
			{
				(Work firstWork, int firstTarget) = assignments[0];
				Expr firstTargetExpr = ((AssignStatement)firstWork.Statement).Targets[firstTarget];

				if (IsBuiltinFunction(name))
				{
					Error(firstTargetExpr, $"'{name}' is a built-in function and cannot be assigned.");
					continue;
				}

				if (firstWork.Constants.ContainsKey(name))
				{
					Error(firstTargetExpr, $"'{name}' is a loop variable and cannot be assigned.");
					continue;
				}

				if (symbols.TryGetValue(name, out Symbol existing))
				{
					switch (existing)
					{
						case SignalSymbol { IsInput: true }:
							Error(firstTargetExpr, $"'{name}' is an input; it is driven from outside the circuit and cannot be assigned.");
							continue;
						case ConstSymbol:
							Error(firstTargetExpr, $"'{name}' is a constant and cannot be assigned inside the circuit.");
							continue;
						case SignalSymbol signal:
							if (assignments.Count > 1 && !AllTriState(assignments))
							{
								(Work second, int secondTarget) = assignments[1];
								Error(((AssignStatement)second.Statement).Targets[secondTarget],
									$"'{name}' is assigned more than once. A pin or declared signal is driven by one assignment; " +
									"use a new name for the second value.");
								continue;
							}

							signal.Definition = firstWork;
							signal.TargetIndex = firstTarget;
							signal.IsJunction = assignments.Count > 1;
							continue;
					}
				}

				if (assignments.Count > 1 && !AllTriState(assignments))
				{
					VariableSymbol variable = new() { Name = name, Line = firstWork.Statement.Line };
					variable.Assignments.AddRange(assignments);
					symbols[name] = variable;
					continue;
				}

				AssignStatement statement = (AssignStatement)firstWork.Statement;
				if (assignments.Count == 1 && statement.Targets.Count == 1 && statement.AnnotatedWidth == 0 &&
				    statement.Value is CallExpr call && TryResolveComponent(call, firstWork, out ChipDescription component, report: false))
				{
					// name = Component(...): the instance exists from the start, so its outputs
					// can be used above this line.
					ComponentCell cell = CreateComponentCell(component, name, firstWork, call);
					precreated[(firstWork.Index, firstTarget)] = cell;
					if (component.OutputPins.Length == 1)
					{
						SignalSymbol single = NewSignal(name, cell.Outputs[0].Length, statement.Line, declared: false);
						single.Definition = firstWork;
						single.TargetIndex = firstTarget;
						symbols[name] = single;
						BindBits(single, cell.Outputs[0], 0, statement);
					}
					else
					{
						symbols[name] = new InstanceSymbol { Name = name, Line = statement.Line, Cell = cell };
					}

					continue;
				}

				SignalSymbol wire = NewSignal(name, statement.AnnotatedWidth, statement.Line, declared: false);
				wire.Definition = firstWork;
				wire.TargetIndex = firstTarget;
				wire.IsJunction = assignments.Count > 1;
				symbols[name] = wire;
			}

			// Unpacking assignments create their component up front as well, so that their
			// outputs are available to earlier lines.
			foreach (Work work in works)
			{
				if (work.Statement is not AssignStatement { Targets: { Count: > 1 } } assign) continue;
				if (assign.Value is not CallExpr call || !TryResolveComponent(call, work, out ChipDescription component, report: true))
				{
					Error(assign, "Unpacking 'a, b = ...' needs a component call on the right, e.g. 'q, nq = DffBit(d, clk)'.");
					continue;
				}

				if (component.OutputPins.Length != assign.Targets.Count)
				{
					Error(assign, $"{component.Name} has {component.OutputPins.Length} outputs ({string.Join(", ", component.OutputPins.Select(p => p.Name))}) " +
					              $"but {assign.Targets.Count} names are given.");
					continue;
				}

				ComponentCell cell = CreateComponentCell(component, null, work, call);
				precreated[(work.Index, -1)] = cell;
			}
		}

		bool AllTriState(List<(Work work, int target)> assignments)
		{
			foreach ((Work work, int target) in assignments)
			{
				AssignStatement statement = (AssignStatement)work.Statement;
				if (statement.Targets.Count != 1 || statement.Value is not CallExpr call) return false;
				if (!TryResolveComponent(call, work, out ChipDescription description, report: false)) return false;
				if (description.ChipType != ChipType.TriStateBuffer) return false;
			}

			return true;
		}

		SignalSymbol NewSignal(string name, int width, int line, bool declared)
		{
			SignalSymbol signal = new() { Name = name, Line = line, Width = width, Declared = declared };
			if (width > 0) CreateBits(signal);
			return signal;
		}

		void CreateBits(SignalSymbol signal)
		{
			signal.Bits = new Net[signal.Width];
			signal.BitAssigned = new bool[signal.Width];
			for (int i = 0; i < signal.Width; i++)
			{
				Net net = netlist.NewNet(NetKind.Placeholder);
				net.Label = BitLabel(signal.Name, signal.Width, i);
				net.Line = signal.Line;
				signal.Bits[i] = net;
			}
		}

		static string Describe(Symbol symbol) => symbol switch
		{
			ConstSymbol => "a constant",
			SignalSymbol { IsInput: true } => "an input",
			SignalSymbol { IsOutput: true } => "an output",
			InstanceSymbol => "a component",
			VariableSymbol => "a variable",
			_ => "a signal"
		};

		// ---- Statements ----

		void Process(Work work)
		{
			switch (work.Statement)
			{
				case DeclareStatement:
					return;

				case ExpressionStatement expression:
					if (expression.Value is CallExpr call && TryResolveComponent(call, work, out ChipDescription component, report: true))
					{
						ComponentCell cell = CreateComponentCell(component, null, work, call);
						ConnectArguments(cell, call, work);
						return;
					}

					if (expression.Value is CallExpr) return;
					Error(expression, "This line computes a value but does not assign it. Write 'name = ...'.");
					return;

				case AssignStatement assign:
					ProcessAssign(assign, work);
					return;
			}
		}

		void ProcessAssign(AssignStatement assign, Work work)
		{
			if (assign.Targets.Count > 1)
			{
				if (!precreated.TryGetValue((work.Index, -1), out ComponentCell cell)) return;
				ConnectArguments(cell, (CallExpr)assign.Value, work);
				for (int t = 0; t < assign.Targets.Count; t++)
				{
					Expr target = assign.Targets[t];
					if (target is NameExpr { Name: "_" }) continue;
					if (TargetWidth(target, work, assign) < 0) continue;
					AssignValue(target, Value.Of(cell.Outputs[t]), work, assign, fromUnpack: true);
				}

				return;
			}

			Expr single = assign.Targets[0];
			if (precreated.TryGetValue((work.Index, 0), out ComponentCell instance))
			{
				ConnectArguments(instance, (CallExpr)assign.Value, work);
				return;
			}

			int width = TargetWidth(single, work, assign);
			if (width < 0) return;

			Value value = Synthesize(assign.Value, work, width);
			AssignValue(single, value, work, assign, fromUnpack: false);
		}

		// Width the target expects, 0 when the value decides, -1 after an error.
		int TargetWidth(Expr target, Work work, AssignStatement assign)
		{
			switch (target)
			{
				case NameExpr name:
				{
					if (name.Name == "_") return 0;
					Symbol symbol = Lookup(name.Name, work);
					if (assign.AnnotatedWidth > 0) return assign.AnnotatedWidth;
					if (symbol is SignalSymbol signal)
					{
						EnsureWidth(signal);
						return signal.Width;
					}

					return 0;
				}

				case IndexExpr:
					return 1;

				case SliceExpr slice:
				{
					if (slice.Target is not NameExpr sliceName || Lookup(sliceName.Name, work) is not SignalSymbol signal)
					{
						Error(target, "Only bits of a pin or a declared signal can be assigned, e.g. 'Y[0:4] = ...'.");
						return -1;
					}

					EnsureWidth(signal);
					if (!TryResolveSlice(slice, work, signal.Width, out int low, out int high)) return -1;
					return high - low;
				}

				case AttributeExpr attribute:
				{
					if (!TryResolveInputPin(attribute, work, out ComponentCell cell, out int pin)) return -1;
					return (int)cell.Description.InputPins[pin].BitCount;
				}

				default:
					Error(target, "Only names, bits such as Y[0], slices such as Y[0:4] and component pins such as reg.D can be assigned.");
					return -1;
			}
		}

		void AssignValue(Expr target, Value value, Work work, AssignStatement assign, bool fromUnpack)
		{
			switch (target)
			{
				case NameExpr name:
				{
					Symbol symbol = Lookup(name.Name, work);
					switch (symbol)
					{
						case VariableSymbol variable:
						{
							int width = assign.AnnotatedWidth > 0 ? assign.AnnotatedWidth : value.IsConstant ? 0 : value.Width;
							if (value.IsConstant && width == 0)
							{
								variable.Current = value;
							}
							else
							{
								Net[] bits = MaterializeForTarget(value, width, assign.Value, name.Name);
								if (bits == null) return;
								variable.Current = Value.Of(bits);
							}

							variable.HasValue = true;
							return;
						}

						case SignalSymbol signal:
						{
							EnsureWidth(signal);
							if (signal.Width == 0)
							{
								if (!value.IsConstant)
								{
									signal.Width = value.Width;
									CreateBits(signal);
								}
								else
								{
									Error(assign.Value, $"Give '{name.Name}' a width, e.g. '{name.Name}: u4 = {(assign.Value as NumberExpr)?.Raw ?? "..."}'.");
									return;
								}
							}

							Net[] bits = MaterializeForTarget(value, signal.Width, fromUnpack ? (Expr)target : assign.Value, name.Name);
							if (bits == null) return;

							if (signal.IsJunction)
							{
								AddJunctionDriver(signal, bits[0], assign);
								return;
							}

							BindBits(signal, bits, 0, assign);
							return;
						}

						default:
							Error(target, $"'{name.Name}' cannot be assigned here.");
							return;
					}
				}

				case IndexExpr index:
				{
					if (index.Target is not NameExpr indexName || Lookup(indexName.Name, work) is not SignalSymbol signal || signal.IsInput)
					{
						Error(target, "Only bits of an output or a declared signal can be assigned, e.g. 'Y[0] = ...'. Declare it first with 'x: u4'.");
						return;
					}

					EnsureWidth(signal);
					if (!TryConstant(index.Index, work, out long bit)) return;
					if (bit < 0) bit += signal.Width;
					if (bit < 0 || bit >= signal.Width)
					{
						Error(index, $"Bit {bit} is outside '{indexName.Name}', which has bits 0 to {signal.Width - 1}.");
						return;
					}

					Net[] bits = MaterializeForTarget(value, 1, assign.Value, $"{indexName.Name}[{bit}]");
					if (bits == null) return;
					BindBits(signal, bits, (int)bit, assign);
					return;
				}

				case SliceExpr slice:
				{
					if (slice.Target is not NameExpr sliceName || Lookup(sliceName.Name, work) is not SignalSymbol signal)
					{
						Error(target, "Only bits of an output or a declared signal can be assigned, e.g. 'Y[0:4] = ...'.");
						return;
					}

					EnsureWidth(signal);
					if (signal.IsInput)
					{
						Error(target, $"'{signal.Name}' is an input and cannot be assigned.");
						return;
					}

					if (!TryResolveSlice(slice, work, signal.Width, out int low, out int high)) return;
					Net[] bits = MaterializeForTarget(value, high - low, assign.Value, $"{signal.Name}[{low}:{high}]");
					if (bits == null) return;
					BindBits(signal, bits, low, assign);
					return;
				}

				case AttributeExpr attribute:
				{
					if (!TryResolveInputPin(attribute, work, out ComponentCell cell, out int pin)) return;
					PinDescription pinDescription = cell.Description.InputPins[pin];
					if (cell.Inputs[pin] != null)
					{
						Error(target, $"Pin {pinDescription.Name} of '{cell.Name}' is already connected.");
						return;
					}

					Net[] bits = MaterializeForTarget(value, (int)pinDescription.BitCount, assign.Value, $"{cell.Name}.{pinDescription.Name}");
					if (bits == null) return;
					cell.Inputs[pin] = bits;
					cell.InputLines[pin] = assign.Line;
					return;
				}
			}
		}

		void BindBits(SignalSymbol signal, Net[] bits, int offset, Node at)
		{
			for (int i = 0; i < bits.Length; i++)
			{
				int bit = offset + i;
				if (signal.BitAssigned[bit])
				{
					Error(at, signal.Width == 1
						? $"'{signal.Name}' is assigned more than once."
						: $"Bit {bit} of '{signal.Name}' is assigned more than once.");
					return;
				}

				signal.BitAssigned[bit] = true;
				signal.Bits[bit].Alias = bits[i];
			}
		}

		void AddJunctionDriver(SignalSymbol signal, Net driver, Node at)
		{
			Net placeholder = signal.Bits[0];
			if (placeholder.Alias == null)
			{
				Net junction = netlist.NewNet(NetKind.Junction);
				junction.Label = signal.Name;
				junction.Line = signal.Line;
				placeholder.Alias = junction;
				signal.BitAssigned[0] = true;
			}

			Netlist.Find(placeholder).Drivers.Add(driver);
		}

		// Checks a value against the width of what it is assigned to.
		Net[] MaterializeForTarget(Value value, int width, Expr at, string targetName)
		{
			if (value.IsConstant) return ConstantBits(value.Constant, width, at);
			if (width == 0 || value.Width == width) return value.Bits;

			if (value.Width < width)
			{
				Error(at, $"This value is {value.Width} bit{(value.Width == 1 ? "" : "s")} but '{targetName}' is {width}. " +
				          $"Write u{width}(...) to add zero bits at the top.");
			}
			else
			{
				Error(at, $"This value is {value.Width} bits but '{targetName}' is {width}. " +
				          $"Take the bits you need with a slice such as [0:{width}].");
			}

			return null;
		}

		// ---- Components ----

		static readonly Dictionary<string, ChipType> BuiltinAliases = new(StringComparer.Ordinal)
		{
			["NAND"] = ChipType.Nand,
			["TRISTATE"] = ChipType.TriStateBuffer,
			["CLOCK"] = ChipType.Clock,
			["PULSE"] = ChipType.Pulse,
			["ROM"] = ChipType.Rom_256x16,
			["KEY"] = ChipType.Key,
			["LED"] = ChipType.DisplayLED,
			["SEVEN_SEGMENT"] = ChipType.SevenSegmentDisplay,
			["RGB_DISPLAY"] = ChipType.DisplayRGB,
			["DOT_DISPLAY"] = ChipType.DisplayDot,
			["BUZZER"] = ChipType.Buzzer
		};

		bool TryResolveComponent(CallExpr call, Work work, out ChipDescription description, bool report)
		{
			description = null;
			if (call.Function is not NameExpr name) return false;
			if (IsBuiltinFunction(name.Name)) return false;
			if (symbols.ContainsKey(name.Name) || work.Constants.ContainsKey(name.Name))
			{
				if (report) Error(call, $"'{name.Name}' is not a component, so it cannot be called.");
				return false;
			}

			description = FindChip(name.Name);
			if (description == null)
			{
				if (report)
				{
					string hint = Suggest(name.Name, library.allChips.Where(c => !IsHiddenChip(c)).Select(c => ComponentSpelling(c)).Concat(BuiltinAliases.Keys));
					Error(call, $"Unknown component '{name.Name}'.{hint} Save the chip first, then use it by name.");
				}

				return false;
			}

			if (ChipDescription.NameMatch(description.Name, circuit.Name))
			{
				if (report) Error(call, "A circuit cannot contain itself.");
				description = null;
				return false;
			}

			return true;
		}

		ChipDescription FindChip(string name)
		{
			if (BuiltinAliases.TryGetValue(name, out ChipType type))
			{
				return library.TryGetChipDescription(ChipTypeHelper.GetName(type), out ChipDescription builtin) ? builtin : null;
			}

			string normalized = Normalize(name);
			foreach (ChipDescription chip in library.allChips)
			{
				if (IsHiddenChip(chip)) continue;
				if (Normalize(chip.Name) == normalized) return chip;
			}

			return null;
		}

		static bool IsHiddenChip(ChipDescription chip) =>
			chip.ChipType is ChipType.In_1Bit or ChipType.In_4Bit or ChipType.In_8Bit or
				ChipType.Out_1Bit or ChipType.Out_4Bit or ChipType.Out_8Bit ||
			ChipTypeHelper.IsBusType(chip.ChipType);

		static string ComponentSpelling(ChipDescription chip)
		{
			foreach ((string alias, ChipType type) in BuiltinAliases)
			{
				if (chip.ChipType == type) return alias;
			}

			return chip.Name.Replace(' ', '_');
		}

		ComponentCell CreateComponentCell(ChipDescription description, string name, Work work, CallExpr call)
		{
			ComponentCell cell = new()
			{
				Name = name ?? $"{description.Name} on line {call.Line}",
				Description = description,
				Inputs = new Net[description.InputPins.Length][],
				InputLines = new int[description.InputPins.Length],
				Outputs = new Net[description.OutputPins.Length][],
				Statement = work.Index,
				Line = call.Line
			};

			for (int pin = 0; pin < description.OutputPins.Length; pin++)
			{
				int width = (int)description.OutputPins[pin].BitCount;
				cell.Outputs[pin] = new Net[width];
				for (int bit = 0; bit < width; bit++)
				{
					Net net = netlist.NewNet(NetKind.PinBit);
					net.Component = cell;
					net.Pin = pin;
					net.Bit = bit;
					cell.Outputs[pin][bit] = net;
				}
			}

			netlist.Components.Add(cell);
			return cell;
		}

		void ConnectArguments(ComponentCell cell, CallExpr call, Work work)
		{
			PinDescription[] pins = cell.Description.InputPins;
			int positional = 0;
			foreach (CallArgument argument in call.Arguments)
			{
				int pin;
				if (argument.Keyword == null)
				{
					pin = positional++;
					if (pin >= pins.Length)
					{
						Error(argument.Line, argument.Column, $"{cell.Description.Name} has only {pins.Length} input{(pins.Length == 1 ? "" : "s")}" +
						                                      (pins.Length == 0 ? "." : $": {string.Join(", ", pins.Select(p => PinSpelling(p.Name)))}."));
						return;
					}
				}
				else
				{
					pin = FindPin(pins, argument.Keyword);
					if (pin < 0)
					{
						string outputs = cell.Description.OutputPins.Any(p => Normalize(p.Name) == Normalize(argument.Keyword))
							? $" {PinSpelling(argument.Keyword)} is an output: read it as name.{PinSpelling(argument.Keyword)} instead."
							: string.Empty;
						Error(argument.Line, argument.Column, $"{cell.Description.Name} has no input named '{argument.Keyword}'. " +
						                                      $"Its inputs are {string.Join(", ", pins.Select(p => PinSpelling(p.Name)))}.{outputs}");
						continue;
					}
				}

				if (cell.Inputs[pin] != null)
				{
					Error(argument.Line, argument.Column, $"Input {PinSpelling(pins[pin].Name)} of {cell.Description.Name} is given twice.");
					continue;
				}

				int width = (int)pins[pin].BitCount;
				Value value = Synthesize(argument.Value, work, width);
				Net[] bits = MaterializeForTarget(value, width, argument.Value, $"{cell.Description.Name}.{PinSpelling(pins[pin].Name)}");
				if (bits == null) continue;
				cell.Inputs[pin] = bits;
				cell.InputLines[pin] = argument.Line;
			}
		}

		static int FindPin(PinDescription[] pins, string name)
		{
			string normalized = Normalize(name);
			for (int i = 0; i < pins.Length; i++)
			{
				if (Normalize(pins[i].Name) == normalized) return i;
			}

			return -1;
		}

		static string PinSpelling(string pinName) => pinName.Trim().Replace(' ', '_');

		bool TryResolveInputPin(AttributeExpr attribute, Work work, out ComponentCell cell, out int pin)
		{
			cell = null;
			pin = -1;
			if (attribute.Target is not NameExpr owner || Lookup(owner.Name, work) is not Symbol symbol)
			{
				Error(attribute, "Only pins of a named component can be assigned, e.g. 'reg.D = x'.");
				return false;
			}

			cell = symbol switch
			{
				InstanceSymbol instance => instance.Cell,
				SignalSymbol signal => FindPrecreatedFor(signal),
				_ => null
			};

			if (cell == null)
			{
				Error(attribute, $"'{owner.Name}' is not a component.");
				return false;
			}

			pin = FindPin(cell.Description.InputPins, attribute.Name);
			if (pin < 0)
			{
				Error(attribute, $"{cell.Description.Name} has no input named '{attribute.Name}'. " +
				                 $"Its inputs are {string.Join(", ", cell.Description.InputPins.Select(p => PinSpelling(p.Name)))}.");
				return false;
			}

			return true;
		}

		ComponentCell FindPrecreatedFor(SignalSymbol signal)
		{
			if (signal.Definition == null) return null;
			return precreated.TryGetValue((signal.Definition.Index, signal.TargetIndex), out ComponentCell cell) ? cell : null;
		}

		// ---- Names ----

		Symbol Lookup(string name, Work work)
		{
			if (work.Constants.TryGetValue(name, out long loopValue)) return new ConstSymbol { Name = name, Value = loopValue };
			return symbols.TryGetValue(name, out Symbol symbol) ? symbol : null;
		}

		void EnsureWidth(SignalSymbol signal)
		{
			if (signal.Width > 0 || signal.Definition == null) return;
			if (signal.InferringWidth)
			{
				Error(signal.Line, 0, $"The width of '{signal.Name}' depends on itself. Give it a type, e.g. '{signal.Name}: bit = ...'.");
				signal.Width = 1;
				CreateBits(signal);
				return;
			}

			signal.InferringWidth = true;
			AssignStatement statement = (AssignStatement)signal.Definition.Statement;
			int width = InferWidth(statement.Value, signal.Definition);
			signal.InferringWidth = false;
			if (signal.Width > 0) return;

			if (width == 0)
			{
				Error(statement.Value, $"Give '{signal.Name}' a width, e.g. '{signal.Name}: u4 = ...', or use a constant instead.");
				width = 1;
			}

			signal.Width = width;
			CreateBits(signal);
		}

		// Natural width of an expression without building it; 0 for a constant without a width.
		int InferWidth(Expr expr, Work work)
		{
			switch (expr)
			{
				case NumberExpr number:
					return number.SpelledWidth;

				case NameExpr name:
				{
					Symbol symbol = Lookup(name.Name, work);
					switch (symbol)
					{
						case ConstSymbol:
							return 0;
						case SignalSymbol signal:
							EnsureWidth(signal);
							return signal.Width;
						case VariableSymbol variable:
							return InferVariableWidth(variable, work, name);
						case InstanceSymbol instance:
							return instance.Cell.Outputs.Length == 1 ? instance.Cell.Outputs[0].Length : 1;
						default:
							return 1;
					}
				}

				case AttributeExpr attribute:
				{
					if (attribute.Target is NameExpr owner && Lookup(owner.Name, work) is InstanceSymbol instance)
					{
						int pin = FindPin(instance.Cell.Description.OutputPins, attribute.Name);
						if (pin >= 0) return instance.Cell.Outputs[pin].Length;
					}

					return 1;
				}

				case IndexExpr:
					return 1;

				case SliceExpr slice:
				{
					int targetWidth = InferWidth(slice.Target, work);
					if (TryResolveSlice(slice, work, targetWidth, out int low, out int high, report: false)) return high - low;
					return 1;
				}

				case CallExpr call:
				{
					if (call.Function is NameExpr function)
					{
						if (function.Name == "cat") return call.Arguments.Sum(a => InferWidth(a.Value, work));
						if (RhdlParser.TryParseTypeName(function.Name, out int castWidth)) return castWidth;
						if (TryResolveComponent(call, work, out ChipDescription component, report: false) && component.OutputPins.Length > 0)
						{
							return (int)component.OutputPins[0].BitCount;
						}
					}

					return 1;
				}

				case UnaryExpr unary:
					return unary.Op == "not" ? 1 : InferWidth(unary.Operand, work);

				case BinaryExpr binary:
					return binary.Op switch
					{
						"and" or "or" or "==" or "!=" or "<" or ">" or "<=" or ">=" => 1,
						"<<" or ">>" => InferWidth(binary.Left, work),
						_ => Math.Max(InferWidth(binary.Left, work), InferWidth(binary.Right, work))
					};

				case ConditionalExpr conditional:
					return Math.Max(InferWidth(conditional.WhenTrue, work), InferWidth(conditional.WhenFalse, work));

				default:
					return 1;
			}
		}

		int InferVariableWidth(VariableSymbol variable, Work work, Node at)
		{
			(Work work, int target) latest = default;
			bool found = false;
			foreach ((Work assignedIn, int target) in variable.Assignments)
			{
				if (assignedIn.Index >= work.Index) break;
				latest = (assignedIn, target);
				found = true;
			}

			if (!found) return 0;
			if (variable.InferredWidths.TryGetValue(latest.work.Index, out int known)) return known;
			if (!variable.Inferring.Add(latest.work.Index)) return 1;

			AssignStatement statement = (AssignStatement)latest.work.Statement;
			int width = statement.AnnotatedWidth > 0 ? statement.AnnotatedWidth : InferWidth(statement.Value, latest.work);
			variable.Inferring.Remove(latest.work.Index);
			variable.InferredWidths[latest.work.Index] = width;
			return width;
		}

		static bool IsBuiltinFunction(string name) => name == "cat" || name == "True" || name == "False" || RhdlParser.TryParseTypeName(name, out _);

		// ---- Constants ----

		bool TryConstant(Expr expr, Work work, out long value)
		{
			Value result = Synthesize(expr, work, 0, constantOnly: true);
			value = result.Constant;
			if (result.IsConstant) return true;
			if (diagnostics.Count == 0 || diagnostics[^1].Line != expr.Line)
			{
				Error(expr, "This must be a number known when the circuit is built (a constant or loop variable).");
			}

			return false;
		}

		public static bool TryEvaluateModuleConstant(Expr expr, Dictionary<string, long> constants, out long value, out string error)
		{
			RhdlElaborator evaluator = new(null, new List<RhdlDiagnostic>());
			foreach ((string name, long constant) in constants) evaluator.symbols[name] = new ConstSymbol { Name = name, Value = constant };
			evaluator.circuit = new CircuitSyntax { Name = string.Empty };
			Work work = new() { Constants = new Dictionary<string, long>(), Statement = null };
			try
			{
				Value result = evaluator.Synthesize(expr, work, 0, constantOnly: true);
				value = result.Constant;
				error = evaluator.diagnostics.Count > 0 ? evaluator.diagnostics[0].Message : null;
				if (error == null && !result.IsConstant) error = "A top-level name must be a constant number, e.g. 'WIDTH = 4'.";
				return error == null;
			}
			catch (ElaborationStop)
			{
				value = 0;
				error = evaluator.diagnostics[0].Message;
				return false;
			}
		}

		Net[] ConstantBits(long value, int width, Node at)
		{
			if (width <= 0)
			{
				Error(at, "This constant needs a width. Write it as e.g. 0b0101 or u4(5).");
				return null;
			}

			long max = width >= 63 ? long.MaxValue : (1L << width) - 1;
			if (value < 0)
			{
				long min = width >= 63 ? long.MinValue : -(1L << (width - 1));
				if (value < min)
				{
					Error(at, $"{value} does not fit in {width} bits.");
					return null;
				}

				value &= max;
			}
			else if (value > max)
			{
				Error(at, $"{value} does not fit in {width} bit{(width == 1 ? "" : "s")} (largest is {max}).");
				return null;
			}

			Net[] bits = new Net[width];
			for (int i = 0; i < width; i++) bits[i] = ((value >> i) & 1) != 0 ? netlist.Const1 : netlist.Const0;
			return bits;
		}

		// ---- Expressions ----

		// Builds an expression. contextWidth is the width the result is used at (0 when
		// unknown); operators widen their operands to it, so that 's: u5 = A + B' keeps the
		// carry. With constantOnly, only compile-time numbers are allowed.
		Value Synthesize(Expr expr, Work work, int contextWidth, bool constantOnly = false)
		{
			switch (expr)
			{
				case NumberExpr number:
					return Value.Const(number.Value);

				case NameExpr name:
					return SynthesizeName(name, work, constantOnly);

				case UnaryExpr unary:
					return SynthesizeUnary(unary, work, contextWidth, constantOnly);

				case BinaryExpr binary:
					return SynthesizeBinary(binary, work, contextWidth, constantOnly);

				case ConditionalExpr conditional:
					return SynthesizeConditional(conditional, work, contextWidth, constantOnly);

				case ListExpr list:
					Error(list, "Lists are not values. Join bits into a bus with cat(high, ..., low).");
					return Value.Const(0);
			}

			if (constantOnly)
			{
				Error(expr, "This must be a number known when the circuit is built.");
				return Value.Const(0);
			}

			switch (expr)
			{
				case IndexExpr index:
				{
					Value target = Synthesize(index.Target, work, 0);
					if (!TryConstant(index.Index, work, out long bit)) return Bits(target, 1, expr);
					if (target.IsConstant)
					{
						return Value.Const((target.Constant >> (int)Math.Clamp(bit, 0, 62)) & 1);
					}

					if (bit < 0) bit += target.Width;
					if (bit < 0 || bit >= target.Width)
					{
						Error(index, $"Bit {bit} is outside this {target.Width}-bit value (bits 0 to {target.Width - 1}).");
						return Value.Of(new[] { netlist.Const0 });
					}

					return Value.Of(new[] { target.Bits[bit] });
				}

				case SliceExpr slice:
				{
					Value target = Synthesize(slice.Target, work, 0);
					if (target.IsConstant)
					{
						Error(slice, "Slice a signal, not a number. Write the number you need directly.");
						return Value.Const(0);
					}

					if (!TryResolveSlice(slice, work, target.Width, out int low, out int high)) return Value.Of(new[] { netlist.Const0 });
					Net[] bits = new Net[high - low];
					Array.Copy(target.Bits, low, bits, 0, bits.Length);
					return Value.Of(bits);
				}

				case AttributeExpr attribute:
					return SynthesizeAttribute(attribute, work);

				case CallExpr call:
					return SynthesizeCall(call, work, contextWidth);
			}

			Error(expr, "Unsupported expression.");
			return Value.Const(0);
		}

		Value SynthesizeName(NameExpr name, Work work, bool constantOnly)
		{
			Symbol symbol = Lookup(name.Name, work);
			if (symbol is ConstSymbol constant) return Value.Const(constant.Value);

			if (symbol == null)
			{
				IEnumerable<string> candidates = symbols.Keys.Concat(work.Constants.Keys);
				Error(name, $"Unknown name '{name.Name}'.{Suggest(name.Name, candidates)}");
				return Value.Const(0);
			}

			if (constantOnly)
			{
				Error(name, $"'{name.Name}' is a signal, but a number known when the circuit is built is needed here.");
				return Value.Const(0);
			}

			switch (symbol)
			{
				case SignalSymbol signal:
					EnsureWidth(signal);
					signal.Used = true;
					return Value.Of(signal.Bits);

				case VariableSymbol variable:
				{
					// The latest assignment above this line.
					bool assignedAbove = variable.Assignments.Any(a => a.work.Index < work.Index);
					if (!assignedAbove || !variable.HasValue)
					{
						Error(name, $"'{name.Name}' is used before it is assigned. Names that are assigned more than once " +
						            "are read in order, like Python variables.");
						return Value.Of(new[] { netlist.Const0 });
					}

					return variable.Current;
				}

				case InstanceSymbol instance:
					if (instance.Cell.Outputs.Length == 1) return Value.Of(instance.Cell.Outputs[0]);
					Error(name, $"'{name.Name}' has several outputs ({string.Join(", ", instance.Cell.Description.OutputPins.Select(p => PinSpelling(p.Name)))}). " +
					            $"Pick one, e.g. {name.Name}.{PinSpelling(instance.Cell.Description.OutputPins[0].Name)}.");
					return Value.Of(new[] { netlist.Const0 });
			}

			return Value.Const(0);
		}

		Value SynthesizeAttribute(AttributeExpr attribute, Work work)
		{
			if (attribute.Target is NameExpr owner)
			{
				Symbol symbol = Lookup(owner.Name, work);
				ComponentCell cell = symbol switch
				{
					InstanceSymbol instance => instance.Cell,
					SignalSymbol signal => FindPrecreatedFor(signal),
					_ => null
				};

				if (cell != null)
				{
					int pin = FindPin(cell.Description.OutputPins, attribute.Name);
					if (pin >= 0) return Value.Of(cell.Outputs[pin]);

					bool isInput = FindPin(cell.Description.InputPins, attribute.Name) >= 0;
					Error(attribute, isInput
						? $"{PinSpelling(attribute.Name)} is an input of {cell.Description.Name}; only outputs can be read."
						: $"{cell.Description.Name} has no output named '{attribute.Name}'. " +
						  $"Its outputs are {string.Join(", ", cell.Description.OutputPins.Select(p => PinSpelling(p.Name)))}.");
					return Value.Of(new[] { netlist.Const0 });
				}
			}

			Error(attribute, "Only outputs of a named component can be read with '.', e.g. 'reg.Q'.");
			return Value.Of(new[] { netlist.Const0 });
		}

		Value SynthesizeCall(CallExpr call, Work work, int contextWidth)
		{
			if (call.Function is not NameExpr function)
			{
				Error(call, "Only components and the built-ins cat() and uN() can be called.");
				return Value.Const(0);
			}

			if (function.Name == "cat")
			{
				if (call.Arguments.Count == 0)
				{
					Error(call, "cat() needs at least one value: cat(high, ..., low).");
					return Value.Const(0);
				}

				List<Net> bits = new();
				// Arguments are written most significant first.
				for (int i = call.Arguments.Count - 1; i >= 0; i--)
				{
					CallArgument argument = call.Arguments[i];
					if (argument.Keyword != null) Error(argument.Line, argument.Column, "cat() takes values only, without names.");
					Value value = Synthesize(argument.Value, work, 0);
					if (value.IsConstant)
					{
						int spelled = argument.Value is NumberExpr n ? n.SpelledWidth : 0;
						if (spelled == 0)
						{
							Error(argument.Value, "Give each constant in cat() a width, e.g. 0b0000 or u4(0).");
							continue;
						}

						bits.AddRange(ConstantBits(value.Constant, spelled, argument.Value) ?? Array.Empty<Net>());
						continue;
					}

					bits.AddRange(value.Bits);
				}

				return Value.Of(bits.ToArray());
			}

			if (RhdlParser.TryParseTypeName(function.Name, out int castWidth))
			{
				if (call.Arguments.Count != 1 || call.Arguments[0].Keyword != null)
				{
					Error(call, $"{function.Name}() takes one value.");
					return Value.Const(0);
				}

				Value value = Synthesize(call.Arguments[0].Value, work, castWidth);
				if (value.IsConstant) return Value.Of(ConstantBits(value.Constant, castWidth, call) ?? ConstantBits(0, castWidth, call));
				return Value.Of(Resize(value.Bits, castWidth));
			}

			if (!TryResolveComponent(call, work, out ChipDescription component, report: true)) return Value.Of(new[] { netlist.Const0 });

			ComponentCell cell;
			if (component.ChipType == ChipType.Nand)
			{
				// NAND(...) written in the source becomes exactly one gate.
				cell = null;
				Net[] inputs = new Net[2];
				int positional = 0;
				foreach (CallArgument argument in call.Arguments)
				{
					int pin = argument.Keyword == null ? positional++ : FindNandPin(argument.Keyword);
					if (pin < 0 || pin > 1)
					{
						Error(argument.Line, argument.Column, argument.Keyword == null
							? "NAND has two inputs."
							: $"NAND has no input named '{argument.Keyword}'. Its inputs are A and B.");
						continue;
					}

					Value value = Synthesize(argument.Value, work, 1);
					inputs[pin] = MaterializeForTarget(value, 1, argument.Value, "NAND input")?[0];
				}

				if (inputs[0] == null || inputs[1] == null)
				{
					if (call.Arguments.Count < 2) Error(call, "NAND needs both inputs, e.g. NAND(a, b).");
					return Value.Of(new[] { netlist.Const0 });
				}

				return Value.Of(new[] { Nand(inputs[0], inputs[1], explicitGate: true) });
			}

			cell = CreateComponentCell(component, null, work, call);
			ConnectArguments(cell, call, work);
			if (cell.Outputs.Length == 1) return Value.Of(cell.Outputs[0]);

			Error(call, cell.Outputs.Length == 0
				? $"{component.Name} has no outputs, so it has no value. Write it on a line of its own."
				: $"{component.Name} has several outputs. Unpack them, e.g. " +
				  $"'{string.Join(", ", component.OutputPins.Select(p => PinSpelling(p.Name).ToLowerInvariant()))} = {function.Name}(...)'.");
			return Value.Of(new[] { netlist.Const0 });
		}

		static int FindNandPin(string keyword)
		{
			string normalized = Normalize(keyword);
			return normalized switch
			{
				"A" or "INA" => 0,
				"B" or "INB" => 1,
				_ => -1
			};
		}

		Value SynthesizeUnary(UnaryExpr unary, Work work, int contextWidth, bool constantOnly)
		{
			if (unary.Op == "not")
			{
				Value operand = Synthesize(unary.Operand, work, 1, constantOnly);
				if (operand.IsConstant) return Value.Const(operand.Constant == 0 ? 1 : 0);
				if (!RequireBit(operand, unary.Operand, "not")) return Value.Of(new[] { netlist.Const0 });
				return Value.Of(new[] { Not(operand.Bits[0]) });
			}

			int width = Math.Max(contextWidth, InferWidthSafe(unary.Operand, work, constantOnly));
			Value value = Synthesize(unary.Operand, work, width, constantOnly);

			if (unary.Op == "-")
			{
				if (value.IsConstant) return Value.Const(-value.Constant);
				Net[] zero = ConstantBits(0, value.Width, unary);
				return Value.Of(AddOrSubtract(zero, Resize(value.Bits, width == 0 ? value.Width : width), subtract: true));
			}

			// ~
			if (value.IsConstant)
			{
				if (width == 0)
				{
					Error(unary, "~ on a number needs a width. Write the inverted number directly, or ~u4(5).");
					return Value.Const(0);
				}

				return Value.Const(~value.Constant & ((1L << width) - 1));
			}

			Net[] bits = Resize(value.Bits, Math.Max(width, value.Width));
			Net[] result = new Net[bits.Length];
			for (int i = 0; i < bits.Length; i++)
			{
				currentSlice = i;
				result[i] = Not(bits[i]);
			}

			currentSlice = -1;
			return Value.Of(result);
		}

		int InferWidthSafe(Expr expr, Work work, bool constantOnly) => constantOnly ? 0 : InferWidth(expr, work);

		Value SynthesizeBinary(BinaryExpr binary, Work work, int contextWidth, bool constantOnly)
		{
			switch (binary.Op)
			{
				case "and":
				case "or":
				{
					Value left = Synthesize(binary.Left, work, 1, constantOnly);
					Value right = Synthesize(binary.Right, work, 1, constantOnly);
					bool isAnd = binary.Op == "and";
					if (left.IsConstant && right.IsConstant)
					{
						bool l = left.Constant != 0, r = right.Constant != 0;
						return Value.Const((isAnd ? l && r : l || r) ? 1 : 0);
					}

					if (left.IsConstant || right.IsConstant)
					{
						Value known = left.IsConstant ? left : right;
						Value other = left.IsConstant ? right : left;
						if (!RequireBit(other, left.IsConstant ? binary.Right : binary.Left, binary.Op)) return Value.Of(new[] { netlist.Const0 });
						bool k = known.Constant != 0;
						if (isAnd) return k ? other : Value.Of(new[] { netlist.Const0 });
						return k ? Value.Of(new[] { netlist.Const1 }) : other;
					}

					if (!RequireBit(left, binary.Left, binary.Op) || !RequireBit(right, binary.Right, binary.Op))
					{
						return Value.Of(new[] { netlist.Const0 });
					}

					return Value.Of(new[] { isAnd ? And(left.Bits[0], right.Bits[0]) : Or(left.Bits[0], right.Bits[0]) });
				}

				case "<<":
				case ">>":
				{
					int width = Math.Max(contextWidth, InferWidthSafe(binary.Left, work, constantOnly));
					Value value = Synthesize(binary.Left, work, width, constantOnly);
					if (!TryConstant(binary.Right, work, out long amount)) return value;
					if (amount < 0)
					{
						Error(binary.Right, "Shift amounts cannot be negative.");
						return value;
					}

					bool left = binary.Op == "<<";
					if (value.IsConstant)
					{
						long shifted = left ? value.Constant << (int)Math.Min(amount, 62) : value.Constant >> (int)Math.Min(amount, 62);
						if (width > 0 && width < 63) shifted &= (1L << width) - 1;
						return Value.Const(shifted);
					}

					Net[] bits = Resize(value.Bits, Math.Max(width, value.Width));
					Net[] result = new Net[bits.Length];
					for (int i = 0; i < result.Length; i++)
					{
						long source = left ? i - amount : i + amount;
						result[i] = source >= 0 && source < bits.Length ? bits[source] : netlist.Const0;
					}

					return Value.Of(result);
				}

				case "&":
				case "|":
				case "^":
				case "+":
				case "-":
				{
					int width = constantOnly ? 0 : Math.Max(contextWidth, Math.Max(InferWidth(binary.Left, work), InferWidth(binary.Right, work)));
					Value left = Synthesize(binary.Left, work, width, constantOnly);
					Value right = Synthesize(binary.Right, work, width, constantOnly);
					if (left.IsConstant && right.IsConstant)
					{
						long result = binary.Op switch
						{
							"&" => left.Constant & right.Constant,
							"|" => left.Constant | right.Constant,
							"^" => left.Constant ^ right.Constant,
							"+" => left.Constant + right.Constant,
							_ => left.Constant - right.Constant
						};

						if (width > 0 && width < 63) result &= (1L << width) - 1;
						return Value.Const(result);
					}

					width = Math.Max(width, Math.Max(left.Width, right.Width));
					Net[] a = left.IsConstant ? ConstantBits(left.Constant, width, binary.Left) : Resize(left.Bits, width);
					Net[] b = right.IsConstant ? ConstantBits(right.Constant, width, binary.Right) : Resize(right.Bits, width);
					if (a == null || b == null) return Value.Of(ConstantBits(0, Math.Max(1, width), binary));

					switch (binary.Op)
					{
						case "+": return Value.Of(AddOrSubtract(a, b, subtract: false));
						case "-": return Value.Of(AddOrSubtract(a, b, subtract: true));
					}

					Net[] bits = new Net[width];
					for (int i = 0; i < width; i++)
					{
						currentSlice = i;
						bits[i] = binary.Op switch
						{
							"&" => And(a[i], b[i]),
							"|" => Or(a[i], b[i]),
							_ => Xor(a[i], b[i])
						};
					}

					currentSlice = -1;
					return Value.Of(bits);
				}

				default:
				{
					// Comparisons.
					int width = constantOnly ? 0 : Math.Max(InferWidth(binary.Left, work), InferWidth(binary.Right, work));
					Value left = Synthesize(binary.Left, work, width, constantOnly);
					Value right = Synthesize(binary.Right, work, width, constantOnly);
					if (left.IsConstant && right.IsConstant)
					{
						bool result = binary.Op switch
						{
							"==" => left.Constant == right.Constant,
							"!=" => left.Constant != right.Constant,
							"<" => left.Constant < right.Constant,
							">" => left.Constant > right.Constant,
							"<=" => left.Constant <= right.Constant,
							_ => left.Constant >= right.Constant
						};

						return Value.Const(result ? 1 : 0);
					}

					width = Math.Max(width, Math.Max(left.Width, right.Width));
					Net[] a = left.IsConstant ? ConstantBits(left.Constant, width, binary.Left) : Resize(left.Bits, width);
					Net[] b = right.IsConstant ? ConstantBits(right.Constant, width, binary.Right) : Resize(right.Bits, width);
					if (a == null || b == null) return Value.Of(new[] { netlist.Const0 });

					Net bit = binary.Op switch
					{
						"==" => Equal(a, b),
						"!=" => Not(Equal(a, b)),
						"<" => LessThan(a, b),
						">" => LessThan(b, a),
						"<=" => Not(LessThan(b, a)),
						_ => Not(LessThan(a, b))
					};

					return Value.Of(new[] { bit });
				}
			}
		}

		Value SynthesizeConditional(ConditionalExpr conditional, Work work, int contextWidth, bool constantOnly)
		{
			Value condition = Synthesize(conditional.Condition, work, 1, constantOnly);
			if (condition.IsConstant)
			{
				// Chosen when the circuit is built.
				return Synthesize(condition.Constant != 0 ? conditional.WhenTrue : conditional.WhenFalse, work, contextWidth, constantOnly);
			}

			if (!RequireBit(condition, conditional.Condition, "if")) return Value.Of(new[] { netlist.Const0 });

			int width = Math.Max(contextWidth, Math.Max(InferWidth(conditional.WhenTrue, work), InferWidth(conditional.WhenFalse, work)));
			Value whenTrue = Synthesize(conditional.WhenTrue, work, width);
			Value whenFalse = Synthesize(conditional.WhenFalse, work, width);
			width = Math.Max(width, Math.Max(whenTrue.Width, whenFalse.Width));
			if (width == 0)
			{
				Error(conditional, "Both choices are numbers without a width. Give one a width, e.g. u4(3).");
				return Value.Const(0);
			}

			Net[] a = whenTrue.IsConstant ? ConstantBits(whenTrue.Constant, width, conditional.WhenTrue) : Resize(whenTrue.Bits, width);
			Net[] b = whenFalse.IsConstant ? ConstantBits(whenFalse.Constant, width, conditional.WhenFalse) : Resize(whenFalse.Bits, width);
			if (a == null || b == null) return Value.Of(ConstantBits(0, width, conditional));

			Net select = condition.Bits[0];
			Net notSelect = Not(select);
			Net[] result = new Net[width];
			for (int i = 0; i < width; i++)
			{
				currentSlice = i;
				result[i] = Nand(Nand(a[i], select), Nand(b[i], notSelect));
			}

			currentSlice = -1;
			return Value.Of(result);
		}

		bool RequireBit(Value value, Expr at, string op)
		{
			if (value.IsConstant || value.Width == 1) return true;
			Error(at, op == "if"
				? $"The choice in 'a if select else b' must be a single bit, but this is {value.Width} bits."
				: $"'{op}' works on single bits, but this is {value.Width} bits. Use {(op == "not" ? "~" : op == "and" ? "&" : "|")} for every bit of a bus.");
			return false;
		}

		bool TryResolveSlice(SliceExpr slice, Work work, int width, out int low, out int high, bool report = true)
		{
			low = 0;
			high = width;
			if (slice.Low != null)
			{
				if (!(report ? TryConstant(slice.Low, work, out long l) : TryQuietConstant(slice.Low, work, out l))) return false;
				low = (int)(l < 0 ? l + width : l);
			}

			if (slice.High != null)
			{
				if (!(report ? TryConstant(slice.High, work, out long h) : TryQuietConstant(slice.High, work, out h))) return false;
				high = (int)(h < 0 ? h + width : h);
			}

			if (low < 0 || high > width || low >= high)
			{
				if (report)
				{
					Error(slice, $"Slice [{low}:{high}] does not select bits of this {width}-bit value. " +
					             "Slices work like Python: A[0:4] is bits 0, 1, 2 and 3.");
				}

				return false;
			}

			return true;
		}

		bool TryQuietConstant(Expr expr, Work work, out long value)
		{
			int errors = diagnostics.Count;
			try
			{
				Value result = Synthesize(expr, work, 0, constantOnly: true);
				value = result.Constant;
				bool ok = result.IsConstant && diagnostics.Count == errors;
				diagnostics.RemoveRange(errors, diagnostics.Count - errors);
				return ok;
			}
			catch (ElaborationStop)
			{
				diagnostics.RemoveRange(errors, diagnostics.Count - errors);
				value = 0;
				return false;
			}
		}

		Value Bits(Value value, int width, Node at) => value.IsConstant ? Value.Of(ConstantBits(value.Constant, width, at)) : value;

		Net[] Resize(Net[] bits, int width)
		{
			if (bits.Length == width) return bits;
			Net[] result = new Net[width];
			for (int i = 0; i < width; i++) result[i] = i < bits.Length ? bits[i] : netlist.Const0;
			return result;
		}

		// ---- Gates ----

		Net Nand(Net a, Net b, bool explicitGate = false)
		{
			NandCell cell = new()
			{
				A = a,
				B = b,
				Explicit = explicitGate,
				Statement = currentStatement,
				Slice = currentSlice,
				Line = currentLine
			};

			Net output = netlist.NewNet(NetKind.Nand);
			output.Nand = cell;
			cell.Out = output;
			netlist.Nands.Add(cell);
			return output;
		}

		Net Not(Net a) => Nand(a, a);
		Net And(Net a, Net b) => Not(Nand(a, b));
		Net Or(Net a, Net b) => Nand(Not(a), Not(b));

		Net Xor(Net a, Net b)
		{
			Net both = Nand(a, b);
			return Nand(Nand(a, both), Nand(b, both));
		}

		// Ripple-carry adder of nine-NAND full adders. Subtraction adds the inverted
		// operand plus one.
		Net[] AddOrSubtract(Net[] a, Net[] b, bool subtract)
		{
			Net carry = subtract ? netlist.Const1 : netlist.Const0;
			Net[] sum = new Net[a.Length];
			for (int i = 0; i < a.Length; i++)
			{
				currentSlice = i;
				Net bi = subtract ? Not(b[i]) : b[i];
				FullAdder(a[i], bi, carry, out sum[i], out carry);
			}

			currentSlice = -1;
			return sum;
		}

		void FullAdder(Net a, Net b, Net carryIn, out Net sum, out Net carryOut)
		{
			Net n1 = Nand(a, b);
			Net n2 = Nand(a, n1);
			Net n3 = Nand(b, n1);
			Net half = Nand(n2, n3);
			Net n5 = Nand(half, carryIn);
			Net n6 = Nand(half, n5);
			Net n7 = Nand(carryIn, n5);
			sum = Nand(n6, n7);
			carryOut = Nand(n1, n5);
		}

		Net Equal(Net[] a, Net[] b)
		{
			// Equal when no bit differs: NOT(d0 OR d1 OR ...), built as a balanced tree.
			List<Net> differences = new();
			for (int i = 0; i < a.Length; i++)
			{
				currentSlice = i;
				differences.Add(Xor(a[i], b[i]));
			}

			currentSlice = -1;
			return Not(OrTree(differences));
		}

		Net OrTree(List<Net> inputs)
		{
			if (inputs.Count == 1) return inputs[0];
			List<Net> next = new();
			for (int i = 0; i + 1 < inputs.Count; i += 2) next.Add(Or(inputs[i], inputs[i + 1]));
			if (inputs.Count % 2 == 1) next.Add(inputs[^1]);
			return OrTree(next);
		}

		// Unsigned a < b: the borrow out of a - b, i.e. no carry out of a + ~b + 1.
		Net LessThan(Net[] a, Net[] b)
		{
			Net carry = netlist.Const1;
			for (int i = 0; i < a.Length; i++)
			{
				currentSlice = i;
				Net nb = Not(b[i]);
				Net n1 = Nand(a[i], nb);
				Net half = Xor(a[i], nb);
				carry = Nand(n1, Nand(half, carry));
			}

			currentSlice = -1;
			return Not(carry);
		}

		// ---- Final checks ----

		void CheckComplete()
		{
			foreach (Symbol symbol in symbols.Values)
			{
				if (symbol is not SignalSymbol signal || signal.IsInput) continue;
				if (signal.Bits == null)
				{
					if (signal.Declared) Error(signal.Line, 0, $"'{signal.Name}' is declared but never assigned.");
					continue;
				}

				List<int> missing = new();
				for (int i = 0; i < signal.Width; i++)
				{
					if (!signal.BitAssigned[i]) missing.Add(i);
				}

				if (missing.Count == 0) continue;
				if (signal.IsOutput)
				{
					Error(signal.Line, 0, missing.Count == signal.Width
						? $"Output '{signal.Name}' is never assigned. Add a line such as '{signal.Name} = ...'."
						: $"Bits {string.Join(", ", missing)} of output '{signal.Name}' are never assigned.");
				}
				else if (signal.Declared || signal.Used)
				{
					Error(signal.Line, 0, missing.Count == signal.Width
						? $"'{signal.Name}' is used but never assigned."
						: $"Bits {string.Join(", ", missing)} of '{signal.Name}' are never assigned.");
				}
			}

			foreach (ComponentCell cell in netlist.Components)
			{
				List<string> unconnected = new();
				for (int pin = 0; pin < cell.Inputs.Length; pin++)
				{
					if (cell.Inputs[pin] == null) unconnected.Add(PinSpelling(cell.Description.InputPins[pin].Name));
				}

				if (unconnected.Count > 0)
				{
					Error(cell.Line, 0, $"{cell.Description.Name} is missing input{(unconnected.Count == 1 ? "" : "s")} {string.Join(", ", unconnected)}.");
				}
			}
		}

		void CheckAliasCycles()
		{
			HashSet<Net> done = new();
			foreach (Net net in netlist.Nets)
			{
				if (net.Alias == null || done.Contains(net)) continue;

				HashSet<Net> path = new();
				Net current = net;
				while (current.Alias != null && !done.Contains(current))
				{
					if (!path.Add(current))
					{
						string names = string.Join(" -> ", path.Where(n => n.Label != null).Select(n => n.Label).Distinct().Take(6));
						Error(current.Line, 0, $"These signals are wired to each other in a loop with no gate in between: {names}.");
						return;
					}

					current = current.Alias;
				}

				done.UnionWith(path);
			}
		}

		// ---- Optimisation ----

		// Folds constants, merges identical gates, removes double inversions and drops gates
		// nothing reads. Gates written as NAND(...) are never touched.
		void Optimize()
		{
			bool changed = true;
			Dictionary<(int, int), NandCell> seen = new();
			while (changed)
			{
				changed = false;
				seen.Clear();
				foreach (NandCell cell in netlist.Nands)
				{
					if (!cell.Alive) continue;
					Net a = Netlist.Find(cell.A);
					Net b = Netlist.Find(cell.B);
					cell.A = a;
					cell.B = b;
					if (cell.Explicit) continue;

					Net replacement = null;
					if (a == netlist.Const0 || b == netlist.Const0)
					{
						replacement = netlist.Const1;
					}
					else if (a == netlist.Const1 && b == netlist.Const1)
					{
						replacement = netlist.Const0;
					}
					else if (a == netlist.Const1 || b == netlist.Const1)
					{
						Net other = a == netlist.Const1 ? b : a;
						cell.A = cell.B = a = b = other;
						changed = true;
					}

					if (replacement == null && a == b && a.Kind == NetKind.Nand && a.Nand.Alive && !a.Nand.Explicit &&
					    Netlist.Find(a.Nand.A) == Netlist.Find(a.Nand.B))
					{
						// NOT(NOT(x)) is x.
						Net inner = Netlist.Find(a.Nand.A);
						if (inner != cell.Out) replacement = inner;
					}

					if (replacement == null)
					{
						(int, int) key = a.Id < b.Id ? (a.Id, b.Id) : (b.Id, a.Id);
						if (seen.TryGetValue(key, out NandCell existing) && existing.Alive && existing != cell)
						{
							replacement = existing.Out;
						}
						else
						{
							seen[key] = cell;
						}
					}

					if (replacement != null && replacement != cell.Out)
					{
						cell.Alive = false;
						cell.Out.Alias = replacement;
						changed = true;
					}
				}
			}

			// Keep only gates that something reads.
			HashSet<NandCell> live = new();
			Stack<Net> pending = new();
			foreach (PortSignal output in netlist.Outputs)
			{
				foreach (Net bit in output.Bits) pending.Push(bit);
			}

			foreach (ComponentCell component in netlist.Components)
			{
				foreach (Net[] input in component.Inputs)
				{
					if (input == null) continue;
					foreach (Net bit in input) pending.Push(bit);
				}
			}

			foreach (NandCell cell in netlist.Nands)
			{
				if (cell.Alive && cell.Explicit) pending.Push(cell.Out);
			}

			while (pending.Count > 0)
			{
				Net net = Netlist.Find(pending.Pop());
				if (net.Kind == NetKind.Junction)
				{
					foreach (Net driver in net.Drivers) pending.Push(driver);
					continue;
				}

				if (net.Kind != NetKind.Nand || !live.Add(net.Nand)) continue;
				pending.Push(net.Nand.A);
				pending.Push(net.Nand.B);
			}

			foreach (NandCell cell in netlist.Nands)
			{
				if (!live.Contains(cell)) cell.Alive = false;
			}

			netlist.Nands.RemoveAll(cell => !cell.Alive);
		}

		// ---- Helpers ----

		internal static string Normalize(string text)
		{
			if (string.IsNullOrEmpty(text)) return string.Empty;
			char[] buffer = new char[text.Length];
			int n = 0;
			foreach (char c in text)
			{
				if (char.IsLetterOrDigit(c)) buffer[n++] = char.ToUpperInvariant(c);
			}

			return new string(buffer, 0, n);
		}

		static string Suggest(string name, IEnumerable<string> candidates)
		{
			string best = null;
			int bestDistance = int.MaxValue;
			foreach (string candidate in candidates.Distinct())
			{
				int distance = Distance(name.ToUpperInvariant(), candidate.ToUpperInvariant());
				if (distance < bestDistance)
				{
					bestDistance = distance;
					best = candidate;
				}
			}

			return best != null && bestDistance <= Math.Max(1, name.Length / 3) ? $" Did you mean '{best}'?" : string.Empty;
		}

		static int Distance(string a, string b)
		{
			int[] previous = new int[b.Length + 1];
			int[] current = new int[b.Length + 1];
			for (int j = 0; j <= b.Length; j++) previous[j] = j;
			for (int i = 1; i <= a.Length; i++)
			{
				current[0] = i;
				for (int j = 1; j <= b.Length; j++)
				{
					int cost = a[i - 1] == b[j - 1] ? 0 : 1;
					current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
				}

				(previous, current) = (current, previous);
			}

			return previous[b.Length];
		}
	}
}
