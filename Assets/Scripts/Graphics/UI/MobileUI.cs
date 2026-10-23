using System;
using DLS.Description;
using DLS.Game;
using DLS.Simulation;
using Seb.Helpers;
using Seb.Helpers.InputHandling;
using Seb.Types;
using Seb.Vis;
using Seb.Vis.UI;
using UnityEngine;

namespace DLS.Graphics
{
	/// <summary>
	/// Touch-first Android shell. Desktop keeps using the original Rewired UI path.
	/// This class owns only mobile navigation/chrome; domain-specific editors can still
	/// render their existing content inside the phone-sized UI scope.
	/// </summary>
	public static class MobileUI
	{
		public const float TopBarHeight = 5.4f;
		public const float DockHeight = 7.0f;
		public const float QuickBarHeight = 5.0f;

		const string PrefPanDefault = "rewired.mobile.panDefault";
		const string PrefTargetFps = "rewired.mobile.targetFps";
		const string PrefHelpShown = "rewired.mobile.helpShown";

		static readonly string[] DockLabels = { "PAN", "ADD CHIP", "SAVE", "LIBRARY", "TOOLS" };
		static bool panMode;
		static readonly System.Collections.Generic.List<string> quickChips = new();

		// "-mobile-ui" on the command line shows the touch interface in a desktop build, to
		// preview and screenshot it without a phone. Touch gestures stay Android-only.
		public static bool PreviewOnDesktop { get; private set; }

		public static bool IsActive => MobileInputBridge.RuntimeEnabled || Application.isMobilePlatform || PreviewOnDesktop;

		public static void ReadCommandLine()
		{
			PreviewOnDesktop = Array.Exists(Environment.GetCommandLineArgs(), a => string.Equals(a, "-mobile-ui", StringComparison.OrdinalIgnoreCase));
		}
		public static bool PanMode => panMode;
		public static bool PanByDefault => PlayerPrefs.GetInt(PrefPanDefault, 0) != 0;
		public static int TargetFrameRate => Mathf.Clamp(PlayerPrefs.GetInt(PrefTargetFps, 60), 30, 120);

		public static Rect SafeRectUI
		{
			get
			{
				if (Screen.width <= 0) return new Rect(0, 0, UI.Width, UI.Height);
				Rect safe = Screen.safeArea;
				float s = UI.Width / Screen.width;
				return new Rect(safe.xMin * s, safe.yMin * s, safe.width * s, safe.height * s);
			}
		}

		public static Rect KeyboardAwareSafeRectUI
		{
			get
			{
				Rect safe = SafeRectUI;
				if (!MobileInputBridge.KeyboardVisible ||
				    MobileInputBridge.KeyboardScreenRect.height <= 0 ||
				    Screen.width <= 0)
				{
					return safe;
				}

				float s = UI.Width / Screen.width;
				float keyboardTop = MobileInputBridge.KeyboardScreenRect.yMax * s;
				float visibleBottom = Mathf.Clamp(keyboardTop + 0.6f, safe.yMin, safe.yMax);
				return Rect.MinMaxRect(safe.xMin, visibleBottom, safe.xMax, safe.yMax);
			}
		}

		public static void ApplyRuntimePreferences()
		{
			panMode = PanByDefault;
			Application.targetFrameRate = TargetFrameRate;
		}

		public static void SetPanMode(bool enabled)
		{
			panMode = enabled;
		}

		public static bool IsScreenPointOverPersistentChrome(Vector2 screenPosition)
		{
			if (!IsActive) return false;
			if (UIDrawer.ActiveMenu == UIDrawer.MenuType.BottomBarMenuPopup) return true;
			if (UIDrawer.ActiveMenu != UIDrawer.MenuType.None) return false;

			Rect safe = Screen.safeArea;
			float pixelsPerUIUnit = Screen.width / UI.Width;
			float topBottom = safe.yMax - TopBarHeight * pixelsPerUIUnit;
			float dockTop = safe.yMin + (DockHeight + (QuickBarVisible(Project.ActiveProject) ? QuickBarHeight : 0)) * pixelsPerUIUnit;
			return screenPosition.y >= topBottom || screenPosition.y <= dockTop;
		}

		public static void SetPanDefault(bool enabled)
		{
			PlayerPrefs.SetInt(PrefPanDefault, enabled ? 1 : 0);
			PlayerPrefs.Save();
		}

