using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DLS.Description;
using DLS.Game;

namespace DLS.RHDL
{
	internal sealed class RhdlLowerResult
	{
		public readonly string Source;
		public readonly List<RhdlDiagnostic> Diagnostics;
		readonly int[] originalLineByGeneratedLine;

		public bool Success => Diagnostics.Count == 0;

		public RhdlLowerResult(string source, List<RhdlDiagnostic> diagnostics, List<int> originalLines)
		{
			Source = source ?? string.Empty;
			Diagnostics = diagnostics ?? new List<RhdlDiagnostic>();
			originalLineByGeneratedLine = originalLines?.ToArray() ?? Array.Empty<int>();
		}

		public int MapGeneratedLine(int generatedLine)
		{
			if (generatedLine <= 0 || generatedLine > originalLineByGeneratedLine.Length) return generatedLine;
			int mapped = originalLineByGeneratedLine[generatedLine - 1];
			return mapped > 0 ? mapped : generatedLine;
		}
	}

	internal static class RhdlV3Lowerer
	{
		enum SignalKind
		{
			Input,
			Output,
			Wire
		}

		sealed class SignalDecl
		{
			public string Name;
			public int Width;
			public SignalKind Kind;
			public int Line;
			public string BackingInstance;
		}

		sealed class InstanceDecl
		{
			public string TypeName;
			public string Name;
			public int Line;
			public ChipDescription Description;
			public bool Generated;
		}

		sealed class ConnectionDecl
		{
			public string Source;
			public string Target;
			public int Line;
		}

		sealed class AssignmentDecl
		{
			public string Target;
			public string Expression;
			public int Line;
			public ExprNode Parsed;
		}

		sealed class NamedBinding
		{
			public string Pin;
			public string Value;
			public int Line;
		}

		sealed class NamedInstance
		{
			public InstanceDecl Instance;
			public readonly List<NamedBinding> Bindings = new();
		}

		readonly struct SignalValue
		{
			public readonly string Endpoint;
			public readonly int Width;

			public SignalValue(string endpoint, int width)
			{
				Endpoint = endpoint;
				Width = width;
			}
		}

		abstract class ExprNode
		{
			public int Position;
		}

		sealed class RefExpr : ExprNode
		{
			public string Name;
		}

		sealed class ConstExpr : ExprNode
		{
			public ulong Value;
			public int WidthHint;
			public string Raw;
		}

		sealed class UnaryExpr : ExprNode
		{
			public string Op;
			public ExprNode Value;
		}

		sealed class BinaryExpr : ExprNode
		{
			public string Op;
			public ExprNode Left;
			public ExprNode Right;
		}

		sealed class TernaryExpr : ExprNode
		{
			public ExprNode Condition;
			public ExprNode WhenTrue;
			public ExprNode WhenFalse;
		}

		sealed class SliceExpr : ExprNode
		{
			public ExprNode Value;
			public int High;
			public int Low;
		}

		sealed class ConcatExpr : ExprNode
		{
			public readonly List<ExprNode> Parts = new();
		}

		readonly struct Token
		{
			public readonly string Text;
			public readonly int Position;

			public Token(string text, int position)
			{
				Text = text;
				Position = position;
			}
		}

		public static RhdlLowerResult Lower(string source, ChipLibrary library)
		{
			List<RhdlDiagnostic> diagnostics = new();
			Lowerer state = new(source ?? string.Empty, library, diagnostics);
			state.Run();
			return state.Result();
		}

		sealed class Lowerer
		{
			readonly string source;
			readonly string[] lines;
			readonly ChipLibrary library;
			readonly List<RhdlDiagnostic> diagnostics;

			readonly List<SignalDecl> signals = new();
			readonly List<InstanceDecl> instances = new();
			readonly List<ConnectionDecl> connections = new();
			readonly List<AssignmentDecl> assignments = new();
			readonly List<NamedInstance> namedInstances = new();
			readonly Dictionary<string, ulong> parameters = new(StringComparer.OrdinalIgnoreCase);
			readonly Dictionary<string, SignalDecl> signalByName = new(StringComparer.OrdinalIgnoreCase);
			readonly Dictionary<string, InstanceDecl> instanceByName = new(StringComparer.OrdinalIgnoreCase);
			readonly HashSet<string> usedNames = new(StringComparer.OrdinalIgnoreCase);
			readonly Dictionary<string, SignalValue[]> splitCache = new(StringComparer.Ordinal);

			string chipName;
			int chipLine;
			int generatedCounter;
			SignalValue constZero;
			SignalValue constOne;
			bool constantsReady;

			public Lowerer(string source, ChipLibrary library, List<RhdlDiagnostic> diagnostics)
			{
				this.source = source.Replace("\r", string.Empty);
				lines = this.source.Split('\n');
				this.library = library;
				this.diagnostics = diagnostics;
			}

			public void Run()
			{
				if (library == null)
				{
					diagnostics.Add(new RhdlDiagnostic(0, "No active chip library."));
					return;
				}

				PreScanParametersAndChip();
				if (diagnostics.Count != 0) return;

				ParseStatements();
				if (string.IsNullOrWhiteSpace(chipName))
					diagnostics.Add(new RhdlDiagnostic(0, "Missing 'chip NAME {' declaration."));
				if (diagnostics.Count != 0) return;

				ValidateAndResolveNames();
				if (diagnostics.Count != 0) return;

				ExpandNamedBindings();
				ParseExpressions();
				DetectAssignmentCycles();
				if (diagnostics.Count != 0) return;

				foreach (AssignmentDecl assignment in assignments)
					LowerAssignment(assignment);
			}

			public RhdlLowerResult Result()
			{
				if (diagnostics.Count != 0)
					return new RhdlLowerResult(string.Empty, diagnostics, new List<int>());

				StringBuilder output = new();
				List<int> lineMap = new();

				void Emit(string text, int originalLine)
				{
					output.AppendLine(text);
					lineMap.Add(originalLine);
				}

				Emit($"chip {chipName} {{", chipLine);

				foreach (SignalDecl signal in signals)
				{
					if (signal.Kind == SignalKind.Wire) continue;
					string keyword = signal.Kind == SignalKind.Input ? "input" : "output";
					string width = signal.Width == 1 ? string.Empty : $"[{signal.Width}]";
					Emit($"  {keyword} {signal.Name}{width}", signal.Line);
				}

				foreach (InstanceDecl instance in instances)
					Emit($"  {instance.TypeName} {instance.Name}", instance.Line);

				foreach (ConnectionDecl connection in connections)
					Emit($"  connect {connection.Source} -> {connection.Target}", connection.Line);

				Emit("}", lines.Length);
				return new RhdlLowerResult(output.ToString(), diagnostics, lineMap);
			}

