using System;
using DLS.Description;
using DLS.Game;
using DLS.SaveSystem;
using Seb.Helpers.InputHandling;
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
		static readonly UIHandle ID_ChipScope = new("SaveMenu_ChipScope");
		static readonly Func<string, bool> chipNameValidator = ValidateChipNameInput;
		static readonly Random rng = new();
		static Vector2 sizeBeyondNameMinimum;
		static string saveErrorMessage = string.Empty;
		static readonly string[] ChipScopeOptions = { "THIS PROJECT", "ALL PROJECTS" };

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
			bool startingNewSaveSession = ActiveCustomizeChip == null;
			ActiveCustomizeChip ??= CreateCustomizationState();
			Vector2 currentMinimum = SubChipInstance.CalculateMinChipSize(
				ActiveCustomizeDescription.InputPins,
				ActiveCustomizeDescription.OutputPins,
				ActiveCustomizeDescription.Name);
			sizeBeyondNameMinimum = Vector2.Max(Vector2.zero, ActiveCustomizeDescription.Size - currentMinimum);
			InitUIFromDescription(ActiveCustomizeChip.Description);
			if (startingNewSaveSession)
			{
				saveErrorMessage = string.Empty;
				bool isGlobal = Project.ActiveProject.ChipHasBeenSavedBefore &&
				                Project.ActiveProject.chipLibrary.IsGlobalChip(Project.ActiveProject.ViewedChip.LastSavedDescription.Name);
				UI.GetWheelSelectorState(ID_ChipScope).index = isGlobal ? 1 : 0;
			}
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

				Vector2 scopeLabelPos = UI.PrevBounds.BottomLeft + Vector2.down * 1.35f;
				UI.DrawText("AVAILABILITY", theme.FontBold, theme.FontSizeRegular * 0.7f,
					scopeLabelPos, Anchor.TextCentreLeft, new Color(1, 1, 1, 0.72f));
				Vector2 scopeTopLeft = scopeLabelPos + Vector2.down * 1.25f;
				UI.WheelSelector(
					ID_ChipScope,
					ChipScopeOptions,
					scopeTopLeft,
					new Vector2(inputFieldSize.x, DrawSettings.ButtonHeight),
					theme.OptionsWheel,
					Anchor.TopLeft);

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

				if (!string.IsNullOrEmpty(saveErrorMessage))
				{
					Vector2 errorPos = UI.PrevBounds.BottomLeft + Vector2.down * 1.2f;
					string formatted = UI.LineBreakByCharCount(saveErrorMessage, 45);
					UI.DrawText(formatted, theme.FontRegular, theme.FontSizeRegular * 0.68f,
						errorPos, Anchor.TopLeft, new Color(1f, 0.45f, 0.4f));
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
			UI.DrawText("AVAILABILITY", theme.FontBold, theme.FontSizeRegular * 0.68f,
				topLeft + new Vector2(0.2f, -0.9f), Anchor.TextCentreLeft, new Color(1, 1, 1, 0.72f));
			float scopeWidth = Mathf.Min(25f, width * 0.48f);
			UI.WheelSelector(
				ID_ChipScope,
				ChipScopeOptions,
				new Vector2(topLeft.x + width, topLeft.y),
				new Vector2(scopeWidth, 4.3f),
				theme.OptionsWheel,
				Anchor.TopRight);

			topLeft.y -= 5.1f;
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
				string hint = SaveGlobally
					? "One shared copy will be available in every project."
					: "This copy is stored only in the current project.";
				UI.DrawText(hint, theme.FontRegular, theme.FontSizeRegular * 0.76f,
					topLeft + new Vector2(0.2f, -1.1f), Anchor.TextCentreLeft, RewiredUI.SecondaryText);
				if (!string.IsNullOrEmpty(saveErrorMessage))
				{
					UI.DrawText(UI.LineBreakByCharCount(saveErrorMessage, 70), theme.FontRegular,
						theme.FontSizeRegular * 0.68f, topLeft + new Vector2(0.2f, -3.0f),
						Anchor.TopLeft, new Color(1f, 0.45f, 0.4f));
				}
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


		static bool SaveGlobally => UI.GetWheelSelectorState(ID_ChipScope).index == 1;

		static void Save(Project.SaveMode mode)
		{
			if (Project.ActiveProject.TrySaveFromDescription(
				    ActiveCustomizeDescription,
				    mode,
				    SaveGlobally,
				    out string error))
			{
				CloseMenu();
			}
			else saveErrorMessage = error;
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
			saveErrorMessage = string.Empty;
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
