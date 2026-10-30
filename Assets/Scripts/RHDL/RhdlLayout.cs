using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DLS.RHDL
{
	// Draws a generated circuit the way a schematic is drawn by hand: signals flow left to
	// right in columns, every wire runs horizontally into its pin, and the vertical parts of
	// wires get their own tracks between the columns so two wires never lie on top of each
	// other. Feedback wires leave to the right and loop back over the top or bottom of the
	// columns they cross.
	//
	// The steps are those of a layered (Sugiyama) layout:
	// 1. reverse feedback wires and give every part a column (longest path);
	// 2. add pass-through points where a wire crosses a column;
	// 3. order each column to reduce crossings, keeping the source order as a tie-break;
	// 4. place parts vertically so connected pins line up (isotonic regression);
	// 5. give each wire's vertical segment a track in the channel it crosses.
	internal sealed class LayoutNode
	{
		public float Width;
		public float Height;
		// Pin positions relative to the node centre.
		public Vector2[] InputPins = Array.Empty<Vector2>();
		public Vector2[] OutputPins = Array.Empty<Vector2>();
		public bool IsInputPort;
		public bool IsOutputPort;
		// Source order hints: statement index and bit, used to break ties.
		public int Statement;
		public int Slice = -1;
		public int Index;

		// Results.
		public Vector2 Centre;

		internal int Layer;
		internal float Order;
		internal bool IsDummy;
		internal readonly List<Hop> Left = new();
		internal readonly List<Hop> Right = new();
	}

	internal sealed class LayoutEdge
	{
		public LayoutNode Source;
		public int SourcePin;
		public LayoutNode Target;
		public int TargetPin;

		// Result: bend points, without the two pin positions.
		public List<Vector2> Bends = new();

		internal bool Reversed;
		internal readonly List<Hop> Route = new();
	}

	// One stretch of a wire between two neighbouring columns.
	internal sealed class Hop
	{
		public int Channel;
		public LayoutNode LeftNode;
		public int LeftPin;
		public bool LeftIsOutput;
		public LayoutNode RightNode;
		public int RightPin;
		public bool RightIsOutput;
		// For U-turns both ends are on the same side of the channel.
		public bool UTurnLeft;
		public bool UTurnRight;
		public NetTrunk Trunk;

		public float LeftY => PinY(LeftNode, LeftPin, LeftIsOutput);
		public float RightY => PinY(RightNode, RightPin, RightIsOutput);

		static float PinY(LayoutNode node, int pin, bool output)
		{
			if (node.IsDummy) return node.Centre.y;
			Vector2[] pins = output ? node.OutputPins : node.InputPins;
			return node.Centre.y + pins[pin].y;
		}
	}

	// All stretches of one signal in one channel share one vertical segment.
	internal sealed class NetTrunk
	{
		public int Channel;
		public readonly List<Hop> Hops = new();
		public float Top;
		public float Bottom;
		public int Track = -1;
		public readonly HashSet<float> LeftYs = new();
		public readonly HashSet<float> RightYs = new();
		public bool NeedsTrack;
	}

	internal static class RhdlLayout
	{
		const float Grid = 0.125f;
		const float TrackSpacing = Grid * 2;
		const float NodeGap = Grid * 3;
		const float PortGap = Grid * 2;
		const float DummyHeight = Grid * 2;
		const float MinChannelWidth = Grid * 6;

		public static void Arrange(List<LayoutNode> nodes, List<LayoutEdge> edges)
		{
			if (nodes.Count == 0) return;
			for (int i = 0; i < nodes.Count; i++) nodes[i].Index = i;

			AssignLayers(nodes, edges);
			List<LayoutNode> dummies = new();
			BuildRoutes(nodes, edges, dummies);

			List<LayoutNode> all = nodes.Concat(dummies).ToList();
			int layerCount = all.Max(n => n.Layer) + 1;
			List<LayoutNode>[] layers = new List<LayoutNode>[layerCount];
			for (int i = 0; i < layerCount; i++) layers[i] = new List<LayoutNode>();
			foreach (LayoutNode node in all) layers[node.Layer].Add(node);

			OrderLayers(layers);
			PlaceVertically(layers);
			float[] channelWidths = AssignTracks(edges, layerCount);
			PlaceHorizontally(layers, channelWidths, out float[] channelLeft);
			BuildBends(edges, channelLeft);
		}

		// ---- 1. Columns ----

		static void AssignLayers(List<LayoutNode> nodes, List<LayoutEdge> edges)
		{
			// Two parts that drive each other (the two NANDs of a latch) are drawn one above
			// the other in one column, with the feedback wires crossing between them.
			Dictionary<LayoutNode, LayoutNode> group = nodes.ToDictionary(n => n, n => n);
			HashSet<(LayoutNode, LayoutNode)> links = new(edges.Select(e => (e.Source, e.Target)));
			foreach (LayoutEdge edge in edges)
			{
				LayoutNode a = edge.Source;
				LayoutNode b = edge.Target;
				if (a == b || a.IsInputPort || b.IsOutputPort || group[a] != a || group[b] != b) continue;
				if (a.Height > 1f || b.Height > 1f || !links.Contains((b, a))) continue;
				if (nodes.Any(n => n != a && group[n] == a) || nodes.Any(n => n != b && group[n] == b)) continue;
				group[b] = a;
			}

			foreach (LayoutEdge edge in edges)
			{
				if (edge.Source != edge.Target && group[edge.Source] == group[edge.Target]) edge.Reversed = true;
			}

			// A wire into a part written above the part that drives it is feedback: in RHDL,
			// using a name before the line that assigns it is how loops are written.
			foreach (LayoutEdge edge in edges)
			{
				LayoutNode source = group[edge.Source];
				LayoutNode target = group[edge.Target];
				if (source == target || target.IsOutputPort || source.IsInputPort) continue;
				if (target.Statement >= 0 && target.Statement < source.Statement) edge.Reversed = true;
			}

			Dictionary<LayoutNode, List<LayoutEdge>> outgoing = nodes.ToDictionary(n => n, _ => new List<LayoutEdge>());
			foreach (LayoutEdge edge in edges)
			{
				if (group[edge.Source] == group[edge.Target] || edge.Reversed) continue;
				outgoing[group[edge.Source]].Add(edge);
			}

			// Feedback: a depth-first search in source order; edges that close a loop are
			// drawn backwards.
			Dictionary<LayoutNode, int> state = new();
			IEnumerable<LayoutNode> roots = nodes
				.OrderBy(n => n.IsInputPort ? 0 : 1)
				.ThenBy(n => n.Statement)
				.ThenBy(n => n.Slice)
				.ThenBy(n => n.Index);

			foreach (LayoutNode root in roots)
			{
				if (group[root] != root || state.ContainsKey(root)) continue;
				Stack<(LayoutNode node, int next)> stack = new();
				stack.Push((root, 0));
				state[root] = 1;
				while (stack.Count > 0)
				{
					(LayoutNode node, int next) = stack.Pop();
					List<LayoutEdge> list = outgoing[node];
					if (next < list.Count)
					{
						stack.Push((node, next + 1));
						LayoutEdge edge = list[next];
						LayoutNode target = group[edge.Target];
						if (target.IsOutputPort) continue;
						if (state.TryGetValue(target, out int targetState))
						{
							if (targetState == 1) edge.Reversed = true;
							continue;
						}

						state[target] = 1;
						stack.Push((target, 0));
					}
					else
					{
						state[node] = 2;
					}
				}
			}

			// Longest path from the inputs.
			Dictionary<LayoutNode, List<LayoutNode>> successors = nodes.ToDictionary(n => n, _ => new List<LayoutNode>());
			Dictionary<LayoutNode, int> indegree = nodes.ToDictionary(n => n, _ => 0);
			foreach (LayoutEdge edge in edges)
			{
				LayoutNode source = group[edge.Source];
				LayoutNode target = group[edge.Target];
				if (target.IsOutputPort || source == target) continue;
				(LayoutNode from, LayoutNode to) = edge.Reversed ? (target, source) : (source, target);
				successors[from].Add(to);
				indegree[to]++;
			}

			Queue<LayoutNode> ready = new(nodes.Where(n => indegree[n] == 0 && !n.IsOutputPort && group[n] == n));
			foreach (LayoutNode node in nodes) node.Layer = node.IsInputPort ? 0 : 1;
			List<LayoutNode> topological = new();
			while (ready.Count > 0)
			{
				LayoutNode node = ready.Dequeue();
				topological.Add(node);
				foreach (LayoutNode next in successors[node])
				{
					next.Layer = Math.Max(next.Layer, node.Layer + 1);
					if (--indegree[next] == 0) ready.Enqueue(next);
				}
			}

			// Parts without inputs (constants, clocks) sit just before their first reader.
			for (int i = topological.Count - 1; i >= 0; i--)
			{
				LayoutNode node = topological[i];
				if (node.IsInputPort) continue;
				bool hasPredecessor = edges.Any(e => group[e.Target] == node && group[e.Source] != node);
				if (hasPredecessor || successors[node].Count == 0) continue;
				int earliestReader = successors[node].Min(s => s.Layer);
				node.Layer = Math.Max(1, earliestReader - 1);
			}

			foreach (LayoutNode node in nodes) node.Layer = group[node].Layer;

			int last = nodes.Where(n => !n.IsOutputPort).Select(n => n.Layer).DefaultIfEmpty(0).Max() + 1;
			foreach (LayoutNode node in nodes)
			{
				if (node.IsOutputPort) node.Layer = last;
			}
		}

		// ---- 2. Routes through the columns ----

		static void BuildRoutes(List<LayoutNode> nodes, List<LayoutEdge> edges, List<LayoutNode> dummies)
		{
			Dictionary<(LayoutNode, int, int, bool), LayoutNode> sharedDummies = new();
			Dictionary<(int, LayoutNode, int, bool), NetTrunk> trunks = new();

			LayoutNode Dummy(LayoutEdge edge, int layer, bool backward)
			{
				var key = (edge.Source, edge.SourcePin, layer, backward);
				if (sharedDummies.TryGetValue(key, out LayoutNode dummy)) return dummy;
				dummy = new LayoutNode
				{
					IsDummy = true,
					Layer = layer,
					Height = DummyHeight,
					Statement = edge.Source.Statement,
					Slice = edge.Source.Slice,
					Index = nodes.Count + dummies.Count
				};
				dummies.Add(dummy);
				sharedDummies[key] = dummy;
				return dummy;
			}

			foreach (LayoutEdge edge in edges)
			{
				LayoutNode source = edge.Source;
				LayoutNode target = edge.Target;
				bool backward = edge.Reversed || target.Layer <= source.Layer;
				edge.Reversed = backward;

				List<Hop> hops = new();
				if (!backward)
				{
					LayoutNode leftNode = source;
					int leftPin = edge.SourcePin;
					bool leftIsOutput = true;
					for (int layer = source.Layer + 1; layer < target.Layer; layer++)
					{
						LayoutNode dummy = Dummy(edge, layer, false);
						hops.Add(new Hop { Channel = layer - 1, LeftNode = leftNode, LeftPin = leftPin, LeftIsOutput = leftIsOutput, RightNode = dummy });
						leftNode = dummy;
						leftPin = 0;
						leftIsOutput = true;
					}

					hops.Add(new Hop
					{
						Channel = target.Layer - 1,
						LeftNode = leftNode,
						LeftPin = leftPin,
						LeftIsOutput = leftIsOutput,
						RightNode = target,
						RightPin = edge.TargetPin,
						RightIsOutput = false
					});
				}
				else
				{
					// Out of the source to the right, back over the columns, into the target
					// from the left.
					int j = source.Layer;
					int i = target.Layer;
					LayoutNode turn = Dummy(edge, j, true);
					hops.Add(new Hop
					{
						Channel = j,
						LeftNode = source,
						LeftPin = edge.SourcePin,
						LeftIsOutput = true,
						RightNode = turn,
						UTurnLeft = true
					});

					LayoutNode previous = turn;
					for (int layer = j - 1; layer >= i; layer--)
					{
						LayoutNode dummy = Dummy(edge, layer, true);
						hops.Add(new Hop { Channel = layer, LeftNode = dummy, RightNode = previous });
						previous = dummy;
					}

					hops.Add(new Hop
					{
						Channel = i - 1,
						LeftNode = previous,
						RightNode = target,
						RightPin = edge.TargetPin,
						RightIsOutput = false,
						UTurnRight = true
					});
				}

				foreach (Hop hop in hops)
				{
					var key = (hop.Channel, edge.Source, edge.SourcePin, backward);
					if (!trunks.TryGetValue(key, out NetTrunk trunk))
					{
						trunks[key] = trunk = new NetTrunk { Channel = hop.Channel };
					}

					if (!trunk.Hops.Contains(hop)) trunk.Hops.Add(hop);
					hop.Trunk = trunk;
					edge.Route.Add(hop);

					if (!hop.UTurnLeft && !hop.UTurnRight)
					{
						hop.LeftNode.Right.Add(hop);
						hop.RightNode.Left.Add(hop);
					}
					else if (hop.UTurnLeft)
					{
						hop.LeftNode.Right.Add(hop);
						hop.RightNode.Right.Add(hop);
					}
					else
					{
						hop.LeftNode.Left.Add(hop);
						hop.RightNode.Left.Add(hop);
					}
				}
			}
		}

		// ---- 3. Order within columns ----

		static void OrderLayers(List<LayoutNode>[] layers)
		{
			foreach (List<LayoutNode> layer in layers)
			{
				// Rewired draws the most significant bit of a bus at the top, so bit slices are
				// stacked the same way.
				List<LayoutNode> sorted = layer
					.OrderBy(n => n.IsInputPort || n.IsOutputPort ? n.Index : 0)
					.ThenBy(n => n.Statement)
					.ThenByDescending(n => n.Slice)
					.ThenBy(n => n.Index)
					.ToList();
				SetOrder(layer, sorted);
			}

			float[][] best = layers.Select(l => l.Select(n => n.Order).ToArray()).ToArray();
			long bestCrossings = CountCrossings(layers);

			for (int pass = 0; pass < 12 && bestCrossings > 0; pass++)
			{
				bool down = pass % 2 == 0;
				if (down)
				{
					for (int i = 1; i < layers.Length; i++) Reorder(layers[i], useLeft: true);
				}
				else
				{
					for (int i = layers.Length - 2; i >= 0; i--) Reorder(layers[i], useLeft: false);
				}

				long crossings = CountCrossings(layers);
				if (crossings < bestCrossings)
				{
					bestCrossings = crossings;
					best = layers.Select(l => l.Select(n => n.Order).ToArray()).ToArray();
				}
			}

			for (int i = 0; i < layers.Length; i++)
			{
				for (int j = 0; j < layers[i].Count; j++) layers[i][j].Order = best[i][j];
				layers[i].Sort((a, b) => a.Order.CompareTo(b.Order));
			}
		}

		static void SetOrder(List<LayoutNode> layer, List<LayoutNode> sorted)
		{
			for (int i = 0; i < sorted.Count; i++) sorted[i].Order = i;
			layer.Clear();
			layer.AddRange(sorted);
		}

		static void Reorder(List<LayoutNode> layer, bool useLeft)
		{
			// Ports keep their declared order; everything else moves to the average position
			// of what it connects to on the given side. Ties keep the current order, so
			// bit-sliced logic stays in bit order.
			if (layer.Count == 0 || layer[0].IsInputPort || layer[0].IsOutputPort) return;

			Dictionary<LayoutNode, float> key = new();
			foreach (LayoutNode node in layer)
			{
				List<Hop> hops = useLeft ? node.Left : node.Right;
				float sum = 0;
				int count = 0;
				foreach (Hop hop in hops)
				{
					LayoutNode other = OtherEnd(hop, node);
					if (other == null || other == node) continue;
					sum += other.Order;
					count++;
				}

				key[node] = count > 0 ? sum / count : node.Order;
			}

			List<LayoutNode> sorted = layer.OrderBy(n => key[n]).ThenBy(n => n.Order).ToList();
			SetOrder(layer, sorted);
		}

		static LayoutNode OtherEnd(Hop hop, LayoutNode node)
		{
			if (hop.UTurnLeft || hop.UTurnRight) return hop.LeftNode == node ? hop.RightNode : hop.LeftNode;
			return hop.LeftNode == node ? hop.RightNode : hop.LeftNode;
		}

		static long CountCrossings(List<LayoutNode>[] layers)
		{
			long crossings = 0;
			for (int i = 0; i + 1 < layers.Length; i++)
			{
				List<(float a, float b)> segments = new();
				foreach (LayoutNode node in layers[i])
				{
					foreach (Hop hop in node.Right)
					{
						if (hop.UTurnLeft || hop.UTurnRight || hop.LeftNode != node) continue;
						segments.Add((node.Order, hop.RightNode.Order));
					}
				}

				segments.Sort();
				for (int a = 0; a < segments.Count; a++)
				{
					for (int b = a + 1; b < segments.Count; b++)
					{
						if (segments[b].a > segments[a].a && segments[b].b < segments[a].b) crossings++;
					}
				}
			}

			return crossings;
		}

		// ---- 4. Vertical positions ----

		static void PlaceVertically(List<LayoutNode>[] layers)
		{
			// Start from plain stacks, then repeatedly move every column so that each part's
			// pins line up with the pins they connect to, keeping the column order.
			foreach (List<LayoutNode> layer in layers)
			{
				float y = 0;
				for (int i = 0; i < layer.Count; i++)
				{
					if (i > 0) y -= Separation(layer[i - 1], layer[i]);
					layer[i].Centre = new Vector2(0, y);
				}
			}

			for (int pass = 0; pass < 10; pass++)
			{
				bool down = pass % 2 == 0;
				int start = down ? 0 : layers.Length - 1;
				int step = down ? 1 : -1;
				for (int i = start; i >= 0 && i < layers.Length; i += step)
				{
					AlignLayer(layers[i], useLeft: pass < 2 ? down : (bool?)null);
				}
			}

			// Centre the whole drawing and snap to the grid without breaking spacing.
			float top = layers.SelectMany(l => l).Max(n => n.Centre.y + n.Height / 2);
			float bottom = layers.SelectMany(l => l).Min(n => n.Centre.y - n.Height / 2);
			float shift = -(top + bottom) / 2;
			foreach (List<LayoutNode> layer in layers)
			{
				float previous = float.MaxValue;
				LayoutNode previousNode = null;
				foreach (LayoutNode node in layer)
				{
					float y = Snap(node.Centre.y + shift);
					if (previousNode != null && previous - y < Separation(previousNode, node) - 0.001f)
					{
						y = (float)Math.Floor((previous - Separation(previousNode, node)) / Grid) * Grid;
					}

					node.Centre = new Vector2(0, y);
					previous = y;
					previousNode = node;
				}
			}
		}

		static float Separation(LayoutNode upper, LayoutNode lower)
		{
			float gap = upper.IsDummy || lower.IsDummy ? Grid * 2 : upper.IsInputPort || upper.IsOutputPort ? PortGap : NodeGap;
			if (upper.IsDummy && lower.IsDummy) gap = 0;
			return upper.Height / 2 + lower.Height / 2 + gap;
		}

		static void AlignLayer(List<LayoutNode> layer, bool? useLeft)
		{
			int n = layer.Count;
			if (n == 0) return;
			float[] desired = new float[n];
			float[] weight = new float[n];
			for (int i = 0; i < n; i++)
			{
				LayoutNode node = layer[i];
				List<float> wishes = new();
				if (useLeft != false) Collect(node, node.Left, wishes);
				if (useLeft != true) Collect(node, node.Right, wishes);
				if (wishes.Count == 0)
				{
					desired[i] = node.Centre.y;
					weight[i] = 0.01f;
					continue;
				}

				wishes.Sort();
				desired[i] = wishes[wishes.Count / 2];
				weight[i] = wishes.Count;
			}

			// Positions must keep the order: y[i+1] <= y[i] - sep(i). With offsets
			// c[i] = sum of separations, z = y + c must be non-increasing; solve the weighted
			// least-squares problem with pool-adjacent-violators.
			float[] offset = new float[n];
			for (int i = 1; i < n; i++) offset[i] = offset[i - 1] + Separation(layer[i - 1], layer[i]);

			List<(float value, float weight, int count)> blocks = new();
			for (int i = 0; i < n; i++)
			{
				blocks.Add((desired[i] + offset[i], weight[i], 1));
				while (blocks.Count > 1 && blocks[^2].value < blocks[^1].value)
				{
					var b = blocks[^1];
					var a = blocks[^2];
					float w = a.weight + b.weight;
					blocks.RemoveAt(blocks.Count - 1);
					blocks[^1] = ((a.value * a.weight + b.value * b.weight) / w, w, a.count + b.count);
				}
			}

			int index = 0;
			foreach (var block in blocks)
			{
				for (int k = 0; k < block.count; k++, index++)
				{
					layer[index].Centre = new Vector2(0, block.value - offset[index]);
				}
			}
		}

		static void Collect(LayoutNode node, List<Hop> hops, List<float> wishes)
		{
			foreach (Hop hop in hops)
			{
				// Where would this node's centre be if the pin at this end lined up with the
				// pin at the other end?
				bool nodeIsLeft = hop.LeftNode == node && !hop.UTurnRight || hop.UTurnLeft && hop.LeftNode == node;
				LayoutNode other;
				float otherY;
				float ownOffset;
				if (hop.UTurnLeft || hop.UTurnRight)
				{
					// A wire turning back sits just above or below the part it turns at.
					if (!node.IsDummy) continue;
					LayoutNode part = hop.LeftNode == node ? hop.RightNode : hop.LeftNode;
					if (part == node || part.IsDummy) continue;
					wishes.Add(part.Centre.y);
					continue;
				}

				if (nodeIsLeft)
				{
					other = hop.RightNode;
					otherY = hop.RightY;
					ownOffset = node.IsDummy ? 0 : node.OutputPins[hop.LeftPin].y;
				}
				else
				{
					other = hop.LeftNode;
					otherY = hop.LeftY;
					ownOffset = node.IsDummy ? 0 : (hop.RightIsOutput ? node.OutputPins[hop.RightPin].y : node.InputPins[hop.RightPin].y);
				}

				if (other == node) continue;
				wishes.Add(otherY - ownOffset);
			}
		}

		static float Snap(float value) => Mathf.Round(value / Grid) * Grid;

		// ---- 5. Tracks ----

		static float[] AssignTracks(List<LayoutEdge> edges, int layerCount)
		{
			// Channel c lies between column c and c + 1; channel -1 is never used.
			Dictionary<int, List<NetTrunk>> byChannel = new();
			HashSet<NetTrunk> trunks = new();
			foreach (LayoutEdge edge in edges)
			{
				foreach (Hop hop in edge.Route) trunks.Add(hop.Trunk);
			}

			foreach (NetTrunk trunk in trunks)
			{
				trunk.Top = float.MinValue;
				trunk.Bottom = float.MaxValue;
				foreach (Hop hop in trunk.Hops)
				{
					float leftY = hop.UTurnRight ? float.NaN : hop.LeftY;
					float rightY = hop.UTurnLeft ? float.NaN : hop.RightY;
					if (hop.UTurnLeft) trunk.LeftYs.Add(hop.RightY);
					if (hop.UTurnRight) trunk.RightYs.Add(hop.LeftY);
					if (!float.IsNaN(leftY)) trunk.LeftYs.Add(leftY);
					if (!float.IsNaN(rightY)) trunk.RightYs.Add(rightY);
				}

				foreach (float y in trunk.LeftYs.Concat(trunk.RightYs))
				{
					trunk.Top = Math.Max(trunk.Top, y);
					trunk.Bottom = Math.Min(trunk.Bottom, y);
				}

				trunk.NeedsTrack = trunk.Top - trunk.Bottom > 0.001f ||
				                   trunk.Hops.Any(h => h.UTurnLeft || h.UTurnRight);
				if (!byChannel.TryGetValue(trunk.Channel, out List<NetTrunk> list)) byChannel[trunk.Channel] = list = new List<NetTrunk>();
				list.Add(trunk);
			}

			float[] widths = new float[Math.Max(0, layerCount - 1)];
			for (int c = 0; c < widths.Length; c++) widths[c] = MinChannelWidth;

			foreach ((int channel, List<NetTrunk> list) in byChannel)
			{
				if (channel < 0 || channel >= widths.Length) continue;
				List<NetTrunk> needing = list.Where(t => t.NeedsTrack).ToList();

				// A wire whose horizontal leaves the left side at height y must use a track to
				// the left of any wire that enters the right side at the same height, or the
				// two horizontals would overlap.
				Dictionary<NetTrunk, List<NetTrunk>> mustBeLeftOf = needing.ToDictionary(t => t, _ => new List<NetTrunk>());
				Dictionary<NetTrunk, int> before = needing.ToDictionary(t => t, _ => 0);
				foreach (NetTrunk p in needing)
				{
					foreach (NetTrunk q in needing)
					{
						if (p == q) continue;
						if (p.LeftYs.Any(y => q.RightYs.Any(r => Math.Abs(r - y) < 0.001f)))
						{
							mustBeLeftOf[p].Add(q);
							before[q]++;
						}
					}
				}

				// Topological order (constraints first), wider spans first within a rank.
				List<NetTrunk> order = new();
				List<NetTrunk> available = needing.Where(t => before[t] == 0).ToList();
				HashSet<NetTrunk> placed = new();
				while (order.Count < needing.Count)
				{
					if (available.Count == 0)
					{
						// A cycle of constraints: take any remaining trunk.
						available.Add(needing.First(t => !placed.Contains(t)));
					}

					available.Sort((a, b) => b.Top.CompareTo(a.Top));
					NetTrunk next = available[0];
					available.RemoveAt(0);
					if (!placed.Add(next)) continue;
					order.Add(next);
					foreach (NetTrunk later in mustBeLeftOf[next])
					{
						if (--before[later] == 0 && !placed.Contains(later)) available.Add(later);
					}
				}

				List<List<NetTrunk>> tracks = new();
				foreach (NetTrunk trunk in order)
				{
					int minimum = 0;
					foreach (NetTrunk p in needing)
					{
						if (p.Track >= 0 && mustBeLeftOf[p].Contains(trunk)) minimum = Math.Max(minimum, p.Track + 1);
					}

					int track = minimum;
					while (track < tracks.Count && tracks[track].Any(other => Overlaps(other, trunk))) track++;
					if (track == tracks.Count) tracks.Add(new List<NetTrunk>());
					tracks[track].Add(trunk);
					trunk.Track = track;
				}

				widths[channel] = Math.Max(MinChannelWidth, (tracks.Count + 1) * TrackSpacing);
			}

			return widths;
		}

		static bool Overlaps(NetTrunk a, NetTrunk b) =>
			a.Bottom - TrackSpacing / 2 < b.Top && b.Bottom - TrackSpacing / 2 < a.Top;

		// ---- Horizontal positions and wires ----

		static void PlaceHorizontally(List<LayoutNode>[] layers, float[] channelWidths, out float[] channelLeft)
		{
			channelLeft = new float[channelWidths.Length];
			float x = 0;
			for (int i = 0; i < layers.Length; i++)
			{
				float width = layers[i].Count == 0 ? 0 : layers[i].Max(n => n.Width);
				float centre = x + width / 2;
				foreach (LayoutNode node in layers[i]) node.Centre = new Vector2(Snap(centre), node.Centre.y);
				x += width;
				if (i < channelWidths.Length)
				{
					channelLeft[i] = x;
					x += channelWidths[i];
				}
			}

			// Centre the drawing horizontally.
			float shift = -Snap(x / 2);
			foreach (LayoutNode node in layers.SelectMany(l => l)) node.Centre += new Vector2(shift, 0);
			for (int i = 0; i < channelLeft.Length; i++) channelLeft[i] += shift;
		}

		static void BuildBends(List<LayoutEdge> edges, float[] channelLeft)
		{
			foreach (LayoutEdge edge in edges)
			{
				List<Vector2> points = new();
				foreach (Hop hop in edge.Route)
				{
					NetTrunk trunk = hop.Trunk;
					if (!trunk.NeedsTrack) continue;
					float x = channelLeft[trunk.Channel] + (trunk.Track + 1) * TrackSpacing;

					// The order in which this wire visits the hop's two ends.
					float from;
					float to;
					if (hop.UTurnLeft)
					{
						from = hop.LeftY;
						to = hop.RightNode.Centre.y;
					}
					else if (hop.UTurnRight)
					{
						from = hop.LeftNode.Centre.y;
						to = hop.RightY;
					}
					else if (edge.Reversed)
					{
						from = hop.RightY;
						to = hop.LeftY;
					}
					else
					{
						from = hop.LeftY;
						to = hop.RightY;
					}

					if (Math.Abs(from - to) < 0.001f && !hop.UTurnLeft && !hop.UTurnRight) continue;
					points.Add(new Vector2(Snap(x), from));
					points.Add(new Vector2(Snap(x), to));
				}

				edge.Bends = points;
			}
		}
	}
}
