using DLS.Simulation;
using Seb.Types;
using Seb.Vis;
using Seb.Vis.UI;
using UnityEngine;

namespace DLS.Graphics
{
	public static class SimulationDiagnosticsMenu
	{
		const float menuWidth = 74;
		const float rowHeight = 3.1f;
		const float spacing = 0.45f;
		static readonly string[] OffOn = { "OFF", "ON" };
		static readonly UIHandle ID_Profiler = new("SIM_DIAG_Profiler");

		public static void OnMenuOpened()
		{
			UI.GetWheelSelectorState(ID_Profiler).index = SimulationProfiler.Enabled ? 1 : 0;
		}

		public static void DrawMenu()
		{
			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			MenuHelper.DrawBackgroundOverlay();
			Draw.ID panelID = UI.ReservePanel();

			Vector2 topLeft = UI.Centre + new Vector2(-menuWidth / 2, 24);
			Vector2 pos = topLeft;
			Color rowCol = new(0.12f, 0.12f, 0.12f, 0.92f);
			Color textCol = Color.white;
			Color dim = Color.white * 0.72f;

			using (UI.BeginBoundsScope(true))
			{
				UI.DrawText("SIMULATION DIAGNOSTICS", theme.FontBold, theme.FontSizeRegular * 1.15f, pos, Anchor.TextCentreLeft, textCol);
				Next(1.4f);

				int profilerMode = MenuHelper.LabeledOptionsWheel(
					"Hot-chip profiler",
					textCol,
					pos,
					new Vector2(menuWidth, rowHeight),
					ID_Profiler,
					OffOn,
					12,
					true);
				SimulationProfiler.Enabled = profilerMode == 1;
				Next();

				int benchmarkButton = MenuHelper.DrawButtonPair(
					SimulationBenchmark.Active ? "CANCEL BENCH" : "START BENCH",
					"RESET STATS",
					pos,
					menuWidth,
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
					? "Benchmark: sampling raw engine compute time..."
					: benchmark.SampledSteps > 0
						? $"Benchmark: {benchmark.RawStepsPerSecond:0} steps/s raw | {benchmark.AverageMicrosecondsPerStep:0.###} us/step | max {benchmark.MaxMicrosecondsPerStep:0.###} us"
						: "Benchmark: not run";
				DrawRow(benchmarkText, benchmark.SampledSteps > 0 || SimulationBenchmark.Active ? textCol : dim);

				SimulationStepProfile last = SimulationProfiler.LastStep;
				string lastStepText = last.Frame > 0
					? $"Last step: {last.StepMilliseconds:0.###} ms | gates {last.GateEvaluations:N0} | signals {last.SignalPropagations:N0} | delta {last.DeltaCycles}"
					: "Last step: profiler has no sample yet";
				DrawRow(lastStepText, SimulationProfiler.Enabled ? textCol : dim);

				string accelerationText = last.Frame > 0
					? $"Acceleration: LUT {last.CacheHits:N0} | JIT {last.JitHits:N0} | feedback JIT {last.FeedbackJitHits:N0} ({last.FeedbackJitSweeps:N0} sweeps)"
					: "Acceleration: no profiler sample";
				DrawRow(accelerationText, SimulationProfiler.Enabled ? textCol : dim);

				string convergence = DeterministicSimulator.LastSettleConverged
					? "Convergence: OK"
					: "Convergence: FAILED - " + DeterministicSimulator.LastNonConvergenceDetails;
				DrawRow(convergence, DeterministicSimulator.LastSettleConverged ? dim : Color.yellow);

				Next(0.5f);
				UI.DrawText("HOT CHIPS", theme.FontBold, theme.FontSizeRegular, pos, Anchor.TextCentreLeft, textCol);
				Next(1.1f);

				SimulationHotChip[] hot = SimulationProfiler.GetHotChips(6);
				if (!SimulationProfiler.Enabled)
				{
					DrawRow("Profiler is OFF. Enable it above to collect hot-chip timings.", dim);
				}
				else if (hot.Length == 0)
				{
					DrawRow("No hot-chip samples yet.", dim);
				}
				else
				{
					for (int i = 0; i < hot.Length; i++)
					{
						SimulationHotChip chip = hot[i];
						DrawRow(
							$"{i + 1}. {chip.Path} | {chip.ExecutionPath} | {chip.AverageMicroseconds:0.###} us avg | {chip.Evaluations:N0} eval",
							textCol);
					}
				}

				Next(0.7f);
				bool close = UI.Button(
					"CLOSE",
					theme.ButtonTheme,
					pos,
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

			void DrawRow(string text, Color col)
			{
				MenuHelper.DrawLeftAlignTextWithBackground(
					text,
					pos,
					new Vector2(menuWidth, rowHeight),
					Anchor.TopLeft,
					col,
					rowCol,
					false,
					1);
				pos = UI.PrevBounds.BottomLeft + Vector2.down * spacing;
			}

			void Next(float multiplier = 1)
			{
				pos.y -= (rowHeight + spacing) * multiplier;
			}
		}
	}
}