		public static void SetTargetFrameRate(int fps)
		{
			fps = Mathf.Clamp(fps, 30, 120);
			PlayerPrefs.SetInt(PrefTargetFps, fps);
			PlayerPrefs.Save();
			Application.targetFrameRate = fps;
		}

		public static void Draw()
		{
			if (UIDrawer.ActiveMenu == UIDrawer.MenuType.MainMenu)
			{
				MobileMainMenu.Draw();
				return;
			}

			Project project = Project.ActiveProject;
			if (project == null)
			{
				UIDrawer.SetActiveMenu(UIDrawer.MenuType.MainMenu);
				return;
			}

			DrawProjectMenus(project);
		}

		static void DrawProjectMenus(Project project)
		{
			UIDrawer.MenuType menu = UIDrawer.ActiveMenu;

			if (menu is UIDrawer.MenuType.None or UIDrawer.MenuType.BottomBarMenuPopup)
			{
				// Explain the touch gestures the first time a project is opened.
				if (PlayerPrefs.GetInt(PrefHelpShown, 0) == 0)
				{
					PlayerPrefs.SetInt(PrefHelpShown, 1);
					PlayerPrefs.Save();
					UIDrawer.SetActiveMenu(UIDrawer.MenuType.MobileHelp);
					return;
				}

				DrawProjectChrome(project);
				ContextMenu.Update();
				return;
			}

			// Full-screen/specialized tools keep their proven content, but Android gets
			// a phone-aspect canvas, no desktop bottom bar, and a large persistent Back target.
			switch (menu)
			{
				case UIDrawer.MenuType.ChipSave: ChipSaveMenu.DrawMenu(); break;
				case UIDrawer.MenuType.ChipLibrary: ChipLibraryMenu.DrawMenu(); break;
				case UIDrawer.MenuType.ChipCustomization: ChipCustomizationMenu.DrawMenu(); break;
				case UIDrawer.MenuType.Preferences: PreferencesMenu.DrawMenu(project); break;
				case UIDrawer.MenuType.SimulationDiagnostics: SimulationDiagnosticsMenu.DrawMenu(); break;
				case UIDrawer.MenuType.RhdlStudio: RhdlStudioMenu.DrawMenu(); break;
				case UIDrawer.MenuType.PinRename: PinEditMenu.DrawMenu(); break;
				case UIDrawer.MenuType.RebindKeyChip: RebindKeyChipMenu.DrawMenu(); break;
				case UIDrawer.MenuType.RomEdit: RomEditMenu.DrawMenu(); break;
				case UIDrawer.MenuType.UnsavedChanges: UnsavedChangesPopup.DrawMenu(); break;
				case UIDrawer.MenuType.Search: SearchPopup.DrawMenu(); break;
				case UIDrawer.MenuType.ChipLabelPopup: ChipLabelMenu.DrawMenu(); break;
				case UIDrawer.MenuType.PulseEdit: PulseEditMenu.DrawMenu(); break;
				case UIDrawer.MenuType.TruthTable: TruthTableMenu.DrawMenu(); break;
				case UIDrawer.MenuType.Export: ExportMenu.DrawMenu(); break;
				case UIDrawer.MenuType.Breakpoint: BreakpointMenu.DrawMenu(); break;
				case UIDrawer.MenuType.MobileHelp: MobileHelpOverlay.Draw(); break;
				case UIDrawer.MenuType.Challenges: ChallengesMenu.DrawMenu(); break;
			}

			DrawModalBackButton(menu);
			ContextMenu.Update();
		}

		static void DrawProjectChrome(Project project)
		{
			Rect safe = SafeRectUI;
			DrawTopBar(project, safe);
			DrawDock(project, safe);

			if (UIDrawer.ActiveMenu == UIDrawer.MenuType.BottomBarMenuPopup)
			{
				DrawMoreSheet(project, safe);
			}
			else if (QuickBarVisible(project))
			{
				DrawQuickBar(project, safe);
				DrawCanvasHint(project, safe);
			}
		}

		// The quick bar is hidden under the tools sheet, so its buttons can't be pressed through it.
		static bool QuickBarVisible(Project project) =>
			project != null && project.CanEditViewedChip && UIDrawer.ActiveMenu == UIDrawer.MenuType.None;

