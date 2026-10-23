using System;
using System.Collections.Generic;
using DLS.Description;
using DLS.Game;
using DLS.SaveSystem;
using DLS.Simulation;
using Seb.Helpers;
using Seb.Types;
using Seb.Vis;
using Seb.Vis.UI;
using UnityEngine;

namespace DLS.Graphics
{
	// Shows every input combination of a combinational chip with its outputs, and lets the
	// user record the table as a test and re-run it after editing the chip.
	public static class TruthTableMenu
	{
		enum ValueFormat
		{
			Decimal,
			Binary,
			Hex
		}

		static readonly string[] FormatLabels = { "DEC", "BIN", "HEX" };
		static readonly UIHandle ID_Rows = new("TruthTable_Rows");
		static readonly UI.ScrollViewDrawElementFunc drawRowFunc = DrawRow;

		const float RowHeight = 2.3f;
		const float ColumnGap = 0.5f;

		static string chipName;
		static TruthTable table;
		static ChipTestSuite suite;
		static ChipTestRunResult testResult;
		static readonly HashSet<int> failedRows = new();
		static string statusText;
		static Color statusColour;
		static ValueFormat format;

		static float inputColumnsWidth;
		static float outputColumnsWidth;

		public static void Open(string name)
		{
			chipName = name;
			UIDrawer.SetActiveMenu(UIDrawer.MenuType.TruthTable);
		}

		public static void OnMenuOpened()
		{
			Project project = Project.ActiveProject;
			table = null;
			suite = null;
			testResult = null;
			failedRows.Clear();
			UI.GetScrollbarState(ID_Rows).scrollY = 0;

			if (project == null || !project.chipLibrary.TryGetChipDescription(chipName, out ChipDescription description))
			{
				SetStatus("Chip not found.", Color.red);
				return;
			}

			table = TruthTableGenerator.Generate(description, project.chipLibrary);
			if (!table.Supported)
			{
				SetStatus("No truth table: " + table.UnsupportedReason, RewiredUI.SecondaryText);
				return;
			}

			try
			{
				suite = Loader.LoadChipTests(project.description.ProjectName, chipName);
			}
			catch (Exception e)
			{
				Debug.LogWarning("Could not load chip tests: " + e.Message);
			}

			// Re-check recorded tests immediately so a broken chip is visible on open.
			if (suite != null) RunTests();
			else SetStatus("No test yet. RECORD TEST saves this table as expected.", RewiredUI.SecondaryText);
		}

		public static void DrawMenu()
		{
			MenuHelper.DrawBackgroundOverlay();
			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;

			Bounds2D area = MenuArea();
			string rightText = table == null || !table.Supported
				? string.Empty
				: table.Sampled
					? $"{table.RowCount} SAMPLED ROWS / {table.TotalInputBits} INPUT BITS"
					: $"{table.RowCount} ROWS";

			RewiredUI.DrawSectionHeader("TRUTH TABLE  /  " + (chipName ?? string.Empty).ToUpperInvariant(), area.TopLeft, area.Width, true, rightText);
			float top = UI.PrevBounds.Bottom - 0.5f;

			// Buttons sit at the bottom so they stay reachable with a thumb on phones.
			float buttonHeight = MobileUI.IsActive ? 4.2f : DrawSettings.ButtonHeight;
			float bottom = area.Bottom;
			int action = DrawButtons(new Vector2(area.Left, bottom + buttonHeight), area.Width, buttonHeight);
			bottom += buttonHeight + 0.6f;

			UI.DrawText(statusText ?? string.Empty, theme.FontRegular, theme.FontSizeRegular * 0.68f, new Vector2(area.Left, bottom + 0.9f), Anchor.TextCentreLeft, statusColour);
			bottom += 1.9f;

			if (table != null && table.Supported)
			{
				LayoutColumns(area.Width - DrawSettings.ActiveUITheme.ScrollTheme.scrollBarWidth - 1f);
				DrawHeaderRow(new Vector2(area.Left, top), area.Width);
				top = UI.PrevBounds.Bottom - 0.2f;

				Vector2 size = new(area.Width, Mathf.Max(RowHeight, top - bottom));
				UI.DrawScrollView(ID_Rows, new Vector2(area.Left, top), size, 0, Anchor.TopLeft, theme.ScrollTheme, drawRowFunc, table.RowCount);
			}

			if (action == 0) RecordTests();
			else if (action == 1) RunTests();
			else if (action == 2) format = (ValueFormat)(((int)format + 1) % FormatLabels.Length);
			else if (action == 3 || KeyboardShortcuts.CancelShortcutTriggered) UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);
		}

