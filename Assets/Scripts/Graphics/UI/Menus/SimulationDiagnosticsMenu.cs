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
		const float infoFontScale = 0.82f;
		const float statsRefreshInterval = 0.75f;

		enum TouchPage
		{
			Status,
			Profiler,
			Analyzer,
			Replay
		}

		static readonly string[] TouchPageNames = { "STATUS", "PROFILER", "ANALYZER", "REPLAY" };
		static TouchPage touchPage;

		static readonly string[] OffOn = { "OFF", "ON" };
		static readonly UIHandle ID_Profiler = new("SIM_DIAG_Profiler");
		static readonly UIHandle ID_Waveform = new("SIM_DIAG_Waveform");
		static string projectStats = "Graph: unavailable";
		static string acceleratorStats = "Accelerators: unavailable";
		static float nextStatsRefreshTime;

		public static void OnMenuOpened()
		{
			UI.GetWheelSelectorState(ID_Profiler).index = SimulationProfiler.Enabled ? 1 : 0;
			UI.GetWheelSelectorState(ID_Waveform).index = SimulationWaveformRecorder.Enabled ? 1 : 0;
			RefreshProjectStats();
			nextStatsRefreshTime = Time.unscaledTime + statsRefreshInterval;
			if (TouchUILayout.Enabled) touchPage = TouchPage.Status;
		}

		public static void DrawMenu()
		{
			if (Time.unscaledTime >= nextStatsRefreshTime)
			{
				RefreshProjectStats();
				nextStatsRefreshTime = Time.unscaledTime + statsRefreshInterval;
			}

			if (TouchUILayout.Enabled)
			{
				DrawTouchMenu();
				return;
			}

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

				UI.DrawText(
					$"Rewired {Main.RewiredVersion}  |  live engine telemetry",
					theme.FontRegular,
					theme.FontSizeRegular * 0.78f,
					topLeft + Vector2.down * 1.45f,
					Anchor.TextCentreLeft,
					dim);

				Vector2 leftPos = topLeft + Vector2.down * 3.6f;
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
				DrawSectionHeader(ref pos, "PERFORMANCE");
				DrawInfoRow(ref pos, "Engine: REWIRED FAST | deterministic + feedback JIT", dim);
				DrawInfoRow(ref pos, projectStats, dim);
				DrawInfoRow(ref pos, acceleratorStats, dim);

				Project activeProject = Project.ActiveProject;
				if (activeProject != null)
				{
					DrawInfoRow(ref pos, $"Live: {activeProject.simAvgTicksPerSec:N0} steps/s | target {activeProject.targetTicksPerSecond:N0}", dim);
				}

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

				DrawSectionHeader(ref pos, "STABILITY", 0.25f);

				string convergence = DeterministicSimulator.LastSettleConverged
					? "Convergence: OK"
					: "Convergence: FAILED";
				DrawInfoRow(ref pos, convergence, DeterministicSimulator.LastSettleConverged ? dim : Color.yellow);

				if (DeterministicSimulator.LastFailureFrame >= 0)
				{
					DrawInfoRow(
						ref pos,
						$"Last failure: frame {DeterministicSimulator.LastFailureFrame:N0} | {DeterministicSimulator.LastFailureKind}",
						Color.yellow);

					string failureLocation = !string.IsNullOrWhiteSpace(DeterministicSimulator.LastFailureChipPath)
						? DeterministicSimulator.LastFailureChipPath
						: DeterministicSimulator.LastFailureSuspects;

					if (!string.IsNullOrWhiteSpace(failureLocation))
					{
						DrawInfoRow(ref pos, "At: " + CompactPath(failureLocation, 64), Color.yellow);
					}
				}

				DrawSectionHeader(ref pos, "HOT CHIPS", 0.25f);

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
							$"{i + 1}. {CompactPath(chip.Path, 38)} | {chip.ExecutionPath} | {chip.AverageMicroseconds:0.###} us",
							textCol);
					}
				}

			}

			void DrawWaveformColumn(ref Vector2 pos)
			{
				DrawSectionHeader(ref pos, "LOGIC ANALYZER");

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
				}
				else
				{
					int count = Math.Min(4, probes.Length);
					for (int i = 0; i < count; i++)
					{
						DrawProbe(ref pos, probes[i]);
					}

					if (probes.Length > count)
					{
						DrawInfoRow(ref pos, $"+ {probes.Length - count} more probes", dim);
					}
				}

				DrawReplayControls(ref pos);
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
					FitTextToWidth($"{CompactPath(probe.Name, 30)}  [{probe.BitCount}b]  now={latest}  transitions={probe.SampleCount}", columnWidth - 4.2f, theme.FontSizeRegular * 0.88f),
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
				int endFrame = Math.Max(samples[^1].Frame, Simulator.simulationFrame);
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
					FitTextToWidth(history.ToString(), columnWidth - 1.6f, theme.FontSizeRegular * 0.82f),
					theme.FontRegular,
					theme.FontSizeRegular * 0.82f,
					bounds.CentreLeft + new Vector2(0.8f, -1.0f),
					Anchor.TextCentreLeft,
					textCol);
			}

			void DrawReplayControls(ref Vector2 pos)
			{
				DrawSectionHeader(ref pos, "DETERMINISTIC REPLAY", 0.45f);

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
					DrawInfoRow(ref pos, "Pause simulation to enable replay.", dim);
				}
			}

			void DrawSectionHeader(ref Vector2 pos, string title, float topGap = 0f)
			{
				pos.y -= topGap;
				UI.DrawText(title, theme.FontBold, theme.FontSizeRegular * 0.92f, pos, Anchor.TextCentreLeft, textCol);
				Vector2 lineStart = pos + new Vector2(0, -1.1f);
				UI.DrawLine(lineStart, lineStart + Vector2.right * columnWidth, 0.04f, dim * 0.45f);
				pos.y -= 1.65f;
			}

			void DrawInfoRow(ref Vector2 pos, string text, Color col)
			{
				UI.DrawPanel(pos, new Vector2(columnWidth, rowHeight), rowCol, Anchor.TopLeft);
				Bounds2D bounds = UI.PrevBounds;
				string fitted = FitTextToWidth(text, columnWidth - 1.6f, theme.FontSizeRegular * infoFontScale);
				UI.DrawText(
					fitted,
					theme.FontRegular,
					theme.FontSizeRegular * infoFontScale,
					bounds.CentreLeft + Vector2.right * 0.8f,
					Anchor.TextCentreLeft,
					col);
				UI.OverridePreviousBounds(bounds);
				pos = bounds.BottomLeft + Vector2.down * spacing;
			}
		}

		static void DrawTouchMenu()
		{
			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			Color background = new(0.055f, 0.055f, 0.065f, 0.98f);
			Color card = new(0.11f, 0.11f, 0.13f, 1f);
			Color dim = Color.white * 0.72f;
			Color textCol = Color.white;
			float edge = 1.2f;
			float gap = TouchUILayout.TouchGap;

			UI.DrawFullscreenPanel(background);

			Vector2 titlePos = UI.TopLeft + new Vector2(edge, -1.25f);
			UI.DrawText(
				"SIMULATION",
				theme.FontBold,
				theme.FontSizeRegular * 1.15f,
				titlePos,
				Anchor.TextCentreLeft,
				textCol);
			UI.DrawText(
				$"Rewired {Main.RewiredVersion}",
				theme.FontRegular,
				theme.FontSizeRegular * 0.72f,
				titlePos + Vector2.down * 1.35f,
				Anchor.TextCentreLeft,
				dim);

			if (UI.Button(
				    "CLOSE",
				    theme.ButtonTheme,
				    UI.TopRight + new Vector2(-edge, -0.7f),
				    new Vector2(13, TouchUILayout.TouchButtonHeight),
				    true,
				    false,
				    true,
				    Anchor.TopRight))
			{
				UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);
				return;
			}

			float tabTop = UI.Height - 6.5f;
			float tabWidth = (UI.Width - edge * 2 - gap * (TouchPageNames.Length - 1)) / TouchPageNames.Length;
			Vector2 tabPos = new(edge, tabTop);

			for (int i = 0; i < TouchPageNames.Length; i++)
			{
				bool selected = (int)touchPage == i;
				ButtonTheme tabTheme = selected ? theme.MenuButtonTheme : theme.ButtonTheme;
				if (UI.Button(
					    TouchPageNames[i],
					    tabTheme,
					    tabPos,
					    new Vector2(tabWidth, TouchUILayout.TouchButtonHeight),
					    true,
					    false,
					    false,
					    Anchor.TopLeft))
				{
					touchPage = (TouchPage)i;
				}
				tabPos.x += tabWidth + gap;
			}

			float contentTop = tabTop - TouchUILayout.TouchButtonHeight - 1.1f;
			switch (touchPage)
			{
				case TouchPage.Status:
					DrawTouchStatus(contentTop, edge, card, dim, textCol);
					break;
				case TouchPage.Profiler:
					DrawTouchProfiler(contentTop, edge, card, dim, textCol);
					break;
				case TouchPage.Analyzer:
					DrawTouchAnalyzer(contentTop, edge, card, dim, textCol);
					break;
				case TouchPage.Replay:
					DrawTouchReplay(contentTop, edge, card, dim, textCol);
					break;
			}

			if (KeyboardShortcuts.CancelShortcutTriggered)
			{
				UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);
			}
		}

		static void DrawTouchStatus(float top, float edge, Color card, Color dim, Color textCol)
		{
			Project project = Project.ActiveProject;
			float y = top;
			string live = project == null
				? "No active project"
				: $"{project.simAvgTicksPerSec:N0} steps/s   target {project.targetTicksPerSecond:N0}";

			DrawTouchInfoCard(ref y, edge, card, "ENGINE", "REWIRED FAST | deterministic + feedback JIT", dim);
			DrawTouchInfoCard(ref y, edge, card, "LIVE", live, textCol);
			DrawTouchInfoCard(ref y, edge, card, "GRAPH", projectStats, dim);
			DrawTouchInfoCard(ref y, edge, card, "ACCELERATION", acceleratorStats, dim);

			string stability = DeterministicSimulator.LastSettleConverged ? "Convergence OK" : "CONVERGENCE FAILED";
			Color stabilityCol = DeterministicSimulator.LastSettleConverged ? dim : Color.yellow;
			DrawTouchInfoCard(ref y, edge, card, "STABILITY", stability, stabilityCol);

			if (DeterministicSimulator.LastFailureFrame >= 0)
			{
				string location = !string.IsNullOrWhiteSpace(DeterministicSimulator.LastFailureChipPath)
					? DeterministicSimulator.LastFailureChipPath
					: DeterministicSimulator.LastFailureSuspects;
				string failure = $"frame {DeterministicSimulator.LastFailureFrame:N0} | {DeterministicSimulator.LastFailureKind}";
				if (!string.IsNullOrWhiteSpace(location)) failure += " | " + CompactPath(location, 70);
				DrawTouchInfoCard(ref y, edge, card, "LAST FAILURE", failure, Color.yellow);
			}
		}

		static void DrawTouchProfiler(float top, float edge, Color card, Color dim, Color textCol)
		{
			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			float gap = TouchUILayout.TouchGap;
			float buttonWidth = (UI.Width - edge * 2 - gap * 2) / 3f;
			Vector2 pos = new(edge, top);

			string profilerLabel = SimulationProfiler.Enabled ? "PROFILER ON" : "PROFILER OFF";
			if (UI.Button(profilerLabel, theme.ButtonTheme, pos, new Vector2(buttonWidth, TouchUILayout.TouchButtonHeight), true, false, false, Anchor.TopLeft))
			{
				SimulationProfiler.Enabled = !SimulationProfiler.Enabled;
				UI.GetWheelSelectorState(ID_Profiler).index = SimulationProfiler.Enabled ? 1 : 0;
			}
			pos.x += buttonWidth + gap;

			if (UI.Button(
				    SimulationBenchmark.Active ? "CANCEL BENCH" : "START BENCH",
				    theme.ButtonTheme,
				    pos,
				    new Vector2(buttonWidth, TouchUILayout.TouchButtonHeight),
				    true,
				    false,
				    false,
				    Anchor.TopLeft))
			{
				if (SimulationBenchmark.Active) SimulationBenchmark.Cancel();
				else SimulationBenchmark.Start(5000, 128);
			}
			pos.x += buttonWidth + gap;

			if (UI.Button("RESET", theme.ButtonTheme, pos, new Vector2(buttonWidth, TouchUILayout.TouchButtonHeight), true, false, false, Anchor.TopLeft))
			{
				SimulationBenchmark.Reset();
				SimulationProfiler.Reset();
			}

			float y = top - TouchUILayout.TouchButtonHeight - 0.9f;
			SimulationBenchmarkResult benchmark = SimulationBenchmark.Latest;
			string bench = SimulationBenchmark.Active
				? "Sampling raw engine time..."
				: benchmark.SampledSteps > 0
					? $"{benchmark.RawStepsPerSecond:0} steps/s | {benchmark.AverageMicrosecondsPerStep:0.###} us avg | {benchmark.MaxMicrosecondsPerStep:0.###} us max"
					: "Not run";
			DrawTouchInfoCard(ref y, edge, card, "BENCHMARK", bench, benchmark.SampledSteps > 0 ? textCol : dim);

			SimulationStepProfile last = SimulationProfiler.LastStep;
			string lastText = last.Frame > 0
				? $"{last.StepMilliseconds:0.###} ms | gates {last.GateEvaluations:N0} | signals {last.SignalPropagations:N0} | delta {last.DeltaCycles}"
				: "No profiler sample";
			DrawTouchInfoCard(ref y, edge, card, "LAST STEP", lastText, SimulationProfiler.Enabled ? textCol : dim);

			SimulationHotChip[] hot = SimulationProfiler.GetHotChips(4);
			if (!SimulationProfiler.Enabled)
			{
				DrawTouchInfoCard(ref y, edge, card, "HOT CHIPS", "Enable profiler to collect samples", dim);
			}
			else if (hot.Length == 0)
			{
				DrawTouchInfoCard(ref y, edge, card, "HOT CHIPS", "Waiting for samples...", dim);
			}
			else
			{
				for (int i = 0; i < hot.Length; i++)
				{
					SimulationHotChip chip = hot[i];
					DrawTouchInfoCard(
						ref y,
						edge,
						card,
						$"HOT #{i + 1}",
						$"{CompactPath(chip.Path, 45)} | {chip.ExecutionPath} | {chip.AverageMicroseconds:0.###} us",
						textCol);
				}
			}
		}

		static void DrawTouchAnalyzer(float top, float edge, Color card, Color dim, Color textCol)
		{
			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			float gap = TouchUILayout.TouchGap;
			float buttonWidth = (UI.Width - edge * 2 - gap * 2) / 3f;
			Vector2 pos = new(edge, top);

			if (UI.Button(
				    SimulationWaveformRecorder.Enabled ? "CAPTURE ON" : "CAPTURE OFF",
				    theme.ButtonTheme,
				    pos,
				    new Vector2(buttonWidth, TouchUILayout.TouchButtonHeight),
				    true,
				    false,
				    false,
				    Anchor.TopLeft))
			{
				SimulationWaveformRecorder.Enabled = !SimulationWaveformRecorder.Enabled;
				UI.GetWheelSelectorState(ID_Waveform).index = SimulationWaveformRecorder.Enabled ? 1 : 0;
			}
			pos.x += buttonWidth + gap;

			if (UI.Button("CLEAR", theme.ButtonTheme, pos, new Vector2(buttonWidth, TouchUILayout.TouchButtonHeight), true, false, false, Anchor.TopLeft))
			{
				SimulationWaveformRecorder.ClearSamples();
			}
			pos.x += buttonWidth + gap;

			if (UI.Button("REMOVE ALL", theme.ButtonTheme, pos, new Vector2(buttonWidth, TouchUILayout.TouchButtonHeight), true, false, false, Anchor.TopLeft))
			{
				SimulationWaveformRecorder.ClearAll();
			}

			float y = top - TouchUILayout.TouchButtonHeight - 0.9f;
			WaveformProbeInfo[] probes = SimulationWaveformRecorder.GetProbes();
			if (probes.Length == 0)
			{
				DrawTouchInfoCard(ref y, edge, card, "PROBES", "Right-click / context action on a pin -> TOGGLE PROBE", dim);
				return;
			}

			int count = Math.Min(5, probes.Length);
			for (int i = 0; i < count; i++)
			{
				WaveformProbeInfo probe = probes[i];
				WaveformSample[] samples = SimulationWaveformRecorder.GetSamples(probe.Id);
				string latest = samples.Length == 0 ? "--" : FormatState(samples[^1].State, probe.BitCount);
				string value = $"{probe.BitCount}b | now {latest} | transitions {probe.SampleCount}";
				DrawTouchProbeRow(ref y, edge, card, CompactPath(probe.Name, 48), value, probe.Id, textCol);
			}

			if (probes.Length > count)
			{
				DrawTouchInfoCard(ref y, edge, card, "MORE", $"+ {probes.Length - count} probes", dim);
			}
		}

		static void DrawTouchReplay(float top, float edge, Color card, Color dim, Color textCol)
		{
			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			Project project = Project.ActiveProject;
			float gap = TouchUILayout.TouchGap;
			float buttonWidth = (UI.Width - edge * 2 - gap * 2) / 3f;
			Vector2 pos = new(edge, top);

			bool replayPending = project != null && project.ReplayCommandPending;
			bool recording = project != null && project.ReplayRecordingActive;
			bool canReplay = project != null && project.simPaused && project.HasReplayRecording && !recording && !replayPending;

			if (UI.Button(
				    recording ? "STOP RECORD" : "START RECORD",
				    theme.ButtonTheme,
				    pos,
				    new Vector2(buttonWidth, TouchUILayout.TouchButtonHeight),
				    project != null && !replayPending,
				    false,
				    false,
				    Anchor.TopLeft))
			{
				if (recording) project.RequestStopReplayRecording();
				else project.RequestStartReplayRecording(5000);
			}
			pos.x += buttonWidth + gap;

			if (UI.Button("REPLAY", theme.ButtonTheme, pos, new Vector2(buttonWidth, TouchUILayout.TouchButtonHeight), canReplay, false, false, Anchor.TopLeft))
			{
				project.RequestReplayLatest();
			}
			pos.x += buttonWidth + gap;

			if (UI.Button(
				    project != null && project.simPaused ? "RESUME" : "PAUSE",
				    theme.ButtonTheme,
				    pos,
				    new Vector2(buttonWidth, TouchUILayout.TouchButtonHeight),
				    project != null,
				    false,
				    false,
				    Anchor.TopLeft))
			{
				project.description.Prefs_SimPaused = !project.description.Prefs_SimPaused;
			}

			float y = top - TouchUILayout.TouchButtonHeight - 0.9f;
			string status = project == null
				? "No active project"
				: recording
					? $"Recording {project.ReplayRecordedFrames:N0} frames..."
					: project.ReplayStatus;
			DrawTouchInfoCard(ref y, edge, card, "STATUS", status, project != null && project.LatestReplayResult.Success ? textCol : dim);
			DrawTouchInfoCard(ref y, edge, card, "HOW TO USE", "Record a run, pause simulation, then press REPLAY to verify deterministic output.", dim);

			if (project != null && project.HasReplayRecording && !project.simPaused)
			{
				DrawTouchInfoCard(ref y, edge, card, "REPLAY LOCKED", "Pause simulation before replay.", Color.yellow);
			}
		}

		static void DrawTouchInfoCard(ref float y, float edge, Color card, string label, string value, Color valueCol)
		{
			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			const float height = 4.7f;
			float width = UI.Width - edge * 2;
			Vector2 topLeft = new(edge, y);
			UI.DrawPanel(topLeft, new Vector2(width, height), card, Anchor.TopLeft);
			Bounds2D bounds = UI.PrevBounds;

			UI.DrawText(
				label,
				theme.FontBold,
				theme.FontSizeRegular * 0.72f,
				bounds.CentreLeft + new Vector2(1.0f, 0.85f),
				Anchor.TextCentreLeft,
				Color.white * 0.62f);

			string fitted = FitTextToWidth(value, width - 2.0f, theme.FontSizeRegular * 0.88f);
			UI.DrawText(
				fitted,
				theme.FontRegular,
				theme.FontSizeRegular * 0.88f,
				bounds.CentreLeft + new Vector2(1.0f, -0.75f),
				Anchor.TextCentreLeft,
				valueCol);

			y = bounds.Bottom - TouchUILayout.TouchGap;
		}

		static void DrawTouchProbeRow(ref float y, float edge, Color card, string name, string value, int probeId, Color textCol)
		{
			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			const float height = 5.1f;
			const float removeWidth = 10f;
			float width = UI.Width - edge * 2;
			Vector2 topLeft = new(edge, y);
			UI.DrawPanel(topLeft, new Vector2(width, height), card, Anchor.TopLeft);
			Bounds2D bounds = UI.PrevBounds;

			UI.DrawText(
				FitTextToWidth(name, width - removeWidth - 2.2f, theme.FontSizeRegular * 0.82f),
				theme.FontBold,
				theme.FontSizeRegular * 0.82f,
				bounds.CentreLeft + new Vector2(1.0f, 0.75f),
				Anchor.TextCentreLeft,
				textCol);
			UI.DrawText(
				FitTextToWidth(value, width - removeWidth - 2.2f, theme.FontSizeRegular * 0.78f),
				theme.FontRegular,
				theme.FontSizeRegular * 0.78f,
				bounds.CentreLeft + new Vector2(1.0f, -0.85f),
				Anchor.TextCentreLeft,
				Color.white * 0.72f);

			if (UI.Button(
				    "REMOVE",
				    theme.ButtonTheme,
				    bounds.CentreRight + Vector2.left * 0.6f,
				    new Vector2(removeWidth - 1.0f, height - 0.8f),
				    true,
				    false,
				    false,
				    Anchor.CentreRight))
			{
				SimulationWaveformRecorder.RemoveProbe(probeId);
			}

			y = bounds.Bottom - TouchUILayout.TouchGap;
		}

		static string FitTextToWidth(string text, float maxWidth, float fontSize)
		{
			if (string.IsNullOrEmpty(text)) return string.Empty;
			if (Draw.CalculateTextBoundsSize(text, fontSize, DrawSettings.ActiveUITheme.FontRegular).x <= maxWidth) return text;

			const string ellipsis = "...";
			int low = 0;
			int high = text.Length;
			while (low < high)
			{
				int mid = (low + high + 1) / 2;
				string candidate = text.Substring(0, mid) + ellipsis;
				if (Draw.CalculateTextBoundsSize(candidate, fontSize, DrawSettings.ActiveUITheme.FontRegular).x <= maxWidth) low = mid;
				else high = mid - 1;
			}

			return text.Substring(0, low) + ellipsis;
		}

		static string CompactPath(string path, int maxChars)
		{
			if (string.IsNullOrWhiteSpace(path) || path.Length <= maxChars) return path ?? string.Empty;
			if (maxChars < 12) return path.Substring(0, Math.Min(path.Length, maxChars));

			int tailLength = (maxChars - 3) * 2 / 3;
			int headLength = maxChars - 3 - tailLength;
			return path.Substring(0, headLength) + "..." + path.Substring(path.Length - tailLength);
		}

		static void RefreshProjectStats()
		{
			Project project = Project.ActiveProject;
			SimChip root = project?.rootSimChip;
			if (project == null || root == null)
			{
				projectStats = "Graph: unavailable";
				acceleratorStats = "Accelerators: unavailable";
				return;
			}

			int total = 0;
			int custom = 0;
			int primitive = 0;
			int lut = 0;
			int jit = 0;
			int feedbackJit = 0;
			int feedbackActive = 0;

			Count(root);

			int visibleWires = project.ViewedChip?.Wires?.Count ?? 0;
			projectStats = $"Graph: {total:N0} chips | {primitive:N0} primitive | {custom:N0} custom | {visibleWires:N0} visible wires";
			acceleratorStats = $"Accel: LUT {lut:N0} | JIT {jit:N0} | FB {feedbackJit:N0} ({feedbackActive:N0} active)";

			void Count(SimChip chip)
			{
				if (chip == null) return;

				total++;
				if (chip.ChipType == DLS.Description.ChipType.Custom) custom++;
				else primitive++;

				if (chip.MemoCache != null && chip.MemoCache.Ready) lut++;
				if (chip.CompiledExecutor != null) jit++;
				if (chip.FeedbackExecutor != null)
				{
					feedbackJit++;
					if (chip.FeedbackExecutor.RuntimeActive) feedbackActive++;
				}

				for (int i = 0; i < chip.SubChips.Length; i++) Count(chip.SubChips[i]);
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
