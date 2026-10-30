using Seb.Helpers;
using Seb.Vis.UI;
using UnityEngine;

namespace DLS.Graphics
{
	// The Rewired logo: a NAND gate, the one gate everything in Rewired is built from, with two
	// input wires and an output wire. The app icon (Assets/Build/Icon.png) is the same drawing
	// on a rounded square, drawn by Tools/Logo/make_logo.py with the same geometry.
	public static class RewiredLogo
	{
		static readonly Color GateColour = ColHelper.MakeCol255(87, 94, 230);
		static readonly Color OutlineColour = ColHelper.MakeCol255(242, 243, 255);
		static readonly Color InputA = ColHelper.MakeCol255(255, 90, 95);
		static readonly Color InputB = ColHelper.MakeCol255(62, 166, 255);
		static readonly Color Output = ColHelper.MakeCol255(255, 210, 63);
		static readonly Color BubbleHole = ColHelper.MakeCol255(14, 15, 32);

		const float WireThickness = 0.075f;
		const float PinRadius = 0.075f;

		// Draws the logo in a square of the given size.
		public static void Draw(Vector2 centre, float size)
		{
			float s = size / 2;
			Vector2 P(float x, float y) => centre + new Vector2(x, y) * s;

			Wire(P(-0.80f, 0.20f), P(-0.42f, 0.20f), InputA, s);
			Wire(P(-0.80f, -0.20f), P(-0.42f, -0.20f), InputB, s);
			Wire(P(0.58f, 0), P(0.80f, 0), Output, s);

			Gate(centre, s, 0, OutlineColour);
			Gate(centre, s, 0.055f, GateColour);
			UI.DrawCircle(P(0.505f, 0), 0.085f * s, OutlineColour);
			UI.DrawCircle(P(0.505f, 0), 0.035f * s, BubbleHole);
		}

		static void Wire(Vector2 a, Vector2 b, Color col, float s)
		{
			UI.DrawLine(a, b, WireThickness * s, col);
			UI.DrawCircle(a, PinRadius * s, col);
			UI.DrawCircle(b, WireThickness / 2 * s, col);
		}

		// D shape: a box from x0 to xm and a half disc of radius h centred on xm.
		static void Gate(Vector2 centre, float s, float inset, Color col)
		{
			const float xm = 0.02f;
			float x0 = -0.42f + inset;
			float h = 0.40f - inset;
			UI.DrawPanel(centre + new Vector2((x0 + xm) / 2, 0) * s, new Vector2(xm - x0, 2 * h) * s, col);
			UI.DrawCircle(centre + new Vector2(xm, 0) * s, h * s, col);
		}
	}
}