			void PreScanParametersAndChip()
			{
				for (int i = 0; i < lines.Length; i++)
				{
					int lineNo = i + 1;
					string line = CleanLine(lines[i]);
					if (line.Length == 0) continue;

					if (line.StartsWith("chip ", StringComparison.OrdinalIgnoreCase))
					{
						if (chipName != null)
						{
							diagnostics.Add(new RhdlDiagnostic(lineNo, "Only one chip declaration is allowed per source."));
							continue;
						}

						string rest = line.Substring(5).Trim();
						int brace = rest.IndexOf('{');
						if (brace >= 0) rest = rest.Substring(0, brace).Trim();

						string paramText = null;
						int paren = rest.IndexOf('(');
						if (paren >= 0)
						{
							int close = rest.LastIndexOf(')');
							if (close < paren)
							{
								diagnostics.Add(new RhdlDiagnostic(lineNo, paren + 1, "Missing ')' in chip parameter list."));
								continue;
							}
							paramText = rest.Substring(paren + 1, close - paren - 1);
							rest = rest.Substring(0, paren).Trim();
						}

						if (!ValidIdentifier(rest))
						{
							diagnostics.Add(new RhdlDiagnostic(lineNo, "Invalid chip name. Use letters, digits and underscore."));
							continue;
						}

						chipName = rest;
						chipLine = lineNo;
						if (!string.IsNullOrWhiteSpace(paramText))
						{
							foreach (string entry in SplitTopLevel(paramText, ','))
								ParseParameter(entry.Trim(), lineNo);
						}
						continue;
					}

					if (line.StartsWith("param ", StringComparison.OrdinalIgnoreCase))
						ParseParameter(line.Substring(6).Trim(), lineNo);
				}
			}

			void ParseParameter(string declaration, int line)
			{
				int eq = declaration.IndexOf('=');
				if (eq <= 0)
				{
					diagnostics.Add(new RhdlDiagnostic(line, "Expected: param NAME = VALUE"));
					return;
				}

				string name = declaration.Substring(0, eq).Trim();
				string valueText = declaration.Substring(eq + 1).Trim();
				if (!ValidIdentifier(name))
				{
					diagnostics.Add(new RhdlDiagnostic(line, $"Invalid parameter name '{name}'."));
					return;
				}

				if (!TryParseIntegerLiteral(valueText, out ulong value, out _))
				{
					diagnostics.Add(new RhdlDiagnostic(line, $"Invalid parameter value '{valueText}'."));
					return;
				}

				parameters[name] = value;
			}

			void ParseStatements()
			{
				for (int i = 0; i < lines.Length; i++)
				{
					int lineNo = i + 1;
					string line = CleanLine(lines[i]);
					if (line.Length == 0 || line == "{" || line == "}") continue;
					if (line.StartsWith("chip ", StringComparison.OrdinalIgnoreCase)) continue;
					if (line.StartsWith("param ", StringComparison.OrdinalIgnoreCase)) continue;

					if (line.StartsWith("input ", StringComparison.OrdinalIgnoreCase))
					{
						ParseSignalList(line.Substring(6).Trim(), SignalKind.Input, lineNo);
						continue;
					}

					if (line.StartsWith("output ", StringComparison.OrdinalIgnoreCase))
					{
						ParseSignalList(line.Substring(7).Trim(), SignalKind.Output, lineNo);
						continue;
					}

					if (line.StartsWith("wire ", StringComparison.OrdinalIgnoreCase))
					{
						ParseWire(line.Substring(5).Trim(), lineNo);
						continue;
					}

					if (line.StartsWith("connect ", StringComparison.OrdinalIgnoreCase))
					{
						string rest = line.Substring(8).Trim();
						int arrow = rest.IndexOf("->", StringComparison.Ordinal);
						if (arrow <= 0 || arrow >= rest.Length - 2)
						{
							diagnostics.Add(new RhdlDiagnostic(lineNo, "Expected: connect SOURCE -> TARGET"));
							continue;
						}
						connections.Add(new ConnectionDecl
						{
							Source = rest.Substring(0, arrow).Trim(),
							Target = rest.Substring(arrow + 2).Trim(),
							Line = lineNo
						});
						continue;
					}

					if (TryParseInstance(line, lineNo)) continue;

					int assignmentEq = FindAssignmentEquals(line);
					if (assignmentEq > 0)
					{
						string target = line.Substring(0, assignmentEq).Trim();
						string expression = line.Substring(assignmentEq + 1).Trim();
						if (target.Length == 0 || expression.Length == 0)
						{
							diagnostics.Add(new RhdlDiagnostic(lineNo, "Expected: TARGET = expression"));
							continue;
						}

						assignments.Add(new AssignmentDecl
						{
							Target = target,
							Expression = expression,
							Line = lineNo
						});
						continue;
					}

					string firstWord = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? line;
					string hint = Suggestion(firstWord, new[] { "input", "output", "wire", "connect", "param", "use" });
					diagnostics.Add(new RhdlDiagnostic(lineNo,
						$"Unrecognized statement: {line}. Expected a declaration, chip instance, connect statement, or TARGET = expression.{hint}"));
				}
			}

			void ParseWire(string rest, int line)
			{
				int eq = FindAssignmentEquals(rest);
				string declaration = eq >= 0 ? rest.Substring(0, eq).Trim() : rest;
				string initializer = eq >= 0 ? rest.Substring(eq + 1).Trim() : null;

				List<string> parts = SplitTopLevel(declaration, ',');
				if (initializer != null && parts.Count != 1)
				{
					diagnostics.Add(new RhdlDiagnostic(line, "A wire initializer can only be used with one wire declaration."));
					return;
				}

				foreach (string part in parts)
				{
					if (!TryParseSignalDecl(part.Trim(), out string name, out int width, out string error))
					{
						diagnostics.Add(new RhdlDiagnostic(line, error));
						continue;
					}

					SignalDecl signal = new()
					{
						Name = name,
						Width = width,
						Kind = SignalKind.Wire,
						Line = line
					};
					signals.Add(signal);

					if (initializer != null)
					{
						assignments.Add(new AssignmentDecl
						{
							Target = name,
							Expression = initializer,
							Line = line
						});
					}
				}
			}

			void ParseSignalList(string rest, SignalKind kind, int line)
			{
				foreach (string part in SplitTopLevel(rest, ','))
				{
					if (!TryParseSignalDecl(part.Trim(), out string name, out int width, out string error))
					{
						diagnostics.Add(new RhdlDiagnostic(line, error));
						continue;
					}
					signals.Add(new SignalDecl { Name = name, Width = width, Kind = kind, Line = line });
				}
			}

			bool TryParseSignalDecl(string token, out string name, out int width, out string error)
			{
				name = token;
				width = 1;
				error = null;

				int colon = token.IndexOf(':');
				int bracket = token.IndexOf('[');
				if (colon >= 0)
				{
					name = token.Substring(0, colon).Trim();
					string widthText = token.Substring(colon + 1).Trim();
					if (!TryResolveWidth(widthText, out width))
					{
						error = $"Unsupported width '{widthText}'. Declare buses as NAME: 4, NAME: 8, NAME[4] or NAME[8]. Ranges such as [7:0] are only used when slicing expressions.";
						return false;
					}
				}
				else if (bracket >= 0)
				{
					int close = token.IndexOf(']', bracket + 1);
					if (close < 0 || close != token.Length - 1)
					{
						error = $"Invalid signal declaration '{token}'.";
						return false;
					}
					name = token.Substring(0, bracket).Trim();
					string widthText = token.Substring(bracket + 1, close - bracket - 1).Trim();
					if (!TryResolveWidth(widthText, out width))
					{
						error = $"Unsupported width '{widthText}'. Declare buses as NAME: 4, NAME: 8, NAME[4] or NAME[8]. Ranges such as [7:0] are only used when slicing expressions.";
						return false;
					}
				}

				if (!ValidIdentifier(name))
				{
					error = $"Invalid signal name '{name}'.";
					return false;
				}
				return true;
			}

