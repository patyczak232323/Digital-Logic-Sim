using System;
using DLS.Description;
using DLS.Game;
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
		public const float TopBarHeight = 6.0f;
		public const float DockHeight = 7.0f;

		const string PrefPanDefault = "rewired.mobile.panDefault";
		const string PrefTargetFps = "rewired.mobile.targetFps";

		static readonly string[] DockLabels = { "MOVE", "ADD CHIP", "SAVE", "TOOLS" };
		static bool panMode;

		public static bool IsActive => MobileInputBridge.RuntimeEnabled || Application.isMobilePlatform;
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
			float dockTop = safe.yMin + DockHeight * pixelsPerUIUnit;
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

		public static void DrawLabel(string text, Vector2 pos, float width, float scale = 1f, Color? colour = null, bool bold = false)
		{
			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			FontType font = bold ? theme.FontBold : theme.FontRegular;
			float size = theme.FontSizeRegular * scale;
			UI.DrawText(UI.TruncateText(text, Mathf.Max(0, width), size, font), font, size, pos, Anchor.TextCentreLeft, colour ?? RewiredUI.SecondaryText);
		}

		public static void DrawParagraph(string text, Vector2 topLeft, float width, float scale = 0.85f, Color? colour = null)
		{
			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			float size = theme.FontSizeRegular * scale;
			int chars = Mathf.Max(1, Mathf.FloorToInt(width / UI.CalculateTextSize("M", size, theme.FontRegular).x));
			UI.DrawText(UI.LineBreakByCharCount(text, chars), theme.FontRegular, size, topLeft, Anchor.TopLeft, colour ?? RewiredUI.SecondaryText);
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
			}

			DrawModalBackButton(menu);
			ContextMenu.Update();
		}

		static void DrawProjectChrome(Project project)
		{
			Rect safe = SafeRectUI;
			using (UI.BeginDisabledScope(UIDrawer.ActiveMenu == UIDrawer.MenuType.BottomBarMenuPopup))
			{
				DrawTopBar(project, safe);
				DrawDock(project, safe);
			}

			if (UIDrawer.ActiveMenu == UIDrawer.MenuType.BottomBarMenuPopup)
			{
				DrawMoreSheet(project, safe);
			}
		}

		static void DrawTopBar(Project project, Rect safe)
		{
			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			UI.DrawPanel(new Vector2(safe.xMin, safe.yMax), new Vector2(safe.width, TopBarHeight), RewiredUI.SurfaceRaised, Anchor.TopLeft);
			Bounds2D bar = UI.PrevBounds;
			float x = bar.Left + 1.2f;
			if (project.chipViewStack.Count > 1)
			{
				if (UI.Button("BACK", theme.MenuButtonTheme, new Vector2(x, bar.Centre.y), new Vector2(10f, 4.8f), true, false, false, Anchor.CentreLeft))
					project.ReturnToPreviousViewedChip();
				x += 11f;
			}

			float right = bar.Right - 1.2f;
			if (UI.Button(project.simPaused ? "RUN" : "PAUSE", theme.MainMenuButtonTheme,
				new Vector2(right, bar.Centre.y), new Vector2(11f, 4.8f), true, false, false, Anchor.CentreRight))
			{
				project.description.Prefs_SimPaused = !project.simPaused;
				project.SaveCurrentProjectDescription();
			}
			right -= 11.6f;
			if (UI.Button("REDO", theme.MenuButtonTheme, new Vector2(right, bar.Centre.y), new Vector2(9f, 4.8f), project.CanEditViewedChip, false, false, Anchor.CentreRight))
			{
				project.controller.CancelEverything();
				project.ViewedChip.UndoController.TryRedo();
			}
			right -= 9.6f;
			if (UI.Button("UNDO", theme.MenuButtonTheme, new Vector2(right, bar.Centre.y), new Vector2(9f, 4.8f), project.CanEditViewedChip, false, false, Anchor.CentreRight))
			{
				project.controller.CancelEverything();
				project.ViewedChip.UndoController.TryUndo();
			}
			right -= 10.2f;

			string chipName = string.IsNullOrWhiteSpace(project.ActiveDevChipName) ? "Untitled chip" : project.ActiveDevChipName;
			DrawLabel(chipName, new Vector2(x, bar.Centre.y + 1f), right - x, 1.05f, Color.white, true);
			DrawLabel(project.description.ProjectName, new Vector2(x, bar.Centre.y - 1.25f), right - x, 0.78f, RewiredUI.SecondaryText);
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
				bool enabled = (i != 1 && i != 2) || project.CanEditViewedChip;
				string label = i == 0 && panMode ? "EDIT" : DockLabels[i];
				if (UI.Button(label, buttonTheme, new Vector2(x, safe.yMin + innerPad), new Vector2(buttonW, buttonH), enabled, false, false, Anchor.BottomLeft))
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
					ToggleDrawer();
					break;
			}
		}

		static void DrawMoreSheet(Project project, Rect safe)
		{
			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			Bounds2D sheet = GetToolsBounds(safe);
			UI.StartNewLayer();
			UI.DrawFullscreenPanel(new Color(0, 0, 0, 0.55f));
			Vector2 pointer = InputHelper.MousePos * (UI.Width / Screen.width);
			if (!new Rect(sheet.Left, sheet.Bottom, sheet.Width, sheet.Height).Contains(pointer) &&
			    InputHelper.IsMouseUpThisFrame(MouseButton.Left) &&
			    MobileInputBridge.TryConsumeUITap(new Vector2(Screen.width, Screen.height) * 0.5f, new Vector2(Screen.width, Screen.height)))
			{
				UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);
				return;
			}

			UI.DrawRoundedPanel(sheet.Centre, sheet.Size, 1f, RewiredUI.SurfaceRaised);
			DrawLabel("Tools", sheet.TopLeft + new Vector2(1.2f, -2.1f), sheet.Width - 15f, 1.1f, Color.white, true);
			if (UI.Button("CLOSE", theme.MenuButtonTheme, sheet.TopRight + new Vector2(-0.8f, -0.6f), new Vector2(11f, 4.2f), true, false, false, Anchor.TopRight))
			{
				UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);
				return;
			}

			string[] labels = { "NEW CHIP", "CHIP LIBRARY", "RHDL STUDIO", "PREFERENCES", "DIAGNOSTICS", "MAIN MENU" };
			const float gap = 0.65f;
			float h = Mathf.Min(5.2f, (sheet.Height - 5.8f - gap * 2f) / 3f);
			float w = (sheet.Width - 2.4f - gap) / 2f;
			for (int i = 0; i < labels.Length; i++)
			{
				Vector2 pos = sheet.TopLeft + new Vector2(1.2f + (i % 2) * (w + gap), -5f - (i / 2) * (h + gap));
				if (!UI.Button(labels[i], theme.MenuButtonTheme, pos, new Vector2(w, h), i != 0 || project.CanEditViewedChip, false, false, Anchor.TopLeft)) continue;
				switch (i)
				{
					case 0: CreateNewChip(project); break;
					case 1: UIDrawer.SetActiveMenu(UIDrawer.MenuType.ChipLibrary); break;
					case 2: UIDrawer.SetActiveMenu(UIDrawer.MenuType.RhdlStudio); break;
					case 3: UIDrawer.SetActiveMenu(UIDrawer.MenuType.Preferences); break;
					case 4: UIDrawer.SetActiveMenu(UIDrawer.MenuType.SimulationDiagnostics); break;
					case 5: ExitToMainMenu(project); break;
				}
				return;
			}
		}

		public static Bounds2D GetToolsBounds(Rect safe)
		{
			float width = Mathf.Min(64f, safe.width - 2.4f);
			float height = Mathf.Min(23.5f, safe.height - TopBarHeight - DockHeight - 1.2f);
			return Bounds2D.CreateFromTopLeftAndSize(
				new Vector2(safe.center.x - width * 0.5f, safe.yMin + DockHeight + 0.6f + height), new Vector2(width, height));
		}


		static void DrawModalBackButton(UIDrawer.MenuType menu)
		{
			// UnsavedChanges owns an explicit decision and should not be visually obscured.
			if (menu is UIDrawer.MenuType.UnsavedChanges or UIDrawer.MenuType.Search) return;

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
