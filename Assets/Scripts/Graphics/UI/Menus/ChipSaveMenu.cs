using System;
using DLS.Description;
using DLS.Game;
using DLS.SaveSystem;
using Seb.Types;
using Seb.Vis;
using Seb.Vis.UI;
using UnityEngine;
using Random = System.Random;

namespace DLS.Graphics
{
	public static class ChipSaveMenu
	{
		public const string MaxLengthChipName = "MY VERY LONG CHIP NAME";

		const int CancelButtonIndex = 0;
		const int CustomizeButtonIndex = 1;
		const int SaveButtonIndex = 2;
		const int SaveAsButtonIndex = 3;
		static readonly UIHandle ID_ChipNameField = new("SaveMenu_ChipNameField");
		static readonly Func<string, bool> chipNameValidator = ValidateChipNameInput;
		static readonly Random rng = new();
		static Vector2 sizeBeyondNameMinimum;

		public static SubChipInstance ActiveCustomizeChip;
		static SubChipInstance CustomizeStateBeforeEnteringCustomizeMenu;

		static readonly string[] CancelSaveButtonNames =
		{
			"CANCEL", "CUSTOMIZE", "SAVE"
		};

		static readonly string[] CancelRenameSaveButtonNames =
		{
			"CANCEL", "CUSTOMIZE", "RENAME", "SAVE AS"
		};

		static readonly bool[] ButtonGroupInteractStates = { true, true, true, true };
		public static ChipDescription ActiveCustomizeDescription => ActiveCustomizeChip.Description;

		public static void OnMenuOpened()
		{
			ActiveCustomizeChip ??= CreateCustomizationState();
			Vector2 currentMinimum = SubChipInstance.CalculateMinChipSize(
				ActiveCustomizeDescription.InputPins,
				ActiveCustomizeDescription.OutputPins,
				ActiveCustomizeDescription.Name);
			sizeBeyondNameMinimum = Vector2.Max(Vector2.zero, ActiveCustomizeDescription.Size - currentMinimum);
			InitUIFromDescription(ActiveCustomizeChip.Description);
		}

		public static (Vector2 size, float pad) GetTextInputSize()
		{
			const float textPad = 2;
			InputFieldTheme inputTheme = DrawSettings.ActiveUITheme.ChipNameInputField;
			Vector2 inputFieldSize = UI.CalculateTextSize(MaxLengthChipName, inputTheme.fontSize, inputTheme.font) + new Vector2(textPad * 2, 3);
			return (inputFieldSize, textPad);
		}

		public static void DrawMenu()
		{
			if (MobileUI.IsActive)
			{
				DrawMobileMenu();
				return;
			}

			MenuHelper.DrawBackgroundOverlay();

			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			InputFieldTheme inputTheme = DrawSettings.ActiveUITheme.ChipNameInputField;
			InputFieldState inputFieldState;

			using (UI.BeginBoundsScope(true))
			{
				Draw.ID panelID = UI.ReservePanel();

				// -- Chip name input field --
				(Vector2 inputFieldSize, float inputFieldTextPad) = GetTextInputSize();
				Vector2 inputCentre = new(50, 32);
				Vector2 headerTopLeft = new(inputCentre.x - inputFieldSize.x / 2f, inputCentre.y + inputFieldSize.y / 2f + 3f);
				RewiredUI.DrawSectionHeader("SAVE CHIP", headerTopLeft, inputFieldSize.x, true);
				inputFieldState = UI.InputField(ID_ChipNameField, inputTheme, inputCentre, inputFieldSize, "Name", Anchor.Centre, inputFieldTextPad, chipNameValidator, true);

				Vector2 buttonTopLeft = UI.PrevBounds.BottomLeft + Vector2.down * (DrawSettings.DefaultButtonSpacing * 2);
				bool renaming = Project.ActiveProject.ChipHasBeenSavedBefore &&
				                !string.Equals(inputFieldState.text, Project.ActiveProject.ViewedChip.LastSavedDescription.Name, StringComparison.Ordinal);

				bool saveButtonEnabled = IsValidSaveName(inputFieldState.text);
				ButtonGroupInteractStates[SaveButtonIndex] = saveButtonEnabled;
				ButtonGroupInteractStates[SaveAsButtonIndex] = saveButtonEnabled;
				string[] buttonGroupNames = renaming ? CancelRenameSaveButtonNames : CancelSaveButtonNames;
				int buttonIndex = UI.HorizontalButtonGroup(buttonGroupNames, ButtonGroupInteractStates, theme.ButtonTheme, buttonTopLeft, UI.PrevBounds.Width, DrawSettings.DefaultButtonSpacing, 0, Anchor.TopLeft);
				bool confirmShortcut = !renaming && saveButtonEnabled && KeyboardShortcuts.ConfirmShortcutTriggered;

				if (buttonIndex == CancelButtonIndex || KeyboardShortcuts.CancelShortcutTriggered)
				{
					Cancel();
				}
				else if (buttonIndex == CustomizeButtonIndex)
				{
					OpenCustomizationMenu();
				}
				else if (buttonIndex == SaveButtonIndex || confirmShortcut)
				{
					Save(renaming ? Project.SaveMode.Rename : Project.SaveMode.Normal);
				}
				else if (buttonIndex == SaveAsButtonIndex)
				{
					Save(Project.SaveMode.SaveAs);
				}

				Bounds2D uiBounds = UI.GetCurrentBoundsScope();
				MenuHelper.DrawReservedMenuPanel(panelID, uiBounds);

				// Update customization state
				if (ActiveCustomizeChip != null)
				{
					string newName = inputFieldState.text;
					if (ActiveCustomizeDescription.Name != newName)
					{
						ActiveCustomizeDescription.Name = newName;
						Vector2 minChipSize = SubChipInstance.CalculateMinChipSize(ActiveCustomizeDescription.InputPins, ActiveCustomizeDescription.OutputPins, newName);
						ActiveCustomizeDescription.Size = minChipSize + sizeBeyondNameMinimum;
					}
				}
			}
		}


