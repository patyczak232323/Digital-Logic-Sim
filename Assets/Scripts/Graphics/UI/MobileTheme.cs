using Seb.Helpers;
using Seb.Types;
using Seb.Vis;
using Seb.Vis.UI;
using UnityEngine;

namespace DLS.Graphics
{
	public enum MobileIcon
	{
		None,
		Menu,
		Back,
		Undo,
		Redo,
		Play,
		Pause,
		Step,
		Plus,
		Minus,
		Fit,
		Select,
		Search,
		Close
	}

	// Colours, button styles and vector icons shared by the touch interface.
	public static class MobileTheme
	{
		public static Color BarCol => ColHelper.MakeCol255(21, 22, 26);
		public static Color PanelCol => ColHelper.MakeCol255(27, 28, 33);
		public static Color CardCol => ColHelper.MakeCol255(36, 38, 44);
		public static Color LineCol => ColHelper.MakeCol255(54, 56, 63);
		public static Color Accent => RewiredUI.Accent;
		public static Color TextCol => Color.white;
		public static Color SecondaryTextCol => RewiredUI.SecondaryText;
		public static Color DimTextCol => RewiredUI.DimText;
		public static Color WarningCol => new(1f, 0.82f, 0.3f);

		static readonly Color inactiveButtonCol = ColHelper.MakeCol255(38, 39, 44);
		static readonly Color inactiveTextCol = ColHelper.MakeCol255(104, 106, 114);

		public static ButtonTheme Neutral => Make(ColHelper.MakeCol255(46, 48, 55), ColHelper.MakeCol255(58, 61, 70), Color.white);
		public static ButtonTheme Quiet => Make(ColHelper.MakeCol255(34, 36, 41), ColHelper.MakeCol255(50, 53, 61), Color.white);
		public static ButtonTheme Primary => Make(ColHelper.MakeCol255(72, 108, 233), ColHelper.MakeCol255(92, 126, 245), Color.white);
		public static ButtonTheme Danger => Make(ColHelper.MakeCol255(122, 44, 48), ColHelper.MakeCol255(156, 58, 62), Color.white);
		public static ButtonTheme Toggle(bool on) => on ? Primary : Neutral;

		static ButtonTheme Make(Color normal, Color pressed, Color text)
		{
			ButtonTheme baseTheme = DrawSettings.ActiveUITheme.MainMenuButtonTheme;
			return new ButtonTheme
			{
				font = baseTheme.font,
				fontSize = baseTheme.fontSize * 0.86f,
				paddingScale = baseTheme.paddingScale,
				buttonCols = new ButtonTheme.StateCols(normal, ColHelper.Brighten(normal, 0.06f), pressed, inactiveButtonCol),
				textCols = new ButtonTheme.StateCols(text, text, text, inactiveTextCol)
			};
		}

		public static ButtonTheme WithFontScale(ButtonTheme theme, float scale)
		{
			theme.fontSize *= scale;
			return theme;
		}

		// Buttons of the touch interface are drawn at finger size, so their touch area is not
		// enlarged: enlarged areas of neighbouring buttons would overlap and both would press.
		public static bool Button(string label, ButtonTheme theme, Vector2 pos, Vector2 size, bool enabled = true, Anchor anchor = Anchor.TopLeft) =>
			UI.Button(label, theme, pos, size, enabled, false, false, anchor, false, 0, false, false);

		// A button with a vector icon and an optional label. With a label the icon sits on the
		// left; without one it is centred.
		public static bool IconButton(string label, MobileIcon icon, ButtonTheme theme, Vector2 pos, Vector2 size, bool enabled = true, Anchor anchor = Anchor.TopLeft)
		{
			bool hasLabel = !string.IsNullOrEmpty(label);
			float iconSize = Mathf.Min(size.y * 0.42f, 2.1f);
			const float iconGap = 0.8f;
			// Shifts the centred label right by half the icon and gap, so both stay centred as a group.
			float labelOffset = icon == MobileIcon.None ? 0 : (iconSize + iconGap) / 2;
			bool pressed = UI.Button(label ?? string.Empty, theme, pos, size, enabled, false, false, anchor, false, hasLabel ? labelOffset : 0, false, false);
			Bounds2D bounds = UI.PrevBounds;

			if (icon != MobileIcon.None)
			{
				float labelWidth = hasLabel ? UI.CalculateTextSize(label, theme.fontSize, theme.font).x : 0;
				float groupWidth = hasLabel ? iconSize + iconGap + labelWidth : iconSize;
				Vector2 iconCentre = new(bounds.Centre.x - groupWidth / 2 + iconSize / 2, bounds.Centre.y);
				Color col = enabled ? theme.textCols.normal : theme.textCols.inactive;
				DrawIcon(icon, iconCentre, iconSize, col);
			}

			UI.OverridePreviousBounds(bounds);
			return pressed;
		}

