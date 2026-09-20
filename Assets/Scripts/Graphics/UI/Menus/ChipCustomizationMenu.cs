using System;
using System.Collections.Generic;
using System.Linq;
using DLS.Description;
using DLS.Game;
using DLS.Simulation;
using Seb.Helpers;
using Seb.Vis;
using Seb.Vis.UI;
using UnityEngine;

namespace DLS.Graphics
{
	public static class ChipCustomizationMenu
	{
		static readonly string[] nameDisplayOptions =
		{
			"Name: Middle",
			"Name: Top",
			"Name: Hidden"
		};

		static readonly string[] cacheModeOptions = { "AUTO", "NORMAL", "FULL" };


		// ---- State ----
		static SubChipInstance[] subChipsWithDisplays;
		static string displayLabelString;
		static string colHexCodeString;
		static ChipCacheAnalysis cacheAnalysis;
		static PersistentCacheInfo persistentCacheInfo;
		static int nestedCacheableCount;

		static readonly UIHandle ID_DisplaysScrollView = new("CustomizeMenu_DisplaysScroll");
		static readonly UIHandle ID_ColourPicker = new("CustomizeMenu_ChipCol");
		static readonly UIHandle ID_ColourHexInput = new("CustomizeMenu_ChipColHexInput");
		static readonly UIHandle ID_NameDisplayOptions = new("CustomizeMenu_NameDisplayOptions");
		static readonly UIHandle ID_CacheMode = new("CustomizeMenu_CacheMode");
		static readonly UI.ScrollViewDrawElementFunc drawDisplayScrollEntry = DrawDisplayScroll;
		static readonly Func<string, bool> hexStringInputValidator = ValidateHexStringInput;

		public static void OnMenuOpened()
		{
			DevChipInstance chip = Project.ActiveProject.ViewedChip;
			subChipsWithDisplays = chip.GetSubchips().Where(c => c.Description.HasDisplay()).OrderBy(c => c.Position.x).ThenBy(c => c.Position.y).ToArray();
			CustomizationSceneDrawer.OnCustomizationMenuOpened();
			displayLabelString = $"DISPLAYS ({subChipsWithDisplays.Length}):";

			cacheAnalysis = CombinationalChipCacheManager.Analyze(
				ChipSaveMenu.ActiveCustomizeDescription,
				Project.ActiveProject.chipLibrary);
			persistentCacheInfo = CombinationalChipCacheManager.GetPersistentCacheInfo(
				ChipSaveMenu.ActiveCustomizeDescription,
				Project.ActiveProject.chipLibrary);
			nestedCacheableCount = CombinationalChipCacheManager.CountCacheableNestedDescriptions(
				ChipSaveMenu.ActiveCustomizeDescription,
				Project.ActiveProject.chipLibrary);
			InitUIFromChipDescription();
		}