		static void DrawMobileMenu()
		{
			MenuHelper.DrawBackgroundOverlay();
			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			Rect safe = MobileUI.SafeRectUI;
			float pad = 1.2f;
			float width = safe.width - pad * 2f;
			Vector2 topLeft = new(safe.xMin + pad, safe.yMax - 7.2f);

			UI.DrawText("SAVE CHIP", theme.FontBold, theme.FontSizeRegular * 1.1f,
				topLeft + new Vector2(12.5f, -1f), Anchor.TextCentreLeft, Color.white);

			topLeft.y -= 3.2f;
			InputFieldTheme inputTheme = theme.ChipNameInputField;
			InputFieldState inputFieldState = UI.InputField(
				ID_ChipNameField,
				inputTheme,
				topLeft,
				new Vector2(width, 6.3f),
				"CHIP NAME",
				Anchor.TopLeft,
				1.2f,
				chipNameValidator,
				true);

			string newName = inputFieldState.text;
			bool renaming = Project.ActiveProject.ChipHasBeenSavedBefore &&
			                !string.Equals(newName, Project.ActiveProject.ViewedChip.LastSavedDescription.Name, StringComparison.Ordinal);
			bool canSave = IsValidSaveName(newName);

			topLeft = UI.PrevBounds.BottomLeft + Vector2.down * 0.8f;
			float gap = 0.5f;
			float buttonHeight = MobileInputBridge.KeyboardVisible ? 4.8f : 5.5f;

			if (renaming)
			{
				float third = (width - gap * 2f) / 3f;
				if (UI.Button("CUSTOMIZE", theme.MenuButtonTheme, topLeft, new Vector2(third, buttonHeight),
					true, false, false, Anchor.TopLeft))
				{
					OpenCustomizationMenu();
					return;
				}

				if (UI.Button("RENAME", theme.MainMenuButtonTheme,
					topLeft + Vector2.right * (third + gap), new Vector2(third, buttonHeight),
					canSave, false, false, Anchor.TopLeft))
				{
					Save(Project.SaveMode.Rename);
					return;
				}

				if (UI.Button("SAVE AS", theme.MenuButtonTheme,
					topLeft + Vector2.right * ((third + gap) * 2f), new Vector2(third, buttonHeight),
					canSave, false, false, Anchor.TopLeft))
				{
					Save(Project.SaveMode.SaveAs);
					return;
				}
			}
			else
			{
				float half = (width - gap) / 2f;
				if (UI.Button("CUSTOMIZE", theme.MenuButtonTheme, topLeft, new Vector2(half, buttonHeight),
					true, false, false, Anchor.TopLeft))
				{
					OpenCustomizationMenu();
					return;
				}

				if (UI.Button("SAVE", theme.MainMenuButtonTheme,
					topLeft + Vector2.right * (half + gap), new Vector2(half, buttonHeight),
					canSave, false, false, Anchor.TopLeft))
				{
					Save(Project.SaveMode.Normal);
					return;
				}
			}

			topLeft.y -= buttonHeight + gap;
			if (!MobileInputBridge.KeyboardVisible)
			{
				string hint = Project.ActiveProject.ChipHasBeenSavedBefore
					? "Change the name to rename or save a copy."
					: "Choose a name, optionally customize the chip, then save.";
				UI.DrawText(hint, theme.FontRegular, theme.FontSizeRegular * 0.76f,
					topLeft + new Vector2(0.2f, -1.1f), Anchor.TextCentreLeft, RewiredUI.SecondaryText);
			}

			if (KeyboardShortcuts.CancelShortcutTriggered)
			{
				Cancel();
				return;
			}

			if (!renaming && canSave && KeyboardShortcuts.ConfirmShortcutTriggered)
			{
				Save(Project.SaveMode.Normal);
				return;
			}

			UpdateCustomizationName(newName);
		}

