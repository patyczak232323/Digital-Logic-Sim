using System.Linq;
using Seb.Vis;
using Seb.Vis.UI;
using UnityEngine;
using static Seb.Helpers.ColHelper;

namespace DLS.Graphics
{
	public static class DrawSettings
	{
		// ---- World draw settings ----
		public const float GridSize = 0.125f;
		public const float PinHeight1Bit = 0.185f;
		public const float PinHeight4Bit = 0.3f;
		public const float PinHeight8Bit = 0.43f;
		public const float PinRadius = PinHeight1Bit / 2;

		public const FontType FontBold = FontType.JetbrainsMonoBold;
		public const FontType FontRegular = FontType.JetbrainsMonoRegular;

		public const float FontSizeChipName = 0.25f;
		public const float FontSizePinLabel = 0.2f;

		public const float SubChipPinInset = 0.015f;
		public const float SelectionBoundsPadding = 0.08f;
		public const float ChipOutlineWidth = 0.05f;
		public const float WireThickness = 0.025f;
		public const float WireHighlightedThickness = WireThickness + 0.012f;
		public const float GridThickness = 0.0035f;
		public const float DevPinStateDisplayRadius = 0.2f;
		public const float DevPinStateDisplayOutline = 0.0175f;
		public const float DevPinHandleWidth = DevPinStateDisplayRadius * 0.64f;
		public const float MultiBitPinStateDisplaySquareSize = 0.21f;

		// ---- UI draw settings ----
		public const float PanelUIPadding = 1.15f * 2;
		public const float SpacingUnitUI = 0.5f;
		public const float ChipNameLineSpacing = 0.75f;
		public const float VerticalButtonSpacing = 1f;
		public const float HorizontalButtonSpacing = 0.5f;
		public const float SelectorWheelHeight = 2.8f;
		public const float ButtonHeight = 2.5f;

		public const float DefaultButtonSpacing = SpacingUnitUI * 1;
		public const float InfoBarHeight = 3.5f;
		public static readonly Vector2 LabelBackgroundPadding = new(0.15f, 0.1f);

		// ---- Themes ----
		public static readonly ThemeDLS ActiveTheme = CreateTheme();
		public static readonly UIThemeDLS ActiveUITheme = CreateUITheme();

		// ---- Helper functions ----
		public static Color GetStateColour(bool isHigh, uint index, bool hover = false)
		{
			index = (uint)Mathf.Min(index, ActiveTheme.StateHighCol.Length - 1); // clamp just to be safe...
			if (!isHigh && hover) return ActiveTheme.StateHoverCol[index];
			return isHigh ? ActiveTheme.StateHighCol[index] : ActiveTheme.StateLowCol[index];
		}

		static ThemeDLS CreateTheme()
		{
			const float whiteLow = 0.35f;
			const float whiteHigh = 0.9f;
			Color[] stateLow =
			{
				new(0.2f, 0.1f, 0.1f),
				new(0.28f, 0.15f, 0.01f),
				new(0.26f, 0.2f, 0.07f),
				new(0.1f, 0.2f, 0.1f),
				new(0.1f, 0.14f, 0.35f),
				new(0.19f, 0.12f, 0.28f),
				new(0.25f, 0.1f, 0.25f),
				new(whiteLow, whiteLow, whiteLow)
			};

			Color[] stateHigh =
			{
				new(0.95f, 0.3f, 0.31f),
				new(0.92f, 0.44f, 0.12f),
				new(0.98f, 0.76f, 0.26f),
				new(0.25f, 0.66f, 0.31f),
				new(0.2f, 0.5f, 1f),
				new(0.6f, 0.4f, 0.98f),
				new(0.84f, 0.33f, 0.9f),
				new(whiteHigh, whiteHigh, whiteHigh)
			};

			Color[] stateHover = stateLow.Select(c => Brighten(c, 0.075f)).ToArray();

			return new ThemeDLS
			{
				SelectionBoxCol = new Color(1, 1, 1, 0.1f),
				SelectionBoxMovingCol = new Color(1, 1, 1, 0.125f),
				SelectionBoxInvalidCol = MakeCol255(243, 81, 75, 120),
				SelectionBoxOtherIsInvaldCol = MakeCol255(243, 150, 75, 80),
				StateLowCol = stateLow,
				StateHighCol = stateHigh,
				StateHoverCol = stateHover,
				StateDisconnectedCol = Color.black,
				DevPinHandle = MakeCol(0.31f),
				DevPinHandleHighlighted = MakeCol(0.7f),
				PinCol = Color.black,
				PinLabelCol = new Color(0, 0, 0, 0.7f),
				PinHighlightCol = Color.white,
				PinInvalidCol = MakeCol(0.15f),
				SevenSegCols = new Color[]
				{
					new(0.1f, 0.09f, 0.09f), new(1, 0.32f, 0.28f), new(0.19f, 0.15f, 0.15f), // Col A: OFF, ON, HIGHLIGHT
					new(0.09f, 0.09f, 0.1f), new(0, 0.61f, 1f), new(0.15f, 0.15f, 0.19f) // Col B: OFF, ON, HIGHLIGHT
				},
				BackgroundCol = MakeCol255(66, 66, 69),
				GridCol = MakeCol255(49, 49, 51),
			};
		}