		static Bounds2D MenuArea()
		{
			if (MobileUI.IsActive)
			{
				// Leave room for the persistent mobile BACK button at the top.
				Rect safe = MobileUI.SafeRectUI;
				return Bounds2D.CreateFromTopLeftAndSize(new Vector2(safe.xMin + 1f, safe.yMax - 6.2f), new Vector2(safe.width - 2f, safe.height - 7.2f));
			}

			Vector2 size = new(Mathf.Min(UI.Width * 0.7f, 90f), UI.Height * 0.82f);
			return Bounds2D.CreateFromCentreAndSize(UI.Centre, size);
		}

		static readonly string[] buttonNames = new string[4];
		static readonly bool[] buttonStates = new bool[4];

		static int DrawButtons(Vector2 topLeft, float width, float height)
		{
			bool supported = table != null && table.Supported;
			buttonNames[0] = suite == null ? "RECORD TEST" : "RE-RECORD";
			buttonNames[1] = "RUN TEST";
			buttonNames[2] = FormatLabels[(int)format];
			buttonNames[3] = "CLOSE";
			buttonStates[0] = supported;
			buttonStates[1] = supported && suite != null;
			buttonStates[2] = supported;
			buttonStates[3] = true;

			return UI.HorizontalButtonGroup(buttonNames, buttonStates, DrawSettings.ActiveUITheme.ButtonTheme, topLeft, new Vector2(width, height), DrawSettings.DefaultButtonSpacing, 0, Anchor.TopLeft);
		}

		static void LayoutColumns(float width)
		{
			int inputs = table.InputNames.Length;
			int outputs = table.OutputNames.Length;
			int columns = Mathf.Max(1, inputs + outputs);
			float columnWidth = (width - ColumnGap * 2f) / columns;
			inputColumnsWidth = columnWidth * inputs;
			outputColumnsWidth = columnWidth * outputs;
		}

		static void DrawHeaderRow(Vector2 topLeft, float width)
		{
			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			UI.DrawPanel(topLeft, new Vector2(width, RowHeight), RewiredUI.Surface, Anchor.TopLeft);
			Bounds2D bounds = UI.PrevBounds;
			DrawCells(bounds, table.InputNames, table.OutputNames, theme.FontBold, RewiredUI.Accent);
			UI.OverridePreviousBounds(bounds);
		}

		static void DrawRow(Vector2 topLeft, float width, int index, bool isLayoutOnly)
		{
			Vector2 size = new(width, RowHeight);
			if (isLayoutOnly)
			{
				UI.OverridePreviousBounds(Bounds2D.CreateFromTopLeftAndSize(topLeft, size));
				return;
			}

			bool failed = failedRows.Contains(index);
			Color bg = failed ? new Color(0.45f, 0.08f, 0.08f) : index % 2 == 0 ? RewiredUI.SurfaceRaised : RewiredUI.Surface;
			UI.DrawPanel(topLeft, size, bg, Anchor.TopLeft);
			Bounds2D bounds = UI.PrevBounds;

			string[] inputText = Format(table.Inputs[index], table.InputBits);
			string[] outputText = Format(table.Outputs[index], table.OutputBits);
			DrawCells(bounds, inputText, outputText, DrawSettings.ActiveUITheme.FontRegular, Color.white);
			UI.OverridePreviousBounds(bounds);
		}