			bool TryResolveWidth(string text, out int width)
			{
				width = 0;
				if (int.TryParse(text, out width)) return IsSupportedWidth(width);
				if (parameters.TryGetValue(text, out ulong value) && value <= int.MaxValue)
				{
					width = (int)value;
					return IsSupportedWidth(width);
				}
				return false;
			}

			bool TryParseInstance(string line, int lineNo)
			{
				string declaration = line;
				if (declaration.StartsWith("use ", StringComparison.OrdinalIgnoreCase))
					declaration = declaration.Substring(4).Trim();

				int open = declaration.IndexOf('(');
				string head = open >= 0 ? declaration.Substring(0, open).Trim() : declaration;
				string[] parts = head.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
				if (parts.Length != 2 || !ValidIdentifier(parts[1])) return false;

				// Avoid treating arithmetic expressions such as "a + b" as instances.
				if (parts[0] is "+" or "-" or "!" or "~") return false;

				InstanceDecl instance = new()
				{
					TypeName = parts[0],
					Name = parts[1],
					Line = lineNo
				};
				instances.Add(instance);

				if (open >= 0)
				{
					int close = declaration.LastIndexOf(')');
					if (close < open)
					{
						diagnostics.Add(new RhdlDiagnostic(lineNo, open + 1, "Missing ')' in instance binding list."));
						return true;
					}

					string trailing = declaration.Substring(close + 1).Trim();
					if (trailing.Length != 0)
					{
						diagnostics.Add(new RhdlDiagnostic(lineNo, close + 2, $"Unexpected text after instance binding list: {trailing}"));
						return true;
					}

					NamedInstance named = new() { Instance = instance };
					string body = declaration.Substring(open + 1, close - open - 1);
					foreach (string binding in SplitTopLevel(body, ','))
					{
						if (string.IsNullOrWhiteSpace(binding)) continue;
						int eq = FindAssignmentEquals(binding);
						if (eq <= 0)
						{
							diagnostics.Add(new RhdlDiagnostic(lineNo, $"Expected named binding PIN = signal/expression, got '{binding.Trim()}'."));
							continue;
						}
						named.Bindings.Add(new NamedBinding
						{
							Pin = binding.Substring(0, eq).Trim(),
							Value = binding.Substring(eq + 1).Trim(),
							Line = lineNo
						});
					}
					namedInstances.Add(named);
				}

				return true;
			}

			void ValidateAndResolveNames()
			{
				foreach (SignalDecl signal in signals)
				{
					string key = Normalize(signal.Name);
					if (!usedNames.Add(key))
					{
						diagnostics.Add(new RhdlDiagnostic(signal.Line, $"Duplicate name '{signal.Name}'."));
						continue;
					}
					signalByName[key] = signal;
				}

				foreach (InstanceDecl instance in instances)
				{
					string key = Normalize(instance.Name);
					if (!usedNames.Add(key))
					{
						diagnostics.Add(new RhdlDiagnostic(instance.Line, $"Duplicate name '{instance.Name}'."));
						continue;
					}
					instanceByName[key] = instance;
				}

				foreach (InstanceDecl instance in instances)
					ResolveInstance(instance);

				foreach (SignalDecl wire in signals.Where(s => s.Kind == SignalKind.Wire))
				{
					string busType = BusTypeForWidth(wire.Width);
					string backing = UniqueGeneratedName("wire_" + wire.Name);
					wire.BackingInstance = backing;
					InstanceDecl instance = AddGeneratedInstance(busType, backing, wire.Line);
					if (instance != null) instanceByName[Normalize(backing)] = instance;
				}

				// Rewrite old-style structural connections now that wire aliases are known.
				foreach (ConnectionDecl connection in connections)
				{
					if (TryRewriteEndpoint(connection.Source, true, connection.Line, out string sourceEndpoint, out _))
						connection.Source = sourceEndpoint;
					if (TryRewriteEndpoint(connection.Target, false, connection.Line, out string targetEndpoint, out _))
						connection.Target = targetEndpoint;
				}
			}

			void ResolveInstance(InstanceDecl instance)
			{
				if (instance.Description != null) return;
				instance.Description = ResolveChipType(instance.TypeName);
				if (instance.Description == null)
				{
					string hint = Suggestion(instance.TypeName, library.allChips.Select(c => c.Name));
					diagnostics.Add(new RhdlDiagnostic(instance.Line, $"Unknown chip type '{instance.TypeName}'.{hint}"));
					return;
				}
				if (ChipDescription.NameMatch(instance.Description.Name, chipName))
					diagnostics.Add(new RhdlDiagnostic(instance.Line, "A generated chip cannot directly instantiate itself."));
			}

			ChipDescription ResolveChipType(string typeName)
			{
				if (library.TryGetChipDescription(typeName, out ChipDescription exact)) return exact;
				string normalized = Normalize(typeName);
				foreach (ChipDescription chip in library.allChips)
					if (Normalize(chip.Name) == normalized) return chip;
				return null;
			}

			void ExpandNamedBindings()
			{
				foreach (NamedInstance named in namedInstances)
				{
					InstanceDecl instance = named.Instance;
					if (instance.Description == null) continue;

					foreach (NamedBinding binding in named.Bindings)
					{
						PinDescription? input = FindPin(instance.Description.InputPins, binding.Pin);
						PinDescription? output = FindPin(instance.Description.OutputPins, binding.Pin);

						if (!input.HasValue && !output.HasValue)
						{
							IEnumerable<string> pins = (instance.Description.InputPins ?? Array.Empty<PinDescription>()).Select(p => p.Name)
								.Concat((instance.Description.OutputPins ?? Array.Empty<PinDescription>()).Select(p => p.Name));
							string hint = Suggestion(binding.Pin, pins);
							diagnostics.Add(new RhdlDiagnostic(binding.Line,
								$"Chip '{instance.Description.Name}' has no pin '{binding.Pin}'.{hint}"));
							continue;
						}

						if (input.HasValue && output.HasValue)
						{
							diagnostics.Add(new RhdlDiagnostic(binding.Line,
								$"Pin name '{binding.Pin}' is ambiguous on chip '{instance.Description.Name}'."));
							continue;
						}

						if (input.HasValue)
						{
							assignments.Add(new AssignmentDecl
							{
								Target = instance.Name + "." + binding.Pin,
								Expression = binding.Value,
								Line = binding.Line
							});
						}
						else
						{
							if (!TryRewriteEndpoint(binding.Value, false, binding.Line, out string target, out _))
								continue;
							connections.Add(new ConnectionDecl
							{
								Source = instance.Name + "." + binding.Pin,
								Target = target,
								Line = binding.Line
							});
						}
					}
				}
			}

			void ParseExpressions()
			{
				foreach (AssignmentDecl assignment in assignments)
				{
					if (!TryParseExpression(assignment.Expression, assignment.Line, out ExprNode expr))
						continue;
					assignment.Parsed = expr;
				}
			}

			void DetectAssignmentCycles()
			{
				Dictionary<string, AssignmentDecl> assignmentByTarget = new(StringComparer.OrdinalIgnoreCase);
				foreach (AssignmentDecl assignment in assignments)
				{
					string target = Normalize(assignment.Target);
					if (signalByName.ContainsKey(target))
						assignmentByTarget[target] = assignment;
				}

				Dictionary<string, HashSet<string>> deps = new(StringComparer.OrdinalIgnoreCase);
				foreach ((string key, AssignmentDecl assignment) in assignmentByTarget)
				{
					HashSet<string> refs = new(StringComparer.OrdinalIgnoreCase);
					CollectSignalRefs(assignment.Parsed, refs);
					refs.RemoveWhere(r => !assignmentByTarget.ContainsKey(r));
					deps[key] = refs;
				}

				HashSet<string> visiting = new(StringComparer.OrdinalIgnoreCase);
				HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);

