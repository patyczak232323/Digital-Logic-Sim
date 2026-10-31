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

		// The R as square pixels on a 4 x 6 grid (top row first), drawn in the box
		// LetterLeft..LetterRight, LetterBottom..LetterTop, like the pixel font of the title.
		static readonly string[] LetterPixels =
		{
			"###.",
			"#..#",
			"#..#",
			"###.",
			"#.#.",
			"#..#"
		};
		const float LetterLeft = -0.24f;
		const float LetterRight = 0.24f;
		const float LetterBottom = -0.36f;
		const float LetterTop = 0.36f;

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

			// One box per horizontal run of pixels.
			int cols = LetterPixels[0].Length;
			float cw = (LetterRight - LetterLeft) / cols;
			float rh = (LetterTop - LetterBottom) / LetterPixels.Length;
			for (int r = 0; r < LetterPixels.Length; r++)
			{
				string line = LetterPixels[r];
				float y1 = LetterTop - r * rh;
				for (int c = 0; c < cols; c++)
				{
					if (line[c] != '#') continue;
					int start = c;
					while (c + 1 < cols && line[c + 1] == '#') c++;
					Box(LetterLeft + start * cw, y1 - rh, LetterLeft + (c + 1) * cw, y1, Letter);
				}
			}
		}
	}
}