		public static void DrawMenu()
		{
			// Don't draw menu when placing display
			if (CustomizationSceneDrawer.IsPlacingDisplay) return;

			const float width = 20;
			const float pad = UILayoutHelper.DefaultSpacing;
			const float pw = width - pad * 2;

			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			UI.DrawPanel(UI.TopLeft, new Vector2(width, UI.Height), theme.MenuPanelCol, Anchor.TopLeft);

			// ---- Cancel/confirm buttons ----
			int cancelConfirmButtonIndex = MenuHelper.DrawButtonPair("CANCEL", "CONFIRM", UI.TopLeft + Vector2.down * pad, pw, false);

			// ---- Chip name UI ----
			int nameDisplayMode = UI.WheelSelector(ID_NameDisplayOptions, nameDisplayOptions, NextPos(), new Vector2(pw, DrawSettings.ButtonHeight), theme.OptionsWheel, Anchor.TopLeft);
			ChipSaveMenu.ActiveCustomizeDescription.NameLocation = (NameDisplayLocation)nameDisplayMode;

			// ---- Simulation cache UI ----
			int cacheModeIndex = MenuHelper.LabeledOptionsWheel(
				"SIM CACHE",
				Color.white,
				NextPos(),
				new Vector2(pw, DrawSettings.ButtonHeight),
				ID_CacheMode,
				cacheModeOptions,
				7,
				true);
			ChipSaveMenu.ActiveCustomizeDescription.CacheMode = (ChipCacheMode)cacheModeIndex;

			DrawCacheInfoPanel(theme);

			// ---- Chip colour UI ----
			Color newCol = UI.DrawColourPicker(ID_ColourPicker, NextPos(), pw, Anchor.TopLeft);
			InputFieldTheme inputTheme = MenuHelper.Theme.ChipNameInputField;
			inputTheme.fontSize = MenuHelper.Theme.FontSizeRegular;

			InputFieldState hexColInput = UI.InputField(ID_ColourHexInput, inputTheme, NextPos(), new Vector2(pw, DrawSettings.ButtonHeight), "#", Anchor.TopLeft, 1, hexStringInputValidator);

			if (newCol != ChipSaveMenu.ActiveCustomizeDescription.Colour)
			{
				ChipSaveMenu.ActiveCustomizeDescription.Colour = newCol;
				UpdateChipColHexStringFromColour(newCol);
			}
			else if (colHexCodeString != hexColInput.text)
			{
				UpdateChipColFromHexString(hexColInput.text);
			}

			// ---- Displays UI ----
			Color labelCol = ColHelper.Darken(theme.MenuPanelCol, 0.01f);
			Vector2 labelPos = NextPos(1);
			UI.TextWithBackground(labelPos, new Vector2(pw, DrawSettings.ButtonHeight), Anchor.TopLeft, displayLabelString, theme.FontBold, theme.FontSizeRegular, Color.white, labelCol);

			float scrollViewHeight = 20;
			float scrollViewSpacing = UILayoutHelper.DefaultSpacing;
			UI.DrawScrollView(ID_DisplaysScrollView, NextPos(), new Vector2(pw, scrollViewHeight), scrollViewSpacing, Anchor.TopLeft, theme.ScrollTheme, drawDisplayScrollEntry, subChipsWithDisplays.Length);

			Vector2 NextPos(float extraPadding = 0)
			{
				return UI.PrevBounds.BottomLeft + Vector2.down * (pad + extraPadding);
			}

			// Cancel
			if (cancelConfirmButtonIndex == 0)
			{
				RevertChanges();
				UIDrawer.SetActiveMenu(UIDrawer.MenuType.ChipSave);
			}
			// Confirm
			else if (cancelConfirmButtonIndex == 1)
			{
				UpdateCustomizeDescription();
				UIDrawer.SetActiveMenu(UIDrawer.MenuType.ChipSave);
			}
		}

		static void DrawDisplayScroll(Vector2 pos, float width, int i, bool isLayoutPass)
		{
			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;

			SubChipInstance subChip = subChipsWithDisplays[i];
			ChipDescription chipDesc = subChip.Description;
			string label = subChip.Label;
			string displayName = string.IsNullOrWhiteSpace(label) ? chipDesc.Name : label;

			// Don't allow adding same display multiple times
			bool enabled = CustomizationSceneDrawer.SelectedDisplay == null || subChip.ID != CustomizationSceneDrawer.SelectedDisplay.Desc.SubChipID; // display is removed from list when selected, so check manually here
			foreach (DisplayInstance d in ChipSaveMenu.ActiveCustomizeChip.Displays)
			{
				if (d.Desc.SubChipID == subChip.ID)
				{
					enabled = false;
					break;
				}
			}

			// Display selected, start placement
			if (UI.Button(displayName, theme.ButtonTheme, pos, new Vector2(width, 0), enabled, false, true, Anchor.TopLeft))
			{
				SubChipDescription subChipDesc = new(chipDesc.Name, subChipsWithDisplays[i].ID, string.Empty, Vector2.zero, null);
				SubChipInstance instance = new(chipDesc, subChipDesc);
				CustomizationSceneDrawer.StartPlacingDisplay(instance);
			}
		}

		static void RevertChanges()
		{
			ChipSaveMenu.RevertCustomizationStateToBeforeEnteringCustomizeMenu();
			InitUIFromChipDescription();
		}

		static void InitUIFromChipDescription()
		{
			// Init col picker to chip colour
			ColourPickerState chipColourPickerState = UI.GetColourPickerState(ID_ColourPicker);
			Color.RGBToHSV(ChipSaveMenu.ActiveCustomizeDescription.Colour, out chipColourPickerState.hue, out chipColourPickerState.sat, out chipColourPickerState.val);
			UpdateChipColHexStringFromColour(chipColourPickerState.GetRGB());

			// Init name display mode
			WheelSelectorState nameDisplayWheelState = UI.GetWheelSelectorState(ID_NameDisplayOptions);
			nameDisplayWheelState.index = (int)ChipSaveMenu.ActiveCustomizeDescription.NameLocation;
			UI.GetWheelSelectorState(ID_CacheMode).index = (int)ChipSaveMenu.ActiveCustomizeDescription.CacheMode;
		}