				bool Visit(string key)
				{
					if (visited.Contains(key)) return false;
					if (!visiting.Add(key)) return true;
					if (deps.TryGetValue(key, out HashSet<string> children))
					{
						foreach (string child in children)
						{
							if (Visit(child)) return true;
						}
					}
					visiting.Remove(key);
					visited.Add(key);
					return false;
				}

				foreach (string key in deps.Keys)
				{
					visiting.Clear();
					if (Visit(key))
					{
						diagnostics.Add(new RhdlDiagnostic(
							assignmentByTarget[key].Line,
							$"Combinational assignment loop detected around '{assignmentByTarget[key].Target}'."));
						break;
					}
				}
			}

			void CollectSignalRefs(ExprNode node, HashSet<string> refs)
			{
				if (node == null) return;
				switch (node)
				{
					case RefExpr r:
						if (!r.Name.Contains('.')) refs.Add(Normalize(r.Name));
						break;
					case UnaryExpr u:
						CollectSignalRefs(u.Value, refs);
						break;
					case BinaryExpr b:
						CollectSignalRefs(b.Left, refs);
						CollectSignalRefs(b.Right, refs);
						break;
					case TernaryExpr t:
						CollectSignalRefs(t.Condition, refs);
						CollectSignalRefs(t.WhenTrue, refs);
						CollectSignalRefs(t.WhenFalse, refs);
						break;
					case SliceExpr s:
						CollectSignalRefs(s.Value, refs);
						break;
					case ConcatExpr c:
						foreach (ExprNode part in c.Parts) CollectSignalRefs(part, refs);
						break;
				}
			}

			void LowerAssignment(AssignmentDecl assignment)
			{
				if (assignment.Parsed == null) return;
				if (!TryRewriteEndpoint(assignment.Target, false, assignment.Line, out string target, out int targetWidth))
					return;

				SignalValue value = Synthesize(assignment.Parsed, targetWidth, assignment.Line);
				if (value.Endpoint == null) return;
				if (value.Width != targetWidth)
				{
					diagnostics.Add(new RhdlDiagnostic(assignment.Line,
						$"Bit-width mismatch: expression is {value.Width}-bit, target '{assignment.Target}' is {targetWidth}-bit."));
					return;
				}

				connections.Add(new ConnectionDecl { Source = value.Endpoint, Target = target, Line = assignment.Line });
			}

			SignalValue Synthesize(ExprNode node, int expectedWidth, int line)
			{
				switch (node)
				{
					case RefExpr reference:
						{
							if (parameters.TryGetValue(reference.Name, out ulong parameter))
								return Constant(parameter, expectedWidth > 0 ? expectedWidth : SmallestWidth(parameter), line);

							if (!TryRewriteEndpoint(reference.Name, true, line, out string endpoint, out int width))
								return default;

							if (expectedWidth > 0 && width != expectedWidth)
							{
								diagnostics.Add(new RhdlDiagnostic(line, node.Position + 1,
									$"Signal '{reference.Name}' is {width}-bit, expected {expectedWidth}-bit."));
								return default;
							}
							return new SignalValue(endpoint, width);
						}

					case ConstExpr constant:
						{
							int width = expectedWidth > 0 ? expectedWidth :
								constant.WidthHint > 0 ? constant.WidthHint : SmallestWidth(constant.Value);
							if (!IsSupportedWidth(width))
							{
								diagnostics.Add(new RhdlDiagnostic(line, node.Position + 1,
									$"Constant '{constant.Raw}' requires unsupported width {width}."));
								return default;
							}
							if (constant.Value >= (1UL << width))
							{
								diagnostics.Add(new RhdlDiagnostic(line, node.Position + 1,
									$"Constant '{constant.Raw}' does not fit in {width} bits."));
								return default;
							}
							return Constant(constant.Value, width, line);
						}

					case SliceExpr slice:
						{
							SignalValue sourceValue = Synthesize(slice.Value, 0, line);
							if (sourceValue.Endpoint == null) return default;
							if (slice.Low < 0 || slice.High < slice.Low || slice.High >= sourceValue.Width)
							{
								diagnostics.Add(new RhdlDiagnostic(line, node.Position + 1,
									$"Slice [{slice.High}:{slice.Low}] is outside {sourceValue.Width}-bit value."));
								return default;
							}
							int width = slice.High - slice.Low + 1;
							if (!IsSupportedWidth(width))
							{
								diagnostics.Add(new RhdlDiagnostic(line, node.Position + 1,
									$"Slice width {width} is unsupported. Use 1, 4 or 8 bits."));
								return default;
							}
							SignalValue[] bits = GetBits(sourceValue, line);
							if (bits == null) return default;
							SignalValue[] selected = bits.Skip(slice.Low).Take(width).ToArray();
							return MergeBits(selected, line);
						}

					case ConcatExpr concat:
						{
							List<SignalValue[]> partBits = new();
							int total = 0;
							foreach (ExprNode part in concat.Parts)
							{
								int inferred = InferWidth(part, 0, line);
								if (inferred <= 0) return default;
								SignalValue value = Synthesize(part, inferred, line);
								if (value.Endpoint == null) return default;
								SignalValue[] bits = GetBits(value, line);
								if (bits == null) return default;
								partBits.Add(bits);
								total += bits.Length;
							}
							if (!IsSupportedWidth(total))
							{
								diagnostics.Add(new RhdlDiagnostic(line, node.Position + 1,
									$"Concatenation is {total}-bit; RHDL supports 1, 4 and 8-bit buses."));
								return default;
							}

							List<SignalValue> resultBits = new();
							for (int i = partBits.Count - 1; i >= 0; i--)
								resultBits.AddRange(partBits[i]);
							return MergeBits(resultBits.ToArray(), line);
						}

					case UnaryExpr unary:
						{
							int width = expectedWidth > 0 ? expectedWidth : InferWidth(unary.Value, 0, line);
							SignalValue value = Synthesize(unary.Value, width, line);
							if (value.Endpoint == null) return default;
							SignalValue[] bits = GetBits(value, line);
							if (bits == null) return default;
							for (int i = 0; i < bits.Length; i++) bits[i] = LogicNot(bits[i], line);
							return MergeBits(bits, line);
						}

					case BinaryExpr binary:
						return SynthesizeBinary(binary, expectedWidth, line);

					case TernaryExpr ternary:
						{
							SignalValue cond = Synthesize(ternary.Condition, 1, line);
							if (cond.Endpoint == null) return default;

							int width = expectedWidth > 0 ? expectedWidth :
								Math.Max(InferWidth(ternary.WhenTrue, 0, line), InferWidth(ternary.WhenFalse, 0, line));
							if (!IsSupportedWidth(width))
							{
								diagnostics.Add(new RhdlDiagnostic(line, node.Position + 1, "Cannot infer a supported ternary result width."));
								return default;
							}

							SignalValue a = Synthesize(ternary.WhenTrue, width, line);
							SignalValue b = Synthesize(ternary.WhenFalse, width, line);
							if (a.Endpoint == null || b.Endpoint == null) return default;
							return Mux(cond, a, b, line);
						}
				}

				diagnostics.Add(new RhdlDiagnostic(line, "Internal RHDL expression error."));
				return default;
			}