		static UIThemeDLS CreateUITheme()
		{
			FontType fontRegular = FontRegular;
			FontType fontBold = FontBold;
			float fontSizeRegular = UIThemeLibrary.FontSizeMedium;

			Color inactiveButtonCol = MakeCol255(48, 49, 54);
			Color inactiveTextol = MakeCol255(112, 114, 122);
			Color chipLibaryButtonOff = MakeCol255(43, 45, 51);
			Color chipLibaryButtonOn = MakeCol255(72, 108, 233);
			Color menuPanelCol = MakeCol255(32, 33, 37);

			Color chipLibraryCollectionHighlightCol = MakeCol255(72, 108, 233);
			Color chipLibraryChipHighlightCol = MakeCol255(86, 124, 245);

			Color scrollBarCol = MakeCol255(72, 108, 233);


			return new UIThemeDLS
			{
				// --- Text settings ---
				FontRegular = fontRegular,
				FontBold = fontBold,
				FontSizeRegular = fontSizeRegular,

				// --- Menu colours ---
				MenuPanelCol = menuPanelCol,
				MenuBackgroundOverlayCol = new Color(0, 0, 0, 0.78f),
				// --- Buttons ---
				ButtonTheme = MakeButtonTheme(fontRegular, MakeCol255(61, 62, 68), MakeCol255(72, 108, 233), MakeCol255(55, 82, 181), Color.white, Color.white, Color.white),
				ProjectSelectionButton = MakeButtonTheme(fontRegular, MakeCol255(37, 38, 43), MakeCol255(54, 72, 130), MakeCol255(58, 84, 184), Color.white, Color.white, Color.white),
				ProjectSelectionButtonSelected = MakeButtonTheme(fontRegular, MakeCol255(72, 108, 233), MakeCol255(86, 124, 245), MakeCol255(55, 82, 181), Color.white, Color.white, Color.white),
				ChipButton = MakeButtonTheme(fontRegular, MakeCol255(45, 46, 51), MakeCol255(61, 79, 137), MakeCol255(72, 108, 233), Color.white, Color.white, Color.white),
				MainMenuButtonTheme = MakeButtonTheme(fontRegular, MakeCol255(52, 53, 59), MakeCol255(72, 108, 233), MakeCol255(55, 82, 181), MakeCol255(235, 239, 255), Color.white, Color.white),
				MenuButtonTheme = MakeButtonTheme(fontRegular, MakeCol255(48, 50, 58), MakeCol255(72, 108, 233), MakeCol255(55, 82, 181), MakeCol255(235, 239, 255), Color.white, Color.white),
				MenuPopupButtonTheme = MakeButtonThemeFull(fontRegular, MakeCol255(38, 39, 44), MakeCol255(55, 72, 126), MakeCol255(72, 108, 233), inactiveButtonCol, Color.white, Color.white, Color.white, inactiveTextol),

				ChipLibraryCollectionToggleOff = MakeButtonTheme(fontRegular, MakeCol255(38, 39, 44), MakeCol255(55, 72, 126), chipLibraryCollectionHighlightCol, Color.white, Color.white, Color.white),
				ChipLibraryCollectionToggleOn = MakeButtonThemeAuto(fontRegular, chipLibraryCollectionHighlightCol, Color.white),
				ChipLibraryChipToggleOff = MakeButtonTheme(fontRegular, MakeCol255(44, 45, 50), MakeCol255(62, 79, 135), chipLibraryChipHighlightCol, Color.white, Color.white, Color.white),
				ChipLibraryChipToggleOn = MakeButtonThemeAuto(fontRegular, chipLibraryChipHighlightCol, Color.white),

				// --- Other stuff ---
				ChipNameInputField = new InputFieldTheme
				{
					font = fontBold,
					fontSize = UIThemeLibrary.FontSizeVeryLarge,
					bgCol = MakeCol255(22, 23, 27),
					defaultTextCol = MakeCol255(94, 96, 105),
					textCol = Color.white,
					focusBorderCol = MakeCol255(72, 108, 233)
				},
				OptionsWheel = new WheelSelectorTheme
				{
					backgroundCol = MakeCol255(29, 30, 34),
					buttonTheme = MakeButtonTheme(fontBold, MakeCol255(48, 49, 55), MakeCol255(72, 108, 233), MakeCol255(55, 82, 181), Color.white, Color.white, Color.white),
					textCol = Color.white,
					inactiveTextCol = MakeCol255(112, 114, 122)
				},
				ScrollTheme = new ScrollViewTheme
				{
					backgroundCol = MakeCol255(25, 26, 30),
					padding = 1,
					scrollBarColBackground = MakeCol255(20, 21, 24),
					scrollBarColInactive = MakeCol255(51, 53, 59),
					scrollBarColNormal = scrollBarCol,
					scrollBarColHover = Brighten(scrollBarCol, 0.05f, -0.025f),
					scrollBarColPressed = Darken(scrollBarCol, 0.05f, 0.025f),
					scrollBarWidth = 1
				},
				CheckBoxTheme = new CheckboxTheme
				{
					boxCol = MakeCol255(45, 47, 53),
					tickCol = Color.white
				},
				InfoBarCol = new Color(0.07f, 0.075f, 0.085f, 0.96f),
				StarredBarCol = MakeCol255(27, 28, 32)
			};

			ButtonTheme MakeButtonThemeAuto(FontType font, Color colNormal, Color textCol)
			{
				Color colHover = Brighten(colNormal, 0.2f, -0.1f);
				Color colPress = Darken(colNormal, 0.05f, 0.05f);
				return MakeButtonThemeFull(font, colNormal, colHover, colPress, inactiveButtonCol, textCol, textCol, textCol, inactiveTextol);
			}

			ButtonTheme MakeButtonTheme(FontType font, Color colNormal, Color colHover, Color colPress, Color textNormal, Color textHover, Color textPress)
			{
				return MakeButtonThemeFull(font, colNormal, colHover, colPress, inactiveButtonCol, textNormal, textHover, textPress, inactiveTextol);
			}

			ButtonTheme MakeButtonThemeFull(FontType font, Color colNormal, Color colHover, Color colPress, Color colInactive, Color textNormal, Color textHover, Color textPress, Color textInactive)
			{
				return new ButtonTheme
				{
					font = font,
					fontSize = fontSizeRegular,
					paddingScale = UIThemeLibrary.PaddingScaleButton,
					buttonCols = new ButtonTheme.StateCols(colNormal, colHover, colPress, colInactive),
					textCols = new ButtonTheme.StateCols(textNormal, textHover, textPress, textInactive)
				};
			}
		}


