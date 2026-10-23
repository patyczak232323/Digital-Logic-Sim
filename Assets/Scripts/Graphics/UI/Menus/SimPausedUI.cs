using DLS.Game;
using Seb.Types;
using Seb.Vis;
using Seb.Vis.UI;
using UnityEngine;
using static DLS.Graphics.DrawSettings;

namespace DLS.Graphics
{
	public static class SimPausedUI
	{
		static int stepCountPrev;
		static string stepString;
		
		public static void DrawPausedBanner()
		{
			UI.DrawPanel(UI.TopLeft, new Vector2(UI.Width, InfoBarHeight), ActiveUITheme.InfoBarCol, Anchor.TopLeft);
			UI.DrawLine(UI.TopLeft + Vector2.down * InfoBarHeight, UI.TopRight + Vector2.down * InfoBarHeight, 0.06f, RewiredUI.Accent);
			Bounds2D panelBounds = UI.PrevBounds;

			string breakpoint = Project.ActiveProject.lastBreakpointHit;
			string text = breakpoint == null
				? "SIMULATION PAUSED  <color=#9aa7dfff>(SPACE = STEP)"
				: $"BREAKPOINT: {breakpoint.ToUpperInvariant()}  <color=#9aa7dfff>(SPACE = STEP)";
			UI.DrawText(text, MenuHelper.Theme.FontBold, MenuHelper.Theme.FontSizeRegular * 0.82f, panelBounds.Centre, Anchor.TextCentre, breakpoint == null ? Color.white : Color.yellow);

			if (stepCountPrev != Project.ActiveProject.simPausedSingleStepCounter || string.IsNullOrEmpty(stepString))
			{
				stepCountPrev = Project.ActiveProject.simPausedSingleStepCounter;
				stepString = Project.ActiveProject.simPausedSingleStepCounter + "";
			}

			Vector2 frameLabelPos = panelBounds.CentreRight + Vector2.left * 1;
			UI.DrawText(stepString, ActiveUITheme.FontBold, ActiveUITheme.FontSizeRegular, frameLabelPos, Anchor.TextCentreRight, Color.white * 0.8f);
		}
	}
}