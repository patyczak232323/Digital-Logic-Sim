using DLS.Game;
using Seb.Types;
using Seb.Vis;
using Seb.Vis.UI;
using UnityEngine;

namespace DLS.Graphics
{
	public static class ChipLabelMenu
	{
		const string MaxLabelLength = "MY LONG LABEL TEXT";
		static SubChipInstance subChip;
		static readonly UIHandle ID_NameField = new("ChipLabelMenu_NameField");

		public static void OnMenuOpened()
		{
			subChip = (SubChipInstance)ContextMenu.interactionContext;

			InputFieldState inputFieldState = UI.GetInputFieldState(ID_NameField);
			inputFieldState.SetText(subChip.Label);
			inputFieldState.SelectAll();
		}

		public static void DrawMenu()
		{
			UI.DrawFullscreenPanel(DrawSettings.ActiveUITheme.MenuBackgroundOverlayCol);
			float spacing = 0.8f;

			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			InputFieldTheme inputTheme = DrawSettings.ActiveUITheme.ChipNameInputField;
			Draw.ID panelID = UI.ReservePanel();

			using (UI.BeginBoundsScope(true))
			{
				Vector2 unpaddedSize = Draw.CalculateTextBoundsSize(MaxLabelLength, inputTheme.fontSize, inputTheme.font);
				const float padX = 2.25f;
				Vector2 inputFieldSize = unpaddedSize + new Vector2(padX, 2.25f);
				Vector2 pos = MenuHelper.PopupCentre(4);
				Vector2 headerTopLeft = new(pos.x - inputFieldSize.x / 2f, pos.y + inputFieldSize.y / 2f + 3f);
				RewiredUI.DrawSectionHeader("CHIP LABEL", headerTopLeft, inputFieldSize.x, true);

				// Draw input field
				InputFieldState inputFieldState = UI.InputField(ID_NameField, inputTheme, pos, inputFieldSize, subChip.Label, Anchor.Centre, padX / 2, ValidateNameInput, true);
				Bounds2D inputFieldBounds = UI.PrevBounds;
				string newName = inputFieldState.text;

				// Draw cancel/confirm buttons
				Vector2 buttonsTopLeft = UI.PrevBounds.BottomLeft + Vector2.down * spacing;
				MenuHelper.CancelConfirmResult result = MenuHelper.DrawCancelConfirmButtons(buttonsTopLeft, inputFieldBounds.Width, false, false);
				int buttonIndex = result == MenuHelper.CancelConfirmResult.Cancel ? 0 : result == MenuHelper.CancelConfirmResult.Confirm ? 1 : -1;

				MenuHelper.DrawReservedMenuPanel(panelID, UI.GetCurrentBoundsScope());

				// Keyboard shortcuts and UI input
				if (KeyboardShortcuts.CancelShortcutTriggered || buttonIndex == 0) Cancel();
				else if (KeyboardShortcuts.ConfirmShortcutTriggered || buttonIndex == 1) Confirm(newName);
			}
		}

		static void Confirm(string newName)
		{
			subChip.Label = newName;
			UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);
		}

		static void Cancel()
		{
			UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);
		}

		static bool ValidateNameInput(string name) => name.Length <= MaxLabelLength.Length;
	}
}