		public class ThemeDLS
		{
			public Color BackgroundCol;
			public Color DevPinHandle;
			public Color DevPinHandleHighlighted;
			public Color GridCol;
			public Color PinCol;
			public Color PinHighlightCol;
			public Color PinInvalidCol;
			public Color PinLabelCol;
			public Color SelectionBoxCol;
			public Color SelectionBoxInvalidCol;
			public Color SelectionBoxMovingCol;
			public Color SelectionBoxOtherIsInvaldCol;
			public Color[] SevenSegCols; // Off, On, Highlight
			public Color StateDisconnectedCol;
			public Color[] StateHighCol;
			public Color[] StateHoverCol;
			public Color[] StateLowCol;
		}

		public class UIThemeDLS
		{
			public ButtonTheme ButtonTheme;
			public CheckboxTheme CheckBoxTheme;

			public ButtonTheme ChipButton; // Bottom bar -> chip buttons
			public ButtonTheme ChipLibraryChipToggleOff;
			public ButtonTheme ChipLibraryChipToggleOn;
			public ButtonTheme ChipLibraryCollectionToggleOff;
			public ButtonTheme ChipLibraryCollectionToggleOn;

			public InputFieldTheme ChipNameInputField;
			public FontType FontBold;
			public FontType FontRegular;
			public float FontSizeRegular;

			public Color InfoBarCol;

			public ButtonTheme MainMenuButtonTheme; // Main menu buttons
			public Color MenuBackgroundOverlayCol;
			public ButtonTheme MenuButtonTheme; // Bottom bar -> menu button
			public Color MenuPanelCol;
			public ButtonTheme MenuPopupButtonTheme; // Bottom bar -> menu -> popup buttons theme
			public WheelSelectorTheme OptionsWheel;
			public ButtonTheme ProjectSelectionButton; // Main menu -> load project -> unselected project button
			public ButtonTheme ProjectSelectionButtonSelected; // Main menu -> load project -> selected project button
			public ScrollViewTheme ScrollTheme;
			public Color StarredBarCol;
		}
	}
}