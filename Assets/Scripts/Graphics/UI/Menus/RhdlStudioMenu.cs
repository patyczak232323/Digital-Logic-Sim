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
			if (string.IsNullOrWhiteSpace(source)) source = DefaultExample;
			requestedSourceChipName = null;
			SetEditorSource(source);
			statusText = "Ready. Edit source and press BUILD.";
			statusSuccess = false;
			latestResult = null;
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
				"RHDL v0.2  /  LOGIC + STRUCTURAL");

			Vector2 contentTop = mainHeader.BottomLeft + Vector2.down * 0.7f;
			Vector2 sourceTop = contentTop;
			Vector2 infoTop = contentTop + Vector2.right * (LeftWidth + Gap);

			Bounds2D sourceHeader = RewiredUI.DrawSectionHeader("SOURCE", sourceTop, LeftWidth);
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
				statusText = "Draft saved.  Ctrl+S";
				statusSuccess = true;
			}
			if ((editorCommand & RhdlEditorCommand.Build) != 0)
			{
				Build(project, false);
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
				statusSuccess = true;
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
			Color statusCol = statusSuccess ? new Color(0.62f, 0.9f, 0.68f) : RewiredUI.SecondaryText;

			RewiredUI.DrawLabel(
				statusSuccess ? "BUILD STATUS: OK" : "BUILD STATUS",
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
			Bounds2D syntaxHeader = RewiredUI.DrawSectionHeader("RHDL v0.2 SYNTAX", cursor, RightWidth);
			cursor = syntaxHeader.BottomLeft;

			RewiredUI.DrawCard(cursor, new Vector2(RightWidth, 20.5f));
			Bounds2D syntaxCard = UI.PrevBounds;
			string help =
				"chip Name {\n" +
				"  input a, b, c\n" +
				"  output y\n\n" +
				"  y = (a AND b) OR c\n" +
				"}\n\n" +
				"Logic: AND OR XOR NOT\n" +
				"Also: &  |  ^  !\n" +
				"Parentheses are supported.\n\n" +
				"Structural connect still works.\n" +
				"Bus ports: [1] [4] [8]\n\n" +
				"ENTER new line + auto-indent\n" +
				"TAB / SHIFT+TAB indent\n" +
				"CTRL+S save  CTRL+SHIFT+B build\n" +
				"CTRL+C/V/X/A whole-document editing";

			UI.DrawText(
				help,
				theme.FontRegular,
				theme.FontSizeRegular * 0.56f,
				syntaxCard.TopLeft + new Vector2(0.8f, -0.8f),
				Anchor.TopLeft,
				RewiredUI.SecondaryText);

			cursor = syntaxCard.BottomLeft + Vector2.down * 0.6f;
			if (UI.Button(
				"LOAD HALF ADDER EXAMPLE",
				theme.MainMenuButtonTheme,
				cursor,
				new Vector2(RightWidth, DrawSettings.ButtonHeight),
				true,
				false,
				false,
				Anchor.TopLeft))
			{
				SetEditorSource(DefaultExample);
				statusText = "Example loaded. Press BUILD & OPEN.";
				statusSuccess = false;
			}
		}

		static void Build(Project project, bool openAfterBuild)
		{
			string source = GetEditorSource();
			RhdlSourceStore.SaveDraft(project.description.ProjectName, source);

			latestResult = RhdlCompiler.Compile(source, project.chipLibrary);
			if (!latestResult.Success)
			{
				statusSuccess = false;
				StringBuilder message = new();
				RhdlDiagnostic[] diagnostics = latestResult.Diagnostics.Take(5).ToArray();
				for (int i = 0; i < diagnostics.Length; i++)
				{
					if (i > 0) message.Append('\n');
					message.Append(diagnostics[i]);
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

			if (openAfterBuild)
			{
				project.LoadDevChipOrCreateNewIfDoesntExist(description.Name);
				UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);
			}
		}

		static void SaveDraft(Project project) =>
			RhdlSourceStore.SaveDraft(project.description.ProjectName, GetEditorSource());

		static string GetEditorSource() => CodeEditor.Text;

		static void SetEditorSource(string source) => CodeEditor.SetText(source);

		public const string DefaultExample =
@"// RHDL v0.2 example: readable half adder
chip HalfAdder {
  input a, b
  output sum, carry

  sum = a XOR b
  carry = a AND b
}";
	}
}
