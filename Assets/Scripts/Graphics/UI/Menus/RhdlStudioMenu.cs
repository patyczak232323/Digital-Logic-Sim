using System;
using System.Linq;
using System.Text;
using DLS.Description;
using DLS.Game;
using DLS.RHDL;
using DLS.SaveSystem;
using DLS.Simulation;
using Seb.Helpers;
using Seb.Types;
using Seb.Vis;
using Seb.Vis.UI;
using UnityEngine;

namespace DLS.Graphics
{
	public static class RhdlStudioMenu
	{
		const float WorkspaceWidth = 88f;
		const float LeftWidth = 56f;
		const float Gap = 1.2f;
		const float RightWidth = WorkspaceWidth - LeftWidth - Gap;
		const float RowHeight = 1.85f;

		static readonly RhdlCodeEditor CodeEditor = new();

		static RhdlCompileResult latestResult;
		static string statusText = "Ready.";
		static bool statusSuccess;
		static bool buildAttempted;
		static int lastBuildSubChipCount;
		static int lastBuildWireCount;
		static string requestedSourceChipName;

		public static void OpenSource(string chipName)
		{
			requestedSourceChipName = chipName;
			UIDrawer.SetActiveMenu(UIDrawer.MenuType.RhdlStudio);
		}

		public static void OnMenuOpened()
		{
			Project project = Project.ActiveProject;
			if (project == null) return;

			string source = string.Empty;
			if (!string.IsNullOrWhiteSpace(requestedSourceChipName))
			{
				source = RhdlSourceStore.LoadChipSource(project.description.ProjectName, requestedSourceChipName);
			}
			if (string.IsNullOrWhiteSpace(source)) source = RhdlSourceStore.LoadDraft(project.description.ProjectName);
			bool loadedTemplate = string.IsNullOrWhiteSpace(source);
			if (loadedTemplate) source = StarterTemplate;
			requestedSourceChipName = null;
			SetEditorSource(source);
			if (loadedTemplate) CodeEditor.MarkDirty();
			statusText = loadedTemplate
				? "New RHDL chip template. Rename it and start coding."
				: "Ready. Edit source and press BUILD.";
			statusSuccess = false;
			buildAttempted = false;
			latestResult = null;
			CodeEditor.SetDiagnosticLines(Array.Empty<int>());
		}

		public static void DrawMenu()
		{
			Project project = Project.ActiveProject;
			if (project == null)
			{
				UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);
				return;
			}

			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			MenuHelper.DrawBackgroundOverlay();

			Vector2 topLeft = UI.Centre + new Vector2(-WorkspaceWidth / 2f, 26.2f);
			Bounds2D mainHeader = RewiredUI.DrawSectionHeader(
				"REWIRED / RHDL STUDIO",
				topLeft,
				WorkspaceWidth,
				true,
				"RHDL v0.4  /  PYTHON-LIKE + STRUCTURAL");

			Vector2 contentTop = mainHeader.BottomLeft + Vector2.down * 0.7f;
			Vector2 sourceTop = contentTop;
			Vector2 infoTop = contentTop + Vector2.right * (LeftWidth + Gap);

			string sourceInfo = $"Ln {CodeEditor.CaretLine}, Col {CodeEditor.CaretColumn}  /  {CodeEditor.LineCount} lines" +
			                    (CodeEditor.IsDirty ? "  /  MODIFIED" : string.Empty);
			Bounds2D sourceHeader = RewiredUI.DrawSectionHeader(
				CodeEditor.IsDirty ? "SOURCE *" : "SOURCE",
				sourceTop,
				LeftWidth,
				false,
				sourceInfo);
			Vector2 sourceBodyTop = sourceHeader.BottomLeft;
			Vector2 sourceSize = new(LeftWidth, 39.5f);
			RewiredUI.DrawCard(sourceBodyTop, sourceSize);
			Bounds2D sourceCard = UI.PrevBounds;

			Vector2 scrollTopLeft = sourceCard.TopLeft + new Vector2(0.55f, -0.55f);
			Vector2 scrollSize = new(sourceCard.Width - 1.1f, sourceCard.Height - 1.1f);
			InputFieldTheme editorTheme = theme.ChipNameInputField;
			editorTheme.font = theme.FontRegular;
			editorTheme.fontSize = theme.FontSizeRegular * 0.62f;
			editorTheme.bgCol = Color.clear;
			editorTheme.focusBorderCol = RewiredUI.Accent;

			RhdlEditorCommand editorCommand = CodeEditor.Draw(
				scrollTopLeft,
				scrollSize,
				theme.ScrollTheme,
				editorTheme);

