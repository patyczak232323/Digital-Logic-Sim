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
			"MIDDLE",
			"TOP",
			"HIDDEN"
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

			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			WheelSelectorTheme wheelTheme = GetCustomizationWheelTheme(theme);

			const float leftPanelWidth = 24f;
			const float rightPanelWidth = 31f;
			const float edgePad = 1.1f;
			const float sectionGap = 0.75f;
			const float headerHeight = 3.1f;
			const float subHeaderHeight = 2.55f;

			Color panelCol = theme.MenuPanelCol;
			Color dividerCol = ColHelper.MakeCol255(58);

			// Two fixed sidebars leave the centre of the screen exclusively for the
			// live chip preview/customization scene.
			UI.DrawPanel(UI.TopLeft, new Vector2(leftPanelWidth, UI.Height), panelCol, Anchor.TopLeft);
			UI.DrawPanel(UI.TopRight, new Vector2(rightPanelWidth, UI.Height), panelCol, Anchor.TopRight);
			UI.DrawLine(new Vector2(leftPanelWidth, 0), new Vector2(leftPanelWidth, UI.Height), 0.08f, dividerCol);
			UI.DrawLine(new Vector2(UI.Width - rightPanelWidth, 0), new Vector2(UI.Width - rightPanelWidth, UI.Height), 0.08f, dividerCol);

			// ---------------- Left: chip properties ----------------
			float leftContentWidth = leftPanelWidth - edgePad * 2;
			Vector2 leftTop = new(edgePad, UI.Height - edgePad);
			DrawSectionHeader("CUSTOMIZE CHIP", leftTop, leftContentWidth, headerHeight, theme, true);
			float leftY = leftTop.y - headerHeight - 1.35f;

			DrawFieldLabel("NAME DISPLAY", new Vector2(edgePad, leftY), theme);
			leftY -= 1.85f;
			int nameDisplayMode = UI.WheelSelector(
				ID_NameDisplayOptions,
				nameDisplayOptions,
				new Vector2(edgePad, leftY),
				new Vector2(leftContentWidth, DrawSettings.ButtonHeight),
				wheelTheme,
				Anchor.TopLeft);
			ChipSaveMenu.ActiveCustomizeDescription.NameLocation = (NameDisplayLocation)nameDisplayMode;
			leftY -= DrawSettings.ButtonHeight + 2.1f;

			DrawFieldLabel("SIM CACHE", new Vector2(edgePad, leftY), theme);
			leftY -= 1.85f;
			int cacheModeIndex = UI.WheelSelector(
				ID_CacheMode,
				cacheModeOptions,
				new Vector2(edgePad, leftY),
				new Vector2(leftContentWidth, DrawSettings.ButtonHeight),
				wheelTheme,
				Anchor.TopLeft);
			ChipSaveMenu.ActiveCustomizeDescription.CacheMode = (ChipCacheMode)cacheModeIndex;
			leftY -= DrawSettings.ButtonHeight + 1.2f;

			Color hintCol = new(1, 1, 1, 0.55f);
			UI.DrawText("Controls simulation cache", theme.FontRegular, theme.FontSizeRegular * 0.64f, new Vector2(edgePad, leftY), Anchor.TextCentreLeft, hintCol);
			leftY -= 1.45f;
			UI.DrawText("behaviour for this chip.", theme.FontRegular, theme.FontSizeRegular * 0.64f, new Vector2(edgePad, leftY), Anchor.TextCentreLeft, hintCol);

			// Actions stay pinned to the bottom instead of shifting with content.
			Vector2 actionTopLeft = new(edgePad, edgePad + DrawSettings.ButtonHeight);
			int cancelConfirmButtonIndex = MenuHelper.DrawButtonPair(
				"CANCEL",
				"CONFIRM",
				actionTopLeft,
				leftContentWidth,
				false);

			// ---------------- Right: unified inspector ----------------
			float rightX = UI.Width - rightPanelWidth + edgePad;
			float rightContentWidth = rightPanelWidth - edgePad * 2;
			Vector2 rightTop = new(rightX, UI.Height - edgePad);
			DrawSectionHeader("CHIP INSPECTOR", rightTop, rightContentWidth, headerHeight, theme, true);
			float rightY = rightTop.y - headerHeight - sectionGap;

			DrawSectionHeader("CACHE INFO", new Vector2(rightX, rightY), rightContentWidth, subHeaderHeight, theme, false);
			rightY -= subHeaderHeight;
			const float cacheBodyHeight = 13.7f;
			DrawCacheInfoPanel(theme, new Vector2(rightX, rightY), rightContentWidth, cacheBodyHeight);
			rightY -= cacheBodyHeight + sectionGap;

			DrawSectionHeader("APPEARANCE", new Vector2(rightX, rightY), rightContentWidth, subHeaderHeight, theme, false);
			rightY -= subHeaderHeight;
			const float appearanceBodyHeight = 17.4f;
			DrawAppearancePanel(theme, new Vector2(rightX, rightY), rightContentWidth, appearanceBodyHeight);
			rightY -= appearanceBodyHeight + sectionGap;

			DrawSectionHeader($"DISPLAYS ({subChipsWithDisplays.Length})", new Vector2(rightX, rightY), rightContentWidth, subHeaderHeight, theme, false);
			rightY -= subHeaderHeight;

			float displayHeight = Mathf.Max(4f, rightY - edgePad);
			DrawDisplaysPanel(theme, new Vector2(rightX, rightY), rightContentWidth, displayHeight);

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

		static WheelSelectorTheme GetCustomizationWheelTheme(DrawSettings.UIThemeDLS theme)
		{
			WheelSelectorTheme wheelTheme = theme.OptionsWheel;
			wheelTheme.buttonTheme = theme.MainMenuButtonTheme;
			wheelTheme.buttonTheme.font = theme.FontBold;
			wheelTheme.buttonTheme.fontSize = theme.FontSizeRegular;
			wheelTheme.backgroundCol = ColHelper.Darken(theme.MenuPanelCol, 0.08f);
			wheelTheme.textCol = Color.white;
			wheelTheme.inactiveTextCol = ColHelper.MakeCol255(125);
			return wheelTheme;
		}

		static void DrawSectionHeader(string text, Vector2 topLeft, float width, float height, DrawSettings.UIThemeDLS theme, bool major)
		{
			Color bg = ColHelper.Darken(theme.MenuPanelCol, major ? 0.055f : 0.035f);
			UI.DrawPanel(topLeft, new Vector2(width, height), bg, Anchor.TopLeft);
			Bounds2D bounds = UI.PrevBounds;
			float fontScale = major ? 0.92f : 0.78f;
			UI.DrawText(
				text,
				theme.FontBold,
				theme.FontSizeRegular * fontScale,
				bounds.CentreLeft + Vector2.right * 0.9f,
				Anchor.TextCentreLeft,
				Color.white);
			UI.DrawLine(bounds.BottomLeft, bounds.BottomRight, 0.07f, theme.MainMenuButtonTheme.buttonCols.hover);
			UI.OverridePreviousBounds(bounds);
		}

		static void DrawFieldLabel(string text, Vector2 pos, DrawSettings.UIThemeDLS theme)
		{
			UI.DrawText(
				text,
				theme.FontBold,
				theme.FontSizeRegular * 0.72f,
				pos,
				Anchor.TextCentreLeft,
				new Color(1, 1, 1, 0.78f));
		}

		static void DrawAppearancePanel(DrawSettings.UIThemeDLS theme, Vector2 topLeft, float width, float height)
		{
			Color bodyCol = ColHelper.Darken(theme.MenuPanelCol, 0.065f);
			UI.DrawPanel(topLeft, new Vector2(width, height), bodyCol, Anchor.TopLeft);

			const float innerPad = 0.9f;
			const float pickerWidth = 12.8f;
			Vector2 pickerTopLeft = topLeft + new Vector2(innerPad, -innerPad - 1.45f);
			UI.DrawText(
				"CHIP COLOR",
				theme.FontBold,
				theme.FontSizeRegular * 0.68f,
				topLeft + new Vector2(innerPad, -0.8f),
				Anchor.TextCentreLeft,
				new Color(1, 1, 1, 0.78f));

			Color newCol = UI.DrawColourPicker(ID_ColourPicker, pickerTopLeft, pickerWidth, Anchor.TopLeft);

			float detailsX = topLeft.x + innerPad + pickerWidth + 1.25f;
			float detailsWidth = width - (detailsX - topLeft.x) - innerPad;
			Vector2 detailsTop = new(detailsX, topLeft.y - 1.25f);

			UI.DrawText(
				"PREVIEW",
				theme.FontBold,
				theme.FontSizeRegular * 0.62f,
				detailsTop,
				Anchor.TextCentreLeft,
				new Color(1, 1, 1, 0.72f));

			Vector2 previewTopLeft = detailsTop + Vector2.down * 1.25f;
			float previewHeight = 4.4f;
			UI.DrawPanel(previewTopLeft, new Vector2(detailsWidth, previewHeight), ChipSaveMenu.ActiveCustomizeDescription.Colour, Anchor.TopLeft);
			Bounds2D previewBounds = UI.PrevBounds;
			UI.DrawLine(previewBounds.BottomLeft, previewBounds.TopLeft, 0.05f, ColHelper.MakeCol255(100));
			UI.DrawLine(previewBounds.TopLeft, previewBounds.TopRight, 0.05f, ColHelper.MakeCol255(100));
			UI.DrawLine(previewBounds.TopRight, previewBounds.BottomRight, 0.05f, ColHelper.MakeCol255(100));
			UI.DrawLine(previewBounds.BottomRight, previewBounds.BottomLeft, 0.05f, ColHelper.MakeCol255(100));

			Vector2 hexLabelPos = previewTopLeft + Vector2.down * (previewHeight + 1.25f);
			UI.DrawText(
				"HEX VALUE",
				theme.FontBold,
				theme.FontSizeRegular * 0.62f,
				hexLabelPos,
				Anchor.TextCentreLeft,
				new Color(1, 1, 1, 0.72f));

			InputFieldTheme inputTheme = theme.ChipNameInputField;
			inputTheme.fontSize = theme.FontSizeRegular * 0.8f;
			InputFieldState hexColInput = UI.InputField(
				ID_ColourHexInput,
				inputTheme,
				hexLabelPos + Vector2.down * 1.1f,
				new Vector2(detailsWidth, DrawSettings.ButtonHeight),
				"#",
				Anchor.TopLeft,
				0.65f,
				hexStringInputValidator);

			if (newCol != ChipSaveMenu.ActiveCustomizeDescription.Colour)
			{
				ChipSaveMenu.ActiveCustomizeDescription.Colour = newCol;
				UpdateChipColHexStringFromColour(newCol);
			}
			else if (colHexCodeString != hexColInput.text)
			{
				UpdateChipColFromHexString(hexColInput.text);
			}
		}

		static void DrawDisplaysPanel(DrawSettings.UIThemeDLS theme, Vector2 topLeft, float width, float height)
		{
			if (subChipsWithDisplays.Length == 0)
			{
				Color emptyCol = ColHelper.Darken(theme.MenuPanelCol, 0.065f);
				UI.DrawPanel(topLeft, new Vector2(width, height), emptyCol, Anchor.TopLeft);
				Bounds2D bounds = UI.PrevBounds;
				UI.DrawText(
					"No displays available.",
					theme.FontRegular,
					theme.FontSizeRegular * 0.65f,
					bounds.Centre,
					Anchor.Centre,
					new Color(1, 1, 1, 0.45f));
				return;
			}

			UI.DrawScrollView(
				ID_DisplaysScrollView,
				topLeft,
				new Vector2(width, height),
				UILayoutHelper.DefaultSpacing,
				Anchor.TopLeft,
				theme.ScrollTheme,
				drawDisplayScrollEntry,
				subChipsWithDisplays.Length);
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


		static void DrawCacheInfoPanel(DrawSettings.UIThemeDLS theme, Vector2 topLeft, float width, float height)
		{
			Color bodyCol = ColHelper.Darken(theme.MenuPanelCol, 0.065f);
			UI.DrawPanel(topLeft, new Vector2(width, height), bodyCol, Anchor.TopLeft);

			const float padX = 0.9f;
			const float rowStep = 1.58f;
			Vector2 pos = topLeft + new Vector2(padX, -1.15f);
			Color secondaryCol = new(1, 1, 1, 0.68f);

			DrawInfoLine(GetCacheHeadline(), true, 0.68f, Color.white);

			GetCacheReasonLines(out string reasonA, out string reasonB);
			DrawInfoLine(reasonA, false, 0.52f, secondaryCol);
			if (!string.IsNullOrWhiteSpace(reasonB)) DrawInfoLine(reasonB, false, 0.52f, secondaryCol);

			SplitPanelText(GetCacheRamLine(), 38, out string ramA, out string ramB);
			DrawInfoLine(ramA, false, 0.56f, Color.white);
			if (!string.IsNullOrWhiteSpace(ramB)) DrawInfoLine(ramB, false, 0.56f, secondaryCol);

			SplitPanelText(GetCacheDiskLine(), 38, out string diskA, out string diskB);
			DrawInfoLine(diskA, false, 0.56f, Color.white);
			if (!string.IsNullOrWhiteSpace(diskB)) DrawInfoLine(diskB, false, 0.56f, secondaryCol);

			void DrawInfoLine(string text, bool bold, float scale, Color col)
			{
				if (string.IsNullOrWhiteSpace(text)) return;
				UI.DrawText(
					text,
					bold ? theme.FontBold : theme.FontRegular,
					theme.FontSizeRegular * scale,
					pos,
					Anchor.TextCentreLeft,
					col);
				pos += Vector2.down * rowStep;
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

			SplitPanelText(text, 38, out first, out second);
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