		static void DrawCells(Bounds2D row, string[] inputs, string[] outputs, FontType font, Color outputColour)
		{
			float fontSize = DrawSettings.ActiveUITheme.FontSizeRegular * 0.7f;
			float x = row.Left + 0.5f;
			float inputWidth = inputs.Length == 0 ? 0 : inputColumnsWidth / inputs.Length;
			for (int i = 0; i < inputs.Length; i++)
			{
				UI.DrawText(inputs[i], font, fontSize, new Vector2(x + inputWidth * (i + 0.5f), row.Centre.y), Anchor.TextCentre, Color.white);
			}

			float dividerX = x + inputColumnsWidth + ColumnGap;
			UI.DrawLine(new Vector2(dividerX, row.Bottom), new Vector2(dividerX, row.Top), 0.06f, RewiredUI.Accent);

			x = dividerX + ColumnGap;
			float outputWidth = outputs.Length == 0 ? 0 : outputColumnsWidth / outputs.Length;
			for (int i = 0; i < outputs.Length; i++)
			{
				UI.DrawText(outputs[i], font, fontSize, new Vector2(x + outputWidth * (i + 0.5f), row.Centre.y), Anchor.TextCentre, outputColour);
			}
		}

		static string[] Format(uint[] states, int[] bits)
		{
			string[] text = new string[states.Length];
			for (int i = 0; i < states.Length; i++) text[i] = FormatValue(states[i], bits[i]);
			return text;
		}

		static string FormatValue(uint state, int bits)
		{
			uint mask = (1u << bits) - 1;
			if ((PinState.GetTristateFlags(state) & mask) != 0) return "Z";

			uint value = PinState.GetBitStates(state) & mask;
			if (bits == 1) return value.ToString();

			return format switch
			{
				ValueFormat.Binary => Convert.ToString(value, 2).PadLeft(bits, '0'),
				ValueFormat.Hex => value.ToString("X"),
				_ => value.ToString()
			};
		}

		static void RecordTests()
		{
			Project project = Project.ActiveProject;
			try
			{
				ChipTestSuite recorded = TruthTableGenerator.RecordTests(table, chipName);
				Saver.SaveChipTests(recorded, project.description.ProjectName);
				suite = recorded;
				testResult = null;
				failedRows.Clear();
				SetStatus($"Test recorded ({table.RowCount} rows).", RewiredUI.Accent);
			}
			catch (Exception e)
			{
				SetStatus("Could not save the test: " + e.Message, Color.red);
			}
		}

		static void RunTests()
		{
			Project project = Project.ActiveProject;
			failedRows.Clear();
			if (suite == null || project == null || !project.chipLibrary.TryGetChipDescription(chipName, out ChipDescription description)) return;

			testResult = TruthTableGenerator.RunTests(suite, description, project.chipLibrary);
			if (!testResult.Supported)
			{
				SetStatus("Re-record the test: " + testResult.UnsupportedReason, Color.yellow);
				return;
			}

			foreach (ChipTestFailure failure in testResult.Failures) failedRows.Add(failure.VectorIndex);

			if (testResult.Passed)
			{
				SetStatus($"TEST PASSED ({testResult.PassedVectors}/{testResult.TotalVectors})", new Color(0.35f, 0.9f, 0.45f));
				return;
			}

			ChipTestFailure first = testResult.Failures[0];
			string output = first.OutputIndex >= 0 && first.OutputIndex < table.OutputNames.Length ? table.OutputNames[first.OutputIndex] : "output";
			int bits = first.OutputIndex >= 0 && first.OutputIndex < table.OutputBits.Length ? table.OutputBits[first.OutputIndex] : 16;
			SetStatus($"TEST FAILED: {testResult.FailedVectors} rows. Row {first.VectorIndex}: {output} = {FormatValue(first.Actual, bits)}, expected {FormatValue(first.Expected, bits)}", Color.red);
		}

		static void SetStatus(string text, Color colour)
		{
			statusText = text;
			statusColour = colour;
		}
	}
}