			SignalValue SynthesizeBinary(BinaryExpr binary, int expectedWidth, int line)
			{
				string op = binary.Op.ToUpperInvariant();

				if (op is "==" or "!=" or "<" or ">" or "<=" or ">=")
				{
					int width = Math.Max(InferWidth(binary.Left, 0, line), InferWidth(binary.Right, 0, line));
					if (!IsSupportedWidth(width)) width = expectedWidth;
					if (!IsSupportedWidth(width))
					{
						diagnostics.Add(new RhdlDiagnostic(line, binary.Position + 1, "Cannot infer comparison width."));
						return default;
					}

					SignalValue left = Synthesize(binary.Left, width, line);
					SignalValue right = Synthesize(binary.Right, width, line);
					if (left.Endpoint == null || right.Endpoint == null) return default;

					SignalValue result = op switch
					{
						"==" => Equal(left, right, line),
						"!=" => LogicNot(Equal(left, right, line), line),
						"<" => LessThan(left, right, line),
						">" => LessThan(right, left, line),
						"<=" => LogicNot(LessThan(right, left, line), line),
						">=" => LogicNot(LessThan(left, right, line), line),
						_ => default
					};
					return result;
				}

				if (op is "<<" or ">>")
				{
					int width = expectedWidth > 0 ? expectedWidth : InferWidth(binary.Left, 0, line);
					SignalValue value = Synthesize(binary.Left, width, line);
					if (value.Endpoint == null) return default;
					if (!TryEvaluateCompileTimeInt(binary.Right, out int amount))
					{
						diagnostics.Add(new RhdlDiagnostic(line, binary.Right.Position + 1,
							"Shift amount must be a compile-time constant or parameter."));
						return default;
					}
					if (amount < 0)
					{
						diagnostics.Add(new RhdlDiagnostic(line, binary.Right.Position + 1, "Shift amount cannot be negative."));
						return default;
					}
					return Shift(value, amount, op == "<<", line);
				}

				int binaryWidth = expectedWidth > 0 ? expectedWidth :
					Math.Max(InferWidth(binary.Left, 0, line), InferWidth(binary.Right, 0, line));
				if (!IsSupportedWidth(binaryWidth))
				{
					diagnostics.Add(new RhdlDiagnostic(line, binary.Position + 1,
						$"Operator '{binary.Op}' requires a 1, 4 or 8-bit width."));
					return default;
				}

				SignalValue a = Synthesize(binary.Left, binaryWidth, line);
				SignalValue b = Synthesize(binary.Right, binaryWidth, line);
				if (a.Endpoint == null || b.Endpoint == null) return default;

				return op switch
				{
					"AND" or "&" => Bitwise(a, b, LogicAnd, line),
					"OR" or "|" => Bitwise(a, b, LogicOr, line),
					"XOR" or "^" => Bitwise(a, b, LogicXor, line),
					"+" => Add(a, b, false, line),
					"-" => Add(a, b, true, line),
					_ => Unsupported()
				};

				SignalValue Unsupported()
				{
					diagnostics.Add(new RhdlDiagnostic(line, binary.Position + 1,
						$"Unsupported binary operator '{binary.Op}'."));
					return default;
				}
			}

			int InferWidth(ExprNode node, int expected, int line)
			{
				if (expected > 0) return expected;
				switch (node)
				{
					case RefExpr r:
						if (parameters.TryGetValue(r.Name, out ulong p)) return SmallestWidth(p);
						if (TryGetSourceWidth(r.Name, out int refWidth)) return refWidth;
						return 0;
					case ConstExpr c:
						return c.WidthHint > 0 ? c.WidthHint : SmallestWidth(c.Value);
					case SliceExpr s:
						return s.High - s.Low + 1;
					case ConcatExpr c:
						{
							int total = 0;
							foreach (ExprNode part in c.Parts) total += InferWidth(part, 0, line);
							return total;
						}
					case UnaryExpr u:
						return InferWidth(u.Value, 0, line);
					case BinaryExpr b:
						if (b.Op is "==" or "!=" or "<" or ">" or "<=" or ">=") return 1;
						if (b.Op is "<<" or ">>") return InferWidth(b.Left, 0, line);
						return Math.Max(InferWidth(b.Left, 0, line), InferWidth(b.Right, 0, line));
					case TernaryExpr t:
						return Math.Max(InferWidth(t.WhenTrue, 0, line), InferWidth(t.WhenFalse, 0, line));
					default:
						return 0;
				}
			}

			bool TryGetSourceWidth(string endpointText, out int width)
			{
				width = 0;
				string key = Normalize(endpointText);
				if (!endpointText.Contains('.') && signalByName.TryGetValue(key, out SignalDecl signal))
				{
					if (signal.Kind == SignalKind.Output) return false;
					width = signal.Width;
					return true;
				}

				int dot = endpointText.IndexOf('.');
				if (dot <= 0) return false;
				string owner = endpointText.Substring(0, dot).Trim();
				string pin = endpointText.Substring(dot + 1).Trim();
				if (!instanceByName.TryGetValue(Normalize(owner), out InstanceDecl instance) || instance.Description == null)
					return false;
				PinDescription? found = FindPin(instance.Description.OutputPins, pin);
				if (!found.HasValue) return false;
				width = (int)found.Value.BitCount;
				return true;
			}

			SignalValue Bitwise(SignalValue a, SignalValue b, Func<SignalValue, SignalValue, int, SignalValue> fn, int line)
			{
				SignalValue[] aa = GetBits(a, line);
				SignalValue[] bb = GetBits(b, line);
				if (aa == null || bb == null || aa.Length != bb.Length) return default;
				SignalValue[] result = new SignalValue[aa.Length];
				for (int i = 0; i < result.Length; i++) result[i] = fn(aa[i], bb[i], line);
				return MergeBits(result, line);
			}

			SignalValue Add(SignalValue a, SignalValue b, bool subtract, int line)
			{
				SignalValue[] aa = GetBits(a, line);
				SignalValue[] bb = GetBits(b, line);
				if (aa == null || bb == null || aa.Length != bb.Length) return default;

				EnsureConstants(line);
				SignalValue carry = subtract ? constOne : constZero;
				SignalValue[] result = new SignalValue[aa.Length];

				for (int i = 0; i < aa.Length; i++)
				{
					SignalValue bi = subtract ? LogicNot(bb[i], line) : bb[i];
					SignalValue axb = LogicXor(aa[i], bi, line);
					result[i] = LogicXor(axb, carry, line);
					SignalValue ab = LogicAnd(aa[i], bi, line);
					SignalValue cx = LogicAnd(carry, axb, line);
					carry = LogicOr(ab, cx, line);
				}
				return MergeBits(result, line);
			}

			SignalValue Equal(SignalValue a, SignalValue b, int line)
			{
				SignalValue[] aa = GetBits(a, line);
				SignalValue[] bb = GetBits(b, line);
				if (aa == null || bb == null || aa.Length != bb.Length) return default;

				EnsureConstants(line);
				SignalValue equal = constOne;
				for (int i = 0; i < aa.Length; i++)
				{
					SignalValue xnor = LogicNot(LogicXor(aa[i], bb[i], line), line);
					equal = LogicAnd(equal, xnor, line);
				}
				return equal;
			}

