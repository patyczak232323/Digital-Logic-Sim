using System;
using System.Text;
using DLS.Game;
using DLS.Simulation;
using Seb.Types;
using Seb.Vis;
using Seb.Vis.UI;
using UnityEngine;

namespace DLS.Graphics
{
	public static class SimulationDiagnosticsMenu
	{
		const float menuWidth = 96;
		const float columnGap = 2;
		const float columnWidth = (menuWidth - columnGap) / 2;
		const float rowHeight = 2.8f;
		const float spacing = 0.3f;
		const float traceHeight = 7.1f;

		static readonly string[] OffOn = { "OFF", "ON" };
		static readonly UIHandle ID_Profiler = new("SIM_DIAG_Profiler");
		static readonly UIHandle ID_Waveform = new("SIM_DIAG_Waveform");

		public static void OnMenuOpened()
		{
			UI.GetWheelSelectorState(ID_Profiler).index = SimulationProfiler.Enabled ? 1 : 0;
			UI.GetWheelSelectorState(ID_Waveform).index = SimulationWaveformRecorder.Enabled ? 1 : 0;
		}

		public static void DrawMenu()
		{
			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			MenuHelper.DrawBackgroundOverlay();
			Draw.ID panelID = UI.ReservePanel();

			Vector2 topLeft = UI.Centre + new Vector2(-menuWidth / 2, 25.5f);
			Color rowCol = new(0.12f, 0.12f, 0.12f, 0.92f);
			Color textCol = Color.white;
			Color dim = Color.white * 0.72f;

			using (UI.BeginBoundsScope(true))
			{
				UI.DrawText(
					"SIMULATION DIAGNOSTICS",
					theme.FontBold,
					theme.FontSizeRegular * 1.15f,
					topLeft,
					Anchor.TextCentreLeft,
					textCol);

				Vector2 leftPos = topLeft + Vector2.down * 3.2f;
				Vector2 rightPos = leftPos + Vector2.right * (columnWidth + columnGap);

				DrawPerformanceColumn(ref leftPos);
				DrawWaveformColumn(ref rightPos);

				float bottom = Mathf.Min(leftPos.y, rightPos.y) - 0.8f;
				Vector2 closePos = new(topLeft.x, bottom);
				bool close = UI.Button(
					"CLOSE",
					theme.ButtonTheme,
					closePos,
					new Vector2(menuWidth, DrawSettings.ButtonHeight),
					true,
					false,
					true,
					Anchor.TopLeft);

				if (close || KeyboardShortcuts.CancelShortcutTriggered)
				{
					UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);
				}

				Bounds2D bounds = UI.GetCurrentBoundsScope();
				MenuHelper.DrawReservedMenuPanel(panelID, bounds);
			}

