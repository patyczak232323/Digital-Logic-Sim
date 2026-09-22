using DLS.Game;
using Seb.Helpers.InputHandling;
using Seb.Types;
using Seb.Vis.UI;
using UnityEngine;

namespace DLS.Graphics
{
	/// <summary>
	/// Rewired-native Android keyboard. It intentionally uses the same immediate-mode
	/// renderer and themes as the rest of the application instead of Unity IMGUI or
	/// the Android system keyboard.
	/// </summary>
	public static class MobileKeyboardUI
	{
		static bool shift;
		static bool symbols;
		static bool wasVisible;

		public static void Draw()
		{
			if (!MobileInputBridge.KeyboardVisible)
			{
				wasVisible = false;
				return;
			}

			if (!wasVisible)
			{
				shift = false;
				symbols = false;
				wasVisible = true;
			}

			Rect screen = MobileInputBridge.KeyboardScreenRect;
			if (screen.width <= 0 || screen.height <= 0 || Screen.width <= 0) return;

			// MobileRuntime suppressed this pointer before the world and the screen below
			// the keyboard saw it. Re-enable it now, after those layers have processed,
			// so only the keyboard can consume the touch.
			MobileRuntime.RestorePrimaryPointerForKeyboardUI();

			float scale = UI.Width / Screen.width;
			Rect rect = Rect.MinMaxRect(
				screen.xMin * scale,
				screen.yMin * scale,
				screen.xMax * scale,
				screen.yMax * scale);

			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			bool code = MobileInputBridge.RequestedMode == MobileKeyboardMode.Code;

			UI.StartNewLayer();
			UI.DrawPanel(new Vector2(rect.xMin, rect.yMin), new Vector2(rect.width, rect.height),
				theme.MenuPanelCol, Anchor.BottomLeft);
			Bounds2D panel = UI.PrevBounds;
			RewiredUI.DrawFrame(panel);

			const float pad = 0.65f;
			const float gap = 0.34f;
			float toolbarH = Mathf.Clamp(rect.height * 0.13f, 2.65f, 3.6f);
			int bodyRows = code ? 5 : 4;
			float rowH = (rect.height - pad * 2f - toolbarH - gap * bodyRows) / bodyRows;
			rowH = Mathf.Max(2.7f, rowH);

			float x = rect.xMin + pad;
			float width = rect.width - pad * 2f;
			float y = rect.yMax - pad;

			DrawToolbar(new Vector2(x, y), width, toolbarH, gap, code, theme);
			y -= toolbarH + gap;

			if (code)
			{
				DrawTextRow(
					new Vector2(x, y),
					width,
					rowH,
					gap,
					new[] { "{", "}", "(", ")", "[", "]", ":", "=", ",", ".", "#", "_" },
					theme.MenuPopupButtonTheme,
					0f);
				y -= rowH + gap;
			}

			if (symbols)
			{
				DrawTextRow(new Vector2(x, y), width, rowH, gap,
					new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "-", "+" },
					theme.MainMenuButtonTheme, 0f);
				y -= rowH + gap;

				DrawTextRow(new Vector2(x, y), width, rowH, gap,
					new[] { "!", "@", "#", "$", "%", "^", "&", "*", "/", "\\", "<", ">" },
					theme.MainMenuButtonTheme, 0f);
				y -= rowH + gap;

				DrawTextRow(new Vector2(x, y), width, rowH, gap,
					new[] { "(", ")", "[", "]", "{", "}", ":", ";", "=", "'", "\"", "?" },
					theme.MainMenuButtonTheme, 0f);
				y -= rowH + gap;
			}
			else
			{
				DrawTextRow(new Vector2(x, y), width, rowH, gap,
					new[] { "q", "w", "e", "r", "t", "y", "u", "i", "o", "p" },
					theme.MainMenuButtonTheme, 0f);
				y -= rowH + gap;

				DrawTextRow(new Vector2(x, y), width, rowH, gap,
					new[] { "a", "s", "d", "f", "g", "h", "j", "k", "l" },
					theme.MainMenuButtonTheme, width * 0.035f);
				y -= rowH + gap;

				DrawTextRow(new Vector2(x, y), width, rowH, gap,
					new[] { "z", "x", "c", "v", "b", "n", "m", "-", "_" },
					theme.MainMenuButtonTheme, width * 0.065f);
				y -= rowH + gap;
			}

			DrawBottomRow(new Vector2(x, y), width, rowH, gap, code, theme);
		}

		static void DrawToolbar(
			Vector2 topLeft,
			float width,
			float height,
			float gap,
			bool code,
			DrawSettings.UIThemeDLS theme)
		{
			float titleW = Mathf.Clamp(width * 0.14f, 10f, 14f);
			UI.DrawText(
				code ? "RHDL INPUT" : "TEXT INPUT",
				theme.FontBold,
				theme.FontSizeRegular * 0.66f,
				topLeft + new Vector2(0.35f, -height * 0.5f),
				Anchor.TextCentreLeft,
				RewiredUI.SecondaryText);

			string[] labels = code
				? new[] { "ESC", "←", "→", "TAB", "UNDO", "REDO", "PASTE", "HIDE" }
				: new[] { "←", "→", "ALL", "COPY", "PASTE", "HIDE" };
			string[] actions = code
				? new[] { "ESC", "LEFT", "RIGHT", "TAB", "UNDO", "REDO", "PASTE", "HIDE" }
				: new[] { "LEFT", "RIGHT", "ALL", "COPY", "PASTE", "HIDE" };

			float startX = topLeft.x + titleW + gap;
			float available = width - titleW - gap;
			float keyW = (available - gap * (labels.Length - 1)) / labels.Length;

			for (int i = 0; i < labels.Length; i++)
			{
				ButtonTheme buttonTheme = actions[i] == "HIDE"
					? theme.ChipLibraryCollectionToggleOn
					: theme.MenuPopupButtonTheme;

				if (UI.Button(
					labels[i],
					buttonTheme,
					new Vector2(startX + i * (keyW + gap), topLeft.y),
					new Vector2(keyW, height),
					true,
					false,
					false,
					Anchor.TopLeft, expandTouchTarget: false))
				{
					RunAction(actions[i]);
				}
			}
		}

