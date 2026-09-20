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
		public readonly string Message;

		public RhdlDiagnostic(int line, string message)
		{
			Line = line;
			Message = message;
		}

		public override string ToString() => Line > 0 ? $"line {Line}: {Message}" : Message;
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
			List<RhdlDiagnostic> diagnostics = new();
			List<PortDecl> ports = new();
			List<InstanceDecl> instances = new();
			List<ConnectionDecl> connections = new();

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

			WireDescription[] wires = connections.Select(connection => new WireDescription
			{
				SourcePinAddress = connection.Source.Address,
				TargetPinAddress = connection.Target.Address,
				ConnectionType = WireConnectionType.ToPins,
				ConnectedWireIndex = -1,
				ConnectedWireSegmentIndex = -1,
				Points = new Vector2[2]
			}).ToArray();

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

			Dictionary<string, InstanceDecl> byName = instances.ToDictionary(i => Normalize(i.Name), StringComparer.OrdinalIgnoreCase);
			Dictionary<InstanceDecl, List<InstanceDecl>> outgoing = instances.ToDictionary(i => i, _ => new List<InstanceDecl>());
			Dictionary<InstanceDecl, int> indegree = instances.ToDictionary(i => i, _ => 0);

			foreach (ConnectionDecl connection in connections)
			{
				if (string.IsNullOrEmpty(connection.SourceInstance) || string.IsNullOrEmpty(connection.TargetInstance)) continue;
				InstanceDecl source = byName[Normalize(connection.SourceInstance)];
				InstanceDecl target = byName[Normalize(connection.TargetInstance)];
				if (source == target || outgoing[source].Contains(target)) continue;
				outgoing[source].Add(target);
				indegree[target]++;
			}

			Queue<InstanceDecl> queue = new(instances.Where(i => indegree[i] == 0));
			HashSet<InstanceDecl> visited = new();
			while (queue.Count > 0)
			{
				InstanceDecl current = queue.Dequeue();
				visited.Add(current);
				foreach (InstanceDecl next in outgoing[current])
				{
					next.Level = Mathf.Max(next.Level, current.Level + 1);
					indegree[next]--;
					if (indegree[next] == 0) queue.Enqueue(next);
				}
			}

			// Cyclic networks are legal in Rewired. Keep unresolved SCC members in
			// their declaration-level column instead of endlessly increasing depth.
			foreach (InstanceDecl instance in instances)
			{
				if (!visited.Contains(instance)) instance.Level = 0;
			}

			foreach (IGrouping<int, InstanceDecl> group in instances.GroupBy(i => i.Level).OrderBy(g => g.Key))
			{
				InstanceDecl[] column = group.ToArray();
				float x = -2.5f + group.Key * 4.0f;
				float totalHeight = (column.Length - 1) * 2.0f;
				for (int i = 0; i < column.Length; i++)
				{
					float y = totalHeight / 2f - i * 2.0f;
					column[i].Position = Snap(new Vector2(x, y));
				}
			}
		}

		static PinDescription[] CreateRootPins(List<PortDecl> ports, List<InstanceDecl> instances, bool left)
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
				float y = totalHeight / 2f - i * 1.5f;
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