			void DrawPerformanceColumn(ref Vector2 pos)
			{
				UI.DrawText("PERFORMANCE", theme.FontBold, theme.FontSizeRegular, pos, Anchor.TextCentreLeft, textCol);
				pos.y -= 2.2f;

				DrawInfoRow(ref pos, "Engine: REWIRED FAST | deterministic + feedback JIT", dim);

				int profilerMode = MenuHelper.LabeledOptionsWheel(
					"Hot-chip profiler",
					textCol,
					pos,
					new Vector2(columnWidth, rowHeight),
					ID_Profiler,
					OffOn,
					10,
					true);
				SimulationProfiler.Enabled = profilerMode == 1;
				pos.y -= rowHeight + spacing;

				int benchmarkButton = MenuHelper.DrawButtonPair(
					SimulationBenchmark.Active ? "CANCEL BENCH" : "START BENCH",
					"RESET STATS",
					pos,
					columnWidth,
					false,
					true,
					true);

				if (benchmarkButton == 0)
				{
					if (SimulationBenchmark.Active) SimulationBenchmark.Cancel();
					else SimulationBenchmark.Start(5000, 128);
				}
				else if (benchmarkButton == 1)
				{
					SimulationBenchmark.Reset();
					SimulationProfiler.Reset();
				}
				pos = UI.PrevBounds.BottomLeft + Vector2.down * spacing;

				SimulationBenchmarkResult benchmark = SimulationBenchmark.Latest;
				string benchmarkText = SimulationBenchmark.Active
					? "Benchmark: sampling raw engine time..."
					: benchmark.SampledSteps > 0
						? $"Raw: {benchmark.RawStepsPerSecond:0} steps/s | {benchmark.AverageMicrosecondsPerStep:0.###} us avg | {benchmark.MaxMicrosecondsPerStep:0.###} us max"
						: "Benchmark: not run";
				DrawInfoRow(ref pos, benchmarkText, benchmark.SampledSteps > 0 || SimulationBenchmark.Active ? textCol : dim);

				SimulationStepProfile last = SimulationProfiler.LastStep;
				string lastStepText = last.Frame > 0
					? $"Step: {last.StepMilliseconds:0.###} ms | gates {last.GateEvaluations:N0} | signals {last.SignalPropagations:N0} | delta {last.DeltaCycles}"
					: "Step: profiler has no sample";
				DrawInfoRow(ref pos, lastStepText, SimulationProfiler.Enabled ? textCol : dim);

				string accelerationText = last.Frame > 0
					? $"Accel: LUT {last.CacheHits:N0} | JIT {last.JitHits:N0} | FB JIT {last.FeedbackJitHits:N0} / {last.FeedbackJitSweeps:N0} sweeps"
					: "Accel: no profiler sample";
				DrawInfoRow(ref pos, accelerationText, SimulationProfiler.Enabled ? textCol : dim);

				string convergence = DeterministicSimulator.LastSettleConverged
					? "Convergence: OK"
					: "FAILED: " + DeterministicSimulator.LastNonConvergenceDetails;
				DrawInfoRow(ref pos, convergence, DeterministicSimulator.LastSettleConverged ? dim : Color.yellow);

				pos.y -= 0.5f;
				UI.DrawText("HOT CHIPS", theme.FontBold, theme.FontSizeRegular, pos, Anchor.TextCentreLeft, textCol);
				pos.y -= 2.1f;

				SimulationHotChip[] hot = SimulationProfiler.GetHotChips(4);
				if (!SimulationProfiler.Enabled)
				{
					DrawInfoRow(ref pos, "Profiler is OFF.", dim);
				}
				else if (hot.Length == 0)
				{
					DrawInfoRow(ref pos, "No samples yet.", dim);
				}
				else
				{
					for (int i = 0; i < hot.Length; i++)
					{
						SimulationHotChip chip = hot[i];
						DrawInfoRow(
							ref pos,
							$"{i + 1}. {chip.Path} | {chip.ExecutionPath} | {chip.AverageMicroseconds:0.###} us | {chip.Evaluations:N0} eval",
							textCol);
					}
				}

				pos.y -= 0.5f;
				UI.DrawText("DETERMINISTIC REPLAY", theme.FontBold, theme.FontSizeRegular, pos, Anchor.TextCentreLeft, textCol);
				pos.y -= 2.1f;

				Project project = Project.ActiveProject;
				bool replayPending = project != null && project.ReplayCommandPending;
				bool recording = project != null && project.ReplayRecordingActive;
				bool canReplay =
					project != null &&
					project.simPaused &&
					project.HasReplayRecording &&
					!recording &&
					!replayPending;

				int replayButton = MenuHelper.DrawButtonPair(
					recording ? "STOP RECORD" : "START RECORD",
					"REPLAY",
					pos,
					columnWidth,
					false,
					!replayPending,
					canReplay);

				if (project != null)
				{
					if (replayButton == 0)
					{
						if (recording) project.RequestStopReplayRecording();
						else project.RequestStartReplayRecording(5000);
					}
					else if (replayButton == 1)
					{
						project.RequestReplayLatest();
					}
				}

				pos = UI.PrevBounds.BottomLeft + Vector2.down * spacing;

				string replayStatus = project == null
					? "Replay: no active project"
					: recording
						? $"Replay: recording {project.ReplayRecordedFrames:N0} frames..."
						: $"Replay: {project.ReplayStatus}";
				DrawInfoRow(ref pos, replayStatus, project != null && project.LatestReplayResult.Success ? textCol : dim);

				if (project != null && !project.simPaused && project.HasReplayRecording)
				{
					DrawInfoRow(ref pos, "Pause simulation to enable REPLAY.", dim);
				}
			}

