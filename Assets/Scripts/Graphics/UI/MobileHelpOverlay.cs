using DLS.Game;
using Seb.Types;
using Seb.Vis;
using Seb.Vis.UI;
using UnityEngine;

namespace DLS.Graphics
{
	// "How it works": the touch interface in six steps. Shown once the first time a project is
	// opened, and from MENU > HOW IT WORKS and the empty board.
	public static class MobileHelpOverlay
	{
		static readonly (string title, string text)[] Steps =
		{
			("ADD PARTS", "Drag a part from PARTS onto the board, or tap it and then tap the board."),
			("CONNECT", "Tap a pin, then another pin. Tap empty space on the way to bend the wire."),
			("TRY IT", "Tap the round button of an input to switch it on and off."),
			("CHANGE", "Tap a part to select it: its actions appear below. Drag a part to move it."),
			("LOOK AROUND", "Drag empty space to move, pinch or + and - to zoom. FIT shows everything."),
			("SAVE", "SAVE turns the board into a chip. Find it under MY CHIPS and use it again.")
		};

		public static void Draw()
		{
			MenuHelper.DrawBackgroundOverlay();
			Rect safe = MobileUI.SafeRectUI;

			float width = Mathf.Min(safe.width - 4f, 70f);
			const float stepHeight = 4.5f;
			float height = 4.2f + Steps.Length * stepHeight + 2.6f + 5.6f;
			height = Mathf.Min(height, safe.height - 1f);
			Bounds2D panel = MobileTheme.Card(new Vector2(safe.center.x, safe.center.y), new Vector2(width, height), Anchor.Centre);

			MobileTheme.Text("HOW IT WORKS", panel.TopLeft + new Vector2(1.5f, -2.2f), 0.86f, Color.white, Anchor.TextCentreLeft, true);

			float y = panel.Top - 5.6f;
			float textX = panel.Left + 4.6f;
			for (int i = 0; i < Steps.Length; i++)
			{
				(string title, string text) = Steps[i];
				UI.DrawCircle(new Vector2(panel.Left + 2.4f, y), 1.3f, MobileTheme.Accent);
				MobileTheme.Text((i + 1).ToString(), new Vector2(panel.Left + 2.4f, y), 0.62f, Color.white, Anchor.TextCentre, true);
				MobileTheme.Text(title, new Vector2(textX, y + 0.85f), 0.62f, MobileTheme.Accent, Anchor.TextCentreLeft, true);
				MobileTheme.Text(MobileTheme.Fit(text, panel.Right - textX - 1f, 0.6f), new Vector2(textX, y - 1.05f), 0.6f, Color.white);
				y -= stepHeight;
			}

			MobileTheme.Text("Hold a finger on anything to see all its options.", new Vector2(panel.Left + 1.5f, panel.Bottom + 3f), 0.58f, MobileTheme.SecondaryTextCol);

			if (MobileTheme.Button("GOT IT", MobileTheme.Primary, panel.BottomRight + new Vector2(-1f, 0.9f), new Vector2(15f, 4.4f), true, Anchor.BottomRight) ||
			    KeyboardShortcuts.CancelShortcutTriggered)
			{
				UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);
			}
		}
	}
}
