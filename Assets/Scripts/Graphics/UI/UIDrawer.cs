using DLS.Game;
using Seb.Vis.UI;
using UnityEngine;

namespace DLS.Graphics
{
	public static class UIDrawer
	{
		public enum MenuType
		{
			None,
			ChipSave,
			ChipLibrary,
			BottomBarMenuPopup,
			ChipCustomization,
			Preferences,
			SimulationDiagnostics,
			PinRename,
			MainMenu,
			RebindKeyChip,
			RomEdit,
			PulseEdit,
			UnsavedChanges,
			Search,
			ChipLabelPopup
		}

		static MenuType activeMenuOld;

		public static MenuType ActiveMenu { get; private set; }

		public static void Draw()
		{
			NotifyIfActiveMenuChanged();

			using (TouchUILayout.Enabled
				       ? UI.CreateUIScope()
				       : UI.CreateFixedAspectUIScope(drawLetterbox: true))
			{
				if (ActiveMenu is MenuType.MainMenu)
				{
					DrawAppMenus();
				}
				else
				{
					DrawProjectMenus(Project.ActiveProject);
				}
			}

			InteractionState.MouseIsOverUI = UI.IsMouseOverUIThisFrame;
		}

		static void DrawAppMenus()
		{
			MainMenu.Draw();
		}

		static void DrawProjectMenus(Project project)
		{
			MenuType menuToDraw = ActiveMenu; // cache state in case it changes while drawing/updating the menus

			if (menuToDraw != MenuType.ChipCustomization) BottomBarUI.DrawUI(project);

			if (menuToDraw == MenuType.ChipSave) ChipSaveMenu.DrawMenu();
			else if (menuToDraw == MenuType.ChipLibrary) ChipLibraryMenu.DrawMenu();
			else if (menuToDraw == MenuType.ChipCustomization) ChipCustomizationMenu.DrawMenu();
			else if (menuToDraw == MenuType.Preferences) PreferencesMenu.DrawMenu(project);
			else if (menuToDraw == MenuType.SimulationDiagnostics) SimulationDiagnosticsMenu.DrawMenu();
			else if (menuToDraw == MenuType.PinRename) PinEditMenu.DrawMenu();
			else if (menuToDraw == MenuType.RebindKeyChip) RebindKeyChipMenu.DrawMenu();
			else if (menuToDraw == MenuType.RomEdit) RomEditMenu.DrawMenu();
			else if (menuToDraw == MenuType.UnsavedChanges) UnsavedChangesPopup.DrawMenu();
			else if (menuToDraw == MenuType.Search) SearchPopup.DrawMenu();
			else if (menuToDraw == MenuType.ChipLabelPopup) ChipLabelMenu.DrawMenu();
			else if (menuToDraw == MenuType.PulseEdit) PulseEditMenu.DrawMenu();
			else
			{
				bool showSimPausedBanner = project.simPaused;
				if (showSimPausedBanner) SimPausedUI.DrawPausedBanner();
				if (project.chipViewStack.Count > 1) ViewedChipsBar.DrawViewedChipsBanner(project, showSimPausedBanner);
			}

			ContextMenu.Update();
		}

		public static bool InInputBlockingMenu() => !(ActiveMenu is MenuType.None or MenuType.BottomBarMenuPopup or MenuType.ChipCustomization);

		static void NotifyIfActiveMenuChanged()
		{
			// UI Changed -- notify opened
			if (ActiveMenu != activeMenuOld)
			{
				if (activeMenuOld == MenuType.ChipCustomization) CustomizationSceneDrawer.OnCustomizationMenuClosed();

				if (ActiveMenu == MenuType.ChipSave) ChipSaveMenu.OnMenuOpened();
				else if (ActiveMenu == MenuType.ChipLibrary) ChipLibraryMenu.OnMenuOpened();
				else if (ActiveMenu == MenuType.ChipCustomization) ChipCustomizationMenu.OnMenuOpened();
				else if (ActiveMenu == MenuType.PinRename) PinEditMenu.OnMenuOpened();
				else if (ActiveMenu == MenuType.Preferences) PreferencesMenu.OnMenuOpened();
				else if (ActiveMenu == MenuType.SimulationDiagnostics) SimulationDiagnosticsMenu.OnMenuOpened();
				else if (ActiveMenu == MenuType.MainMenu) MainMenu.OnMenuOpened();
				else if (ActiveMenu == MenuType.RebindKeyChip) RebindKeyChipMenu.OnMenuOpened();
				else if (ActiveMenu == MenuType.RomEdit) RomEditMenu.OnMenuOpened();
				else if (ActiveMenu == MenuType.Search) SearchPopup.OnMenuOpened();
				else if (ActiveMenu == MenuType.ChipLabelPopup) ChipLabelMenu.OnMenuOpened();
				else if (ActiveMenu == MenuType.PulseEdit) PulseEditMenu.OnMenuOpened();

				if (InInputBlockingMenu() && Project.ActiveProject != null && Project.ActiveProject.controller != null)
				{
					Project.ActiveProject.controller.CancelEverything();
				}

				activeMenuOld = ActiveMenu;
			}
		}

		public static void ToggleBottomPopupMenu()
		{
			SetActiveMenu(ActiveMenu is MenuType.None ? MenuType.BottomBarMenuPopup : MenuType.None);
		}

		public static void SetActiveMenu(MenuType type)
		{
			ActiveMenu = type;
		}

		public static void Reset()
		{
			SetActiveMenu(MenuType.None);
			activeMenuOld = MenuType.None;
			ContextMenu.Reset();
			UI.ResetAllStates();
			BottomBarUI.Reset();
			ChipSaveMenu.Reset();
			RomEditMenu.Reset();
			ChipLibraryMenu.Reset();
			SearchPopup.Reset();
		}
	}

	public static class TouchUILayout
	{
		// Android/mobile builds use a dedicated touch layout. This switch also lets
		// the same layout be exercised in the Unity editor without an APK build.
		public static bool ForceTouchLayoutForTesting;

		public static bool Enabled => ForceTouchLayoutForTesting || Application.isMobilePlatform;
		public static bool IsAndroid => Application.platform == RuntimePlatform.Android;
		public static bool IsPortrait => Screen.height > Screen.width;

		public const float BottomBarHeight = 5.8f;
		public const float TouchButtonHeight = 4.4f;
		public const float TouchGap = 0.45f;
		public const float EdgePadding = 0.8f;

		// UI units are scaled from screen width, so safe-area pixel offsets use the
		// same conversion. This keeps controls clear of camera cut-outs and gesture bars.
		public static float SafeLeft => Enabled && Screen.width > 0 ? Screen.safeArea.xMin / Screen.width * UI.Width : 0;
		public static float SafeRight => Enabled && Screen.width > 0 ? (Screen.width - Screen.safeArea.xMax) / Screen.width * UI.Width : 0;
		public static float SafeBottom => Enabled && Screen.width > 0 ? Screen.safeArea.yMin / Screen.width * UI.Width : 0;
		public static float SafeTop => Enabled && Screen.width > 0 ? (Screen.height - Screen.safeArea.yMax) / Screen.width * UI.Width : 0;
		public static float BottomBarTotalHeight => BottomBarHeight + SafeBottom;
	}
}