			void DrawWaveformColumn(ref Vector2 pos)
			{
				UI.DrawText("LOGIC ANALYZER", theme.FontBold, theme.FontSizeRegular, pos, Anchor.TextCentreLeft, textCol);
				pos.y -= 2.2f;

				int waveformMode = MenuHelper.LabeledOptionsWheel(
					"Waveform capture",
					textCol,
					pos,
					new Vector2(columnWidth, rowHeight),
					ID_Waveform,
					OffOn,
					10,
					true);
				SimulationWaveformRecorder.Enabled = waveformMode == 1;
				pos.y -= rowHeight + spacing;

				int waveformButton = MenuHelper.DrawButtonPair(
					"CLEAR SAMPLES",
					"REMOVE ALL",
					pos,
					columnWidth,
					false,
					true,
					true);

				if (waveformButton == 0) SimulationWaveformRecorder.ClearSamples();
				else if (waveformButton == 1) SimulationWaveformRecorder.ClearAll();

				pos = UI.PrevBounds.BottomLeft + Vector2.down * spacing;

				WaveformProbeInfo[] probes = SimulationWaveformRecorder.GetProbes();
				if (probes.Length == 0)
				{
					DrawInfoRow(ref pos, "No probes. Right-click a pin -> TOGGLE PROBE.", dim);
					return;
				}

				int count = Math.Min(4, probes.Length);
				for (int i = 0; i < count; i++)
				{
					DrawProbe(ref pos, probes[i]);
				}

				if (probes.Length > count)
				{
					DrawInfoRow(ref pos, $"+ {probes.Length - count} more probes (remove some to display them)", dim);
				}
			}

			void DrawProbe(ref Vector2 pos, WaveformProbeInfo probe)
			{
				Vector2 traceTopLeft = pos;
				UI.DrawPanel(traceTopLeft, new Vector2(columnWidth, traceHeight), rowCol, Anchor.TopLeft);
				Bounds2D traceBounds = UI.PrevBounds;

				WaveformSample[] samples = SimulationWaveformRecorder.GetSamples(probe.Id);
				string latest = samples.Length == 0
					? "--"
					: FormatState(samples[^1].State, probe.BitCount);

				UI.DrawText(
					$"{probe.Name}  [{probe.BitCount}b]  now={latest}  transitions={probe.SampleCount}",
					theme.FontRegular,
					theme.FontSizeRegular * 0.88f,
					traceBounds.TopLeft + new Vector2(0.8f, -0.8f),
					Anchor.TextCentreLeft,
					textCol);

				bool remove = UI.Button(
					"X",
					theme.ButtonTheme,
					traceBounds.TopRight + new Vector2(-0.4f, -0.35f),
					new Vector2(2.1f, 1.8f),
					true,
					false,
					true,
					Anchor.TopRight);

				if (remove)
				{
					SimulationWaveformRecorder.RemoveProbe(probe.Id);
					pos = traceBounds.BottomLeft + Vector2.down * spacing;
					return;
				}

				if (samples.Length == 0)
				{
					UI.DrawText(
						"waiting for samples...",
						theme.FontRegular,
						theme.FontSizeRegular * 0.85f,
						traceBounds.Centre + Vector2.down * 0.7f,
						Anchor.TextCentre,
						dim);
				}
				else if (probe.BitCount == 1)
				{
					DrawSingleBitTrace(traceBounds, samples);
				}
				else
				{
					DrawBusHistory(traceBounds, samples, probe.BitCount);
				}

				pos = traceBounds.BottomLeft + Vector2.down * spacing;
			}