		static void DrawCacheInfoPanel(DrawSettings.UIThemeDLS theme)
		{
			const float panelWidth = 35;
			const float panelHeight = 24;
			const float rowHeight = 3.1f;
			const float pad = UILayoutHelper.DefaultSpacing;

			Vector2 panelTopRight = UI.TopRight + Vector2.left * pad + Vector2.down * pad;
			UI.DrawPanel(panelTopRight, new Vector2(panelWidth, panelHeight), theme.MenuPanelCol, Anchor.TopRight);

			float contentWidth = panelWidth - pad * 2;
			Vector2 rowPos = panelTopRight + Vector2.left * (panelWidth - pad) + Vector2.down * pad;
			Color rowCol = ColHelper.Darken(theme.MenuPanelCol, 0.01f);

			DrawRow("CACHE INFO", theme.FontBold, 0.92f);
			DrawRow(GetCacheHeadline(), theme.FontBold, 0.72f);

			GetCacheReasonLines(out string reasonA, out string reasonB);
			DrawRow(reasonA, theme.FontRegular, 0.56f);
			DrawRow(reasonB, theme.FontRegular, 0.56f);

			DrawRow(GetCacheRamLine(), theme.FontRegular, 0.62f);
			DrawRow(GetCacheDiskLine(), theme.FontRegular, 0.62f);

			void DrawRow(string text, FontType font, float fontScale)
			{
				UI.TextWithBackground(
					rowPos,
					new Vector2(contentWidth, rowHeight),
					Anchor.TopLeft,
					text,
					font,
					theme.FontSizeRegular * fontScale,
					Color.white,
					rowCol);
				rowPos += Vector2.down * (rowHeight + pad * 0.35f);
			}
		}

		static string GetCacheHeadline()
		{
			ChipCacheMode mode = ChipSaveMenu.ActiveCustomizeDescription.CacheMode;
			if (mode == ChipCacheMode.Normal) return "SIM CACHE: OFF";

			if (cacheAnalysis.CanCache &&
			    CombinationalChipCacheManager.CanBuildFullCache(
				    ChipSaveMenu.ActiveCustomizeDescription,
				    cacheAnalysis,
				    out _))
			{
				return $"FULL LUT: ENABLED ({cacheAnalysis.InputBitCount} bits)";
			}

			if (nestedCacheableCount > 0) return $"CACHE: HYBRID ({nestedCacheableCount} LUT)";
			return "CACHE: STATEFUL / LIVE";
		}

		static void GetCacheReasonLines(out string first, out string second)
		{
			ChipCacheMode mode = ChipSaveMenu.ActiveCustomizeDescription.CacheMode;
			string text;

			if (mode == ChipCacheMode.Normal)
			{
				text = "Reason: disabled by user";
			}
			else if (!cacheAnalysis.CanCache)
			{
				text = nestedCacheableCount > 0
					? "Root stays live (state/feedback); safe nested combinational chips use LUTs"
					: "State/feedback logic stays live; an input-only LUT would be incorrect";
			}
			else if (!CombinationalChipCacheManager.CanBuildFullCache(
				         ChipSaveMenu.ActiveCustomizeDescription,
				         cacheAnalysis,
				         out string reason))
			{
				text = "Reason: " + reason;
			}
			else
			{
				text = "Mode: full binary LUT; build/load runs in background";
			}

			SplitPanelText(text, 48, out first, out second);
		}

		static string GetCacheRamLine()
		{
			ChipCacheMode mode = ChipSaveMenu.ActiveCustomizeDescription.CacheMode;
			if (mode == ChipCacheMode.Normal) return "RAM: disabled";

			if (!cacheAnalysis.CanCache ||
			    !CombinationalChipCacheManager.CanBuildFullCache(
				    ChipSaveMenu.ActiveCustomizeDescription,
				    cacheAnalysis,
				    out _))
			{
				return nestedCacheableCount > 0
					? $"RAM: {nestedCacheableCount} nested LUT(s), background"
					: "RAM: normal live simulation";
			}

			ChipDescription saved = Project.ActiveProject.ViewedChip.LastSavedDescription;
			if (saved != null &&
			    CombinationalChipCacheManager.TryGetStats(saved, out int entries, out int target) &&
			    CombinationalChipCacheManager.TryGetBuildInfo(
				    saved,
				    out bool ready,
				    out double buildMs,
				    out long bytes,
				    out string failure))
			{
				if (!string.IsNullOrWhiteSpace(failure)) return "RAM: build failed";
				if (ready)
				{
					string timing = buildMs > 0.01 ? $" | build {buildMs:0.0} ms" : string.Empty;
					return $"RAM: {entries:N0}/{target:N0} | {CombinationalChipCacheManager.FormatBytes(bytes)}{timing}";
				}

				if (target > 0)
				{
					if (CombinationalChipCacheManager.TryGetRuntimePersistenceInfo(
						    saved,
						    out _,
						    out _,
						    out string runtimeStatus) &&
					    runtimeStatus == "QUEUED")
					{
						return $"RAM: queued {entries:N0}/{target:N0} (background)";
					}

					return $"RAM: building {entries:N0}/{target:N0} (background)";
				}
			}

			long estimated = CombinationalChipCacheManager.GetEstimatedMemoryBytes(
				ChipSaveMenu.ActiveCustomizeDescription,
				cacheAnalysis);
			return $"RAM: queued | ~{CombinationalChipCacheManager.FormatBytes(estimated)}";
		}