		public static void DrawIcon(MobileIcon icon, Vector2 c, float size, Color col)
		{
			float h = size / 2;
			float t = Mathf.Max(0.1f, size * 0.075f);

			switch (icon)
			{
				case MobileIcon.Menu:
					for (int i = -1; i <= 1; i++)
					{
						float y = c.y + i * h * 0.62f;
						UI.DrawLine(new Vector2(c.x - h, y), new Vector2(c.x + h, y), t, col);
					}
					break;
				case MobileIcon.Back:
					UI.DrawLine(new Vector2(c.x + h * 0.35f, c.y + h * 0.8f), new Vector2(c.x - h * 0.45f, c.y), t, col);
					UI.DrawLine(new Vector2(c.x - h * 0.45f, c.y), new Vector2(c.x + h * 0.35f, c.y - h * 0.8f), t, col);
					break;
				case MobileIcon.Undo:
				case MobileIcon.Redo:
				{
					// An arrow pointing back (or forward) that bends round from below.
					float dir = icon == MobileIcon.Undo ? 1 : -1;
					Vector2 P(float x, float y) => new(c.x + x * h * dir, c.y + y * h);
					const int segments = 10;
					const float radius = 0.42f;
					Vector2 arcCentre = new(0.2f, -0.08f);
					Vector2 prev = P(arcCentre.x, arcCentre.y + radius);
					for (int i = 1; i <= segments; i++)
					{
						float angle = Mathf.PI / 2 - Mathf.PI * i / segments;
						Vector2 next = P(arcCentre.x + Mathf.Cos(angle) * radius, arcCentre.y + Mathf.Sin(angle) * radius);
						UI.DrawLine(prev, next, t, col);
						prev = next;
					}

					UI.DrawLine(P(-0.8f, arcCentre.y + radius), P(arcCentre.x, arcCentre.y + radius), t, col);
					UI.DrawLine(prev, P(-0.35f, arcCentre.y - radius), t, col);
					UI.DrawLine(P(-0.8f, arcCentre.y + radius), P(-0.45f, arcCentre.y + radius + 0.35f), t, col);
					UI.DrawLine(P(-0.8f, arcCentre.y + radius), P(-0.45f, arcCentre.y + radius - 0.35f), t, col);
					break;
				}
				case MobileIcon.Play:
					Triangle(new Vector2(c.x - h * 0.6f, c.y + h * 0.8f), new Vector2(c.x - h * 0.6f, c.y - h * 0.8f), new Vector2(c.x + h * 0.8f, c.y), col);
					break;
				case MobileIcon.Pause:
					UI.DrawPanel(new Vector2(c.x - h * 0.4f, c.y), new Vector2(h * 0.45f, h * 1.6f), col);
					UI.DrawPanel(new Vector2(c.x + h * 0.4f, c.y), new Vector2(h * 0.45f, h * 1.6f), col);
					break;
				case MobileIcon.Step:
					Triangle(new Vector2(c.x - h * 0.8f, c.y + h * 0.75f), new Vector2(c.x - h * 0.8f, c.y - h * 0.75f), new Vector2(c.x + h * 0.35f, c.y), col);
					UI.DrawPanel(new Vector2(c.x + h * 0.65f, c.y), new Vector2(h * 0.3f, h * 1.5f), col);
					break;
				case MobileIcon.Plus:
					UI.DrawLine(new Vector2(c.x - h * 0.8f, c.y), new Vector2(c.x + h * 0.8f, c.y), t, col);
					UI.DrawLine(new Vector2(c.x, c.y - h * 0.8f), new Vector2(c.x, c.y + h * 0.8f), t, col);
					break;
				case MobileIcon.Minus:
					UI.DrawLine(new Vector2(c.x - h * 0.8f, c.y), new Vector2(c.x + h * 0.8f, c.y), t, col);
					break;
				case MobileIcon.Fit:
					for (int sx = -1; sx <= 1; sx += 2)
					{
						for (int sy = -1; sy <= 1; sy += 2)
						{
							Vector2 corner = new(c.x + sx * h * 0.85f, c.y + sy * h * 0.85f);
							UI.DrawLine(corner, corner - new Vector2(sx * h * 0.55f, 0), t, col);
							UI.DrawLine(corner, corner - new Vector2(0, sy * h * 0.55f), t, col);
						}
					}
					break;
				case MobileIcon.Select:
				{
					// A dashed square.
					const int dashes = 3;
					float side = h * 1.7f;
					float dash = side / (dashes * 2 - 1);
					Vector2 bl = new(c.x - side / 2, c.y - side / 2);
					for (int i = 0; i < dashes; i++)
					{
						float a = i * dash * 2;
						UI.DrawLine(bl + new Vector2(a, 0), bl + new Vector2(a + dash, 0), t, col);
						UI.DrawLine(bl + new Vector2(a, side), bl + new Vector2(a + dash, side), t, col);
						UI.DrawLine(bl + new Vector2(0, a), bl + new Vector2(0, a + dash), t, col);
						UI.DrawLine(bl + new Vector2(side, a), bl + new Vector2(side, a + dash), t, col);
					}
					break;
				}
				case MobileIcon.Search:
					Ring(new Vector2(c.x - h * 0.2f, c.y + h * 0.2f), h * 0.55f, t, col);
					UI.DrawLine(new Vector2(c.x + h * 0.2f, c.y - h * 0.2f), new Vector2(c.x + h * 0.85f, c.y - h * 0.85f), t * 1.2f, col);
					break;
				case MobileIcon.Close:
					UI.DrawLine(new Vector2(c.x - h * 0.7f, c.y - h * 0.7f), new Vector2(c.x + h * 0.7f, c.y + h * 0.7f), t, col);
					UI.DrawLine(new Vector2(c.x - h * 0.7f, c.y + h * 0.7f), new Vector2(c.x + h * 0.7f, c.y - h * 0.7f), t, col);
					break;
			}
		}