		// One tap places the starred chips, with collections (IN/OUT by default) opened up, so
		// input and output pins are always one tap away.
		static void DrawQuickBar(Project project, Rect safe)
		{
			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			CollectQuickChips(project);

			Vector2 bottomLeft = new(safe.xMin, safe.yMin + DockHeight);
			UI.DrawPanel(bottomLeft, new Vector2(safe.width, QuickBarHeight), ColHelper.Darken(theme.StarredBarCol, 0.04f), Anchor.BottomLeft);

			const float pad = 0.55f;
			const float gap = 0.35f;
			const float minWidth = 8.6f;
			float available = safe.width - pad * 2f;
			int fit = Mathf.Max(1, Mathf.FloorToInt((available + gap) / (minWidth + gap)));
			bool overflow = quickChips.Count > fit;
			int shown = overflow ? fit - 1 : quickChips.Count;
			int slots = overflow ? fit : Mathf.Max(quickChips.Count, 1);
			float buttonW = Mathf.Min(14f, (available - gap * (slots - 1)) / slots);
			float buttonH = QuickBarHeight - pad * 2f;
			float x = safe.xMin + pad;
			float y = bottomLeft.y + pad;

			for (int i = 0; i < shown; i++)
			{
				if (UI.Button(quickChips[i], theme.ChipButton, new Vector2(x, y), new Vector2(buttonW, buttonH), true, false, false, Anchor.BottomLeft))
				{
					project.controller.StartPlacing(quickChips[i]);
				}
				x += buttonW + gap;
			}

			if (overflow && UI.Button("MORE...", theme.MenuButtonTheme, new Vector2(x, y), new Vector2(buttonW, buttonH), true, false, false, Anchor.BottomLeft))
			{
				UIDrawer.SetActiveMenu(UIDrawer.MenuType.Search);
			}
		}

		static void CollectQuickChips(Project project)
		{
			quickChips.Clear();
			foreach (StarredItem item in project.description.StarredList)
			{
				if (!item.IsCollection)
				{
					AddQuickChip(project, item.Name);
					continue;
				}

				foreach (ChipCollection collection in project.description.ChipCollections)
				{
					if (!ChipDescription.NameMatch(collection.Name, item.Name)) continue;
					foreach (string chip in collection.Chips) AddQuickChip(project, chip);
				}
			}
		}

		static void AddQuickChip(Project project, string name)
		{
			if (quickChips.Contains(name) || !project.chipLibrary.HasChip(name) || !project.ViewedChip.CanAddSubchip(name)) return;
			quickChips.Add(name);
		}

		// Says what to do next when it isn't obvious: on an empty chip, and while a chip waits
		// to be placed (it follows the next tap).
		static void DrawCanvasHint(Project project, Rect safe)
		{
			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			float y = safe.yMin + DockHeight + QuickBarHeight + 2.4f;

			if (project.controller.IsPlacingElements)
			{
				const float cancelW = 11f;
				string text = "TAP WHERE THE CHIP SHOULD GO";
				Vector2 size = new(Draw.CalculateTextBoundsSize(text, theme.FontSizeRegular * 0.8f, theme.FontBold).x + cancelW + 3f, 3.6f);
				Vector2 centre = new(safe.center.x, y);
				UI.DrawPanel(centre, size, RewiredUI.SurfaceRaised, Anchor.Centre);
				Bounds2D banner = UI.PrevBounds;
				UI.DrawText(text, theme.FontBold, theme.FontSizeRegular * 0.8f, banner.CentreLeft + Vector2.right * 1f, Anchor.TextCentreLeft, Color.white);
				if (UI.Button("CANCEL", theme.MenuButtonTheme, banner.CentreRight + Vector2.left * 0.3f, new Vector2(cancelW, 3f), true, false, false, Anchor.CentreRight))
				{
					project.controller.CancelEverything();
				}
				return;
			}

			if (project.ViewedChip.Elements.Count == 0)
			{
				const string text = "Empty chip: tap IN-1 and OUT-1 below to add pins, then NAND. Drag from pin to pin to wire them.";
				UI.DrawText(text, theme.FontRegular, theme.FontSizeRegular * 0.72f, new Vector2(safe.center.x, y), Anchor.Centre, RewiredUI.SecondaryText);
			}
		}