		static string GetCacheDiskLine()
		{
			if (!cacheAnalysis.CanCache ||
			    !CombinationalChipCacheManager.CanBuildFullCache(
				    ChipSaveMenu.ActiveCustomizeDescription,
				    cacheAnalysis,
				    out _))
			{
				return nestedCacheableCount > 0 ? "DISK: nested LUTs persisted separately" : "DISK: root LUT not applicable";
			}

			ChipDescription saved = Project.ActiveProject.ViewedChip.LastSavedDescription;
			if (saved != null &&
			    CombinationalChipCacheManager.TryGetRuntimePersistenceInfo(
				    saved,
				    out bool loadedFromDisk,
				    out double loadMs,
				    out string diskMessage))
			{
				if (loadedFromDisk) return $"DISK: loaded in {loadMs:0.0} ms";
				if (diskMessage == "DISK VALID") return "DISK: VALID";
				if (!string.IsNullOrWhiteSpace(diskMessage) &&
				    (diskMessage.Contains("BACKGROUND") || diskMessage == "QUEUED" || diskMessage.StartsWith("LOADING") || diskMessage.StartsWith("SAVING")))
				{
					return "DISK: " + diskMessage;
				}
			}

			if (!persistentCacheInfo.Supported)
			{
				return "DISK: " + persistentCacheInfo.Status;
			}

			string size = persistentCacheInfo.FileBytes > 0
				? " | " + CombinationalChipCacheManager.FormatBytes(persistentCacheInfo.FileBytes)
				: string.Empty;
			return "DISK: " + persistentCacheInfo.Status + size;
		}

		static void SplitPanelText(string text, int maxChars, out string first, out string second)
		{
			first = text ?? string.Empty;
			second = string.Empty;
			if (first.Length <= maxChars) return;

			int split = first.LastIndexOf(' ', Math.Min(maxChars, first.Length - 1));
			if (split <= 0) split = maxChars;

			second = first.Substring(split).TrimStart();
			first = first.Substring(0, split).TrimEnd();

			if (second.Length > maxChars)
			{
				second = second.Substring(0, Math.Max(0, maxChars - 3)) + "...";
			}
		}

		static void UpdateCustomizeDescription()
		{
			List<DisplayInstance> displays = ChipSaveMenu.ActiveCustomizeChip.Displays;
			ChipSaveMenu.ActiveCustomizeDescription.Displays = displays.Select(s => s.Desc).ToArray();
		}

		static void UpdateChipColHexStringFromColour(Color col)
		{
			int colInt = (byte)(col.r * 255) << 16 | (byte)(col.g * 255) << 8 | (byte)(col.b * 255);
			colHexCodeString = "#" + $"{colInt:X6}";
			UI.GetInputFieldState(ID_ColourHexInput).SetText(colHexCodeString, false);
		}

		static void UpdateChipColFromHexString(string hexString)
		{
			colHexCodeString = hexString;
			hexString = hexString.Replace("#", "");
			hexString = hexString.PadRight(6, '0');

			if (ColHelper.TryParseHexCode(hexString, out Color col))
			{
				UI.GetColourPickerState(ID_ColourPicker).SetRGB(col);
				ChipSaveMenu.ActiveCustomizeDescription.Colour = col;
			}
		}

		static bool ValidateHexStringInput(string text)
		{
			if (string.IsNullOrWhiteSpace(text)) return true;

			int numHexDigits = 0;

			for (int i = 0; i < text.Length; i++)
			{
				if (i == 0 && text[i] == '#') continue;

				if (Uri.IsHexDigit(text[i]))
				{
					numHexDigits++;
				}
				else return false;
			}

			return numHexDigits <= 6;
		}
	}
}