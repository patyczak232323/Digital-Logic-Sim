using DLS.Game;
using Seb.Types;
using Seb.Vis;
using Seb.Vis.UI;
using UnityEngine;

namespace DLS.Graphics
{
	public static class TouchProjectHeader
	{
		public static void Draw(Project project)
		{
			if (!TouchUILayout.Enabled || project == null) return;

			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			float totalHeight = TouchUILayout.TopBarTotalHeight;
			float left = TouchUILayout.SafeLeft + TouchUILayout.EdgePadding;
			float right = TouchUILayout.SafeRight + TouchUILayout.EdgePadding;
			float contentY = UI.Height - TouchUILayout.SafeTop - TouchUILayout.TopBarHeight / 2f;
			Color panelCol = new(0.08f, 0.08f, 0.095f, 0.96f);
			Color dim = Color.white * 0.62f;

			UI.DrawPanel(UI.TopLeft, new Vector2(UI.Width, totalHeight), panelCol, Anchor.TopLeft);

			float x = left;
			bool nested = project.chipViewStack.Count > 1;
			if (nested)
			{
				const float backWidth = 11.5f;
				if (UI.Button(
					    "BACK",
					    theme.ButtonTheme,
					    new Vector2(x, contentY),
					    new Vector2(backWidth, TouchUILayout.TouchButtonHeight - 0.4f),
					    true,
					    false,
					    false,
					    Anchor.CentreLeft))
				{
					project.ReturnToPreviousViewedChip();
				}
				x += backWidth + TouchUILayout.TouchGap;
			}

			float statusWidth = project.simPaused ? 23f : 25f;
			float stepWidth = project.simPaused ? 11f : 0f;
			float reservedRight = statusWidth + (project.simPaused ? stepWidth + TouchUILayout.TouchGap : 0f);
			float textWidth = Mathf.Max(12f, UI.Width - right - x - reservedRight - TouchUILayout.TouchGap);

			string chipName = project.ViewedChip?.ChipName ?? "PROJECT";
			string title = FitText(chipName, textWidth, theme.FontSizeRegular * 0.98f, theme.FontBold);
			UI.DrawText(
				title,
				theme.FontBold,
				theme.FontSizeRegular * 0.98f,
				new Vector2(x, contentY + 0.72f),
				Anchor.TextCentreLeft,
				Color.white);

			string breadcrumb = nested ? project.viewedChipsString : project.description.ProjectName;
			breadcrumb = FitText(breadcrumb, textWidth, theme.FontSizeRegular * 0.65f, theme.FontRegular);
			UI.DrawText(
				breadcrumb,
				theme.FontRegular,
				theme.FontSizeRegular * 0.65f,
				new Vector2(x, contentY - 0.85f),
				Anchor.TextCentreLeft,
				dim);

			float rightX = UI.Width - right;

			if (project.simPaused)
			{
				if (UI.Button(
					    "STEP",
					    theme.ButtonTheme,
					    new Vector2(rightX - statusWidth - TouchUILayout.TouchGap, contentY),
					    new Vector2(stepWidth, TouchUILayout.TouchButtonHeight - 0.4f),
					    true,
					    false,
					    false,
					    Anchor.CentreRight))
				{
					project.advanceSingleSimStep = true;
				}

				UI.DrawPanel(
					new Vector2(rightX, contentY),
					new Vector2(statusWidth, TouchUILayout.TouchButtonHeight - 0.4f),
					new Color(0.42f, 0.30f, 0.06f, 0.95f),
					Anchor.CentreRight);
				Bounds2D statusBounds = UI.PrevBounds;
				UI.DrawText(
					$"PAUSED  #{project.simPausedSingleStepCounter}",
					theme.FontBold,
					theme.FontSizeRegular * 0.75f,
					statusBounds.Centre,
					Anchor.TextFirstLineCentre,
					Color.yellow);
			}
			else
			{
				UI.DrawPanel(
					new Vector2(rightX, contentY),
					new Vector2(statusWidth, TouchUILayout.TouchButtonHeight - 0.4f),
					new Color(0.08f, 0.25f, 0.14f, 0.95f),
					Anchor.CentreRight);
				Bounds2D statusBounds = UI.PrevBounds;
				UI.DrawText(
					$"RUN  {FormatRate(project.simAvgTicksPerSec)}",
					theme.FontBold,
					theme.FontSizeRegular * 0.75f,
					statusBounds.Centre,
					Anchor.TextFirstLineCentre,
					Color.white);
			}
		}

		static string FormatRate(double stepsPerSecond)
		{
			if (stepsPerSecond >= 1_000_000) return $"{stepsPerSecond / 1_000_000d:0.00}M/s";
			if (stepsPerSecond >= 1_000) return $"{stepsPerSecond / 1_000d:0.0}K/s";
			return $"{stepsPerSecond:0}/s";
		}

		static string FitText(string text, float maxWidth, float fontSize, FontType font)
		{
			if (string.IsNullOrEmpty(text)) return string.Empty;
			if (Draw.CalculateTextBoundsSize(text, fontSize, font).x <= maxWidth) return text;

			const string ellipsis = "...";
			int low = 0;
			int high = text.Length;
			while (low < high)
			{
				int mid = (low + high + 1) / 2;
				string candidate = text.Substring(0, mid) + ellipsis;
				if (Draw.CalculateTextBoundsSize(candidate, fontSize, font).x <= maxWidth) low = mid;
				else high = mid - 1;
			}

			return text.Substring(0, low) + ellipsis;
		}
	}
}