		static void DrawTopBar(Project project, Rect safe)
		{
			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			Vector2 topLeft = new(safe.xMin, safe.yMax);
			UI.DrawPanel(topLeft, new Vector2(safe.width, TopBarHeight), theme.InfoBarCol, Anchor.TopLeft);
			Bounds2D bar = UI.PrevBounds;
			UI.DrawLine(bar.BottomLeft, bar.BottomRight, 0.08f, RewiredUI.Accent);

			float buttonH = TopBarHeight - 0.8f;
			if (project.chipViewStack.Count > 1)
			{
				if (UI.Button("BACK", theme.MainMenuButtonTheme, bar.CentreLeft + Vector2.right * 0.5f, new Vector2(11f, buttonH), true, false, false, Anchor.CentreLeft))
				{
					project.ReturnToPreviousViewedChip();
				}
			}

			float textX = project.chipViewStack.Count > 1 ? bar.Left + 12.5f : bar.Left + 1.2f;

			// Actions that need a keyboard on desktop: undo/redo and run/pause/step.
			float actionsLeft = DrawTopBarActions(project, bar, buttonH);

			string breakpoint = project.lastBreakpointHit;
			string label;
			Color labelCol = Color.white;
			if (breakpoint != null && project.simPaused)
			{
				label = "BREAKPOINT: " + breakpoint.ToUpperInvariant();
				labelCol = Color.yellow;
			}
			else
			{
				string chipName = string.IsNullOrWhiteSpace(project.ActiveDevChipName) ? "NEW CHIP" : project.ActiveDevChipName.ToUpperInvariant();
				label = $"{project.description.ProjectName.ToUpperInvariant()}  /  {chipName}";
			}

			// Keep the label clear of the action buttons on narrow screens.
			float fontSize = theme.FontSizeRegular * 0.9f;
			int maxChars = Mathf.Max(6, Mathf.FloorToInt((actionsLeft - textX - 1f) / (fontSize * 0.62f)));
			if (label.Length > maxChars) label = label.Substring(0, maxChars - 3) + "...";
			UI.DrawText(label, theme.FontBold, fontSize, new Vector2(textX, bar.Centre.y), Anchor.TextCentreLeft, labelCol);
		}

		// Draws UNDO, REDO, RUN/PAUSE and STEP from the right edge; returns their left edge.
		static float DrawTopBarActions(Project project, Bounds2D bar, float buttonH)
		{
			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			const float width = 8.4f;
			const float gap = 0.4f;
			float x = bar.Right - 0.5f;
			bool paused = project.simPaused;
			bool canEdit = project.CanEditViewedChip;

			x -= width;
			if (UI.Button("STEP", theme.MainMenuButtonTheme, new Vector2(x, bar.Centre.y), new Vector2(width, buttonH), paused, false, false, Anchor.CentreLeft))
			{
				project.advanceSingleSimStep = true;
			}

			x -= width + gap;
			ButtonTheme runTheme = paused ? theme.ChipLibraryCollectionToggleOn : theme.MainMenuButtonTheme;
			if (UI.Button(paused ? "RUN" : "PAUSE", runTheme, new Vector2(x, bar.Centre.y), new Vector2(width, buttonH), true, false, false, Anchor.CentreLeft))
			{
				project.description.Prefs_SimPaused = !paused;
				project.SaveCurrentProjectDescription();
			}

			x -= width + gap * 3;
			if (UI.Button("REDO", theme.MainMenuButtonTheme, new Vector2(x, bar.Centre.y), new Vector2(width, buttonH), canEdit && project.ViewedChip.UndoController.CanRedo, false, false, Anchor.CentreLeft))
			{
				project.ViewedChip.UndoController.TryRedo();
			}

			x -= width + gap;
			if (UI.Button("UNDO", theme.MainMenuButtonTheme, new Vector2(x, bar.Centre.y), new Vector2(width, buttonH), canEdit && project.ViewedChip.UndoController.CanUndo, false, false, Anchor.CentreLeft))
			{
				project.ViewedChip.UndoController.TryUndo();
			}

			return x;
		}

