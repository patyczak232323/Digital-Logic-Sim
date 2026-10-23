using DLS.Game;
using Seb.Types;
using Seb.Vis;
using Seb.Vis.UI;
using UnityEngine;

namespace DLS.Graphics
{
	// One-screen guide to the touch controls. Shown once on the first project opened on a
	// touch device, and from TOOLS > HELP.
	public static class MobileHelpOverlay
	{
		static readonly (string gesture, string effect)[] Lines =
		{
			("PLACE", "tap a chip in the bar above the dock, then tap where it should go"),
			("TAP", "an input pin to switch it on or off"),
			("WIRE", "start on a pin, finish on another pin"),
			("LONG-PRESS", "a chip, pin or wire for its menu: delete, truth table, export, breakpoint"),
			("TWO FINGERS", "move and zoom the view (PAN: ON does it with one finger)"),
			("SWIPE", "in from the left edge, or press TOOLS, for more tools"),
			("ADD CHIP", "search every chip; SAVE saves the chip you are building"),
			("TOP BAR", "UNDO / REDO, RUN / PAUSE and STEP one tick at a time")
		};

		public static void Draw()
		{
			MenuHelper.DrawBackgroundOverlay();
			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			Rect safe = MobileUI.SafeRectUI;

			float width = Mathf.Min(safe.width - 4f, 80f);
			const float lineHeight = 3.1f;
			float height = 4.5f + Lines.Length * lineHeight + 7f;
			Vector2 topLeft = new(safe.center.x - width / 2, safe.center.y + height / 2);

			UI.DrawPanel(topLeft, new Vector2(width, height), RewiredUI.SurfaceRaised, Anchor.TopLeft);
			Bounds2D panel = UI.PrevBounds;
			RewiredUI.DrawFrame(panel);

			RewiredUI.DrawSectionHeader("TOUCH CONTROLS", panel.TopLeft, panel.Width, true);
			float y = UI.PrevBounds.Bottom - 2f;
			float gestureColumn = panel.Left + 1.2f;
			float effectColumn = panel.Left + 15.5f;

			foreach ((string gesture, string effect) in Lines)
			{
				RewiredUI.DrawLabel(gesture, new Vector2(gestureColumn, y), true, 0.72f, RewiredUI.Accent);
				RewiredUI.DrawLabel(effect, new Vector2(effectColumn, y), false, 0.68f, Color.white);
				y -= lineHeight;
			}

			Vector2 buttonSize = new(16f, 4.6f);
			if (UI.Button("GOT IT", theme.MainMenuButtonTheme, panel.CentreBottom + Vector2.up * 1f, buttonSize, true, false, false, Anchor.CentreBottom) ||
			    KeyboardShortcuts.CancelShortcutTriggered)
			{
				UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);
			}
		}
	}
}
