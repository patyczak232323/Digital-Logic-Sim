using Seb.Helpers;
using Seb.Types;
using Seb.Vis.UI;
using UnityEngine;

namespace DLS.Graphics
{
	public static class RewiredUI
	{
		public const float SectionHeaderHeight = 2.15f;
		public const float InnerPad = 0.8f;

		public static Color Accent => DrawSettings.ActiveUITheme.MainMenuButtonTheme.buttonCols.hover;
		public static Color Surface => ColHelper.Darken(DrawSettings.ActiveUITheme.MenuPanelCol, 0.06f);
		public static Color SurfaceRaised => ColHelper.Darken(DrawSettings.ActiveUITheme.MenuPanelCol, 0.025f);
		public static Color DimText => new(1, 1, 1, 0.58f);
		public static Color SecondaryText => new(1, 1, 1, 0.74f);

		public static Bounds2D DrawSectionHeader(
			string title,
			Vector2 topLeft,
			float width,
			bool major = false,
			string rightText = null)
		{
			float height = major ? 2.7f : SectionHeaderHeight;
			return DrawSectionHeader(title, topLeft, width, height, major, rightText);
		}

		public static Bounds2D DrawSectionHeader(
			string title,
			Vector2 topLeft,
			float width,
			float height,
			bool major,
			string rightText = null)
		{
			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			Color bg = ColHelper.Darken(theme.MenuPanelCol, major ? 0.02f : 0.035f);

			UI.DrawPanel(topLeft, new Vector2(width, height), bg, Anchor.TopLeft);
			Bounds2D bounds = UI.PrevBounds;

			UI.DrawText(
				title,
				theme.FontBold,
				theme.FontSizeRegular * (major ? 0.86f : 0.72f),
				bounds.CentreLeft + Vector2.right * InnerPad,
				Anchor.TextCentreLeft,
				Color.white);

			if (!string.IsNullOrEmpty(rightText))
			{
				UI.DrawText(
					rightText,
					theme.FontRegular,
					theme.FontSizeRegular * 0.62f,
					bounds.CentreRight + Vector2.left * InnerPad,
					Anchor.TextCentreRight,
					SecondaryText);
			}

			UI.DrawLine(bounds.BottomLeft, bounds.BottomRight, 0.055f, Accent);
			UI.OverridePreviousBounds(bounds);
			return bounds;
		}

		public static Bounds2D DrawCard(Vector2 topLeft, Vector2 size, bool raised = false)
		{
			UI.DrawPanel(topLeft, size, raised ? SurfaceRaised : Surface, Anchor.TopLeft);
			return UI.PrevBounds;
		}

		public static void DrawLabel(
			string text,
			Vector2 pos,
			bool bold = false,
			float scale = 0.68f,
			Color? colour = null,
			Anchor anchor = Anchor.TextCentreLeft)
		{
			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			UI.DrawText(
				text,
				bold ? theme.FontBold : theme.FontRegular,
				theme.FontSizeRegular * scale,
				pos,
				anchor,
				colour ?? SecondaryText);
		}

		public static void DrawFrame(Bounds2D bounds, bool accentTop = true)
		{
			Color border = ColHelper.MakeCol255(54, 56, 63);
			const float width = 0.05f;
			UI.DrawLine(bounds.BottomLeft, bounds.TopLeft, width, border);
			UI.DrawLine(bounds.TopLeft, bounds.TopRight, accentTop ? 0.07f : width, accentTop ? Accent : border);
			UI.DrawLine(bounds.TopRight, bounds.BottomRight, width, border);
			UI.DrawLine(bounds.BottomRight, bounds.BottomLeft, width, border);
			UI.OverridePreviousBounds(bounds);
		}
	}
}