		static void DrawDock(Project project, Rect safe)
		{
			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			Vector2 bottomLeft = new(safe.xMin, safe.yMin);
			UI.DrawPanel(bottomLeft, new Vector2(safe.width, DockHeight), theme.StarredBarCol, Anchor.BottomLeft);
			Bounds2D dock = UI.PrevBounds;
			UI.DrawLine(dock.TopLeft, dock.TopRight, 0.08f, RewiredUI.Accent);

			const float gap = 0.45f;
			float innerPad = 0.55f;
			float buttonH = DockHeight - innerPad * 2f;
			float buttonW = (safe.width - innerPad * 2f - gap * (DockLabels.Length - 1)) / DockLabels.Length;
			float x = safe.xMin + innerPad;

			for (int i = 0; i < DockLabels.Length; i++)
			{
				ButtonTheme buttonTheme = i == 0 && panMode ? theme.ChipLibraryCollectionToggleOn : theme.MenuButtonTheme;
				bool enabled = i != 2 || project.CanEditViewedChip;
				// The PAN button shows whether one finger moves the view or edits the circuit.
				string dockLabel = i == 0 ? (panMode ? "PAN: ON" : "PAN: OFF") : DockLabels[i];
				if (UI.Button(dockLabel, buttonTheme, new Vector2(x, safe.yMin + innerPad), new Vector2(buttonW, buttonH), enabled, false, false, Anchor.BottomLeft))
				{
					HandleDockButton(project, i);
				}
				x += buttonW + gap;
			}
		}

		static void HandleDockButton(Project project, int index)
		{
			switch (index)
			{
				case 0:
					panMode = !panMode;
					ContextMenu.CloseContextMenu();
					break;
				case 1:
					UIDrawer.SetActiveMenu(UIDrawer.MenuType.Search);
					break;
				case 2:
					UIDrawer.SetActiveMenu(UIDrawer.MenuType.ChipSave);
					break;
				case 3:
					UIDrawer.SetActiveMenu(UIDrawer.MenuType.ChipLibrary);
					break;
				case 4:
					ToggleDrawer();
					break;
			}
		}

		static void DrawMoreSheet(Project project, Rect safe)
		{
			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			float sheetW = Mathf.Min(72f, safe.width - 2f);
			float sheetH = Mathf.Min(24.6f, safe.height - TopBarHeight - DockHeight - 1.5f);
			Vector2 bottomRight = new(safe.xMax - 0.6f, safe.yMin + DockHeight + 0.5f);

			UI.StartNewLayer();
			UI.DrawPanel(bottomRight, new Vector2(sheetW, sheetH), RewiredUI.SurfaceRaised, Anchor.BottomRight);
			Bounds2D sheet = UI.PrevBounds;
			RewiredUI.DrawFrame(sheet);

			UI.DrawText("TOOLS", theme.FontBold, theme.FontSizeRegular * 1.05f, sheet.TopLeft + new Vector2(1.2f, -2.2f), Anchor.TextCentreLeft, Color.white);

			// Three columns keep every tool on screen in landscape without scrolling.
			const int columns = 3;
			const float h = 4.45f;
			const float gap = 0.45f;
			float top = sheet.Top - 4.3f;
			float w = (sheet.Width - 2f - gap * (columns - 1)) / columns;
			int index = 0;

			string current = project.ActiveDevChipName;
			bool currentIsSavedChip = !string.IsNullOrWhiteSpace(current) &&
			                          project.chipLibrary.TryGetChipDescription(current, out ChipDescription currentDescription) &&
			                          currentDescription.ChipType == ChipType.Custom;
			int breakpoints = SimulationBreakpoints.Count;

			// Draw every button first so the layout is the same on the frame one is pressed.
			bool newChip = Tool("NEW CHIP", project.CanEditViewedChip);
			bool truthTable = Tool("TRUTH TABLE", currentIsSavedChip);
			bool export = Tool("EXPORT", true);
			bool rhdl = Tool("RHDL STUDIO", true);
			bool clearBreakpoints = Tool(breakpoints > 0 ? $"CLEAR BREAKS ({breakpoints})" : "NO BREAKPOINTS", breakpoints > 0);
			bool challenges = Tool("CHALLENGES", true);
			bool help = Tool("HELP", true);
			bool preferences = Tool("PREFERENCES", true);
			bool diagnostics = Tool("DIAGNOSTICS", true);
			bool mainMenu = Tool("MAIN MENU", true, theme.MenuPopupButtonTheme);


			if (newChip) CreateNewChip(project);
			else if (truthTable) TruthTableMenu.Open(current);
			else if (export) ExportMenu.Open(current);
			else if (rhdl) UIDrawer.SetActiveMenu(UIDrawer.MenuType.RhdlStudio);
			else if (clearBreakpoints) SimulationBreakpoints.Clear();
			else if (challenges) ChallengesMenu.Open();
			else if (help) UIDrawer.SetActiveMenu(UIDrawer.MenuType.MobileHelp);
			else if (preferences) UIDrawer.SetActiveMenu(UIDrawer.MenuType.Preferences);
			else if (diagnostics) UIDrawer.SetActiveMenu(UIDrawer.MenuType.SimulationDiagnostics);
			else if (mainMenu) ExitToMainMenu(project);

			bool Tool(string label, bool enabled, ButtonTheme? buttonTheme = null)
			{
				int col = index % columns;
				int row = index / columns;
				index++;
				Vector2 pos = new(sheet.Left + 1f + col * (w + gap), top - row * (h + gap));
				return UI.Button(label, buttonTheme ?? theme.MainMenuButtonTheme, pos, new Vector2(w, h), enabled, false, false, Anchor.TopLeft);
			}
		}