		static void DrawTextRow(
			Vector2 topLeft,
			float width,
			float height,
			float gap,
			string[] keys,
			ButtonTheme theme,
			float sideInset)
		{
			float usableWidth = width - sideInset * 2f;
			float keyW = (usableWidth - gap * (keys.Length - 1)) / keys.Length;
			float x = topLeft.x + sideInset;

			for (int i = 0; i < keys.Length; i++)
			{
				bool letter = keys[i].Length == 1 && char.IsLetter(keys[i][0]);
				string text = letter
					? (shift ? keys[i].ToUpperInvariant() : keys[i].ToLowerInvariant())
					: keys[i];

				if (!UI.Button(
					text,
					theme,
					new Vector2(x + i * (keyW + gap), topLeft.y),
					new Vector2(keyW, height),
					true,
					false,
					false,
					Anchor.TopLeft, expandTouchTarget: false))
				{
					continue;
				}

				MobileRuntime.ScheduleVirtualText(text);
				if (shift) shift = false;
			}
		}

		static void DrawBottomRow(
			Vector2 topLeft,
			float width,
			float height,
			float gap,
			bool code,
			DrawSettings.UIThemeDLS theme)
		{
			float shiftW = width * 0.115f;
			float modeW = width * 0.105f;
			float enterW = width * 0.12f;
			float delW = width * 0.12f;
			float spaceW = width - shiftW - modeW - enterW - delW - gap * 4f;

			float x = topLeft.x;
			ButtonTheme shiftTheme = shift ? theme.ChipLibraryCollectionToggleOn : theme.MenuPopupButtonTheme;
			if (UI.Button("SHIFT", shiftTheme, new Vector2(x, topLeft.y), new Vector2(shiftW, height), true, false, false, Anchor.TopLeft, expandTouchTarget: false))
				shift = !shift;
			x += shiftW + gap;

			ButtonTheme modeTheme = symbols ? theme.ChipLibraryCollectionToggleOn : theme.MenuPopupButtonTheme;
			if (UI.Button(symbols ? "ABC" : "123", modeTheme, new Vector2(x, topLeft.y), new Vector2(modeW, height), true, false, false, Anchor.TopLeft, expandTouchTarget: false))
				symbols = !symbols;
			x += modeW + gap;

			if (UI.Button("SPACE", theme.MainMenuButtonTheme, new Vector2(x, topLeft.y), new Vector2(spaceW, height), true, false, false, Anchor.TopLeft, expandTouchTarget: false))
				MobileRuntime.ScheduleVirtualText(" ");
			x += spaceW + gap;

			if (UI.Button(code ? "ENTER" : "DONE", theme.ChipLibraryCollectionToggleOn,
				new Vector2(x, topLeft.y), new Vector2(enterW, height), true, false, false, Anchor.TopLeft, expandTouchTarget: false))
			{
				if (code) MobileRuntime.ScheduleVirtualKey(KeyCode.Return);
				else MobileInputBridge.DismissKeyboard();
			}
			x += enterW + gap;

			if (UI.Button("DEL", theme.MenuPopupButtonTheme,
				new Vector2(x, topLeft.y), new Vector2(delW, height), true, false, false, Anchor.TopLeft, expandTouchTarget: false))
			{
				MobileRuntime.ScheduleVirtualKey(KeyCode.Backspace);
			}
		}

		static void RunAction(string action)
		{
			switch (action)
			{
				case "ESC": MobileRuntime.ScheduleVirtualKey(KeyCode.Escape); break;
				case "LEFT": MobileRuntime.ScheduleVirtualKey(KeyCode.LeftArrow); break;
				case "RIGHT": MobileRuntime.ScheduleVirtualKey(KeyCode.RightArrow); break;
				case "TAB": MobileRuntime.ScheduleVirtualKey(KeyCode.Tab); break;
				case "UNDO": MobileRuntime.ScheduleVirtualShortcut(KeyCode.Z); break;
				case "REDO": MobileRuntime.ScheduleVirtualShortcut(KeyCode.Y); break;
				case "ALL": MobileRuntime.ScheduleVirtualShortcut(KeyCode.A); break;
				case "COPY": MobileRuntime.ScheduleVirtualShortcut(KeyCode.C); break;
				case "PASTE": MobileRuntime.ScheduleVirtualShortcut(KeyCode.V); break;
				case "HIDE": MobileInputBridge.DismissKeyboard(); break;
			}
		}

		public static void Reset()
		{
			shift = false;
			symbols = false;
			wasVisible = false;
		}
	}
}