			SignalValue LessThan(SignalValue a, SignalValue b, int line)
			{
				SignalValue[] aa = GetBits(a, line);
				SignalValue[] bb = GetBits(b, line);
				if (aa == null || bb == null || aa.Length != bb.Length) return default;

				EnsureConstants(line);
				SignalValue less = constZero;
				SignalValue equal = constOne;

				for (int i = aa.Length - 1; i >= 0; i--)
				{
					SignalValue term = LogicAnd(equal, LogicAnd(LogicNot(aa[i], line), bb[i], line), line);
					less = LogicOr(less, term, line);
					SignalValue xnor = LogicNot(LogicXor(aa[i], bb[i], line), line);
					equal = LogicAnd(equal, xnor, line);
				}
				return less;
			}

			SignalValue Shift(SignalValue value, int amount, bool left, int line)
			{
				SignalValue[] bits = GetBits(value, line);
				if (bits == null) return default;
				EnsureConstants(line);
				SignalValue[] result = new SignalValue[bits.Length];

				for (int i = 0; i < result.Length; i++)
				{
					int sourceIndex = left ? i - amount : i + amount;
					result[i] = sourceIndex >= 0 && sourceIndex < bits.Length ? bits[sourceIndex] : constZero;
				}
				return MergeBits(result, line);
			}

			SignalValue Mux(SignalValue select, SignalValue whenTrue, SignalValue whenFalse, int line)
			{
				SignalValue[] a = GetBits(whenTrue, line);
				SignalValue[] b = GetBits(whenFalse, line);
				if (a == null || b == null || a.Length != b.Length) return default;
				SignalValue notSelect = LogicNot(select, line);
				SignalValue[] result = new SignalValue[a.Length];
				for (int i = 0; i < result.Length; i++)
				{
					SignalValue yes = LogicAnd(select, a[i], line);
					SignalValue no = LogicAnd(notSelect, b[i], line);
					result[i] = LogicOr(yes, no, line);
				}
				return MergeBits(result, line);
			}

			SignalValue LogicNot(SignalValue a, int line) => CreateNand(a, a, line);

			SignalValue LogicAnd(SignalValue a, SignalValue b, int line)
			{
				SignalValue n = CreateNand(a, b, line);
				return CreateNand(n, n, line);
			}

			SignalValue LogicOr(SignalValue a, SignalValue b, int line)
			{
				return CreateNand(CreateNand(a, a, line), CreateNand(b, b, line), line);
			}

			SignalValue LogicXor(SignalValue a, SignalValue b, int line)
			{
				SignalValue common = CreateNand(a, b, line);
				SignalValue x = CreateNand(a, common, line);
				SignalValue y = CreateNand(b, common, line);
				return CreateNand(x, y, line);
			}

			SignalValue CreateNand(SignalValue a, SignalValue b, int line)
			{
				InstanceDecl instance = AddGeneratedInstance("NAND", UniqueGeneratedName("nand"), line);
				if (instance == null) return default;
				connections.Add(new ConnectionDecl { Source = a.Endpoint, Target = instance.Name + ".IN_A", Line = line });
				connections.Add(new ConnectionDecl { Source = b.Endpoint, Target = instance.Name + ".IN_B", Line = line });
				return new SignalValue(instance.Name + ".OUT", 1);
			}

			void EnsureConstants(int line)
			{
				if (constantsReady) return;
				InstanceDecl oneGate = AddGeneratedInstance("NAND", UniqueGeneratedName("const1"), line);
				if (oneGate == null) return;
				constOne = new SignalValue(oneGate.Name + ".OUT", 1);
				constZero = CreateNand(constOne, constOne, line);
				constantsReady = true;
			}

			SignalValue Constant(ulong value, int width, int line)
			{
				EnsureConstants(line);
				if (!constantsReady) return default;
				if (width == 1) return (value & 1UL) != 0 ? constOne : constZero;

				SignalValue[] bits = new SignalValue[width];
				for (int i = 0; i < width; i++)
					bits[i] = ((value >> i) & 1UL) != 0 ? constOne : constZero;
				return MergeBits(bits, line);
			}

			SignalValue[] GetBits(SignalValue value, int line)
			{
				if (value.Width == 1) return new[] { value };
				if (!IsSupportedWidth(value.Width)) return null;

				string cacheKey = value.Width + ":" + value.Endpoint;
				if (splitCache.TryGetValue(cacheKey, out SignalValue[] cached)) return cached;

				string type = value.Width == 4 ? "4-1BIT" : "8-1BIT";
				InstanceDecl split = AddGeneratedInstance(type, UniqueGeneratedName("split"), line);
				if (split == null) return null;
				connections.Add(new ConnectionDecl { Source = value.Endpoint, Target = split.Name + ".IN", Line = line });

				SignalValue[] bits = new SignalValue[value.Width];
				for (int i = 0; i < bits.Length; i++)
					bits[i] = new SignalValue(split.Name + ".OUT_" + (char)('A' + i), 1);

				splitCache[cacheKey] = bits;
				return bits;
			}

			SignalValue MergeBits(SignalValue[] bits, int line)
			{
				if (bits == null || bits.Length == 0) return default;
				if (bits.Length == 1) return bits[0];
				if (!IsSupportedWidth(bits.Length))
				{
					diagnostics.Add(new RhdlDiagnostic(line, $"Cannot merge {bits.Length} bits; supported bus widths are 1, 4 and 8."));
					return default;
				}

				string type = bits.Length == 4 ? "1-4BIT" : "1-8BIT";
				InstanceDecl merge = AddGeneratedInstance(type, UniqueGeneratedName("merge"), line);
				if (merge == null) return default;

				for (int i = 0; i < bits.Length; i++)
					connections.Add(new ConnectionDecl
					{
						Source = bits[i].Endpoint,
						Target = merge.Name + ".IN_" + (char)('A' + i),
						Line = line
					});

				return new SignalValue(merge.Name + ".OUT", bits.Length);
			}

			InstanceDecl AddGeneratedInstance(string typeName, string name, int line)
			{
				InstanceDecl instance = new()
				{
					TypeName = typeName,
					Name = name,
					Line = line,
					Generated = true,
					Description = ResolveChipType(typeName)
				};
				if (instance.Description == null)
				{
					diagnostics.Add(new RhdlDiagnostic(line,
						$"Required builtin chip '{typeName}' is not available in the active library."));
					return null;
				}
				instances.Add(instance);
				instanceByName[Normalize(name)] = instance;
				return instance;
			}

			string UniqueGeneratedName(string stem)
			{
				string name;
				do
				{
					name = "__rhdl_" + stem + "_" + generatedCounter++;
				}
				while (usedNames.Contains(Normalize(name)) || instanceByName.ContainsKey(Normalize(name)));
				usedNames.Add(Normalize(name));
				return name;
			}

