using System;
using System.Text;
using DLS.Game;
using DLS.Simulation;
using Seb.Helpers;
using Seb.Types;
using Seb.Vis;
using Seb.Vis.UI;
using UnityEngine;

namespace DLS.Graphics
{
	public static class SimulationDiagnosticsMenu
	{
		const float menuWidth = 86f;
		const float columnGap = 1.2f;
		const float leftColumnWidth = 36f;
		const float rightColumnWidth = menuWidth - leftColumnWidth - columnGap;
		const float headerHeight = 3.4f;
		const float sectionHeaderHeight = 2.15f;
		const float sectionGap = 0.55f;
		const float innerPad = 0.8f;
		const float traceHeight = 6.45f;

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

			Vector2 topLeft = UI.Centre + new Vector2(-menuWidth / 2f, 27f);
			Color bodyCol = ColHelper.Darken(theme.MenuPanelCol, 0.06f);
			Color raisedCol = ColHelper.Darken(theme.MenuPanelCol, 0.025f);
			Color dim = new(1, 1, 1, 0.58f);
			Color secondary = new(1, 1, 1, 0.74f);
			Color accent = theme.MainMenuButtonTheme.buttonCols.hover;

			using (UI.BeginBoundsScope(true))
			{
				DrawTopHeader(theme, topLeft, accent, dim);

				Vector2 contentTop = topLeft + Vector2.down * (headerHeight + 0.75f);
				Vector2 leftPos = contentTop;
				Vector2 rightPos = contentTop + Vector2.right * (leftColumnWidth + columnGap);

				DrawEngineStatus(theme, ref leftPos, bodyCol, secondary, dim, accent);
				DrawPerformance(theme, ref leftPos, bodyCol, secondary, dim, accent);
				DrawHotChips(theme, ref leftPos, bodyCol, secondary, dim, accent);
				DrawReplay(theme, ref leftPos, bodyCol, secondary, dim, accent);

				DrawLogicAnalyzer(theme, ref rightPos, bodyCol, raisedCol, secondary, dim, accent);

				float bottom = Mathf.Min(leftPos.y, rightPos.y) - 0.5f;
				bool close = UI.Button(
					"CLOSE",
					theme.ButtonTheme,
					new Vector2(topLeft.x, bottom),
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
		}

		static void DrawTopHeader(
			DrawSettings.UIThemeDLS theme,
			Vector2 topLeft,
			Color accent,
			Color dim)
		{
			Color headerCol = ColHelper.Darken(theme.MenuPanelCol, 0.035f);
			UI.DrawPanel(topLeft, new Vector2(menuWidth, headerHeight), headerCol, Anchor.TopLeft);
			Bounds2D bounds = UI.PrevBounds;

			UI.DrawText(
				"REWIRED  /  SIMULATION DIAGNOSTICS",
				theme.FontBold,
				theme.FontSizeRegular * 1.02f,
				bounds.CentreLeft + Vector2.right * 1f,
				Anchor.TextCentreLeft,
				Color.white);

			Project project = Project.ActiveProject;
			string state = project == null
				? "NO PROJECT"
				: project.simPaused
					? "PAUSED"
					: "RUNNING";

			string rightText = $"FRAME {RewiredEngine.SimulationFrame}   |   {state}";
			UI.DrawText(
				rightText,
				theme.FontBold,
				theme.FontSizeRegular * 0.72f,
				bounds.CentreRight + Vector2.left * 1f,
				Anchor.TextCentreRight,
				project == null ? dim : Color.white);

			UI.DrawLine(bounds.BottomLeft, bounds.BottomRight, 0.08f, accent);
			UI.OverridePreviousBounds(bounds);
		}

		static void DrawEngineStatus(
			DrawSettings.UIThemeDLS theme,
			ref Vector2 pos,
			Color bodyCol,
			Color secondary,
			Color dim,
			Color accent)
		{
			DrawSectionHeader(theme, ref pos, "ENGINE STATUS", leftColumnWidth, accent);

			const float bodyHeight = 8.4f;
			UI.DrawPanel(pos, new Vector2(leftColumnWidth, bodyHeight), bodyCol, Anchor.TopLeft);
			Bounds2D bounds = UI.PrevBounds;
			RewiredEngine.DiagnosticsSnapshot diag = RewiredEngine.Diagnostics;

			Vector2 line = bounds.TopLeft + new Vector2(innerPad, -1.05f);
			float lineStep = 1.55f;

			DrawLabelValue(
				theme,
				line,
				"CONVERGENCE",
				diag.SettleConverged ? "OK" : "FAILED",
				diag.SettleConverged ? secondary : Color.yellow,
				leftColumnWidth - innerPad * 2f);
			line.y -= lineStep;

			DrawLabelValue(
				theme,
				line,
				"FRAME / DELTA",
				$"{diag.SimulationFrame}  /  {diag.DeltaCycles}",
				secondary,
				leftColumnWidth - innerPad * 2f);
			line.y -= lineStep;

			DrawLabelValue(
				theme,
				line,
				"WORK",
				$"gates {diag.GateEvaluations}   signals {diag.SignalPropagations}   targets {diag.TargetResolutions}",
				secondary,
				leftColumnWidth - innerPad * 2f);
			line.y -= lineStep;

			DrawLabelValue(
				theme,
				line,
				"ACCELERATION",
				$"LUT {diag.CacheHits}   JIT {diag.JitHits}   FB {diag.FeedbackJitHits}",
				secondary,
				leftColumnWidth - innerPad * 2f);

			if (!diag.SettleConverged && !string.IsNullOrWhiteSpace(diag.NonConvergenceDetails))
			{
				UI.DrawText(
					diag.NonConvergenceDetails,
					theme.FontRegular,
					theme.FontSizeRegular * 0.56f,
					bounds.BottomLeft + new Vector2(innerPad, 0.55f),
					Anchor.TextCentreLeft,
					Color.yellow);
			}

			pos = bounds.BottomLeft + Vector2.down * sectionGap;
		}

		static void DrawPerformance(
			DrawSettings.UIThemeDLS theme,
			ref Vector2 pos,
			Color bodyCol,
			Color secondary,
			Color dim,
			Color accent)
		{
			DrawSectionHeader(theme, ref pos, "PERFORMANCE", leftColumnWidth, accent);

			const float bodyHeight = 9.35f;
			UI.DrawPanel(pos, new Vector2(leftColumnWidth, bodyHeight), bodyCol, Anchor.TopLeft);
			Bounds2D body = UI.PrevBounds;

			Vector2 controlPos = body.TopLeft + new Vector2(innerPad, -0.65f);
			float innerWidth = leftColumnWidth - innerPad * 2f;

			int profilerMode = DrawCompactToggle(
				theme,
				controlPos,
				innerWidth,
				"Hot-chip profiler",
				ID_Profiler);
			SimulationProfiler.Enabled = profilerMode == 1;

			Vector2 buttonsPos = controlPos + Vector2.down * 2.65f;
			int benchmarkButton = MenuHelper.DrawButtonPair(
				SimulationBenchmark.Active ? "CANCEL BENCH" : "START BENCH",
				"RESET STATS",
				buttonsPos,
				innerWidth,
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

			SimulationBenchmarkResult benchmark = SimulationBenchmark.Latest;
			string benchmarkText = SimulationBenchmark.Active
				? "Sampling raw engine execution..."
				: benchmark.SampledSteps > 0
					? $"{benchmark.RawStepsPerSecond:0} steps/s   |   {benchmark.AverageMicrosecondsPerStep:0.###} us avg   |   {benchmark.MaxMicrosecondsPerStep:0.###} us max"
					: "Benchmark has not been run.";

			UI.DrawText(
				benchmarkText,
				theme.FontRegular,
				theme.FontSizeRegular * 0.64f,
				body.BottomLeft + new Vector2(innerPad, 2.25f),
				Anchor.TextCentreLeft,
				benchmark.SampledSteps > 0 || SimulationBenchmark.Active ? secondary : dim);

			SimulationStepProfile last = SimulationProfiler.LastStep;
			string profileText = last.Frame > 0
				? $"Last profile: {last.StepMilliseconds:0.###} ms   |   gates {last.GateEvaluations}   |   delta {last.DeltaCycles}"
				: "Profiler has no sample yet.";

			UI.DrawText(
				profileText,
				theme.FontRegular,
				theme.FontSizeRegular * 0.64f,
				body.BottomLeft + new Vector2(innerPad, 0.8f),
				Anchor.TextCentreLeft,
				SimulationProfiler.Enabled ? secondary : dim);

			pos = body.BottomLeft + Vector2.down * sectionGap;
		}

		static void DrawHotChips(
			DrawSettings.UIThemeDLS theme,
			ref Vector2 pos,
			Color bodyCol,
			Color secondary,
			Color dim,
			Color accent)
		{
			DrawSectionHeader(theme, ref pos, "HOT CHIPS", leftColumnWidth, accent);

			const float bodyHeight = 8.9f;
			UI.DrawPanel(pos, new Vector2(leftColumnWidth, bodyHeight), bodyCol, Anchor.TopLeft);
			Bounds2D body = UI.PrevBounds;

			SimulationHotChip[] hot = SimulationProfiler.GetHotChips(4);
			if (!SimulationProfiler.Enabled)
			{
				DrawEmptyState(theme, body, "Profiler is OFF.", dim);
			}
			else if (hot.Length == 0)
			{
				DrawEmptyState(theme, body, "Waiting for profiler samples...", dim);
			}
			else
			{
				Vector2 line = body.TopLeft + new Vector2(innerPad, -1.05f);
				for (int i = 0; i < hot.Length; i++)
				{
					SimulationHotChip chip = hot[i];
					string left = $"{i + 1}.  {chip.Path}";
					string right = $"{chip.ExecutionPath}   {chip.AverageMicroseconds:0.###} us   {chip.Evaluations} eval";

					UI.DrawText(
						left,
						theme.FontBold,
						theme.FontSizeRegular * 0.62f,
						line,
						Anchor.TextCentreLeft,
						Color.white);

					UI.DrawText(
						right,
						theme.FontRegular,
						theme.FontSizeRegular * 0.58f,
						new Vector2(body.Right - innerPad, line.y),
						Anchor.TextCentreRight,
						secondary);

					line.y -= 1.75f;
				}
			}

			pos = body.BottomLeft + Vector2.down * sectionGap;
		}

		static void DrawReplay(
			DrawSettings.UIThemeDLS theme,
			ref Vector2 pos,
			Color bodyCol,
			Color secondary,
			Color dim,
			Color accent)
		{
			DrawSectionHeader(theme, ref pos, "DETERMINISTIC REPLAY", leftColumnWidth, accent);

			const float bodyHeight = 6.65f;
			UI.DrawPanel(pos, new Vector2(leftColumnWidth, bodyHeight), bodyCol, Anchor.TopLeft);
			Bounds2D body = UI.PrevBounds;

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
				body.TopLeft + new Vector2(innerPad, -0.65f),
				leftColumnWidth - innerPad * 2f,
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

			string status = project == null
				? "No active project."
				: recording
					? $"Recording {project.ReplayRecordedFrames} frames..."
					: project.ReplayStatus;

			UI.DrawText(
				status,
				theme.FontRegular,
				theme.FontSizeRegular * 0.64f,
				body.BottomLeft + new Vector2(innerPad, 1.85f),
				Anchor.TextCentreLeft,
				project != null && project.LatestReplayResult.Success ? Color.white : secondary);

			string hint = project != null && !project.simPaused && project.HasReplayRecording
				? "Pause simulation to enable REPLAY."
				: "Replay restores the captured deterministic runtime state.";

			UI.DrawText(
				hint,
				theme.FontRegular,
				theme.FontSizeRegular * 0.58f,
				body.BottomLeft + new Vector2(innerPad, 0.65f),
				Anchor.TextCentreLeft,
				dim);

			pos = body.BottomLeft + Vector2.down * sectionGap;
		}

		static void DrawLogicAnalyzer(
			DrawSettings.UIThemeDLS theme,
			ref Vector2 pos,
			Color bodyCol,
			Color raisedCol,
			Color secondary,
			Color dim,
			Color accent)
		{
			DrawSectionHeader(theme, ref pos, "LOGIC ANALYZER", rightColumnWidth, accent);

			const float controlsHeight = 5.8f;
			UI.DrawPanel(pos, new Vector2(rightColumnWidth, controlsHeight), bodyCol, Anchor.TopLeft);
			Bounds2D controls = UI.PrevBounds;

			int waveformMode = DrawCompactToggle(
				theme,
				controls.TopLeft + new Vector2(innerPad, -0.65f),
				rightColumnWidth - innerPad * 2f,
				"Waveform capture",
				ID_Waveform);
			SimulationWaveformRecorder.Enabled = waveformMode == 1;

			int waveformButton = MenuHelper.DrawButtonPair(
				"CLEAR SAMPLES",
				"REMOVE ALL",
				controls.TopLeft + new Vector2(innerPad, -3.15f),
				rightColumnWidth - innerPad * 2f,
				false,
				true,
				true);

			if (waveformButton == 0) SimulationWaveformRecorder.ClearSamples();
			else if (waveformButton == 1) SimulationWaveformRecorder.ClearAll();

			pos = controls.BottomLeft + Vector2.down * sectionGap;

			WaveformProbeInfo[] probes = SimulationWaveformRecorder.GetProbes();
			if (probes.Length == 0)
			{
				const float emptyHeight = 12f;
				UI.DrawPanel(pos, new Vector2(rightColumnWidth, emptyHeight), raisedCol, Anchor.TopLeft);
				Bounds2D empty = UI.PrevBounds;

				UI.DrawText(
					"NO PROBES",
					theme.FontBold,
					theme.FontSizeRegular * 0.76f,
					empty.Centre + Vector2.up * 1.1f,
					Anchor.TextCentre,
					secondary);

				UI.DrawText(
					"Right-click any pin and choose TOGGLE PROBE.",
					theme.FontRegular,
					theme.FontSizeRegular * 0.64f,
					empty.Centre + Vector2.down * 1.05f,
					Anchor.TextCentre,
					dim);

				pos = empty.BottomLeft + Vector2.down * sectionGap;
				return;
			}

			int count = Math.Min(4, probes.Length);
			for (int i = 0; i < count; i++)
			{
				DrawProbe(theme, ref pos, probes[i], raisedCol, secondary, dim, accent);
			}

			if (probes.Length > count)
			{
				const float footerHeight = 2.25f;
				UI.DrawPanel(pos, new Vector2(rightColumnWidth, footerHeight), bodyCol, Anchor.TopLeft);
				Bounds2D footer = UI.PrevBounds;
				UI.DrawText(
					$"+ {probes.Length - count} MORE PROBES",
					theme.FontBold,
					theme.FontSizeRegular * 0.58f,
					footer.Centre,
					Anchor.TextCentre,
					dim);
				pos = footer.BottomLeft + Vector2.down * sectionGap;
			}
		}

		static void DrawProbe(
			DrawSettings.UIThemeDLS theme,
			ref Vector2 pos,
			WaveformProbeInfo probe,
			Color panelCol,
			Color secondary,
			Color dim,
			Color accent)
		{
			UI.DrawPanel(pos, new Vector2(rightColumnWidth, traceHeight), panelCol, Anchor.TopLeft);
			Bounds2D bounds = UI.PrevBounds;

			WaveformSample[] samples = SimulationWaveformRecorder.GetSamples(probe.Id);
			string latest = samples.Length == 0 ? "--" : FormatState(samples[^1].State, probe.BitCount);

			UI.DrawText(
				probe.Name,
				theme.FontBold,
				theme.FontSizeRegular * 0.66f,
				bounds.TopLeft + new Vector2(innerPad, -0.8f),
				Anchor.TextCentreLeft,
				Color.white);

			UI.DrawText(
				$"{probe.BitCount}b   NOW {latest}   TRANSITIONS {probe.SampleCount}",
				theme.FontRegular,
				theme.FontSizeRegular * 0.55f,
				bounds.TopRight + new Vector2(-3.25f, -0.8f),
				Anchor.TextCentreRight,
				secondary);

			bool remove = UI.Button(
				"X",
				theme.ButtonTheme,
				bounds.TopRight + new Vector2(-0.45f, -0.35f),
				new Vector2(2.1f, 1.7f),
				true,
				false,
				true,
				Anchor.TopRight);

			if (remove)
			{
				SimulationWaveformRecorder.RemoveProbe(probe.Id);
				pos = bounds.BottomLeft + Vector2.down * sectionGap;
				return;
			}

			UI.DrawLine(
				new Vector2(bounds.Left + innerPad, bounds.Top - 1.8f),
				new Vector2(bounds.Right - innerPad, bounds.Top - 1.8f),
				0.045f,
				accent * 0.55f);

			if (samples.Length == 0)
			{
				UI.DrawText(
					"waiting for samples...",
					theme.FontRegular,
					theme.FontSizeRegular * 0.62f,
					bounds.Centre + Vector2.down * 0.65f,
					Anchor.TextCentre,
					dim);
			}
			else if (probe.BitCount == 1)
			{
				DrawSingleBitTrace(theme, bounds, samples, dim);
			}
			else
			{
				DrawBusHistory(theme, bounds, samples, probe.BitCount, secondary);
			}

			pos = bounds.BottomLeft + Vector2.down * sectionGap;
		}

		static void DrawSingleBitTrace(
			DrawSettings.UIThemeDLS theme,
			Bounds2D bounds,
			WaveformSample[] samples,
			Color dim)
		{
			const int maxSamples = 64;
			int start = Math.Max(0, samples.Length - maxSamples);
			if (samples.Length - start <= 0) return;

			float left = bounds.Left + 1.65f;
			float right = bounds.Right - 0.8f;
			float highY = bounds.Bottom + 3.15f;
			float lowY = bounds.Bottom + 0.8f;
			float zY = bounds.Bottom + 1.98f;

			int startFrame = samples[start].Frame;
			int endFrame = Math.Max(samples[^1].Frame, RewiredEngine.SimulationFrame);
			int frameSpan = Math.Max(1, endFrame - startFrame);

			float XForFrame(int frame) =>
				left + (right - left) * Mathf.Clamp01((frame - startFrame) / (float)frameSpan);

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

			Vector2 previous = new(XForFrame(samples[start].Frame), StateY(samples[start].State));
			for (int i = start + 1; i < samples.Length; i++)
			{
				WaveformSample sample = samples[i];
				Vector2 next = new(XForFrame(sample.Frame), StateY(sample.State));
				UI.DrawLine(previous, new Vector2(next.x, previous.y), 0.08f, Color.white * 0.82f);
				if (Mathf.Abs(previous.y - next.y) > 0.001f)
				{
					UI.DrawLine(new Vector2(next.x, previous.y), next, 0.08f, Color.white * 0.82f);
				}
				previous = next;
			}
			UI.DrawLine(previous, new Vector2(right, previous.y), 0.08f, Color.white * 0.82f);

			UI.DrawText("1", theme.FontRegular, theme.FontSizeRegular * 0.58f, new Vector2(left - 0.3f, highY), Anchor.TextCentreRight, dim);
			UI.DrawText("Z", theme.FontRegular, theme.FontSizeRegular * 0.58f, new Vector2(left - 0.3f, zY), Anchor.TextCentreRight, dim);
			UI.DrawText("0", theme.FontRegular, theme.FontSizeRegular * 0.58f, new Vector2(left - 0.3f, lowY), Anchor.TextCentreRight, dim);
		}

		static void DrawBusHistory(
			DrawSettings.UIThemeDLS theme,
			Bounds2D bounds,
			WaveformSample[] samples,
			int bitCount,
			Color secondary)
		{
			const int maxValues = 8;
			int start = Math.Max(0, samples.Length - maxValues);
			StringBuilder history = new();

			for (int i = start; i < samples.Length; i++)
			{
				if (history.Length > 0) history.Append("   ->   ");
				history.Append(FormatState(samples[i].State, bitCount));
			}

			UI.DrawText(
				history.ToString(),
				theme.FontRegular,
				theme.FontSizeRegular * 0.68f,
				bounds.CentreLeft + new Vector2(innerPad, -0.75f),
				Anchor.TextCentreLeft,
				secondary);
		}

		static void DrawSectionHeader(
			DrawSettings.UIThemeDLS theme,
			ref Vector2 pos,
			string title,
			float width,
			Color accent)
		{
			Color headerCol = ColHelper.Darken(theme.MenuPanelCol, 0.035f);
			UI.DrawPanel(pos, new Vector2(width, sectionHeaderHeight), headerCol, Anchor.TopLeft);
			Bounds2D bounds = UI.PrevBounds;

			UI.DrawText(
				title,
				theme.FontBold,
				theme.FontSizeRegular * 0.72f,
				bounds.CentreLeft + Vector2.right * innerPad,
				Anchor.TextCentreLeft,
				Color.white);

			UI.DrawLine(bounds.BottomLeft, bounds.BottomRight, 0.055f, accent);
			UI.OverridePreviousBounds(bounds);
			pos = bounds.BottomLeft;
		}

		static int DrawCompactToggle(
			DrawSettings.UIThemeDLS theme,
			Vector2 topLeft,
			float width,
			string label,
			UIHandle id)
		{
			const float height = 2.15f;
			Color bg = ColHelper.Darken(theme.MenuPanelCol, 0.09f);
			UI.DrawPanel(topLeft, new Vector2(width, height), bg, Anchor.TopLeft);
			Bounds2D bounds = UI.PrevBounds;

			UI.DrawText(
				label,
				theme.FontRegular,
				theme.FontSizeRegular * 0.72f,
				bounds.CentreLeft + Vector2.right * 0.65f,
				Anchor.TextCentreLeft,
				Color.white);

			WheelSelectorTheme selectorTheme = theme.OptionsWheel;
			selectorTheme.buttonTheme = theme.MainMenuButtonTheme;
			selectorTheme.buttonTheme.font = theme.FontBold;
			selectorTheme.buttonTheme.fontSize = theme.FontSizeRegular * 0.72f;
			selectorTheme.backgroundCol = bg;
			selectorTheme.textCol = Color.white;
			selectorTheme.inactiveTextCol = ColHelper.MakeCol255(125);

			int mode = UI.WheelSelector(
				id,
				OffOn,
				bounds.CentreRight,
				new Vector2(9.5f, height),
				selectorTheme,
				Anchor.CentreRight);

			UI.OverridePreviousBounds(bounds);
			return mode;
		}

		static void DrawLabelValue(
			DrawSettings.UIThemeDLS theme,
			Vector2 pos,
			string label,
			string value,
			Color valueCol,
			float width)
		{
			UI.DrawText(
				label,
				theme.FontBold,
				theme.FontSizeRegular * 0.56f,
				pos,
				Anchor.TextCentreLeft,
				new Color(1, 1, 1, 0.48f));

			UI.DrawText(
				value,
				theme.FontRegular,
				theme.FontSizeRegular * 0.62f,
				pos + Vector2.right * width,
				Anchor.TextCentreRight,
				valueCol);
		}

		static void DrawEmptyState(
			DrawSettings.UIThemeDLS theme,
			Bounds2D bounds,
			string text,
			Color col)
		{
			UI.DrawText(
				text,
				theme.FontRegular,
				theme.FontSizeRegular * 0.64f,
				bounds.Centre,
				Anchor.TextCentre,
				col);
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
