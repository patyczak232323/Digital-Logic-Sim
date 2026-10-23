using System;
using System.Globalization;
using System.Linq;
using System.Text;
using DLS.Description;
using DLS.Game;
using DLS.Graphics;
using Seb.Types;
using UnityEngine;

namespace DLS.Export
{
	// Draws a chip's schematic (chips, pins, wires and labels) as a standalone SVG, using
	// the same element positions as the editor. Wire colours show the current signal state.
	public static class SchematicSvgExporter
	{
		const float PixelsPerUnit = 120f;
		const float Margin = 0.6f;

		public static string Export(DevChipInstance chip, string title)
		{
			Bounds2D bounds = CalculateBounds(chip);
			float width = (bounds.Width + Margin * 2) * PixelsPerUnit;
			float height = (bounds.Height + Margin * 2) * PixelsPerUnit;

			// Editor space has y up; SVG has y down. Map world (x, y) to ((x - left), (top - y)).
			float left = bounds.Left - Margin;
			float top = bounds.Top + Margin;

			StringBuilder sb = new();
			sb.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{F(width)}\" height=\"{F(height)}\" viewBox=\"0 0 {F(width)} {F(height)}\">");
			sb.AppendLine($"<title>{Escape(title)}</title>");
			sb.AppendLine($"<rect width=\"100%\" height=\"100%\" fill=\"{Hex(DrawSettings.ActiveTheme.BackgroundCol)}\"/>");
			sb.AppendLine("<g font-family=\"Consolas, 'DejaVu Sans Mono', monospace\" text-anchor=\"middle\" dominant-baseline=\"central\">");

			foreach (WireInstance wire in chip.Wires.OrderBy(w => w.drawOrder))
			{
				if (wire.WirePointCount < 2) continue;
				StringBuilder points = new();
				for (int i = 0; i < wire.WirePointCount; i++)
				{
					Vector2 p = wire.GetWirePoint(i);
					points.Append(F((p.x - left) * PixelsPerUnit)).Append(',').Append(F((top - p.y) * PixelsPerUnit)).Append(' ');
				}

				float thickness = DrawSettings.WireThickness * 2 * PixelsPerUnit * (wire.bitCount == PinBitCount.Bit1 ? 1f : 1.8f);
				sb.AppendLine($"<polyline points=\"{points.ToString().TrimEnd()}\" fill=\"none\" stroke=\"{Hex(wire.GetColour(0))}\" stroke-width=\"{F(thickness)}\" stroke-linejoin=\"round\" stroke-linecap=\"round\"/>");
			}

			foreach (SubChipInstance subChip in chip.GetSubchips()) DrawSubChip(sb, subChip, left, top);
			foreach (DevPinInstance devPin in chip.Elements.OfType<DevPinInstance>()) DrawDevPin(sb, devPin, left, top);

			sb.AppendLine("</g>");
			sb.AppendLine("</svg>");
			return sb.ToString();
		}

		static void DrawSubChip(StringBuilder sb, SubChipInstance subChip, float left, float top)
		{
			Bounds2D body = Bounds2D.CreateFromCentreAndSize(subChip.Position, subChip.Size);
			Rect(sb, body, left, top, subChip.Description.Colour, 0.06f);

			if (!subChip.IsBus && subChip.Description.NameLocation != NameDisplayLocation.Hidden)
			{
				string[] lines = (subChip.MultiLineName ?? subChip.Description.Name ?? string.Empty).Split('\n');
				float lineHeight = 0.22f;
				float y = subChip.Description.NameLocation == NameDisplayLocation.Top
					? body.Top - 0.2f
					: subChip.Position.y + (lines.Length - 1) * lineHeight / 2;
				Color textCol = Luminance(subChip.Description.Colour) > 0.55f ? Color.black : Color.white;
				foreach (string line in lines)
				{
					Text(sb, line, new Vector2(subChip.Position.x, y), left, top, 0.2f, textCol, true);
					y -= lineHeight;
				}
			}

			if (!string.IsNullOrWhiteSpace(subChip.Label))
			{
				Text(sb, subChip.Label, new Vector2(subChip.Position.x, body.Bottom - 0.18f), left, top, 0.15f, DrawSettings.ActiveTheme.PinLabelCol, false);
			}

			foreach (PinInstance pin in subChip.AllPins) DrawPin(sb, pin, left, top);
		}