			bool TryRewriteEndpoint(string text, bool asSource, int line, out string endpoint, out int width)
			{
				endpoint = null;
				width = 0;
				text = text.Trim();

				if (!text.Contains('.'))
				{
					string key = Normalize(text);
					if (!signalByName.TryGetValue(key, out SignalDecl signal))
					{
						string hint = Suggestion(text, signals.Select(s => s.Name));
						diagnostics.Add(new RhdlDiagnostic(line, $"Unknown signal '{text}'.{hint}"));
						return false;
					}

					if (signal.Kind == SignalKind.Wire)
					{
						string bus = BusTypeForWidth(signal.Width);
						endpoint = asSource
							? signal.BackingInstance + "." + bus
							: signal.BackingInstance + "." + bus + " (Hidden)";
						width = signal.Width;
						return true;
					}

					if (asSource && signal.Kind != SignalKind.Input)
					{
						diagnostics.Add(new RhdlDiagnostic(line, $"'{text}' is an output and cannot drive an expression/connection. Use a wire for reusable internal signals."));
						return false;
					}
					if (!asSource && signal.Kind != SignalKind.Output)
					{
						diagnostics.Add(new RhdlDiagnostic(line, $"'{text}' is an input and cannot be driven."));
						return false;
					}

					endpoint = signal.Name;
					width = signal.Width;
					return true;
				}

				int dot = text.IndexOf('.');
				string owner = text.Substring(0, dot).Trim();
				string pin = text.Substring(dot + 1).Trim();
				if (!instanceByName.TryGetValue(Normalize(owner), out InstanceDecl instance) || instance.Description == null)
				{
					string hint = Suggestion(owner, instances.Where(i => !i.Generated).Select(i => i.Name));
					diagnostics.Add(new RhdlDiagnostic(line, $"Unknown instance '{owner}'.{hint}"));
					return false;
				}

				PinDescription? found = FindPin(asSource ? instance.Description.OutputPins : instance.Description.InputPins, pin);
				if (!found.HasValue)
				{
					string direction = asSource ? "output" : "input";
					PinDescription[] availablePins = asSource ? instance.Description.OutputPins : instance.Description.InputPins;
					string hint = Suggestion(pin, (availablePins ?? Array.Empty<PinDescription>()).Select(p => p.Name));
					diagnostics.Add(new RhdlDiagnostic(line,
						$"Chip '{instance.Description.Name}' has no {direction} pin '{pin}'.{hint}"));
					return false;
				}

				endpoint = instance.Name + "." + pin;
				width = (int)found.Value.BitCount;
				return true;
			}

			static PinDescription? FindPin(PinDescription[] pins, string name)
			{
				if (pins == null) return null;
				string normalized = Normalize(name);
				foreach (PinDescription pin in pins)
					if (Normalize(pin.Name) == normalized) return pin;
				return null;
			}

			static string Suggestion(string value, IEnumerable<string> candidates)
			{
				if (string.IsNullOrWhiteSpace(value) || candidates == null) return string.Empty;
				string best = null;
				int bestDistance = int.MaxValue;
				foreach (string candidate in candidates.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase))
				{
					int distance = EditDistance(Normalize(value), Normalize(candidate));
					if (distance < bestDistance)
					{
						bestDistance = distance;
						best = candidate;
					}
				}
				int threshold = Math.Max(1, Math.Min(3, value.Length / 3 + 1));
				return best != null && bestDistance <= threshold ? $" Did you mean '{best}'?" : string.Empty;
			}

