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

			AutoLayout(instances, connections);
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

		static void AutoLayout(List<InstanceDecl> instances, List<ConnectionDecl> connections)
		{
			if (instances.Count == 0) return;

			// BLOCK layout:
			//   1. determine a left-to-right dependency level,
			//   2. keep related component classes together,
			//   3. use neighbour barycentres to reduce wire crossings,
			//   4. space rows/columns using the real chip dimensions.
			Dictionary<string, InstanceDecl> byName =
				instances.ToDictionary(i => Normalize(i.Name), StringComparer.OrdinalIgnoreCase);

			Dictionary<InstanceDecl, List<InstanceDecl>> outgoing =
				instances.ToDictionary(i => i, _ => new List<InstanceDecl>());
			Dictionary<InstanceDecl, List<InstanceDecl>> incoming =
				instances.ToDictionary(i => i, _ => new List<InstanceDecl>());
			Dictionary<InstanceDecl, int> indegree =
				instances.ToDictionary(i => i, _ => 0);

			foreach (ConnectionDecl connection in connections)
			{
				if (string.IsNullOrEmpty(connection.SourceInstance) ||
				    string.IsNullOrEmpty(connection.TargetInstance))
					continue;

				if (!byName.TryGetValue(Normalize(connection.SourceInstance), out InstanceDecl source) ||
				    !byName.TryGetValue(Normalize(connection.TargetInstance), out InstanceDecl target))
					continue;

				if (source == target || outgoing[source].Contains(target)) continue;
				outgoing[source].Add(target);
				incoming[target].Add(source);
				indegree[target]++;
			}

			foreach (InstanceDecl instance in instances) instance.Level = 0;

			Queue<InstanceDecl> queue = new(
				instances
					.Where(i => indegree[i] == 0)
					.OrderBy(LayoutClass)
					.ThenBy(i => i.Line)
					.ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase));

			HashSet<InstanceDecl> visited = new();
			while (queue.Count > 0)
			{
				InstanceDecl current = queue.Dequeue();
				visited.Add(current);

				foreach (InstanceDecl next in outgoing[current])
				{
					next.Level = Math.Max(next.Level, current.Level + 1);
					indegree[next]--;
					if (indegree[next] == 0) queue.Enqueue(next);
				}
			}

			// Feedback/stateful SCCs are legal. Put unresolved members close to
			// their connected forward graph rather than collapsing everything on
			// the exact same point.
			foreach (InstanceDecl instance in instances.Where(i => !visited.Contains(i)))
			{
				int neighbourLevel = 0;
				foreach (InstanceDecl parent in incoming[instance])
					neighbourLevel = Math.Max(neighbourLevel, parent.Level + 1);
				instance.Level = neighbourLevel;
			}

			int maxLevel = instances.Max(i => i.Level);
			Dictionary<InstanceDecl, float> order =
				instances.ToDictionary(i => i, i => (float)i.Line);

			// A few Sugiyama-style barycentre sweeps are enough for generated
			// schematics and are much cheaper than a general graph-layout solver.
			for (int pass = 0; pass < 4; pass++)
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

					int slot = 0;
					foreach (InstanceDecl instance in column
						.OrderBy(LayoutClass)
						.ThenBy(i => order[i])
						.ThenBy(i => i.Line)
						.ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
					{
						order[instance] = slot++;
					}
				}
			}

			// Dynamic X positions prevent large memory/custom blocks from colliding
			// with the next logic column.
			Dictionary<int, float> columnWidth = new();
			for (int level = 0; level <= maxLevel; level++)
			{
				float width = 2.0f;
				foreach (InstanceDecl instance in instances.Where(i => i.Level == level))
					width = Math.Max(width, Math.Max(2.0f, instance.Description.Size.x));
				columnWidth[level] = width;
			}

			Dictionary<int, float> columnX = new();
			float cursorX = 0f;
			for (int level = 0; level <= maxLevel; level++)
			{
				float width = columnWidth[level];
				if (level == 0)
					cursorX = 0f;
				else
					cursorX += columnWidth[level - 1] * 0.5f + 3.0f + width * 0.5f;
				columnX[level] = cursorX;
			}

			// Centre the full layout around X=0 so generated chips open naturally
			// in the editor rather than drifting endlessly to the right.
			float xCentre = (columnX[0] + columnX[maxLevel]) * 0.5f;

			foreach (int level in Enumerable.Range(0, maxLevel + 1))
			{
				InstanceDecl[] column = instances
					.Where(i => i.Level == level)
					.OrderBy(LayoutClass)
					.ThenBy(i => order[i])
					.ThenBy(i => i.Line)
					.ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
					.ToArray();

				if (column.Length == 0) continue;

				float totalHeight = 0f;
				int previousClass = -1;
				for (int i = 0; i < column.Length; i++)
				{
					int cls = LayoutClass(column[i]);
					if (i > 0)
						totalHeight += cls == previousClass ? 1.25f : 2.5f;
					totalHeight += Math.Max(1.5f, column[i].Description.Size.y);
					previousClass = cls;
				}

				float y = totalHeight * 0.5f;
				previousClass = -1;
				for (int i = 0; i < column.Length; i++)
				{
					InstanceDecl instance = column[i];
					int cls = LayoutClass(instance);
					float height = Math.Max(1.5f, instance.Description.Size.y);

					if (i > 0)
						y -= cls == previousClass ? 1.25f : 2.5f;

					y -= height * 0.5f;
					instance.Position = Snap(new Vector2(columnX[level] - xCentre, y));
					y -= height * 0.5f;
					previousClass = cls;
				}
			}
		}

		static int LayoutClass(InstanceDecl instance)
		{
			// Stable coarse grouping makes generated designs read as blocks rather
			// than as an arbitrary list of primitive gates.
			ChipType type = instance.Description.ChipType;
			if (type == ChipType.Rom_256x16) return 0;
			if (type == ChipType.Custom) return 1;

			string name = instance.Name ?? string.Empty;
			if (name.StartsWith("__rhdl_", StringComparison.OrdinalIgnoreCase)) return 2;

			string typeName = instance.Description.Name ?? string.Empty;
			if (typeName.StartsWith("BUS", StringComparison.OrdinalIgnoreCase) ||
			    typeName.Contains("BIT"))
				return 3;

			return 2;
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

			float top = 3f;
			foreach (InstanceDecl instance in instances)
				top = Math.Max(top, instance.Position.y + Math.Max(1.5f, instance.Description.Size.y) * 0.5f + 2f);
			foreach (PinDescription pin in inputPins.Concat(outputPins))
				top = Math.Max(top, pin.Position.y + 2f);

			WireDescription[] result = new WireDescription[connections.Count];
			int feedbackLane = 0;
			Dictionary<(float SourceX, float TargetX), int> forwardLaneCounts = new();
			Dictionary<float, int> sameColumnLaneCounts = new();

			for (int i = 0; i < connections.Count; i++)
			{
				ConnectionDecl connection = connections[i];
				Vector2 source = EndpointPosition(connection.Source.Address, instanceById, rootPins);
				Vector2 target = EndpointPosition(connection.Target.Address, instanceById, rootPins);

				Vector2[] points;
				float dx = target.x - source.x;

				if (dx > 1.0f)
				{
					// Normal left-to-right route: horizontal -> vertical -> horizontal.
					// Parallel wires get neighbouring routing lanes instead of rendering
					// on top of one another at the exact same midpoint.
					(float SourceX, float TargetX) laneKey = (SnapScalar(source.x), SnapScalar(target.x));
					forwardLaneCounts.TryGetValue(laneKey, out int lane);
					forwardLaneCounts[laneKey] = lane + 1;

					float baseMidX = (source.x + target.x) * 0.5f;
					float margin = Math.Min(1.0f, dx * 0.25f);
					float midX = baseMidX + AlternatingLaneOffset(lane, 0.55f);
					midX = SnapScalar(Math.Clamp(midX, source.x + margin, target.x - margin));
					points = new[]
					{
						new Vector2(),
						Snap(new Vector2(midX, source.y)),
						Snap(new Vector2(midX, target.y)),
						new Vector2()
					};
				}
				else if (Math.Abs(dx) <= 1.0f)
				{
					// Same-column routes use lanes local to that column. This prevents a
					// busy column from forcing unrelated columns into arbitrary channels.
					float columnKey = SnapScalar(Math.Max(source.x, target.x));
					sameColumnLaneCounts.TryGetValue(columnKey, out int lane);
					sameColumnLaneCounts[columnKey] = lane + 1;
					float sideX = columnKey + 2.0f + (lane % 6) * 0.5f;
					points = new[]
					{
						new Vector2(),
						Snap(new Vector2(sideX, source.y)),
						Snap(new Vector2(sideX, target.y)),
						new Vector2()
					};
				}
				else
				{
					// Feedback/back-edge: route above the block diagram so it cannot
					// cut diagonally through the datapath.
					float laneY = top + (feedbackLane++ * 0.75f);
					float sourceEscapeX = source.x + 1.5f;
					float targetEscapeX = target.x - 1.5f;
					points = new[]
					{
						new Vector2(),
						Snap(new Vector2(sourceEscapeX, source.y)),
						Snap(new Vector2(sourceEscapeX, laneY)),
						Snap(new Vector2(targetEscapeX, laneY)),
						Snap(new Vector2(targetEscapeX, target.y)),
						new Vector2()
					};
				}

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

		static float AlternatingLaneOffset(int lane, float spacing)
		{
			if (lane <= 0) return 0f;
			int step = (lane + 1) / 2;
			return (lane & 1) != 0 ? step * spacing : -step * spacing;
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