		static void DrawModalBackButton(UIDrawer.MenuType menu)
		{
			// UnsavedChanges owns an explicit decision and should not be visually obscured.
			if (menu == UIDrawer.MenuType.UnsavedChanges) return;

			Rect safe = SafeRectUI;
			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			UI.StartNewLayer();
			Vector2 pos = new(safe.xMin + 0.6f, safe.yMax - 0.6f);
			if (UI.Button("BACK", theme.MainMenuButtonTheme, pos, new Vector2(11.5f, 4.8f), true, false, false, Anchor.TopLeft))
			{
				MobileInputBridge.DismissKeyboard();
				MobileRuntime.ScheduleVirtualKey(KeyCode.Escape);
			}
		}

		static void CreateNewChip(Project project)
		{
			UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);
			if (project.ActiveChipHasUnsavedChanges()) UnsavedChangesPopup.OpenPopup(Confirm);
			else Confirm(true);

			void Confirm(bool ok)
			{
				if (ok) project.CreateBlankDevChip();
			}
		}

		static void ExitToMainMenu(Project project)
		{
			UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);
			if (project.ActiveChipHasUnsavedChanges()) UnsavedChangesPopup.OpenPopup(Confirm);
			else Confirm(true);

			void Confirm(bool ok)
			{
				if (!ok) return;
				project.NotifyExit();
				UIDrawer.SetActiveMenu(UIDrawer.MenuType.MainMenu);
			}
		}

		public static void ToggleDrawer()
		{
			if (!IsActive) return;
			UIDrawer.SetActiveMenu(UIDrawer.ActiveMenu == UIDrawer.MenuType.BottomBarMenuPopup
				? UIDrawer.MenuType.None
				: UIDrawer.MenuType.BottomBarMenuPopup);
		}

		public static void OpenDrawer()
		{
			if (IsActive && UIDrawer.ActiveMenu == UIDrawer.MenuType.None)
				UIDrawer.SetActiveMenu(UIDrawer.MenuType.BottomBarMenuPopup);
		}

		public static bool HandleBackButton()
		{
			if (!IsActive) return false;

			if (UIDrawer.ActiveMenu == UIDrawer.MenuType.MainMenu)
				return MobileMainMenu.HandleBackButton();

			if (UIDrawer.ActiveMenu == UIDrawer.MenuType.BottomBarMenuPopup)
			{
				UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);
				return true;
			}

			// Specialized menus already define their own Escape/cancel semantics.
			// Returning false lets Android Back flow through the existing shortcut path,
			// preserving save/discard/rollback behavior instead of force-closing state.
			if (UIDrawer.ActiveMenu != UIDrawer.MenuType.None)
				return false;

			Project project = Project.ActiveProject;
			if (project != null && project.chipViewStack.Count > 1)
			{
				project.ReturnToPreviousViewedChip();
				return true;
			}

			return false;
		}

		public static void OnMainMenuOpened()
		{
			MobileMainMenu.OnMenuOpened();
			panMode = PanByDefault;
		}

		public static void Reset()
		{
			panMode = PanByDefault;
			MobileMainMenu.Reset();
		}
	}
}
