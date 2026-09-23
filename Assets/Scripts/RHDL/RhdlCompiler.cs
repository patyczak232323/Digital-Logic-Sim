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

	public static class RhdlCompiler
	{
		sealed class PortDecl
		{
			public string Name;
			public PinBitCount Bits;
			public bool IsInput;
			public int Line;
			public int OwnerID;
		}

		sealed class InstanceDecl
		{
			public string TypeName;
			public string Name;
			public int Line;
			public int ID;
			public ChipDescription Description;
			public int Level;
			public int LayoutGroup;
			public Vector2 Position;
		}

		sealed class ConnectionDecl
		{
			public string SourceText;
			public string TargetText;
			public int Line;
			public Endpoint Source;
			public Endpoint Target;
			public string SourceInstance;
			public string TargetInstance;
		}

		sealed class AssignmentDecl
		{
			public string TargetText;
			public string ExpressionText;
			public int Line;
		}

		abstract class ExprNode { }

		sealed class RefExpr : ExprNode
		{
			public readonly string Name;
			public RefExpr(string name) => Name = name;
		}

		sealed class UnaryExpr : ExprNode
		{
			public readonly string Op;
			public readonly ExprNode Value;
			public UnaryExpr(string op, ExprNode value) { Op = op; Value = value; }
		}

		sealed class BinaryExpr : ExprNode
		{
			public readonly string Op;
			public readonly ExprNode Left;
			public readonly ExprNode Right;
			public BinaryExpr(string op, ExprNode left, ExprNode right) { Op = op; Left = left; Right = right; }
		}

		readonly struct ExprToken
		{
			public readonly string Text;
			public readonly int Position;
			public ExprToken(string text, int position) { Text = text; Position = position; }
		}

		readonly struct Endpoint
		{
			public readonly PinAddress Address;
			public readonly PinBitCount Bits;

			public Endpoint(PinAddress address, PinBitCount bits)
			{
				Address = address;
				Bits = bits;
			}
		}

		public static RhdlCompileResult Compile(string source, ChipLibrary library)
		{
			RhdlLowerResult lowered = RhdlV3Lowerer.Lower(source, library);
			if (!lowered.Success) return new RhdlCompileResult(null, lowered.Diagnostics);

			RhdlCompileResult legacy = CompileLegacy(lowered.Source, library);
			if (legacy.Diagnostics.Length == 0) return legacy;

			List<RhdlDiagnostic> mapped = new();
			foreach (RhdlDiagnostic diagnostic in legacy.Diagnostics)
			{
				int line = lowered.MapGeneratedLine(diagnostic.Line);
				mapped.Add(new RhdlDiagnostic(line, diagnostic.Column, diagnostic.Message));
			}
			return new RhdlCompileResult(legacy.Description, mapped);
		}

		static RhdlCompileResult CompileLegacy(string source, ChipLibrary library)
		{
			List<RhdlDiagnostic> diagnostics = new();
			List<PortDecl> ports = new();
			List<InstanceDecl> instances = new();
			List<ConnectionDecl> connections = new();
			List<AssignmentDecl> assignments = new();

			if (library == null)
			{
				diagnostics.Add(new RhdlDiagnostic(0, "No active chip library."));
				return new RhdlCompileResult(null, diagnostics);
			}

			string chipName = null;
			string[] lines = (source ?? string.Empty).Replace("\r", string.Empty).Split('\n');

			for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
			{
				int lineNumber = lineIndex + 1;
				string line = StripComment(lines[lineIndex]).Trim();
				if (line.EndsWith(";")) line = line.Substring(0, line.Length - 1).Trim();
				if (line.Length == 0 || line == "{" || line == "}") continue;

				if (line.StartsWith("chip ", StringComparison.OrdinalIgnoreCase))
				{
					if (chipName != null)
					{
						diagnostics.Add(new RhdlDiagnostic(lineNumber, "Only one chip declaration is allowed per source."));
						continue;
					}

					string rest = line.Substring(5).Trim();
					int brace = rest.IndexOf('{');
					if (brace >= 0) rest = rest.Substring(0, brace).Trim();
					if (!ValidIdentifier(rest))
					{
						diagnostics.Add(new RhdlDiagnostic(lineNumber, "Invalid chip name. Use letters, digits and underscore."));
						continue;
					}

					chipName = rest;
					continue;
				}

				if (line.StartsWith("input ", StringComparison.OrdinalIgnoreCase) ||
				    line.StartsWith("output ", StringComparison.OrdinalIgnoreCase))
				{
					bool isInput = line.StartsWith("input ", StringComparison.OrdinalIgnoreCase);
					string rest = line.Substring(isInput ? 6 : 7).Trim();
					foreach (string tokenRaw in rest.Split(','))
					{
						string token = tokenRaw.Trim();
						if (!TryParsePort(token, out string name, out PinBitCount bits, out string error))
						{
							diagnostics.Add(new RhdlDiagnostic(lineNumber, error));
							continue;
						}

						ports.Add(new PortDecl
						{
							Name = name,
							Bits = bits,
							IsInput = isInput,
							Line = lineNumber
						});
					}
					continue;
				}

				if (line.StartsWith("connect ", StringComparison.OrdinalIgnoreCase))
				{
					string rest = line.Substring(8).Trim();
					int arrow = rest.IndexOf("->", StringComparison.Ordinal);
					if (arrow <= 0 || arrow >= rest.Length - 2)
					{
						diagnostics.Add(new RhdlDiagnostic(lineNumber, "Expected: connect SOURCE -> TARGET"));
						continue;
					}

					connections.Add(new ConnectionDecl
					{
						SourceText = rest.Substring(0, arrow).Trim(),
						TargetText = rest.Substring(arrow + 2).Trim(),
						Line = lineNumber
					});
					continue;
				}

				int assignmentEquals = line.IndexOf('=');
				if (assignmentEquals > 0)
				{
					if (line.IndexOf('=', assignmentEquals + 1) >= 0)
					{
						diagnostics.Add(new RhdlDiagnostic(lineNumber, "RHDL logic assignment uses a single '='."));
						continue;
					}

					string target = line.Substring(0, assignmentEquals).Trim();
					string expression = line.Substring(assignmentEquals + 1).Trim();
					if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(expression))
					{
						diagnostics.Add(new RhdlDiagnostic(lineNumber, "Expected: TARGET = expression"));
						continue;
					}

					assignments.Add(new AssignmentDecl
					{
						TargetText = target,
						ExpressionText = expression,
						Line = lineNumber
					});
					continue;
				}

				string declaration = line;
				if (declaration.StartsWith("use ", StringComparison.OrdinalIgnoreCase))
					declaration = declaration.Substring(4).Trim();

				string[] parts = declaration.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
				if (parts.Length == 2 && ValidIdentifier(parts[1]))
				{
					instances.Add(new InstanceDecl
					{
						TypeName = parts[0],
						Name = parts[1],
						Line = lineNumber
					});
					continue;
				}

				diagnostics.Add(new RhdlDiagnostic(lineNumber, $"Unrecognized statement: {line}"));
			}

			if (string.IsNullOrWhiteSpace(chipName))
				diagnostics.Add(new RhdlDiagnostic(0, "Missing 'chip NAME {' declaration."));

			SynthesizeAssignments(assignments, ports, instances, connections, diagnostics);
			ValidateNames(ports, instances, diagnostics);
			if (diagnostics.Count != 0) return new RhdlCompileResult(null, diagnostics);

			int nextOwnerID = 1;
			foreach (PortDecl port in ports) port.OwnerID = nextOwnerID++;
			foreach (InstanceDecl instance in instances)
			{
				instance.ID = nextOwnerID++;
				instance.Description = ResolveChipType(instance.TypeName, library);
				if (instance.Description == null)
				{
					diagnostics.Add(new RhdlDiagnostic(instance.Line, $"Unknown chip type '{instance.TypeName}'."));
				}
				else if (ChipDescription.NameMatch(instance.Description.Name, chipName))
				{
					diagnostics.Add(new RhdlDiagnostic(instance.Line, "A generated chip cannot directly instantiate itself."));
				}
			}

			if (diagnostics.Count != 0) return new RhdlCompileResult(null, diagnostics);

			Dictionary<string, PortDecl> portByName = ports.ToDictionary(p => Normalize(p.Name), StringComparer.OrdinalIgnoreCase);
			Dictionary<string, InstanceDecl> instanceByName = instances.ToDictionary(i => Normalize(i.Name), StringComparer.OrdinalIgnoreCase);

			foreach (ConnectionDecl connection in connections)
			{
				bool sourceOK = TryResolveEndpoint(
					connection.SourceText,
					asSource: true,
					portByName,
					instanceByName,
					out Endpoint sourceEndpoint,
					out string sourceInstance,
					out string sourceError);
				bool targetOK = TryResolveEndpoint(
					connection.TargetText,
					asSource: false,
					portByName,
					instanceByName,
					out Endpoint targetEndpoint,
					out string targetInstance,
					out string targetError);

				if (!sourceOK) diagnostics.Add(new RhdlDiagnostic(connection.Line, sourceError));
				if (!targetOK) diagnostics.Add(new RhdlDiagnostic(connection.Line, targetError));
				if (!sourceOK || !targetOK) continue;

				if (sourceEndpoint.Bits != targetEndpoint.Bits)
				{
					diagnostics.Add(new RhdlDiagnostic(
						connection.Line,
						$"Bit-width mismatch: {connection.SourceText} is {(int)sourceEndpoint.Bits}-bit, " +
						$"{connection.TargetText} is {(int)targetEndpoint.Bits}-bit."));
					continue;
				}

				connection.Source = sourceEndpoint;
				connection.Target = targetEndpoint;
				connection.SourceInstance = sourceInstance;
				connection.TargetInstance = targetInstance;
			}

			ValidateDrivenTargets(connections, diagnostics);
			if (diagnostics.Count != 0) return new RhdlCompileResult(null, diagnostics);

			AutoLayout(instances, connections, ports);
			PinDescription[] inputPins = CreateRootPins(ports.Where(p => p.IsInput).ToList(), instances, left: true);
			PinDescription[] outputPins = CreateRootPins(ports.Where(p => !p.IsInput).ToList(), instances, left: false);

			SubChipDescription[] subChips = instances.Select(instance =>
				new SubChipDescription(
					instance.Description.Name,
					instance.ID,
					string.Empty,
					instance.Position,
					Array.Empty<OutputPinColourInfo>(),
					null)).ToArray();

			// RHDL-generated schematics use an orthogonal "block" routing style.
			// The logical endpoints are unchanged; only visual bend points are added.
			WireDescription[] wires = BuildBlockWires(connections, instances, inputPins, outputPins);

			Vector2 chipSize = SubChipInstance.CalculateMinChipSize(inputPins, outputPins, chipName);
			chipSize.x = Mathf.Max(chipSize.x, 1.5f);

			ChipDescription description = new()
			{
				DLSVersion = Main.DLSVersion.ToString(),
				Name = chipName,
				NameLocation = NameDisplayLocation.Centre,
				ChipType = ChipType.Custom,
				CacheMode = ChipCacheMode.Auto,
				Size = chipSize,
				Colour = new Color(0.18f, 0.31f, 0.58f),
				InputPins = inputPins,
				OutputPins = outputPins,
				SubChips = subChips,
				Wires = wires,
				Displays = Array.Empty<DisplayDescription>()
			};

			return new RhdlCompileResult(description, diagnostics);
		}

		static void SynthesizeAssignments(
			List<AssignmentDecl> assignments,
			List<PortDecl> ports,
			List<InstanceDecl> instances,
			List<ConnectionDecl> connections,
			List<RhdlDiagnostic> diagnostics)
		{
			HashSet<string> usedInstanceNames = new(instances.Select(i => Normalize(i.Name)), StringComparer.OrdinalIgnoreCase);
			HashSet<string> portNames = new(ports.Select(p => Normalize(p.Name)), StringComparer.OrdinalIgnoreCase);
			int generatedCounter = 0;

			foreach (AssignmentDecl assignment in assignments)
			{
				if (!portNames.Contains(Normalize(assignment.TargetText)))
				{
					diagnostics.Add(new RhdlDiagnostic(
						assignment.Line,
						$"Logic assignment target '{assignment.TargetText}' must currently be a declared output."));
					continue;
				}

				PortDecl targetPort = ports.First(p => Normalize(p.Name) == Normalize(assignment.TargetText));
				if (targetPort.IsInput)
				{
					diagnostics.Add(new RhdlDiagnostic(
						assignment.Line,
						$"Cannot assign to input '{assignment.TargetText}'."));
					continue;
				}

				if (targetPort.Bits != PinBitCount.Bit1)
				{
					diagnostics.Add(new RhdlDiagnostic(
						assignment.Line,
						"RHDL logic expressions currently synthesize 1-bit signals. Use structural connect for 4/8-bit buses."));
					continue;
				}

				if (!TryParseExpression(assignment.ExpressionText, out ExprNode expression, out string parseError))
				{
					diagnostics.Add(new RhdlDiagnostic(assignment.Line, parseError));
					continue;
				}

				string source = SynthesizeExpression(expression, assignment.Line);
				if (source == null) continue;

				connections.Add(new ConnectionDecl
				{
					SourceText = source,
					TargetText = assignment.TargetText,
					Line = assignment.Line
				});
			}

			string SynthesizeExpression(ExprNode node, int line)
			{
				if (node is RefExpr reference) return reference.Name;

				if (node is UnaryExpr unary)
				{
					string input = SynthesizeExpression(unary.Value, line);
					if (input == null) return null;
					if (!string.Equals(unary.Op, "NOT", StringComparison.OrdinalIgnoreCase))
					{
						diagnostics.Add(new RhdlDiagnostic(line, $"Unsupported unary operator '{unary.Op}'."));
						return null;
					}
					return CreateNand(input, input, line);
				}

				if (node is BinaryExpr binary)
				{
					string left = SynthesizeExpression(binary.Left, line);
					string right = SynthesizeExpression(binary.Right, line);
					if (left == null || right == null) return null;

					switch (binary.Op.ToUpperInvariant())
					{
						case "AND":
						{
							string n = CreateNand(left, right, line);
							return CreateNand(n, n, line);
						}
						case "OR":
						{
							string notLeft = CreateNand(left, left, line);
							string notRight = CreateNand(right, right, line);
							return CreateNand(notLeft, notRight, line);
						}
						case "XOR":
						{
							string common = CreateNand(left, right, line);
							string a = CreateNand(left, common, line);
							string b = CreateNand(right, common, line);
							return CreateNand(a, b, line);
						}
						default:
							diagnostics.Add(new RhdlDiagnostic(line, $"Unsupported binary operator '{binary.Op}'."));
							return null;
					}
				}

				diagnostics.Add(new RhdlDiagnostic(line, "Invalid expression node."));
				return null;
			}

			string CreateNand(string a, string b, int line)
			{
				string name;
				do
				{
					name = $"__rhdl_logic_{generatedCounter++}";
				}
				while (!usedInstanceNames.Add(Normalize(name)));

				instances.Add(new InstanceDecl
				{
					TypeName = "NAND",
					Name = name,
					Line = line
				});

				connections.Add(new ConnectionDecl { SourceText = a, TargetText = name + ".IN_A", Line = line });
				connections.Add(new ConnectionDecl { SourceText = b, TargetText = name + ".IN_B", Line = line });
				return name + ".OUT";
			}
		}

		static bool TryParseExpression(string text, out ExprNode expression, out string error)
		{
			expression = null;
			error = null;
			string localError = null;

			List<ExprToken> tokens = TokenizeExpression(text, out localError);
			if (tokens == null)
			{
				error = localError;
				return false;
			}

			int index = 0;
			expression = ParseOr();
			if (expression == null)
			{
				error = localError;
				return false;
			}

			if (index != tokens.Count)
			{
				error = $"Unexpected token '{tokens[index].Text}' in expression.";
				expression = null;
				return false;
			}

			error = localError;
			return true;

			ExprNode ParseOr()
			{
				ExprNode left = ParseXor();
				while (left != null && Match("OR", "|"))
				{
					ExprNode right = ParseXor();
					if (right == null) return null;
					left = new BinaryExpr("OR", left, right);
				}
				return left;
			}

			ExprNode ParseXor()
			{
				ExprNode left = ParseAnd();
				while (left != null && Match("XOR", "^"))
				{
					ExprNode right = ParseAnd();
					if (right == null) return null;
					left = new BinaryExpr("XOR", left, right);
				}
				return left;
			}

			ExprNode ParseAnd()
			{
				ExprNode left = ParseUnary();
				while (left != null && Match("AND", "&"))
				{
					ExprNode right = ParseUnary();
					if (right == null) return null;
					left = new BinaryExpr("AND", left, right);
				}
				return left;
			}

			ExprNode ParseUnary()
			{
				if (Match("NOT", "!"))
				{
					ExprNode value = ParseUnary();
					if (value == null)
					{
						localError = "Expected expression after NOT.";
						return null;
					}
					return new UnaryExpr("NOT", value);
				}
				return ParsePrimary();
			}

			ExprNode ParsePrimary()
			{
				if (Match("("))
				{
					ExprNode inner = ParseOr();
					if (inner == null) return null;
					if (!Match(")"))
					{
						localError = "Missing ')' in expression.";
						return null;
					}
					return inner;
				}

				if (index >= tokens.Count)
				{
					localError = "Expected signal name or expression.";
					return null;
				}

				string token = tokens[index].Text;
				if (token is ")" or "&" or "|" or "^")
				{
					localError = $"Unexpected token '{token}'.";
					return null;
				}

				index++;
				return new RefExpr(token);
			}

			bool Match(params string[] options)
			{
				if (index >= tokens.Count) return false;
				foreach (string option in options)
				{
					if (string.Equals(tokens[index].Text, option, StringComparison.OrdinalIgnoreCase))
					{
						index++;
						return true;
					}
				}
				return false;
			}
		}

		static List<ExprToken> TokenizeExpression(string text, out string error)
		{
			error = null;
			List<ExprToken> tokens = new();
			for (int i = 0; i < text.Length;)
			{
				char c = text[i];
				if (char.IsWhiteSpace(c))
				{
					i++;
					continue;
				}

				if (c is '(' or ')' or '&' or '|' or '^' or '!')
				{
					tokens.Add(new ExprToken(c.ToString(), i));
					i++;
					continue;
				}

				if (char.IsLetter(c) || c == '_')
				{
					int start = i++;
					while (i < text.Length)
					{
						char next = text[i];
						if (char.IsLetterOrDigit(next) || next is '_' or '.')
						{
							i++;
							continue;
						}
						break;
					}
					tokens.Add(new ExprToken(text.Substring(start, i - start), start));
					continue;
				}

				error = $"Unexpected character '{c}' in logic expression.";
				return null;
			}

			if (tokens.Count == 0)
			{
				error = "Expression is empty.";
				return null;
			}

			return tokens;
		}

		static string StripComment(string line)
		{
			int comment = line.IndexOf("//", StringComparison.Ordinal);
			return comment >= 0 ? line.Substring(0, comment) : line;
		}

		static bool TryParsePort(string token, out string name, out PinBitCount bits, out string error)
		{
			name = token;
			bits = PinBitCount.Bit1;
			error = string.Empty;

			int bracket = token.IndexOf('[');
			if (bracket >= 0)
			{
				int close = token.IndexOf(']', bracket + 1);
				if (close < 0 || close != token.Length - 1)
				{
					error = $"Invalid port declaration '{token}'.";
					return false;
				}

				name = token.Substring(0, bracket).Trim();
				string widthText = token.Substring(bracket + 1, close - bracket - 1).Trim();
				if (!int.TryParse(widthText, out int width) || (width != 1 && width != 4 && width != 8))
				{
					error = "RHDL v0.1 supports port widths [1], [4] and [8].";
					return false;
				}
				bits = (PinBitCount)width;
			}

			if (!ValidIdentifier(name))
			{
				error = $"Invalid port name '{name}'.";
				return false;
			}

			return true;
		}

		static void ValidateNames(List<PortDecl> ports, List<InstanceDecl> instances, List<RhdlDiagnostic> diagnostics)
		{
			HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
			foreach (PortDecl port in ports)
			{
				if (!names.Add(Normalize(port.Name)))
					diagnostics.Add(new RhdlDiagnostic(port.Line, $"Duplicate name '{port.Name}'."));
			}

			foreach (InstanceDecl instance in instances)
			{
				if (!names.Add(Normalize(instance.Name)))
					diagnostics.Add(new RhdlDiagnostic(instance.Line, $"Duplicate name '{instance.Name}'."));
			}
		}

		static ChipDescription ResolveChipType(string typeToken, ChipLibrary library)
		{
			string normalized = Normalize(typeToken);
			foreach (ChipDescription description in library.allChips)
			{
				if (Normalize(description.Name) == normalized) return description;
			}

			// Hidden builtin chips are intentionally not considered here. RHDL v0.1
			// only exposes chips that can normally be selected from the editor/library.
			return null;
		}

		static bool TryResolveEndpoint(
			string text,
			bool asSource,
			Dictionary<string, PortDecl> ports,
			Dictionary<string, InstanceDecl> instances,
			out Endpoint endpoint,
			out string instanceName,
			out string error)
		{
			endpoint = default;
			instanceName = null;
			error = null;

			int dot = text.IndexOf('.');
			if (dot < 0)
			{
				string key = Normalize(text);
				if (!ports.TryGetValue(key, out PortDecl port))
				{
					error = $"Unknown port '{text}'.";
					return false;
				}

				if (asSource != port.IsInput)
				{
					error = asSource
						? $"'{text}' is an output and cannot drive a connection."
						: $"'{text}' is an input and cannot be driven.";
					return false;
				}

				endpoint = new Endpoint(new PinAddress(port.OwnerID, 0), port.Bits);
				return true;
			}

			string ownerToken = text.Substring(0, dot).Trim();
			string pinToken = text.Substring(dot + 1).Trim();
			if (!instances.TryGetValue(Normalize(ownerToken), out InstanceDecl instance))
			{
				error = $"Unknown instance '{ownerToken}'.";
				return false;
			}

			PinDescription[] candidates = asSource ? instance.Description.OutputPins : instance.Description.InputPins;
			PinDescription? match = null;
			string normalizedPin = Normalize(pinToken);
			foreach (PinDescription pin in candidates)
			{
				if (Normalize(pin.Name) == normalizedPin)
				{
					match = pin;
					break;
				}
			}

			if (!match.HasValue)
			{
				string direction = asSource ? "output" : "input";
				error = $"Chip '{instance.Description.Name}' has no {direction} pin '{pinToken}'.";
				return false;
			}

			PinDescription resolved = match.Value;
			endpoint = new Endpoint(new PinAddress(instance.ID, resolved.ID), resolved.BitCount);
			instanceName = instance.Name;
			return true;
		}

		static void ValidateDrivenTargets(List<ConnectionDecl> connections, List<RhdlDiagnostic> diagnostics)
		{
			HashSet<string> targets = new(StringComparer.Ordinal);
			foreach (ConnectionDecl connection in connections)
			{
				string target = connection.Target.Address.PinOwnerID + ":" + connection.Target.Address.PinID;
				if (!targets.Add(target))
					diagnostics.Add(new RhdlDiagnostic(connection.Line, $"Target '{connection.TargetText}' is driven more than once."));
			}
		}

		static void AutoLayout(
			List<InstanceDecl> instances,
			List<ConnectionDecl> connections,
			List<PortDecl> ports)
		{
			if (instances.Count == 0) return;

			// The layout follows the conventions normally used on a hand-drawn
			// schematic: signal flow is left-to-right, gates belonging to one
			// output cone form a horizontal band, and feedback stays in one block.
			Dictionary<string, InstanceDecl> byName =
				instances.ToDictionary(i => Normalize(i.Name), StringComparer.OrdinalIgnoreCase);
			Dictionary<int, InstanceDecl> byId = instances.ToDictionary(i => i.ID);
			Dictionary<InstanceDecl, List<InstanceDecl>> outgoing =
				instances.ToDictionary(i => i, _ => new List<InstanceDecl>());
			Dictionary<InstanceDecl, List<InstanceDecl>> incoming =
				instances.ToDictionary(i => i, _ => new List<InstanceDecl>());

			foreach (ConnectionDecl connection in connections)
			{
				if (string.IsNullOrEmpty(connection.SourceInstance) ||
				    string.IsNullOrEmpty(connection.TargetInstance))
					continue;

				if (!byName.TryGetValue(Normalize(connection.SourceInstance), out InstanceDecl source) ||
				    !byName.TryGetValue(Normalize(connection.TargetInstance), out InstanceDecl target) ||
				    source == target || outgoing[source].Contains(target))
					continue;

				outgoing[source].Add(target);
				incoming[target].Add(source);
			}

			// Collapse feedback loops before assigning columns. A latch or another
			// stateful loop is therefore treated as one intentional functional block
			// instead of making every member look like an unrelated level-zero gate.
			List<List<InstanceDecl>> components = FindStrongComponents(instances, outgoing);
			Dictionary<InstanceDecl, int> componentOf = new();
			for (int component = 0; component < components.Count; component++)
				foreach (InstanceDecl instance in components[component])
					componentOf[instance] = component;

			Dictionary<int, HashSet<int>> componentOutgoing = new();
			Dictionary<int, int> componentIndegree = new();
			Dictionary<int, int> componentLevel = new();
			for (int component = 0; component < components.Count; component++)
			{
				componentOutgoing[component] = new HashSet<int>();
				componentIndegree[component] = 0;
				componentLevel[component] = 0;
			}

			foreach (InstanceDecl source in instances)
			{
				int sourceComponent = componentOf[source];
				foreach (InstanceDecl target in outgoing[source])
				{
					int targetComponent = componentOf[target];
					if (sourceComponent == targetComponent ||
					    !componentOutgoing[sourceComponent].Add(targetComponent))
						continue;
					componentIndegree[targetComponent]++;
				}
			}

			List<int> ready = componentIndegree
				.Where(pair => pair.Value == 0)
				.Select(pair => pair.Key)
				.ToList();
			while (ready.Count > 0)
			{
				ready.Sort(CompareComponents);
				int current = ready[0];
				ready.RemoveAt(0);
				foreach (int next in componentOutgoing[current])
				{
					componentLevel[next] = Math.Max(componentLevel[next], componentLevel[current] + 1);
					componentIndegree[next]--;
					if (componentIndegree[next] == 0) ready.Add(next);
				}
			}

			foreach (InstanceDecl instance in instances)
				instance.Level = componentLevel[componentOf[instance]];

			int CompareComponents(int a, int b)
			{
				InstanceDecl firstA = components[a]
					.OrderBy(i => i.Line)
					.ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
					.First();
				InstanceDecl firstB = components[b]
					.OrderBy(i => i.Line)
					.ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
					.First();
				int line = firstA.Line.CompareTo(firstB.Line);
				return line != 0
					? line
					: StringComparer.OrdinalIgnoreCase.Compare(firstA.Name, firstB.Name);
			}

			// Work backwards from declared outputs. Gates contributing to the same
			// output keep the same band even when other gate types are mixed in.
			Dictionary<int, int> outputOrder = new();
			int outputCount = 0;
			foreach (PortDecl port in ports.Where(p => !p.IsInput))
				outputOrder[port.OwnerID] = outputCount++;

			Dictionary<InstanceDecl, int> group =
				instances.ToDictionary(i => i, _ => int.MaxValue);
			Queue<InstanceDecl> groupQueue = new();
			foreach (ConnectionDecl connection in connections)
			{
				if (!outputOrder.TryGetValue(connection.Target.Address.PinOwnerID, out int output) ||
				    !byId.TryGetValue(connection.Source.Address.PinOwnerID, out InstanceDecl source) ||
				    output >= group[source])
					continue;
				group[source] = output;
				groupQueue.Enqueue(source);
			}

			while (groupQueue.Count > 0)
			{
				InstanceDecl current = groupQueue.Dequeue();
				foreach (InstanceDecl parent in incoming[current])
				{
					if (group[current] >= group[parent]) continue;
					group[parent] = group[current];
					groupQueue.Enqueue(parent);
				}
			}

			int nextFallbackGroup = outputCount;
			foreach (InstanceDecl instance in instances
				.OrderBy(i => i.Line)
				.ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
			{
				if (group[instance] != int.MaxValue) continue;

				int fallbackGroup = nextFallbackGroup++;
				Queue<InstanceDecl> weakQueue = new();
				weakQueue.Enqueue(instance);
				group[instance] = fallbackGroup;
				while (weakQueue.Count > 0)
				{
					InstanceDecl current = weakQueue.Dequeue();
					foreach (InstanceDecl neighbour in incoming[current].Concat(outgoing[current]))
					{
						if (group[neighbour] != int.MaxValue) continue;
						group[neighbour] = fallbackGroup;
						weakQueue.Enqueue(neighbour);
					}
				}
			}
			foreach (InstanceDecl instance in instances) instance.LayoutGroup = group[instance];

			int maxLevel = instances.Max(i => i.Level);
			Dictionary<InstanceDecl, float> order =
				instances.ToDictionary(i => i, i => (float)i.Line);

			// Input/output declaration order acts only as a gentle ordering hint.
			// It never reorders the root-pin arrays, so saved pin IDs stay stable.
			Dictionary<int, float> portOrder = new();
			int portIndex = 0;
			foreach (PortDecl port in ports.Where(p => p.IsInput)) portOrder[port.OwnerID] = portIndex++;
			portIndex = 0;
			foreach (PortDecl port in ports.Where(p => !p.IsInput)) portOrder[port.OwnerID] = portIndex++;
			Dictionary<InstanceDecl, List<float>> rootHints =
				instances.ToDictionary(i => i, _ => new List<float>());
			foreach (ConnectionDecl connection in connections)
			{
				if (portOrder.TryGetValue(connection.Source.Address.PinOwnerID, out float sourceHint) &&
				    byId.TryGetValue(connection.Target.Address.PinOwnerID, out InstanceDecl target))
					rootHints[target].Add(sourceHint);
				if (portOrder.TryGetValue(connection.Target.Address.PinOwnerID, out float targetHint) &&
				    byId.TryGetValue(connection.Source.Address.PinOwnerID, out InstanceDecl source))
					rootHints[source].Add(targetHint);
			}
			foreach (InstanceDecl instance in instances)
				if (rootHints[instance].Count > 0)
					order[instance] = rootHints[instance].Average();

			// Repeated forward/backward barycentre passes reduce crossings while the
			// functional group remains the primary, human-readable ordering rule.
			for (int pass = 0; pass < 8; pass++)
			{
				bool forward = (pass & 1) == 0;
				IEnumerable<int> levels = forward
					? Enumerable.Range(0, maxLevel + 1)
					: Enumerable.Range(0, maxLevel + 1).Reverse();
				foreach (int level in levels)
				{
					List<InstanceDecl> column = instances.Where(i => i.Level == level).ToList();
					foreach (InstanceDecl instance in column)
					{
						List<InstanceDecl> neighbours = forward ? incoming[instance] : outgoing[instance];
						if (neighbours.Count > 0)
							order[instance] = neighbours.Average(n => order[n]);
					}

					float slot = 0f;
					int previousGroup = -1;
					foreach (InstanceDecl instance in column
						.OrderBy(i => i.LayoutGroup)
						.ThenBy(i => order[i])
						.ThenBy(i => i.Line)
						.ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
					{
						if (previousGroup >= 0 && previousGroup != instance.LayoutGroup) slot += 1.5f;
						order[instance] = slot++;
						previousGroup = instance.LayoutGroup;
					}
				}
			}

			Dictionary<int, float> columnWidth = new();
			for (int level = 0; level <= maxLevel; level++)
			{
				float width = 2f;
				foreach (InstanceDecl instance in instances.Where(i => i.Level == level))
					width = Math.Max(width, Math.Max(2f, instance.Description.Size.x));
				columnWidth[level] = width;
			}

			Dictionary<int, float> columnX = new();
			float cursorX = 0f;
			for (int level = 0; level <= maxLevel; level++)
			{
				float width = columnWidth[level];
				if (level > 0)
					cursorX += columnWidth[level - 1] * 0.5f + 4f + width * 0.5f;
				columnX[level] = cursorX;
			}
			float xCentre = (columnX[0] + columnX[maxLevel]) * 0.5f;

			// Reserve one horizontal band per output cone. Each column centres its
			// local gates inside the same band, producing recognisable logic rows.
			int[] groups = instances.Select(i => i.LayoutGroup).Distinct().OrderBy(value => value).ToArray();
			Dictionary<int, float> groupHeight = new();
			foreach (int layoutGroup in groups)
			{
				float height = 1.5f;
				for (int level = 0; level <= maxLevel; level++)
				{
					InstanceDecl[] members = instances
						.Where(i => i.Level == level && i.LayoutGroup == layoutGroup)
						.ToArray();
					if (members.Length == 0) continue;
					float localHeight = members.Sum(i => Math.Max(1.5f, i.Description.Size.y));
					localHeight += (members.Length - 1) * 1.25f;
					height = Math.Max(height, localHeight);
				}
				groupHeight[layoutGroup] = height;
			}

			const float groupGap = 3f;
			float fullHeight = groups.Sum(layoutGroup => groupHeight[layoutGroup]);
			fullHeight += Math.Max(0, groups.Length - 1) * groupGap;
			Dictionary<int, float> groupCentreY = new();
			float groupCursor = fullHeight * 0.5f;
			foreach (int layoutGroup in groups)
			{
				groupCursor -= groupHeight[layoutGroup] * 0.5f;
				groupCentreY[layoutGroup] = groupCursor;
				groupCursor -= groupHeight[layoutGroup] * 0.5f + groupGap;
			}

			for (int level = 0; level <= maxLevel; level++)
			{
				foreach (int layoutGroup in groups)
				{
					InstanceDecl[] members = instances
						.Where(i => i.Level == level && i.LayoutGroup == layoutGroup)
						.OrderBy(i => order[i])
						.ThenBy(i => i.Line)
						.ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
						.ToArray();
					if (members.Length == 0) continue;

					float localHeight = members.Sum(i => Math.Max(1.5f, i.Description.Size.y));
					localHeight += (members.Length - 1) * 1.25f;
					float y = groupCentreY[layoutGroup] + localHeight * 0.5f;
					foreach (InstanceDecl instance in members)
					{
						float height = Math.Max(1.5f, instance.Description.Size.y);
						y -= height * 0.5f;
						instance.Position = Snap(new Vector2(columnX[level] - xCentre, y));
						y -= height * 0.5f + 1.25f;
					}
				}
			}
		}

		static List<List<InstanceDecl>> FindStrongComponents(
			List<InstanceDecl> instances,
			Dictionary<InstanceDecl, List<InstanceDecl>> outgoing)
		{
			int nextIndex = 0;
			Dictionary<InstanceDecl, int> index = new();
			Dictionary<InstanceDecl, int> lowLink = new();
			Stack<InstanceDecl> stack = new();
			HashSet<InstanceDecl> onStack = new();
			List<List<InstanceDecl>> result = new();

			foreach (InstanceDecl instance in instances
				.OrderBy(i => i.Line)
				.ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
				if (!index.ContainsKey(instance)) StrongConnect(instance);

			return result;

			void StrongConnect(InstanceDecl instance)
			{
				index[instance] = nextIndex;
				lowLink[instance] = nextIndex;
				nextIndex++;
				stack.Push(instance);
				onStack.Add(instance);

				foreach (InstanceDecl next in outgoing[instance]
					.OrderBy(i => i.Line)
					.ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
				{
					if (!index.ContainsKey(next))
					{
						StrongConnect(next);
						lowLink[instance] = Math.Min(lowLink[instance], lowLink[next]);
					}
					else if (onStack.Contains(next))
					{
						lowLink[instance] = Math.Min(lowLink[instance], index[next]);
					}
				}

				if (lowLink[instance] != index[instance]) return;

				List<InstanceDecl> component = new();
				InstanceDecl member;
				do
				{
					member = stack.Pop();
					onStack.Remove(member);
					component.Add(member);
				}
				while (member != instance);
				result.Add(component);
			}
		}

		readonly struct RoutingObstacle
		{
			public readonly int OwnerID;
			public readonly float Left;
			public readonly float Right;
			public readonly float Bottom;
			public readonly float Top;

			public RoutingObstacle(InstanceDecl instance, float clearance)
			{
				OwnerID = instance.ID;
				float halfWidth = Math.Max(1f, instance.Description.Size.x * 0.5f) + clearance;
				float halfHeight = Math.Max(0.75f, instance.Description.Size.y * 0.5f) + clearance;
				Left = instance.Position.x - halfWidth;
				Right = instance.Position.x + halfWidth;
				Bottom = instance.Position.y - halfHeight;
				Top = instance.Position.y + halfHeight;
			}
		}

		readonly struct RoutedSegment
		{
			public readonly Vector2 A;
			public readonly Vector2 B;
			public bool Horizontal => Math.Abs(A.y - B.y) < 0.01f;

			public RoutedSegment(Vector2 a, Vector2 b)
			{
				A = a;
				B = b;
			}
		}

		static WireDescription[] BuildBlockWires(
			List<ConnectionDecl> connections,
			List<InstanceDecl> instances,
			PinDescription[] inputPins,
			PinDescription[] outputPins)
		{
			Dictionary<int, InstanceDecl> instanceById = instances.ToDictionary(i => i.ID);
			Dictionary<int, PinDescription> rootPins = inputPins
				.Concat(outputPins)
				.ToDictionary(p => p.ID);

			const float obstacleClearance = 0.4f;
			List<RoutingObstacle> obstacles = instances
				.Select(instance => new RoutingObstacle(instance, obstacleClearance))
				.ToList();
			float top = obstacles.Count == 0 ? 3f : obstacles.Max(obstacle => obstacle.Top) + 1.5f;
			float bottom = obstacles.Count == 0 ? -3f : obstacles.Min(obstacle => obstacle.Bottom) - 1.5f;
			foreach (PinDescription pin in inputPins.Concat(outputPins))
			{
				top = Math.Max(top, pin.Position.y + 1.5f);
				bottom = Math.Min(bottom, pin.Position.y - 1.5f);
			}

			WireDescription[] result = new WireDescription[connections.Count];
			Vector2[] sources = new Vector2[connections.Count];
			Vector2[] targets = new Vector2[connections.Count];
			for (int i = 0; i < connections.Count; i++)
			{
				sources[i] = EndpointPosition(connections[i].Source.Address, instanceById, rootPins);
				targets[i] = EndpointPosition(connections[i].Target.Address, instanceById, rootPins);
			}

			// Short local connections claim the cleanest lanes first. Long buses and
			// feedback then route around them, like a person would draw the circuit.
			int[] routeOrder = Enumerable.Range(0, connections.Count)
				.OrderBy(i => Math.Abs(targets[i].x - sources[i].x) + Math.Abs(targets[i].y - sources[i].y))
				.ThenBy(i => i)
				.ToArray();
			List<RoutedSegment> occupied = new();
			int fallbackLane = 0;
			foreach (int i in routeOrder)
			{
				ConnectionDecl connection = connections[i];
				List<Vector2> route = RouteOrthogonal(
					sources[i],
					targets[i],
					connection.Source.Address.PinOwnerID,
					connection.Target.Address.PinOwnerID,
					obstacles,
					occupied,
					top,
					bottom,
					ref fallbackLane);

				for (int segment = 1; segment < route.Count; segment++)
					occupied.Add(new RoutedSegment(route[segment - 1], route[segment]));

				Vector2[] points = new Vector2[route.Count];
				points[0] = new Vector2();
				points[points.Length - 1] = new Vector2();
				for (int point = 1; point < route.Count - 1; point++) points[point] = route[point];

				result[i] = new WireDescription
				{
					SourcePinAddress = connection.Source.Address,
					TargetPinAddress = connection.Target.Address,
					ConnectionType = WireConnectionType.ToPins,
					ConnectedWireIndex = -1,
					ConnectedWireSegmentIndex = -1,
					Points = points
				};
			}

			return result;
		}

		static List<Vector2> RouteOrthogonal(
			Vector2 source,
			Vector2 target,
			int sourceOwner,
			int targetOwner,
			List<RoutingObstacle> obstacles,
			List<RoutedSegment> occupied,
			float top,
			float bottom,
			ref int fallbackLane)
		{
			RoutingObstacle? sourceObstacle = FindObstacle(sourceOwner, obstacles);
			RoutingObstacle? targetObstacle = FindObstacle(targetOwner, obstacles);

			float escapeX = sourceObstacle.HasValue ? sourceObstacle.Value.Right + 0.35f : source.x;
			float entryX = targetObstacle.HasValue ? targetObstacle.Value.Left - 0.35f : target.x;
			List<List<Vector2>> candidates = new();

			if (Math.Abs(source.y - target.y) < 0.01f)
				candidates.Add(new List<Vector2> { source, target });

			List<float> xLanes = new()
			{
				(source.x + target.x) * 0.5f,
				(escapeX + entryX) * 0.5f,
				escapeX,
				entryX
			};
			if (escapeX < entryX)
			{
				xLanes.Add(escapeX + (entryX - escapeX) / 3f);
				xLanes.Add(escapeX + (entryX - escapeX) * 2f / 3f);
			}
			else
			{
				float right = obstacles.Count == 0 ? Math.Max(source.x, target.x) + 2f : obstacles.Max(o => o.Right) + 0.75f;
				float left = obstacles.Count == 0 ? Math.Min(source.x, target.x) - 2f : obstacles.Min(o => o.Left) - 0.75f;
				xLanes.Add(right);
				xLanes.Add(left);
			}
			float routeMinY = Math.Min(source.y, target.y) - 2f;
			float routeMaxY = Math.Max(source.y, target.y) + 2f;
			foreach (RoutingObstacle obstacle in obstacles)
			{
				if (obstacle.Top < routeMinY || obstacle.Bottom > routeMaxY) continue;
				xLanes.Add(obstacle.Left);
				xLanes.Add(obstacle.Right);
			}

			float preferredX = (escapeX + entryX) * 0.5f;
			foreach (float lane in xLanes
				.Select(SnapScalar)
				.Distinct()
				.OrderBy(value => Math.Abs(value - preferredX))
				.Take(18))
				candidates.Add(new List<Vector2>
				{
					source,
					new Vector2(lane, source.y),
					new Vector2(lane, target.y),
					target
				});

			List<float> yLanes = new()
			{
				source.y,
				target.y,
				(source.y + target.y) * 0.5f,
				top,
				bottom
			};
			float routeMinX = Math.Min(escapeX, entryX) - 2f;
			float routeMaxX = Math.Max(escapeX, entryX) + 2f;
			foreach (RoutingObstacle obstacle in obstacles)
			{
				if (obstacle.Right < routeMinX || obstacle.Left > routeMaxX) continue;
				yLanes.Add(obstacle.Top);
				yLanes.Add(obstacle.Bottom);
			}

			float preferredY = (source.y + target.y) * 0.5f;
			foreach (float lane in yLanes
				.Select(SnapScalar)
				.Distinct()
				.OrderBy(value => Math.Abs(value - preferredY))
				.Take(18))
				candidates.Add(new List<Vector2>
				{
					source,
					new Vector2(escapeX, source.y),
					new Vector2(escapeX, lane),
					new Vector2(entryX, lane),
					new Vector2(entryX, target.y),
					target
				});

			List<Vector2> best = null;
			float bestScore = float.MaxValue;
			foreach (List<Vector2> rawCandidate in candidates)
			{
				List<Vector2> candidate = NormalizeRoute(rawCandidate);
				if (!RouteIsClear(candidate, sourceOwner, targetOwner, obstacles)) continue;
				float score = ScoreRoute(candidate, occupied);
				if (score >= bestScore) continue;
				best = candidate;
				bestScore = score;
			}

			if (best != null) return best;

			// Extremely dense or cyclic circuits can exhaust the local channels.
			// The deterministic outer lane is deliberately a last resort.
			bool useTop = (fallbackLane & 1) == 0;
			float outerY = useTop
				? top + (fallbackLane / 2) * 0.75f
				: bottom - (fallbackLane / 2) * 0.75f;
			fallbackLane++;
			return NormalizeRoute(new List<Vector2>
			{
				source,
				new Vector2(escapeX, source.y),
				new Vector2(escapeX, outerY),
				new Vector2(entryX, outerY),
				new Vector2(entryX, target.y),
				target
			});
		}

		static RoutingObstacle? FindObstacle(int ownerID, List<RoutingObstacle> obstacles)
		{
			foreach (RoutingObstacle obstacle in obstacles)
				if (obstacle.OwnerID == ownerID) return obstacle;
			return null;
		}

		static bool RouteIsClear(
			List<Vector2> route,
			int sourceOwner,
			int targetOwner,
			List<RoutingObstacle> obstacles)
		{
			for (int i = 1; i < route.Count; i++)
			{
				Vector2 a = route[i - 1];
				Vector2 b = route[i];
				bool horizontal = Math.Abs(a.y - b.y) < 0.01f;
				bool vertical = Math.Abs(a.x - b.x) < 0.01f;
				if (!horizontal && !vertical) return false;

				foreach (RoutingObstacle obstacle in obstacles)
				{
					bool leavesSource = i == 1 && obstacle.OwnerID == sourceOwner;
					bool entersTarget = i == route.Count - 1 && obstacle.OwnerID == targetOwner;
					if (leavesSource || entersTarget) continue;
					if (horizontal)
					{
						float minX = Math.Min(a.x, b.x);
						float maxX = Math.Max(a.x, b.x);
						if (a.y > obstacle.Bottom + 0.01f && a.y < obstacle.Top - 0.01f &&
						    maxX > obstacle.Left + 0.01f && minX < obstacle.Right - 0.01f)
							return false;
					}
					else
					{
						float minY = Math.Min(a.y, b.y);
						float maxY = Math.Max(a.y, b.y);
						if (a.x > obstacle.Left + 0.01f && a.x < obstacle.Right - 0.01f &&
						    maxY > obstacle.Bottom + 0.01f && minY < obstacle.Top - 0.01f)
							return false;
					}
				}
			}
			return true;
		}

		static float ScoreRoute(List<Vector2> route, List<RoutedSegment> occupied)
		{
			float score = Math.Max(0, route.Count - 2) * 0.6f;
			for (int i = 1; i < route.Count; i++)
			{
				RoutedSegment candidate = new(route[i - 1], route[i]);
				score += Math.Abs(candidate.B.x - candidate.A.x) + Math.Abs(candidate.B.y - candidate.A.y);
				// Checking the most recent routes is enough to spread neighbouring
				// wires and keeps very large generated netlists responsive.
				int firstOccupied = Math.Max(0, occupied.Count - 768);
				for (int occupiedIndex = firstOccupied; occupiedIndex < occupied.Count; occupiedIndex++)
					score += SegmentConflictPenalty(candidate, occupied[occupiedIndex]);
			}
			return score;
		}

		static float SegmentConflictPenalty(RoutedSegment a, RoutedSegment b)
		{
			if (a.Horizontal == b.Horizontal)
			{
				float fixedA = a.Horizontal ? a.A.y : a.A.x;
				float fixedB = b.Horizontal ? b.A.y : b.A.x;
				if (Math.Abs(fixedA - fixedB) > 0.01f) return 0f;
				float a0 = a.Horizontal ? Math.Min(a.A.x, a.B.x) : Math.Min(a.A.y, a.B.y);
				float a1 = a.Horizontal ? Math.Max(a.A.x, a.B.x) : Math.Max(a.A.y, a.B.y);
				float b0 = b.Horizontal ? Math.Min(b.A.x, b.B.x) : Math.Min(b.A.y, b.B.y);
				float b1 = b.Horizontal ? Math.Max(b.A.x, b.B.x) : Math.Max(b.A.y, b.B.y);
				float overlap = Math.Min(a1, b1) - Math.Max(a0, b0);
				return overlap > 0.01f ? 10f + overlap * 2f : 0f;
			}

			RoutedSegment horizontal = a.Horizontal ? a : b;
			RoutedSegment vertical = a.Horizontal ? b : a;
			float horizontalMin = Math.Min(horizontal.A.x, horizontal.B.x);
			float horizontalMax = Math.Max(horizontal.A.x, horizontal.B.x);
			float verticalMin = Math.Min(vertical.A.y, vertical.B.y);
			float verticalMax = Math.Max(vertical.A.y, vertical.B.y);
			return vertical.A.x > horizontalMin + 0.01f && vertical.A.x < horizontalMax - 0.01f &&
			       horizontal.A.y > verticalMin + 0.01f && horizontal.A.y < verticalMax - 0.01f
				? 2.5f
				: 0f;
		}

		static List<Vector2> NormalizeRoute(List<Vector2> raw)
		{
			List<Vector2> route = new();
			foreach (Vector2 rawPoint in raw)
			{
				Vector2 point = Snap(rawPoint);
				if (route.Count > 0 && (route[route.Count - 1] - point).sqrMagnitude < 0.0001f) continue;
				route.Add(point);
				while (route.Count >= 3)
				{
					Vector2 a = route[route.Count - 3];
					Vector2 b = route[route.Count - 2];
					Vector2 c = route[route.Count - 1];
					bool sameX = Math.Abs(a.x - b.x) < 0.01f && Math.Abs(b.x - c.x) < 0.01f;
					bool sameY = Math.Abs(a.y - b.y) < 0.01f && Math.Abs(b.y - c.y) < 0.01f;
					if (!sameX && !sameY) break;
					route.RemoveAt(route.Count - 2);
				}
			}

			if (route.Count == 1) route.Add(route[0]);
			return route;
		}

		static Vector2 EndpointPosition(
			PinAddress address,
			Dictionary<int, InstanceDecl> instanceById,
			Dictionary<int, PinDescription> rootPins)
		{
			if (rootPins.TryGetValue(address.PinOwnerID, out PinDescription root))
				return root.Position;

			return TryGetInstanceEndpointPosition(address, instanceById, out Vector2 position)
				? position
				: new Vector2();
		}

		static bool TryGetInstanceEndpointPosition(
			PinAddress address,
			Dictionary<int, InstanceDecl> instanceById,
			out Vector2 position)
		{
			position = new Vector2();
			if (!instanceById.TryGetValue(address.PinOwnerID, out InstanceDecl instance))
				return false;

			foreach (PinDescription pin in instance.Description.InputPins.Concat(instance.Description.OutputPins))
			{
				if (pin.ID != address.PinID) continue;
				position = new Vector2(
					instance.Position.x + pin.Position.x,
					instance.Position.y + pin.Position.y);
				return true;
			}

			position = instance.Position;
			return true;
		}

		static float SnapScalar(float value)
		{
			float grid = DLS.Graphics.DrawSettings.GridSize;
			return Mathf.Round(value / grid) * grid;
		}

		static PinDescription[] CreateRootPins(
			List<PortDecl> ports,
			List<InstanceDecl> instances,
			bool left)
		{
			if (ports.Count == 0) return Array.Empty<PinDescription>();

			float minX = instances.Count == 0 ? 0 : instances.Min(i => i.Position.x);
			float maxX = instances.Count == 0 ? 0 : instances.Max(i => i.Position.x);
			float x = left ? minX - 4f : maxX + 4f;
			float totalHeight = (ports.Count - 1) * 1.5f;
			PinDescription[] result = new PinDescription[ports.Count];

			for (int i = 0; i < ports.Count; i++)
			{
				PortDecl port = ports[i];
				float y = totalHeight * 0.5f - i * 1.5f;

				result[i] = new PinDescription(
					port.Name,
					port.OwnerID,
					Snap(new Vector2(x, y)),
					port.Bits,
					PinColour.Blue,
					PinValueDisplayMode.Off);
			}
			return result;
		}

		static Vector2 Snap(Vector2 p)
		{
			float grid = DLS.Graphics.DrawSettings.GridSize;
			return new Vector2(
				Mathf.Round(p.x / grid) * grid,
				Mathf.Round(p.y / grid) * grid);
		}

		static string Normalize(string text)
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

		static bool ValidIdentifier(string text)
		{
			if (string.IsNullOrWhiteSpace(text)) return false;
			if (!(char.IsLetter(text[0]) || text[0] == '_')) return false;
			for (int i = 1; i < text.Length; i++)
			{
				char c = text[i];
				if (!(char.IsLetterOrDigit(c) || c == '_')) return false;
			}
			return true;
		}
	}
}