			if ((editorCommand & RhdlEditorCommand.Save) != 0)
			{
				SaveDraft(project);
				statusText = "Draft saved.";
			}
			if ((editorCommand & RhdlEditorCommand.Build) != 0)
			{
				Build(project, false);
			}
			if ((editorCommand & RhdlEditorCommand.BuildAndOpen) != 0)
			{
				Build(project, true);
			}

			DrawRightPanel(theme, infoTop, project);

			Vector2 actionsTopLeft = sourceCard.BottomLeft + Vector2.down * 0.7f;
			string[] actionNames = { "SAVE DRAFT", "BUILD", "BUILD & OPEN", "CLOSE" };
			int action = UI.HorizontalButtonGroup(
				actionNames,
				theme.MainMenuButtonTheme,
				actionsTopLeft,
				WorkspaceWidth,
				DrawSettings.DefaultButtonSpacing,
				0,
				Anchor.TopLeft);

			if (action == 0)
			{
				SaveDraft(project);
				statusText = "Draft saved.";
			}
			else if (action == 1)
			{
				Build(project, false);
			}
			else if (action == 2)
			{
				Build(project, true);
			}
			else if (action == 3 || KeyboardShortcuts.CancelShortcutTriggered)
			{
				SaveDraft(project);
				UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);
			}
		}

		static void DrawRightPanel(DrawSettings.UIThemeDLS theme, Vector2 topLeft, Project project)
		{
			Bounds2D compilerHeader = RewiredUI.DrawSectionHeader("COMPILER", topLeft, RightWidth);
			Vector2 cursor = compilerHeader.BottomLeft;

			RewiredUI.DrawCard(cursor, new Vector2(RightWidth, 10.4f), true);
			Bounds2D statusCard = UI.PrevBounds;
			Color statusCol = !buildAttempted
				? RewiredUI.SecondaryText
				: statusSuccess
					? new Color(0.62f, 0.9f, 0.68f)
					: new Color(1f, 0.48f, 0.48f);
			string statusLabel = !buildAttempted ? "STATUS" : statusSuccess ? "BUILD: OK" : "BUILD: ERRORS";

			RewiredUI.DrawLabel(
				statusLabel,
				statusCard.TopLeft + new Vector2(0.8f, -1.0f),
				true,
				0.66f,
				statusCol);

			string summary = statusText;
			if (statusSuccess && latestResult?.Description != null)
			{
				summary += $"\n\nchip: {latestResult.Description.Name}" +
				           $"\nsubchips: {lastBuildSubChipCount}" +
				           $"\nwires: {lastBuildWireCount}";
			}

			UI.DrawText(
				summary,
				theme.FontRegular,
				theme.FontSizeRegular * 0.58f,
				statusCard.TopLeft + new Vector2(0.8f, -2.4f),
				Anchor.TopLeft,
				statusCol);

			cursor = statusCard.BottomLeft + Vector2.down * 0.6f;
			Bounds2D syntaxHeader = RewiredUI.DrawSectionHeader("RHDL v0.4 QUICK REFERENCE", cursor, RightWidth);
			cursor = syntaxHeader.BottomLeft;

			RewiredUI.DrawCard(cursor, new Vector2(RightWidth, 24.6f));
			Bounds2D syntaxCard = UI.PrevBounds;
			string help =
				"chip Adder:\n" +
				"    input A: 8\n" +
				"    input B: 8\n" +
				"    output Y = A + B\n\n" +
				"Choose: A if select else B   or   mux(select, A, B)\n" +
				"Logic words: and / or / xor / not\n" +
				"Logic values: true/false, high/low, on/off\n" +
				"Internal value: let sum = A + B\n" +
				"Constant: const MASK = 0xFF\n" +
				"Bits: A[3]   slice: A[7:4]\n" +
				"Concat: concat(A[7:4], B[3:0])\n" +
				"Chip instance: n = NAND(A=A, B=B, OUT=Y)\n" +
				"Comments start with #\n\n" +
				"ENTER after ':' indents automatically\n" +
				"TAB / SHIFT+TAB indent   ALT+UP/DOWN move lines\n" +
				"CTRL+BACKSPACE/DELETE delete word\n" +
				"CTRL+Z/Y undo/redo   CTRL+/ comment\n" +
				"CTRL+SHIFT+F format document   F8 next error\n" +
				"CTRL+D duplicate line   HOME smart-home\n" +
				"CTRL+S save   CTRL+B / CTRL+ENTER build   F5 build+open";

			UI.DrawText(
				help,
				theme.FontRegular,
				theme.FontSizeRegular * 0.56f,
				syntaxCard.TopLeft + new Vector2(0.8f, -0.8f),
				Anchor.TopLeft,
				RewiredUI.SecondaryText);

			cursor = syntaxCard.BottomLeft + Vector2.down * 0.6f;
			int templateAction = UI.HorizontalButtonGroup(
				new[] { "NEW CHIP", "LOAD EXAMPLE" },
				theme.MainMenuButtonTheme,
				cursor,
				RightWidth,
				DrawSettings.DefaultButtonSpacing,
				0,
				Anchor.TopLeft);
			if (templateAction == 0)
			{
				SetEditorSource(StarterTemplate);
				CodeEditor.MarkDirty();
				CodeEditor.SetDiagnosticLines(Array.Empty<int>());
				latestResult = null;
				buildAttempted = false;
				statusSuccess = false;
				statusText = "New chip template loaded.";
			}
			else if (templateAction == 1)
			{
				SetEditorSource(DefaultExample);
				CodeEditor.MarkDirty();
				CodeEditor.SetDiagnosticLines(Array.Empty<int>());
				latestResult = null;
				buildAttempted = false;
				statusSuccess = false;
				statusText = "Example loaded. Press BUILD & OPEN.";
			}
		}

		static void Build(Project project, bool openAfterBuild)
		{
			string source = GetEditorSource();
			SaveDraft(project);
			buildAttempted = true;

			latestResult = RhdlCompiler.Compile(source, project.chipLibrary);
			if (!latestResult.Success)
			{
				statusSuccess = false;
				CodeEditor.SetDiagnosticLines(latestResult.Diagnostics.Select(d => d.Line));
				RhdlDiagnostic first = latestResult.Diagnostics.FirstOrDefault(d => d.Line > 0);
				if (first != null) CodeEditor.GoTo(first.Line, first.Column > 0 ? first.Column : 1);

				StringBuilder message = new();
				RhdlDiagnostic[] diagnostics = latestResult.Diagnostics.Take(3).ToArray();
				for (int i = 0; i < diagnostics.Length; i++)
				{
					if (i > 0) message.Append('\n');
					message.Append(CompactDiagnostic(diagnostics[i]));
				}
				if (latestResult.Diagnostics.Length > diagnostics.Length)
					message.Append($"\n+ {latestResult.Diagnostics.Length - diagnostics.Length} more error(s)");
				statusText = message.ToString();
				return;
			}

			ChipDescription description = latestResult.Description;
			if (project.chipLibrary.IsBuiltinChip(description.Name))
			{
				statusSuccess = false;
				statusText = $"Cannot overwrite builtin chip '{description.Name}'.";
				return;
			}

			bool existed = project.chipLibrary.HasChip(description.Name);
			Saver.SaveChip(description, project.description.ProjectName);
			project.chipLibrary.NotifyChipSaved(description);
			RhdlSourceStore.SaveChipSource(project.description.ProjectName, description.Name, source);

			if (!existed)
			{
				project.SetStarred(description.Name, true, false, autoSave: false);
			}
			project.UpdateAndSaveProjectDescription();

			CombinationalChipCacheManager.RefreshPersistentCachesAfterSave(
				description.Name,
				project.chipLibrary,
				project.description.ProjectName);

			lastBuildSubChipCount = description.SubChips?.Length ?? 0;
			lastBuildWireCount = description.Wires?.Length ?? 0;
			statusText = existed ? "Rebuilt existing RHDL chip." : "Generated and saved new RHDL chip.";
			statusSuccess = true;
			CodeEditor.SetDiagnosticLines(Array.Empty<int>());
			CodeEditor.MarkSaved();

			if (openAfterBuild)
			{
				project.LoadDevChipOrCreateNewIfDoesntExist(description.Name);
				UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);
			}
		}

		static void SaveDraft(Project project)
		{
			RhdlSourceStore.SaveDraft(project.description.ProjectName, GetEditorSource());
			CodeEditor.MarkSaved();
		}

		static string CompactDiagnostic(RhdlDiagnostic diagnostic)
		{
			string location = diagnostic.Line > 0
				? diagnostic.Column > 0 ? $"L{diagnostic.Line}:{diagnostic.Column}" : $"L{diagnostic.Line}"
				: "RHDL";
			string message = diagnostic.Message ?? string.Empty;
			const int max = 58;
			if (message.Length > max) message = message.Substring(0, max - 3) + "...";
			return location + "  " + message;
		}

		static string GetEditorSource() => CodeEditor.Text;

		static void SetEditorSource(string source) => CodeEditor.SetText(source);

		public const string StarterTemplate =
@"# A chip starts with its name and a colon.
chip Adder:
    input A: 8
    input B: 8

    output Y = A + B";

		public const string DefaultExample =
@"# RHDL v0.4: readable hardware description
chip AluMini(WIDTH=8):
    input A: WIDTH
    input B: WIDTH
    input select

    let sum = A + B
    let mixed = concat(A[7:4], B[3:0])

    output Y: WIDTH = sum if select else mixed
    output equal = A == B
    output ready = true";

	}
}
