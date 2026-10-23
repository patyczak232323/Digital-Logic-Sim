using System;
using System.Collections.Generic;
using DLS.Description;
using DLS.Game;
using Seb.Types;
using Seb.Vis;
using Seb.Vis.UI;
using UnityEngine;

namespace DLS.Graphics
{
	// Guided steps from NAND to a 4-bit adder. Each step names the chip to build and its
	// pins; CHECK compares the saved chip's truth table with the goal.
	public static class ChallengesMenu
	{
		const string PrefCompleted = "rewired.challenges.completed";

		static int selected;
		static bool showHint;
		static string status;
		static Color statusColour;
		static readonly HashSet<string> completed = new(StringComparer.OrdinalIgnoreCase);

		public static void Open() => UIDrawer.SetActiveMenu(UIDrawer.MenuType.Challenges);

		public static void OnMenuOpened()
		{
			LoadProgress();
			selected = Array.FindIndex(ChallengeLibrary.All, c => !completed.Contains(c.ChipName));
			if (selected < 0) selected = 0;
			SelectStep(selected);
		}

		public static void DrawMenu()
		{
			MenuHelper.DrawBackgroundOverlay();
			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			Challenge[] steps = ChallengeLibrary.All;

			Bounds2D area;
			if (MobileUI.IsActive)
			{
				Rect safe = MobileUI.SafeRectUI;
				area = Bounds2D.CreateFromTopLeftAndSize(new Vector2(safe.xMin + 1f, safe.yMax - 6.2f), new Vector2(safe.width - 2f, safe.height - 7.2f));
			}
			else
			{
				area = Bounds2D.CreateFromCentreAndSize(UI.Centre, new Vector2(Mathf.Min(UI.Width * 0.75f, 88f), UI.Height * 0.8f));
			}

			UI.DrawPanel(area, RewiredUI.SurfaceRaised);
			RewiredUI.DrawFrame(area);
			RewiredUI.DrawSectionHeader("CHALLENGES", area.TopLeft, area.Width, true, $"{completed.Count} / {steps.Length} DONE");
			float top = UI.PrevBounds.Bottom - 0.8f;

			// ---- Step list ----
			float listWidth = Mathf.Min(24f, area.Width * 0.32f);
			float rowHeight = Mathf.Min(MobileUI.IsActive ? 4.2f : 3.2f, (top - area.Bottom - 0.8f) / steps.Length - 0.3f);
			for (int i = 0; i < steps.Length; i++)
			{
				bool done = completed.Contains(steps[i].ChipName);
				ButtonTheme buttonTheme = i == selected ? theme.ProjectSelectionButtonSelected : done ? theme.ChipLibraryCollectionToggleOn : theme.ProjectSelectionButton;
				string label = $"{i + 1}. {steps[i].ChipName}" + (done ? "  (DONE)" : string.Empty);
				Vector2 pos = new(area.Left + 0.8f, top - i * (rowHeight + 0.3f));
				if (UI.Button(label, buttonTheme, pos, new Vector2(listWidth, rowHeight), true, false, false, Anchor.TopLeft, true, 0.6f))
				{
					SelectStep(i);
				}
			}

			// ---- Selected step ----
			Challenge step = steps[selected];
			float x = area.Left + listWidth + 2f;
			float y = top - 0.6f;
			float detailWidth = area.Right - x - 0.8f;

			RewiredUI.DrawLabel($"STEP {selected + 1} OF {steps.Length}", new Vector2(x, y), true, 0.62f, RewiredUI.DimText);
			y -= 1.8f;
			RewiredUI.DrawLabel("BUILD A CHIP NAMED " + step.ChipName, new Vector2(x, y), true, 0.9f, Color.white);
			y -= 2.4f;
			RewiredUI.DrawLabel(step.Goal, new Vector2(x, y), false, 0.7f, Color.white);
			y -= 1.9f;
			RewiredUI.DrawLabel(step.PinSummary, new Vector2(x, y), false, 0.66f, RewiredUI.Accent);
			y -= 1.7f;
			RewiredUI.DrawLabel("Gates allowed: " + string.Join(", ", Array.ConvertAll(step.AllowedBuiltins, ChipTypeHelper.GetName)) + " and your own chips", new Vector2(x, y), false, 0.62f, RewiredUI.SecondaryText);
			y -= 1.9f;
			if (showHint) RewiredUI.DrawLabel("Hint: " + step.Hint, new Vector2(x, y), false, 0.64f, Color.yellow);
			y -= 2.4f;
			RewiredUI.DrawLabel(status ?? string.Empty, new Vector2(x, y), true, 0.66f, statusColour);

			// ---- Actions ----
			float buttonHeight = MobileUI.IsActive ? 4.4f : DrawSettings.ButtonHeight * 1.2f;
			float gap = 0.5f;
			float buttonWidth = (detailWidth - gap * 3) / 4;
			Vector2 buttonPos = new(x, area.Bottom + 0.8f + buttonHeight);
			bool isLast = selected == steps.Length - 1;

			bool check = UI.Button("CHECK", theme.ButtonTheme, buttonPos, new Vector2(buttonWidth, buttonHeight), true, false, false, Anchor.TopLeft);
			bool hint = UI.Button(showHint ? "HIDE HINT" : "HINT", theme.ButtonTheme, buttonPos + Vector2.right * (buttonWidth + gap), new Vector2(buttonWidth, buttonHeight), true, false, false, Anchor.TopLeft);
			bool next = UI.Button("NEXT", theme.ButtonTheme, buttonPos + Vector2.right * (buttonWidth + gap) * 2, new Vector2(buttonWidth, buttonHeight), !isLast, false, false, Anchor.TopLeft);
			bool close = UI.Button("CLOSE", theme.ButtonTheme, buttonPos + Vector2.right * (buttonWidth + gap) * 3, new Vector2(buttonWidth, buttonHeight), true, false, false, Anchor.TopLeft);

			if (check) Check(step);
			else if (hint) showHint = !showHint;
			else if (next) SelectStep(selected + 1);
			else if (close || KeyboardShortcuts.CancelShortcutTriggered) UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);
		}

		static void SelectStep(int index)
		{
			selected = Mathf.Clamp(index, 0, ChallengeLibrary.All.Length - 1);
			showHint = false;
			bool done = completed.Contains(ChallengeLibrary.All[selected].ChipName);
			status = done ? "Done. CHECK again any time." : "Build the chip, save it with this name, then press CHECK.";
			statusColour = done ? new Color(0.35f, 0.9f, 0.45f) : RewiredUI.SecondaryText;
		}

		static void Check(Challenge step)
		{
			Project project = Project.ActiveProject;
			if (project == null) return;

			ChallengeResult result = ChallengeLibrary.Check(step, project.chipLibrary);
			status = result.Message;
			statusColour = result.Passed ? new Color(0.35f, 0.9f, 0.45f) : Color.yellow;

			if (result.Passed && completed.Add(step.ChipName)) SaveProgress();
		}

		static void LoadProgress()
		{
			completed.Clear();
			string saved = PlayerPrefs.GetString(PrefCompleted, string.Empty);
			foreach (string name in saved.Split('|', StringSplitOptions.RemoveEmptyEntries)) completed.Add(name);
		}

		static void SaveProgress()
		{
			PlayerPrefs.SetString(PrefCompleted, string.Join("|", completed));
			PlayerPrefs.Save();
		}
	}
}
