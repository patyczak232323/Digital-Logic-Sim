using DLS.Description;
using Seb.Helpers;
using UnityEngine;

namespace DLS.Game
{
	public static class KeyboardShortcuts
	{
		// ---- Main Menu shortcuts
		public static bool MainMenu_NewProjectShortcutTriggered => Triggered(ShortcutAction.MainMenuNewProject);
		public static bool MainMenu_OpenProjectShortcutTriggered => Triggered(ShortcutAction.MainMenuOpenProject);
		public static bool MainMenu_SettingsShortcutTriggered => Triggered(ShortcutAction.MainMenuSettings);
		public static bool MainMenu_QuitShortcutTriggered => Triggered(ShortcutAction.MainMenuQuit);

		// ---- Bottom Bar Menu shortcuts ----
		public static bool SaveShortcutTriggered => Triggered(ShortcutAction.Save);
		public static bool LibraryShortcutTriggered => Triggered(ShortcutAction.ChipLibrary);
		public static bool PreferencesShortcutTriggered => Triggered(ShortcutAction.Preferences);
		public static bool CreateNewChipShortcutTriggered => Triggered(ShortcutAction.CreateNewChip);
		public static bool QuitToMainMenuShortcutTriggered => Triggered(ShortcutAction.QuitToMainMenu);
		public static bool SearchShortcutTriggered => Triggered(ShortcutAction.Search);

		// ---- Editing shortcuts ----
		public static bool DuplicateShortcutTriggered => Triggered(ShortcutAction.Duplicate);
		public static bool ToggleGridShortcutTriggered => Triggered(ShortcutAction.ToggleGrid);
		public static bool ResetCameraShortcutTriggered => Triggered(ShortcutAction.ResetCamera);
		public static bool UndoShortcutTriggered => Triggered(ShortcutAction.Undo);
		public static bool RedoShortcutTriggered => Triggered(ShortcutAction.Redo);
		public static bool MirrorHorizontalShortcutTriggered => Triggered(ShortcutAction.MirrorHorizontal);
		public static bool MirrorVerticalShortcutTriggered => Triggered(ShortcutAction.MirrorVertical);

		// ---- Single key / simulation shortcuts ----
		// Cancel/confirm/delete remain fixed safety/UI controls.
		public static bool CancelShortcutTriggered => InputHelper.IsKeyDownThisFrame(KeyCode.Escape);
		public static bool ConfirmShortcutTriggered => InputHelper.IsKeyDownThisFrame(KeyCode.Return) || InputHelper.IsKeyDownThisFrame(KeyCode.KeypadEnter);
		public static bool DeleteShortcutTriggered => InputHelper.IsKeyDownThisFrame(KeyCode.Backspace) || InputHelper.IsKeyDownThisFrame(KeyCode.Delete);
		public static bool SimNextStepShortcutTriggered => Triggered(ShortcutAction.SimulationNextStep);
		public static bool SimPauseToggleShortcutTriggered => Triggered(ShortcutAction.SimulationPause);

		// ---- Dev shortcuts ----
		public static bool OpenSaveDataFolderShortcutTriggered => InputHelper.IsKeyDownThisFrame(KeyCode.O) && InputHelper.CtrlIsHeld && InputHelper.ShiftIsHeld && InputHelper.AltIsHeld;

		// ---- Modifiers ----
		public static bool SnapModeHeld => InputHelper.CtrlIsHeld;
		public static bool MultiModeHeld => InputHelper.AltIsHeld || InputHelper.ShiftIsHeld;
		public static bool StraightLineModeHeld => InputHelper.ShiftIsHeld;
		public static bool StraightLineModeTriggered => InputHelper.IsKeyDownThisFrame(KeyCode.LeftShift);
		public static bool CameraActionKeyHeld => InputHelper.AltIsHeld;
		public static bool TakeFirstFromCollectionModifierHeld => InputHelper.CtrlIsHeld || InputHelper.AltIsHeld || InputHelper.ShiftIsHeld;

		public static ShortcutBinding GetBinding(ShortcutAction action)
		{
			AppSettings settings = Main.ActiveAppSettings;
			return settings.GetBinding(action);
		}

		public static string GetBindingDisplayString(ShortcutAction action) => GetBinding(action).ToDisplayString();

		public static string GetActionDisplayName(ShortcutAction action) =>
			action switch
			{
				ShortcutAction.MainMenuNewProject => "New Project",
				ShortcutAction.MainMenuOpenProject => "Open Project",
				ShortcutAction.MainMenuSettings => "Settings",
				ShortcutAction.MainMenuQuit => "Quit",
				ShortcutAction.Save => "Save",
				ShortcutAction.ChipLibrary => "Chip Library",
				ShortcutAction.Preferences => "Preferences",
				ShortcutAction.CreateNewChip => "Create New Chip",
				ShortcutAction.QuitToMainMenu => "Quit To Main Menu",
				ShortcutAction.Search => "Search",
				ShortcutAction.Duplicate => "Duplicate",
				ShortcutAction.ToggleGrid => "Toggle Grid",
				ShortcutAction.ResetCamera => "Reset Camera",
				ShortcutAction.Undo => "Undo",
				ShortcutAction.Redo => "Redo",
				ShortcutAction.SimulationNextStep => "Simulation Step",
				ShortcutAction.SimulationPause => "Pause / Resume Simulation",
				ShortcutAction.MirrorHorizontal => "Mirror Horizontal",
				ShortcutAction.MirrorVertical => "Mirror Vertical",
				_ => action.ToString()
			};

		static bool Triggered(ShortcutAction action)
		{
			ShortcutBinding binding = GetBinding(action);
			if (!binding.IsBound || !InputHelper.IsKeyDownThisFrame(binding.Key)) return false;

			return InputHelper.CtrlIsHeld == binding.Ctrl &&
			       InputHelper.ShiftIsHeld == binding.Shift &&
			       InputHelper.AltIsHeld == binding.Alt;
		}
	}
}