		static void UpdateCustomizationName(string newName)
		{
			if (ActiveCustomizeChip == null || ActiveCustomizeDescription.Name == newName) return;
			ActiveCustomizeDescription.Name = newName;
			Vector2 minChipSize = SubChipInstance.CalculateMinChipSize(
				ActiveCustomizeDescription.InputPins,
				ActiveCustomizeDescription.OutputPins,
				newName);
			ActiveCustomizeDescription.Size = minChipSize + sizeBeyondNameMinimum;
		}


		// Create a subchip instance based on the current dev chip (we need a subchip instance to be able to draw a preview of the chip in the customization menu)
		// The description on this subchip holds potential customizations, such as name changes, resizing, colour etc.
		static SubChipInstance CreateCustomizationState()
		{
			ChipDescription desc = DescriptionCreator.CreateChipDescription(Project.ActiveProject.ViewedChip);
			return CreatePreviewSubChipInstance(desc);
		}

		static void OpenCustomizationMenu()
		{
			ActiveCustomizeChip = CreatePreviewSubChipInstance(ActiveCustomizeDescription);
			CustomizeStateBeforeEnteringCustomizeMenu = CreatePreviewSubChipInstance(Saver.CloneChipDescription(ActiveCustomizeDescription));
			UIDrawer.SetActiveMenu(UIDrawer.MenuType.ChipCustomization);
		}

		public static void RevertCustomizationStateToBeforeEnteringCustomizeMenu()
		{
			ActiveCustomizeChip = CustomizeStateBeforeEnteringCustomizeMenu;
			CustomizeStateBeforeEnteringCustomizeMenu = CreatePreviewSubChipInstance(Saver.CloneChipDescription(ActiveCustomizeDescription));
		}

		static SubChipInstance CreatePreviewSubChipInstance(ChipDescription desc)
		{
			SubChipDescription subChipDesc = new(desc.Name, 0, string.Empty, Vector2.zero, Array.Empty<OutputPinColourInfo>());
			return new SubChipInstance(desc, subChipDesc);
		}

		public static bool ValidateChipNameInput(string nameInput) => nameInput.Length <= MaxLengthChipName.Length && !SaveUtils.NameContainsForbiddenChar(nameInput);

		static bool IsValidSaveName(string chipName)
		{
			Project project = Project.ActiveProject;

			bool validName = !string.IsNullOrWhiteSpace(chipName) && SaveUtils.ValidFileName(chipName);
			bool nameAlreadyUsed = project.chipLibrary.HasChip(chipName);
			bool isNameOfActiveChip = ChipDescription.NameMatch(project.ActiveDevChipName, chipName);

			bool isValid = validName && (!nameAlreadyUsed || isNameOfActiveChip);

			return isValid;
		}

		static void InitUIFromDescription(ChipDescription chipDesc)
		{
			// Set input field to current chip name
			InputFieldState inputFieldState = UI.GetInputFieldState(ID_ChipNameField);
			inputFieldState.SetText(chipDesc.Name);
		}


		static void Save(Project.SaveMode mode)
		{
			Project.ActiveProject.SaveFromDescription(ActiveCustomizeDescription, mode);
			CloseMenu();
		}

		static void Cancel()
		{
			CloseMenu();
		}

		static void CloseMenu()
		{
			ActiveCustomizeChip = null;
			UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);
		}

		public static void Reset()
		{
			ActiveCustomizeChip = null;
			CustomizeStateBeforeEnteringCustomizeMenu = null;
		}

		static Color RandomInitialColour()
		{
			float h = (float)rng.NextDouble();
			float s = Mathf.Lerp(0.2f, 1, (float)rng.NextDouble());
			float v = Mathf.Lerp(0.2f, 1, (float)rng.NextDouble());
			return Color.HSVToRGB(h, s, v);
		}
	}
}
