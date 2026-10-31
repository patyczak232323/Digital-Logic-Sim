using Seb.Helpers;
using Seb.Vis.UI;
using UnityEngine;

namespace DLS.Graphics
{
	// The Rewired logo: a chip with a gold R on it, drawn with UI shapes. The app icon
	// (Assets/Build/Icon.png) is the same drawing on a rounded square, drawn by
	// Tools/Logo/make_logo.py with the same geometry (unit square -1..1, y up).
	public static class RewiredLogo
	{
		static readonly Color Body = ColHelper.MakeCol255(30, 33, 50);
		static readonly Color Border = ColHelper.MakeCol255(72, 108, 233);
		static readonly Color Pin = ColHelper.MakeCol255(168, 175, 198);
		static readonly Color Dot = ColHelper.MakeCol255(74, 79, 104);
		static readonly Color Letter = ColHelper.MakeCol255(255, 196, 0);

		const float BodyHalf = 0.55f;
		const float BorderWidth = 0.035f;
		const float PinWidth = 0.12f;
		const float PinLength = 0.12f;
		static readonly float[] PinOffsets = { -0.39f, -0.13f, 0.13f, 0.39f };

		// The R: boxes (x0, y0, x1, y1) and the leg as a thick line.
		static readonly Vector4[] LetterBoxes =
		{
			new(-0.25f, -0.33f, -0.12f, 0.34f), // stem
			new(-0.25f, 0.21f, 0.17f, 0.34f), // top
			new(0.12f, 0.05f, 0.25f, 0.29f), // right of the bowl
			new(-0.25f, 0.01f, 0.17f, 0.14f) // middle
		};
		static readonly Vector2 LegStart = new(0.06f, 0.06f);
		static readonly Vector2 LegEnd = new(0.22f, -0.30f);
		const float LegThickness = 0.14f;

		// Draws the logo in a square of the given size.
		public static void Draw(Vector2 centre, float size)
		{
			float s = size / 2;

			void Box(float x0, float y0, float x1, float y1, Color col) =>
				UI.DrawPanel(centre + new Vector2(x0 + x1, y0 + y1) / 2 * s, new Vector2(x1 - x0, y1 - y0) * s, col);

			const float h = BodyHalf;
			const float w = PinWidth / 2;
			foreach (float t in PinOffsets)
			{
				Box(-h - PinLength, t - w, -h, t + w, Pin);
				Box(h, t - w, h + PinLength, t + w, Pin);
				Box(t - w, h, t + w, h + PinLength, Pin);
				Box(t - w, -h - PinLength, t + w, -h, Pin);
			}

			Box(-h, -h, h, h, Border);
			const float inner = h - BorderWidth;
			Box(-inner, -inner, inner, inner, Body);
			UI.DrawCircle(centre + new Vector2(-0.38f, 0.38f) * s, 0.045f * s, Dot);

			foreach (Vector4 b in LetterBoxes) Box(b.x, b.y, b.z, b.w, Letter);
			// The leg is a quad with square ends, like the icon. UI.DrawLine would not do: its
			// thickness is the distance from the centre line and its ends are rounded, which drew
			// the leg twice as thick and poking out below the R.
			Vector2 dir = (LegEnd - LegStart).normalized;
			Vector2 side = new Vector2(-dir.y, dir.x) * (LegThickness / 2);
			Vector2 p0 = centre + (LegStart + side) * s;
			Vector2 p1 = centre + (LegEnd + side) * s;
			Vector2 p2 = centre + (LegEnd - side) * s;
			Vector2 p3 = centre + (LegStart - side) * s;
			UI.DrawTriangle(p0, p1, p2, Letter);
			UI.DrawTriangle(p0, p2, p3, Letter);
		}
	}
}