			void DrawSingleBitTrace(Bounds2D bounds, WaveformSample[] samples)
			{
				const int maxSamples = 64;
				int start = Math.Max(0, samples.Length - maxSamples);
				int count = samples.Length - start;
				if (count <= 0) return;

				float left = bounds.Left + 0.8f;
				float right = bounds.Right - 0.8f;
				float highY = bounds.Bottom + 3.3f;
				float lowY = bounds.Bottom + 0.9f;
				float zY = bounds.Bottom + 2.1f;

				int startFrame = samples[start].Frame;
				int endFrame = Math.Max(samples[^1].Frame, DLS.Simulation.Simulator.simulationFrame);
				int frameSpan = Math.Max(1, endFrame - startFrame);

				float XForFrame(int frame) =>
					left + (right - left) * Mathf.Clamp01((frame - startFrame) / (float)frameSpan);

				Vector2 previous = new(XForFrame(samples[start].Frame), StateY(samples[start].State));
				for (int i = 1; i < count; i++)
				{
					WaveformSample sample = samples[start + i];
					Vector2 next = new(XForFrame(sample.Frame), StateY(sample.State));
					UI.DrawLine(previous, new Vector2(next.x, previous.y), 0.08f, Color.white * 0.82f);
					if (Mathf.Abs(previous.y - next.y) > 0.001f)
					{
						UI.DrawLine(new Vector2(next.x, previous.y), next, 0.08f, Color.white * 0.82f);
					}
					previous = next;
				}

				UI.DrawLine(previous, new Vector2(right, previous.y), 0.08f, Color.white * 0.82f);

				UI.DrawText("1", theme.FontRegular, theme.FontSizeRegular * 0.72f, new Vector2(left, highY), Anchor.TextCentreRight, dim);
				UI.DrawText("Z", theme.FontRegular, theme.FontSizeRegular * 0.72f, new Vector2(left, zY), Anchor.TextCentreRight, dim);
				UI.DrawText("0", theme.FontRegular, theme.FontSizeRegular * 0.72f, new Vector2(left, lowY), Anchor.TextCentreRight, dim);

				float StateY(uint state)
				{
					ushort value = PinState.GetBitTristatedValue(state, 0);
					return value switch
					{
						PinState.LogicHigh => highY,
						PinState.LogicDisconnected => zY,
						_ => lowY
					};
				}
			}

			void DrawBusHistory(Bounds2D bounds, WaveformSample[] samples, int bitCount)
			{
				const int maxValues = 9;
				int start = Math.Max(0, samples.Length - maxValues);
				StringBuilder history = new();

				for (int i = start; i < samples.Length; i++)
				{
					if (history.Length > 0) history.Append("  ->  ");
					history.Append(FormatState(samples[i].State, bitCount));
				}

				UI.DrawText(
					history.ToString(),
					theme.FontRegular,
					theme.FontSizeRegular * 0.82f,
					bounds.CentreLeft + new Vector2(0.8f, -1.0f),
					Anchor.TextCentreLeft,
					textCol);
			}

			void DrawInfoRow(ref Vector2 pos, string text, Color col)
			{
				MenuHelper.DrawLeftAlignTextWithBackground(
					text,
					pos,
					new Vector2(columnWidth, rowHeight),
					Anchor.TopLeft,
					col,
					rowCol,
					false,
					0.8f);
				pos = UI.PrevBounds.BottomLeft + Vector2.down * spacing;
			}
		}

		static string FormatState(uint state, int bitCount)
		{
			int width = Math.Max(1, Math.Min(16, bitCount));
			ushort bits = PinState.GetBitStates(state);
			ushort tri = PinState.GetTristateFlags(state);
			uint mask = width >= 16 ? 0xFFFFu : (1u << width) - 1u;

			if (((uint)tri & mask) == mask) return "Z";

			if (((uint)tri & mask) == 0)
			{
				uint value = (uint)bits & mask;
				if (width <= 1) return value == 0 ? "0" : "1";
				return width <= 4 ? $"0x{value:X1}" : $"0x{value:X2}";
			}

			StringBuilder text = new(width);
			for (int bit = width - 1; bit >= 0; bit--)
			{
				uint bitMask = 1u << bit;
				if (((uint)tri & bitMask) != 0) text.Append('Z');
				else text.Append(((uint)bits & bitMask) != 0 ? '1' : '0');
			}
			return text.ToString();
		}
	}
}
