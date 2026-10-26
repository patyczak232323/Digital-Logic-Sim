using System;
using System.Collections.Generic;
using System.Linq;
using DLS.Game;
using Seb.Types;
using Seb.Vis;
using Seb.Vis.UI;
using UnityEngine;

namespace DLS.Graphics
{
	// Touch interface: while something is selected, its actions are shown as buttons along the
	// bottom of the board, so nothing depends on knowing about the long press. MORE opens the
	// full menu (colours, probes, export...).
	public static class MobileSelectionBar
	{
		const float BarHeight = 5.4f;
		const float ButtonHeight = 4f;
		const float Gap = 0.35f;

		readonly struct BarAction
		{
			public readonly string Label;
			public readonly Action OnPress;
			public readonly bool Enabled;
			public readonly bool Danger;

			// When the bar is too wide, actions with the highest value move into MORE first.
			// Zero means the action always stays: COPY and DELETE, which MORE does not have.
			public readonly int DropOrder;

			public BarAction(string label, Action onPress, bool enabled, int dropOrder, bool danger = false)
			{
				Label = label;
				OnPress = onPress;
				Enabled = enabled;
				DropOrder = dropOrder;
				Danger = danger;
			}
		}

		static readonly List<BarAction> actions = new();

		// Short names for a phone and the order they give way in; null keeps an action in MORE only.
		static (string label, int dropOrder) BarLabel(string menuText) => menuText switch
		{
			"VIEW" => ("LOOK INSIDE", 1),
			"EDIT" => ("EDIT", 1),
			"REBIND" => ("SET KEY", 1),
			"TRUTH TABLE" => ("TRUTH TABLE", 2),
			"BREAKPOINT" => ("BREAKPOINT", 2),
			"MIRROR H" => ("FLIP", 3),
			"FLIP" => ("FLIP", 3),
			"LABEL" => ("LABEL", 4),
			"OPEN" => ("EDIT CHIP", 5),
			"DELETE" => ("DELETE", 0),
			_ => (null, 0)
		};

		public static void Draw(Project project, Rect canvas)
		{
			ChipInteractionController controller = project.controller;
			List<IMoveable> selection = controller.SelectedElements;
			if (selection.Count == 0 || controller.IsPlacingOrMovingElementOrCreatingWire || controller.IsCreatingSelectionBox || ContextMenu.IsOpen) return;

			actions.Clear();
			bool canEdit = project.CanEditViewedChip;
			string title;
			IMoveable single = selection.Count == 1 ? selection[0] : null;
			bool showMore = false;

			if (single != null)
			{
				if (!ContextMenu.TryGetEntriesFor(single, out string header, out ContextMenu.MenuEntry[] entries)) return;
				title = header.Trim().ToUpperInvariant();

				BarAction? delete = null;
				foreach (ContextMenu.MenuEntry entry in entries)
				{
					if (ContextMenu.IsDivider(entry) || ContextMenu.IsColourEntry(entry))
					{
						showMore = true;
						continue;
					}

					(string label, int dropOrder) = BarLabel(entry.Text.Trim().ToUpperInvariant());
					if (label == null)
					{
						showMore = true;
						continue;
					}

					BarAction action = new(label, entry.OnPress, entry.IsEnabled(), dropOrder, label == "DELETE");
					if (action.Danger) delete = action;
					else actions.Add(action);
				}

				actions.Add(new BarAction("COPY", controller.DuplicateSelection, canEdit, 0));
				if (delete.HasValue) actions.Add(delete.Value);
			}
			else
			{
				title = $"{selection.Count} SELECTED";
				SubChipInstance[] subchips = selection.OfType<SubChipInstance>().Where(s => !s.IsBus).ToArray();
				actions.Add(new BarAction("COPY", controller.DuplicateSelection, canEdit, 0));
				actions.Add(new BarAction("FLIP", () => controller.MirrorSubChips(subchips, true), canEdit && subchips.Length > 0, 0));
				actions.Add(new BarAction("DELETE", controller.DeleteSelection, canEdit, 0, true));
			}

			// ---- Layout: move actions into MORE until the bar fits the board ----
			ButtonTheme theme = MobileTheme.Neutral;
			const float titleScale = 0.68f;
			float maxWidth = canvas.width - 3f;
			float titleWidth = Mathf.Min(MobileTheme.TextWidth(title, titleScale, true) + 1.6f, 15f);
			float moreWidth = ButtonWidth("MORE", theme);

			while (true)
			{
				float width = 0.6f + titleWidth + actions.Sum(a => ButtonWidth(a.Label, theme) + Gap) + (showMore ? moreWidth + Gap : 0) + 0.3f;
				if (width <= maxWidth) break;

				int removeIndex = -1;
				for (int i = 0; i < actions.Count; i++)
				{
					if (actions[i].DropOrder > 0 && (removeIndex < 0 || actions[i].DropOrder >= actions[removeIndex].DropOrder)) removeIndex = i;
				}

				if (removeIndex < 0) break;
				actions.RemoveAt(removeIndex);
				showMore = single != null;
			}

			float barWidth = 0.6f + titleWidth + actions.Sum(a => ButtonWidth(a.Label, theme) + Gap) + (showMore ? moreWidth + Gap : 0) + 0.3f;
			Vector2 bottomCentre = new(canvas.center.x, canvas.yMin + 0.7f);
			Bounds2D bar = MobileTheme.Card(bottomCentre, new Vector2(barWidth, BarHeight), Anchor.CentreBottom);

			string fittedTitle = MobileTheme.Fit(title, titleWidth - 1.2f, titleScale, true);
			MobileTheme.Text(fittedTitle, bar.CentreLeft + Vector2.right * 0.9f, titleScale, Color.white, Anchor.TextCentreLeft, true);

			float x = bar.Left + 0.6f + titleWidth;
			float y = bar.Centre.y + ButtonHeight / 2;
			Action pressed = null;
			foreach (BarAction action in actions)
			{
				float w = ButtonWidth(action.Label, theme);
				if (MobileTheme.Button(action.Label, action.Danger ? MobileTheme.Danger : theme, new Vector2(x, y), new Vector2(w, ButtonHeight), action.Enabled, Anchor.TopLeft))
				{
					pressed = action.OnPress;
				}

				x += w + Gap;
			}

			if (showMore && MobileTheme.Button("MORE", MobileTheme.Neutral, new Vector2(x, y), new Vector2(moreWidth, ButtonHeight), true, Anchor.TopLeft))
			{
				// Opens above the bar, clear of the finger that pressed MORE.
				ContextMenu.OpenFor(single, new Vector2(bar.Right - 2.5f, bar.Top + 0.6f));
			}

			UI.OverridePreviousBounds(bar);
			pressed?.Invoke();
		}

		static float ButtonWidth(string label, ButtonTheme theme) =>
			Mathf.Max(7f, UI.CalculateTextSize(label, theme.fontSize, theme.font).x + 2.4f);
	}
}
