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
		public const float TopBarHeight = 5.4f;
		public const float DockHeight = 7.0f;

		const string PrefPanDefault = "rewired.mobile.panDefault";
		const string PrefTargetFps = "rewired.mobile.targetFps";

		static readonly string[] DockLabels = { "PAN", "ADD", "SAVE", "LIBRARY", "MORE" };
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
			DrawTopBar(project, safe);
			DrawDock(project, safe);

			if (UIDrawer.ActiveMenu == UIDrawer.MenuType.BottomBarMenuPopup)
			{
				DrawMoreSheet(project, safe);
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
			string chipName = string.IsNullOrWhiteSpace(project.ActiveDevChipName) ? "NEW CHIP" : project.ActiveDevChipName.ToUpperInvariant();
			string label = $"{project.description.ProjectName.ToUpperInvariant()}  /  {chipName}";
			UI.DrawText(label, theme.FontBold, theme.FontSizeRegular * 0.9f, new Vector2(textX, bar.Centre.y), Anchor.TextCentreLeft, Color.white);

			string mode = panMode ? "PAN MODE" : "SELECT MODE";
			Color modeCol = panMode ? RewiredUI.Accent : RewiredUI.SecondaryText;
			UI.DrawText(mode, theme.FontBold, theme.FontSizeRegular * 0.72f, bar.CentreRight + Vector2.left * 1.1f, Anchor.TextCentreRight, modeCol);

			if (project.simPaused)
			{
				UI.DrawText("PAUSED", theme.FontBold, theme.FontSizeRegular * 0.72f, bar.CentreRight + Vector2.left * 13f, Anchor.TextCentreRight, Color.yellow);
			}
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
				if (UI.Button(DockLabels[i], buttonTheme, new Vector2(x, safe.yMin + innerPad), new Vector2(buttonW, buttonH), enabled, false, false, Anchor.BottomLeft))
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
			float sheetW = Mathf.Min(52f, safe.width - 2f);
			float sheetH = Mathf.Min(35f, safe.height - TopBarHeight - DockHeight - 1.5f);
			Vector2 bottomRight = new(safe.xMax - 0.6f, safe.yMin + DockHeight + 0.5f);

			UI.StartNewLayer();
			UI.DrawPanel(bottomRight, new Vector2(sheetW, sheetH), RewiredUI.SurfaceRaised, Anchor.BottomRight);
			Bounds2D sheet = UI.PrevBounds;
			RewiredUI.DrawFrame(sheet);

			UI.DrawText("TOOLS", theme.FontBold, theme.FontSizeRegular * 1.05f, sheet.TopLeft + new Vector2(1.2f, -2.2f), Anchor.TextCentreLeft, Color.white);

			float x = sheet.Left + 1.0f;
			float y = sheet.Top - 4.3f;
			float w = sheet.Width - 2.0f;
			float h = 4.45f;
			float gap = 0.45f;

			if (UI.Button("NEW CHIP", theme.MainMenuButtonTheme, new Vector2(x, y), new Vector2(w, h), project.CanEditViewedChip, false, false, Anchor.TopLeft))
			{
				CreateNewChip(project);
			}
			y -= h + gap;

			if (UI.Button("RHDL STUDIO", theme.MainMenuButtonTheme, new Vector2(x, y), new Vector2(w, h), true, false, false, Anchor.TopLeft))
				UIDrawer.SetActiveMenu(UIDrawer.MenuType.RhdlStudio);
			y -= h + gap;

			if (UI.Button("PREFERENCES", theme.MainMenuButtonTheme, new Vector2(x, y), new Vector2(w, h), true, false, false, Anchor.TopLeft))
				UIDrawer.SetActiveMenu(UIDrawer.MenuType.Preferences);
			y -= h + gap;

			if (UI.Button("SIM DIAGNOSTICS", theme.MainMenuButtonTheme, new Vector2(x, y), new Vector2(w, h), true, false, false, Anchor.TopLeft))
				UIDrawer.SetActiveMenu(UIDrawer.MenuType.SimulationDiagnostics);
			y -= h + gap;

			DrawQuickChips(project, sheet, ref y, h, gap);

			if (UI.Button("MAIN MENU", theme.MenuPopupButtonTheme, sheet.BottomLeft + new Vector2(1f, 1f), new Vector2(w, h), true, false, false, Anchor.BottomLeft))
			{
				ExitToMainMenu(project);
			}
		}

		static void DrawQuickChips(Project project, Bounds2D sheet, ref float y, float h, float gap)
		{
			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			int count = 0;
			float x = sheet.Left + 1f;
			float width = sheet.Width - 2f;
			float buttonGap = 0.35f;
			float buttonW = (width - buttonGap * 2f) / 3f;

			foreach (StarredItem item in project.description.StarredList)
			{
				if (item.IsCollection) continue;
				if (!project.ViewedChip.CanAddSubchip(item.Name)) continue;
				if (count == 0)
				{
					y -= 0.3f;
					UI.DrawText("QUICK CHIPS", theme.FontBold, theme.FontSizeRegular * 0.72f, new Vector2(x, y), Anchor.TextCentreLeft, RewiredUI.SecondaryText);
					y -= 1.35f;
				}

				int col = count % 3;
				if (col == 0 && count > 0) y -= h + gap;
				Vector2 pos = new(x + col * (buttonW + buttonGap), y);
				if (UI.Button(item.Name.ToUpperInvariant(), theme.ChipButton, pos, new Vector2(buttonW, h), true, false, false, Anchor.TopLeft, true, 0.5f))
				{
					project.controller.StartPlacing(project.chipLibrary.GetChipDescription(item.Name));
					UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);
					return;
				}

				count++;
				if (count >= 6) break;
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