		static void DrawDevPin(StringBuilder sb, DevPinInstance devPin, float left, float top)
		{
			Rect(sb, devPin.HandleBounds(), left, top, DrawSettings.ActiveTheme.DevPinHandle, 0.02f);
			Vector2 labelPos = devPin.HandlePosition - devPin.faceDir * 0.45f;
			Text(sb, devPin.Name, labelPos, left, top, 0.18f, DrawSettings.ActiveTheme.PinLabelCol, true, devPin.IsInputPin ? "end" : "start");
			DrawPin(sb, devPin.Pin, left, top);
		}

		static void DrawPin(StringBuilder sb, PinInstance pin, float left, float top)
		{
			Vector2 p = pin.GetWorldPos();
			float r = DrawSettings.PinRadius * PixelsPerUnit;
			sb.AppendLine($"<circle cx=\"{F((p.x - left) * PixelsPerUnit)}\" cy=\"{F((top - p.y) * PixelsPerUnit)}\" r=\"{F(r)}\" fill=\"{Hex(DrawSettings.ActiveTheme.PinCol)}\"/>");
		}

		static void Rect(StringBuilder sb, Bounds2D b, float left, float top, Color fill, float cornerRadius)
		{
			sb.AppendLine($"<rect x=\"{F((b.Left - left) * PixelsPerUnit)}\" y=\"{F((top - b.Top) * PixelsPerUnit)}\" width=\"{F(b.Width * PixelsPerUnit)}\" height=\"{F(b.Height * PixelsPerUnit)}\" rx=\"{F(cornerRadius * PixelsPerUnit)}\" fill=\"{Hex(fill)}\"/>");
		}

		static void Text(StringBuilder sb, string text, Vector2 pos, float left, float top, float size, Color col, bool bold, string anchor = "middle")
		{
			string weight = bold ? " font-weight=\"bold\"" : string.Empty;
			string anchorAttr = anchor == "middle" ? string.Empty : $" text-anchor=\"{anchor}\"";
			sb.AppendLine($"<text x=\"{F((pos.x - left) * PixelsPerUnit)}\" y=\"{F((top - pos.y) * PixelsPerUnit)}\" font-size=\"{F(size * PixelsPerUnit)}\" fill=\"{Hex(col)}\"{weight}{anchorAttr}>{Escape(text)}</text>");
		}

		static Bounds2D CalculateBounds(DevChipInstance chip)
		{
			bool any = false;
			Vector2 min = Vector2.zero, max = Vector2.zero;

			void Include(Vector2 p)
			{
				if (!any)
				{
					min = max = p;
					any = true;
					return;
				}

				min = new Vector2(Mathf.Min(min.x, p.x), Mathf.Min(min.y, p.y));
				max = new Vector2(Mathf.Max(max.x, p.x), Mathf.Max(max.y, p.y));
			}

			foreach (IMoveable element in chip.Elements)
			{
				Bounds2D b = element.BoundingBox;
				Include(b.Min);
				Include(b.Max);
				if (element is DevPinInstance devPin)
				{
					// Leave room for the pin name beside the handle.
					float nameWidth = 0.12f * (devPin.Name?.Length ?? 0) + 0.5f;
					Include(devPin.HandlePosition - devPin.faceDir * nameWidth);
				}
			}

			foreach (WireInstance wire in chip.Wires)
			{
				for (int i = 0; i < wire.WirePointCount; i++) Include(wire.GetWirePoint(i));
			}

			if (!any) return Bounds2D.CreateFromCentreAndSize(Vector2.zero, Vector2.one * 4);
			return new Bounds2D(min, max);
		}

		static float Luminance(Color c) => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;

		static string Hex(Color c)
		{
			int r = Mathf.Clamp(Mathf.RoundToInt(c.r * 255), 0, 255);
			int g = Mathf.Clamp(Mathf.RoundToInt(c.g * 255), 0, 255);
			int b = Mathf.Clamp(Mathf.RoundToInt(c.b * 255), 0, 255);
			return $"#{r:X2}{g:X2}{b:X2}";
		}

		static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

		static string Escape(string s) => (s ?? string.Empty).Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
	}
}