		static void Triangle(Vector2 a, Vector2 b, Vector2 c, Color col)
		{
			Draw.Triangle(UI.UIToScreenSpace(a), UI.UIToScreenSpace(b), UI.UIToScreenSpace(c), col);
		}

		static void Ring(Vector2 centre, float radius, float thickness, Color col)
		{
			Vector2 c = UI.UIToScreenSpace(centre);
			float r = (UI.UIToScreenSpace(centre + Vector2.right * radius) - c).x;
			float th = (UI.UIToScreenSpace(centre + Vector2.right * thickness) - c).x;
			Draw.PointOutline(c, r, th, col);
		}

		// A raised card with a thin outline, used for floating bars and sheets.
		public static Bounds2D Card(Vector2 pos, Vector2 size, Anchor anchor)
		{
			UI.DrawPanel(pos, size, CardCol, anchor);
			Bounds2D bounds = UI.PrevBounds;
			const float w = 0.06f;
			UI.DrawLine(bounds.BottomLeft, bounds.TopLeft, w, LineCol);
			UI.DrawLine(bounds.TopLeft, bounds.TopRight, w, LineCol);
			UI.DrawLine(bounds.TopRight, bounds.BottomRight, w, LineCol);
			UI.DrawLine(bounds.BottomRight, bounds.BottomLeft, w, LineCol);
			UI.OverridePreviousBounds(bounds);
			return bounds;
		}

		public static void Text(string text, Vector2 pos, float scale, Color col, Anchor anchor = Anchor.TextCentreLeft, bool bold = false)
		{
			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			UI.DrawText(text, bold ? theme.FontBold : theme.FontRegular, theme.FontSizeRegular * scale, pos, anchor, col);
		}

		public static float TextWidth(string text, float scale, bool bold = false)
		{
			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			return UI.CalculateTextSize(text, theme.FontSizeRegular * scale, bold ? theme.FontBold : theme.FontRegular).x;
		}

		// Cuts a label to fit a width, ending it with "..." when shortened.
		public static string Fit(string text, float width, float scale, bool bold = false)
		{
			if (string.IsNullOrEmpty(text) || TextWidth(text, scale, bold) <= width) return text;
			for (int len = text.Length - 1; len > 1; len--)
			{
				string cut = text.Substring(0, len) + "...";
				if (TextWidth(cut, scale, bold) <= width) return cut;
			}

			return text.Substring(0, 1);
		}
	}
}