			static int EditDistance(string a, string b)
			{
				a ??= string.Empty;
				b ??= string.Empty;
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
					(int[] tmp, previous) = (previous, current);
					current = tmp;
				}
				return previous[b.Length];
			}

			bool TryParseExpression(string text, int line, out ExprNode expression)
			{
				expression = null;
				List<Token> tokens = Tokenize(text, line);
				if (tokens == null || tokens.Count == 0)
				{
					if (tokens != null) diagnostics.Add(new RhdlDiagnostic(line, "Expression is empty."));
					return false;
				}

				int index = 0;
				ExprNode ParseTernary()
				{
					ExprNode condition = ParseComparison();
					if (condition == null) return null;
					if (!Match("?")) return condition;
					ExprNode whenTrue = ParseTernary();
					if (whenTrue == null) return null;
					if (!Match(":"))
					{
						Error("Missing ':' in ternary expression.");
						return null;
					}
					ExprNode whenFalse = ParseTernary();
					if (whenFalse == null) return null;
					return new TernaryExpr { Condition = condition, WhenTrue = whenTrue, WhenFalse = whenFalse, Position = condition.Position };
				}

				ExprNode ParseComparison()
				{
					ExprNode left = ParseOr();
					while (left != null && Peek("==", "!=", "<", ">", "<=", ">="))
					{
						Token op = tokens[index++];
						ExprNode right = ParseOr();
						if (right == null) return null;
						left = new BinaryExpr { Op = op.Text, Left = left, Right = right, Position = op.Position };
					}
					return left;
				}

				ExprNode ParseOr()
				{
					ExprNode left = ParseXor();
					while (left != null && Peek("OR", "|"))
					{
						Token op = tokens[index++];
						ExprNode right = ParseXor();
						if (right == null) return null;
						left = new BinaryExpr { Op = op.Text, Left = left, Right = right, Position = op.Position };
					}
					return left;
				}

				ExprNode ParseXor()
				{
					ExprNode left = ParseAnd();
					while (left != null && Peek("XOR", "^"))
					{
						Token op = tokens[index++];
						ExprNode right = ParseAnd();
						if (right == null) return null;
						left = new BinaryExpr { Op = op.Text, Left = left, Right = right, Position = op.Position };
					}
					return left;
				}

				ExprNode ParseAnd()
				{
					ExprNode left = ParseShift();
					while (left != null && Peek("AND", "&"))
					{
						Token op = tokens[index++];
						ExprNode right = ParseShift();
						if (right == null) return null;
						left = new BinaryExpr { Op = op.Text, Left = left, Right = right, Position = op.Position };
					}
					return left;
				}

				ExprNode ParseShift()
				{
					ExprNode left = ParseAdd();
					while (left != null && Peek("<<", ">>"))
					{
						Token op = tokens[index++];
						ExprNode right = ParseAdd();
						if (right == null) return null;
						left = new BinaryExpr { Op = op.Text, Left = left, Right = right, Position = op.Position };
					}
					return left;
				}

				ExprNode ParseAdd()
				{
					ExprNode left = ParseUnary();
					while (left != null && Peek("+", "-"))
					{
						Token op = tokens[index++];
						ExprNode right = ParseUnary();
						if (right == null) return null;
						left = new BinaryExpr { Op = op.Text, Left = left, Right = right, Position = op.Position };
					}
					return left;
				}

				ExprNode ParseUnary()
				{
					if (Peek("NOT", "!", "~"))
					{
						Token op = tokens[index++];
						ExprNode value = ParseUnary();
						if (value == null)
						{
							Error("Expected expression after unary NOT.");
							return null;
						}
						return new UnaryExpr { Op = op.Text, Value = value, Position = op.Position };
					}
					return ParsePostfix();
				}

				ExprNode ParsePostfix()
				{
					ExprNode value = ParsePrimary();
					if (value == null) return null;

					while (Match("["))
					{
						int pos = tokens[Math.Max(0, index - 1)].Position;
						if (index >= tokens.Count || !int.TryParse(tokens[index].Text, out int first))
						{
							Error("Expected numeric bit index inside '[...]'.");
							return null;
						}
						index++;

						int high = first;
						int low = first;
						if (Match(":"))
						{
							if (index >= tokens.Count || !int.TryParse(tokens[index].Text, out low))
							{
								Error("Expected numeric low bit in slice.");
								return null;
							}
							index++;
						}

						if (!Match("]"))
						{
							Error("Missing ']' in bit selection.");
							return null;
						}

						value = new SliceExpr { Value = value, High = high, Low = low, Position = pos };
					}
					return value;
				}

				ExprNode ParsePrimary()
				{
					if (Match("("))
					{
						ExprNode inner = ParseTernary();
						if (!Match(")"))
						{
							Error("Missing ')' in expression.");
							return null;
						}
						return inner;
					}

					if (Match("{"))
					{
						ConcatExpr concat = new() { Position = tokens[Math.Max(0, index - 1)].Position };
						if (Match("}"))
						{
							Error("Empty concatenation is not allowed.");
							return null;
						}
						while (true)
						{
							ExprNode part = ParseTernary();
							if (part == null) return null;
							concat.Parts.Add(part);
							if (Match("}")) break;
							if (!Match(","))
							{
								Error("Expected ',' or '}' in concatenation.");
								return null;
							}
						}
						return concat;
					}

					if (index >= tokens.Count)
					{
						Error("Expected expression.");
						return null;
					}

					Token token = tokens[index++];
					if (TryParseIntegerLiteral(token.Text, out ulong value, out int widthHint))
						return new ConstExpr { Value = value, WidthHint = widthHint, Raw = token.Text, Position = token.Position };

					if (ValidReference(token.Text))
						return new RefExpr { Name = token.Text, Position = token.Position };

					Error($"Unexpected token '{token.Text}'.", token.Position);
					return null;
				}

				bool Match(string value)
				{
					if (index >= tokens.Count || !string.Equals(tokens[index].Text, value, StringComparison.OrdinalIgnoreCase))
						return false;
					index++;
					return true;
				}

				bool Peek(params string[] values)
				{
					if (index >= tokens.Count) return false;
					foreach (string value in values)
						if (string.Equals(tokens[index].Text, value, StringComparison.OrdinalIgnoreCase)) return true;
					return false;
				}

				void Error(string message, int position = -1)
				{
					if (position < 0 && index < tokens.Count) position = tokens[index].Position;
					diagnostics.Add(new RhdlDiagnostic(line, position >= 0 ? position + 1 : 0, message));
				}

				expression = ParseTernary();
				if (expression == null) return false;
				if (index != tokens.Count)
				{
					diagnostics.Add(new RhdlDiagnostic(line, tokens[index].Position + 1,
						$"Unexpected token '{tokens[index].Text}' in expression."));
					expression = null;
					return false;
				}
				return true;
			}

			List<Token> Tokenize(string text, int line)
			{
				List<Token> tokens = new();
				for (int i = 0; i < text.Length;)
				{
					char c = text[i];
					if (char.IsWhiteSpace(c))
					{
						i++;
						continue;
					}

					if (i + 1 < text.Length)
					{
						string pair = text.Substring(i, 2);
						if (pair is "==" or "!=" or "<=" or ">=" or "<<" or ">>")
						{
							tokens.Add(new Token(pair, i));
							i += 2;
							continue;
						}
					}

					if ("(){}[],:?+-&|^!~<>".IndexOf(c) >= 0)
					{
						tokens.Add(new Token(c.ToString(), i));
						i++;
						continue;
					}

					if (char.IsDigit(c))
					{
						int start = i++;
						while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
						tokens.Add(new Token(text.Substring(start, i - start), start));
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
						tokens.Add(new Token(text.Substring(start, i - start), start));
						continue;
					}

					diagnostics.Add(new RhdlDiagnostic(line, i + 1, $"Unexpected character '{c}' in expression."));
					return null;
				}
				return tokens;
			}

			bool TryEvaluateCompileTimeInt(ExprNode node, out int value)
			{
				value = 0;
				if (node is ConstExpr c && c.Value <= int.MaxValue)
				{
					value = (int)c.Value;
					return true;
				}
				if (node is RefExpr r && parameters.TryGetValue(r.Name, out ulong p) && p <= int.MaxValue)
				{
					value = (int)p;
					return true;
				}
				return false;
			}
		}

		static string CleanLine(string line)
		{
			int comment = line.IndexOf("//", StringComparison.Ordinal);
			if (comment >= 0) line = line.Substring(0, comment);
			line = line.Trim();
			if (line.EndsWith(";")) line = line.Substring(0, line.Length - 1).Trim();
			return line;
		}

		static int FindAssignmentEquals(string text)
		{
			int depthRound = 0;
			int depthSquare = 0;
			int depthCurly = 0;
			for (int i = 0; i < text.Length; i++)
			{
				char c = text[i];
				switch (c)
				{
					case '(': depthRound++; break;
					case ')': depthRound--; break;
					case '[': depthSquare++; break;
					case ']': depthSquare--; break;
					case '{': depthCurly++; break;
					case '}': depthCurly--; break;
				}

				if (c != '=' || depthRound != 0 || depthSquare != 0 || depthCurly != 0) continue;
				char prev = i > 0 ? text[i - 1] : '\0';
				char next = i + 1 < text.Length ? text[i + 1] : '\0';
				if (prev is '=' or '!' or '<' or '>' || next == '=') continue;
				return i;
			}
			return -1;
		}

		static List<string> SplitTopLevel(string text, char separator)
		{
			List<string> result = new();
			int start = 0;
			int round = 0;
			int square = 0;
			int curly = 0;
			for (int i = 0; i < text.Length; i++)
			{
				switch (text[i])
				{
					case '(': round++; break;
					case ')': round--; break;
					case '[': square++; break;
					case ']': square--; break;
					case '{': curly++; break;
					case '}': curly--; break;
					default:
						if (text[i] == separator && round == 0 && square == 0 && curly == 0)
						{
							result.Add(text.Substring(start, i - start));
							start = i + 1;
						}
						break;
				}
			}
			result.Add(text.Substring(start));
			return result;
		}

		static bool TryParseIntegerLiteral(string text, out ulong value, out int widthHint)
		{
			value = 0;
			widthHint = 0;
			if (string.IsNullOrWhiteSpace(text)) return false;

			string clean = text.Replace("_", string.Empty);
			if (clean.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
			{
				string digits = clean.Substring(2);
				if (digits.Length == 0 || digits.Any(c => c != '0' && c != '1')) return false;
				foreach (char c in digits) value = (value << 1) | (c == '1' ? 1UL : 0UL);
				widthHint = digits.Length <= 1 ? 1 : digits.Length <= 4 ? 4 : digits.Length <= 8 ? 8 : digits.Length;
				return true;
			}

			if (clean.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
			{
				string digits = clean.Substring(2);
				if (digits.Length == 0 || !ulong.TryParse(digits, System.Globalization.NumberStyles.HexNumber, null, out value))
					return false;
				int bits = digits.Length * 4;
				widthHint = bits <= 4 ? 4 : bits <= 8 ? 8 : bits;
				return true;
			}

			return ulong.TryParse(clean, out value);
		}

		static int SmallestWidth(ulong value)
		{
			if (value <= 1) return 1;
			if (value <= 0xF) return 4;
			return 8;
		}

		static bool IsSupportedWidth(int width) => width is 1 or 4 or 8;

		static string BusTypeForWidth(int width) => width switch
		{
			1 => "BUS-1",
			4 => "BUS-4",
			8 => "BUS-8",
			_ => null
		};

		static string Normalize(string text)
		{
			if (string.IsNullOrEmpty(text)) return string.Empty;
			char[] buffer = new char[text.Length];
			int n = 0;
			foreach (char c in text)
				if (char.IsLetterOrDigit(c)) buffer[n++] = char.ToUpperInvariant(c);
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

		static bool ValidReference(string text)
		{
			if (string.IsNullOrWhiteSpace(text)) return false;
			string[] parts = text.Split('.');
			return parts.Length is 1 or 2 && parts.All(ValidIdentifier);
		}
	}
}
