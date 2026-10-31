using System.Collections.Generic;
using DLS.Description;
using DLS.Game;
using Seb.Helpers;
using Seb.Types;
using Seb.Vis;
using Seb.Vis.UI;
using UnityEngine;

namespace DLS.Graphics
{
	// The parts panel of the touch interface: chip collections on the left, the chips of the
	// chosen one on the right. Drag a chip onto the board to place it there, or tap it and
	// then tap the board. A vertical drag scrolls the list (upright, until it leaves the panel).
	public static class MobilePalette
	{
		public const float Width = 26f;
		const float RailWidth = 9.4f;
		const float Pad = 0.5f;
		const float RowHeight = 3.7f;
		const float RowGap = 0.3f;
		const float ItemHeight = 4.6f;
		const float ItemGap = 0.35f;
		const float DragThreshold = 1.1f;

		enum Region
		{
			None,
			Rail,
			Items
		}

		static readonly List<string> items = new();
		// Chips listed but not placeable here: the open chip itself, and chips that use it.
		static readonly HashSet<string> blocked = new();
		static readonly List<string> categories = new();
		static string selectedCategory;
		static float railScroll;
		static float itemScroll;

		static Region pressRegion;
		static Vector2 pressStart;
		static float scrollAtPress;
		static string pressedName;
		static bool pressScrolling;
		static bool dragPlacing;

		// Shown highlighted while it waits for a tap on the board.
		public static string ArmedChip { get; private set; }

		public static bool IsDragPlacing => dragPlacing;

		public static void Reset()
		{
			selectedCategory = null;
			railScroll = itemScroll = 0;
			pressRegion = Region.None;
			dragPlacing = false;
			ArmedChip = null;
		}

		// Sideways the panel is a column on the left; upright it is a strip along the bottom, as wide
		// as the screen, with the same rail and list inside.
		public static Rect PanelRect(Rect safe)
		{
			if (!MobileUI.Portrait) return Rect.MinMaxRect(safe.xMin, safe.yMin, safe.xMin + Width, safe.yMax - MobileUI.TopBarHeight);

			float height = Mathf.Clamp(safe.height * 0.36f, 30f, 48f);
			return Rect.MinMaxRect(safe.xMin, safe.yMin, safe.xMax, safe.yMin + height);
		}

		public static void Draw(Project project, Rect safe)
		{
			Rect panel = PanelRect(safe);
			UI.DrawPanel(new Vector2(panel.xMin, panel.yMax), panel.size, MobileTheme.PanelCol, Anchor.TopLeft);
			if (MobileUI.Portrait) UI.DrawLine(new Vector2(panel.xMin, panel.yMax), new Vector2(panel.xMax, panel.yMax), 0.06f, MobileTheme.LineCol);
			else UI.DrawLine(new Vector2(panel.xMax, panel.yMin), new Vector2(panel.xMax, panel.yMax), 0.06f, MobileTheme.LineCol);

			CollectCategories(project);
			CollectItems(project);
			if (!project.controller.IsPlacingElements && !dragPlacing) ArmedChip = null;

			Rect rail = Rect.MinMaxRect(panel.xMin + Pad, panel.yMin + Pad, panel.xMin + RailWidth, panel.yMax - Pad);
			Rect list = Rect.MinMaxRect(rail.xMax + Pad, panel.yMin + Pad, panel.xMax - Pad, panel.yMax - Pad);

			// The search button stays at the bottom of the rail.
			Rect searchRect = Rect.MinMaxRect(rail.xMin, rail.yMin, rail.xMax, rail.yMin + RowHeight);
			Rect railList = Rect.MinMaxRect(rail.xMin, searchRect.yMax + RowGap * 2, rail.xMax, rail.yMax);
			if (MobileTheme.IconButton("FIND", MobileIcon.Search, MobileTheme.Quiet, new Vector2(searchRect.xMin, searchRect.yMax), searchRect.size))
			{
				project.controller.CancelEverything();
				UIDrawer.SetActiveMenu(UIDrawer.MenuType.Search);
			}

			HandleInput(project, panel, railList, list);
			DrawRail(railList);
			DrawItems(project, list);
		}

		static void DrawRail(Rect rail)
		{
			float contentHeight = categories.Count * (RowHeight + RowGap);
			railScroll = Mathf.Clamp(railScroll, 0, Mathf.Max(0, contentHeight - rail.height));

			using (UI.CreateMaskScopeMinMax(rail.min, rail.max))
			{
				float y = rail.yMax + railScroll;
				foreach (string category in categories)
				{
					bool selected = category == selectedCategory;
					ButtonTheme theme = selected ? MobileTheme.Primary : MobileTheme.Quiet;
					UI.DrawPanel(new Vector2(rail.xMin, y), new Vector2(rail.width, RowHeight), theme.buttonCols.normal, Anchor.TopLeft);
					Bounds2D row = UI.PrevBounds;
					string label = MobileTheme.Fit(DisplayName(category), rail.width - 1f, 0.6f, true);
					MobileTheme.Text(label, row.CentreLeft + Vector2.right * 0.6f, 0.6f, selected ? Color.white : MobileTheme.SecondaryTextCol, Anchor.TextCentreLeft, true);
					y -= RowHeight + RowGap;
				}
			}
		}

		static void DrawItems(Project project, Rect list)
		{
			MobileTheme.Text(DisplayName(selectedCategory), new Vector2(list.xMin + 0.2f, list.yMax - 1f), 0.62f, MobileTheme.SecondaryTextCol, Anchor.TextCentreLeft, true);
			Rect area = ItemArea(list);

			// Chips are saved one at a time; a new one is where saved chips get used.
			if (IsMyChipsCategory(selectedCategory))
			{
				if (MobileTheme.Button("NEW CHIP", MobileTheme.Neutral, new Vector2(list.xMin, list.yMin), new Vector2(list.width, RowHeight), true, Anchor.BottomLeft))
				{
					MobileUI.CreateNewChip(project);
				}
			}

			float contentHeight = items.Count * (ItemHeight + ItemGap);
			itemScroll = Mathf.Clamp(itemScroll, 0, Mathf.Max(0, contentHeight - area.height));

			if (items.Count == 0)
			{
				string hint = IsMyChipsCategory(selectedCategory) ? "Chips you save with SAVE appear here." : "Nothing to add here.";
				UI.DrawText(UI.LineBreakByCharCount(hint, 16), DrawSettings.ActiveUITheme.FontRegular, DrawSettings.ActiveUITheme.FontSizeRegular * 0.6f,
					new Vector2(area.xMin + 0.2f, area.yMax - 1.2f), Anchor.TopLeft, MobileTheme.DimTextCol);
				return;
			}

			using (UI.CreateMaskScopeMinMax(area.min, area.max))
			{
				float y = area.yMax + itemScroll;
				foreach (string name in items)
				{
					DrawItem(project, name, new Vector2(area.xMin, y), area.width);
					y -= ItemHeight + ItemGap;
				}
			}

			// A hint that the list continues.
			if (contentHeight - itemScroll > area.height + 0.1f)
			{
				UI.DrawPanel(new Vector2(area.center.x, area.yMin), new Vector2(area.width, 0.25f), MobileTheme.Accent * 0.8f, Anchor.CentreBottom);
			}
		}

		static void DrawItem(Project project, string name, Vector2 topLeft, float width)
		{
			bool armed = name == ArmedChip;
			bool isBlocked = blocked.Contains(name);
			bool pressed = pressRegion == Region.Items && name == pressedName && !pressScrolling && InputHelper.IsMouseHeld(MouseButton.Left);
			Color bg = armed ? MobileTheme.Accent : isBlocked ? MobileTheme.PanelCol : pressed ? ColHelper.MakeCol255(58, 61, 70) : MobileTheme.CardCol;
			UI.DrawPanel(topLeft, new Vector2(width, ItemHeight), bg, Anchor.TopLeft);
			Bounds2D card = UI.PrevBounds;

			// Colour stripe in the chip's own colour.
			if (project.chipLibrary.TryGetChipDescription(name, out ChipDescription description))
			{
				UI.DrawPanel(card.TopLeft, new Vector2(0.45f, ItemHeight), description.Colour, Anchor.TopLeft);
			}

			string label = MobileTheme.Fit(name, width - 1.6f, 0.68f, true);
			MobileTheme.Text(label, new Vector2(card.Left + 1.1f, card.Centre.y + 0.75f), 0.68f, isBlocked ? MobileTheme.DimTextCol : Color.white, Anchor.TextCentreLeft, true);
			string about = isBlocked ? BlockedReason(project, name) : Describe(project, name);
			string detail = MobileTheme.Fit(about, width - 1.6f, 0.5f);
			MobileTheme.Text(detail, new Vector2(card.Left + 1.1f, card.Centre.y - 0.95f), 0.5f, armed ? Color.white : isBlocked ? MobileTheme.DimTextCol : MobileTheme.SecondaryTextCol);
		}

		static void HandleInput(Project project, Rect panel, Rect rail, Rect list)
		{
			Vector2 mouse = UI.ScreenToUISpace(InputHelper.MousePos);

			if (InputHelper.IsMouseDownThisFrame(MouseButton.Left) && UIDrawer.ActiveMenu == UIDrawer.MenuType.None)
			{
				pressRegion = rail.Contains(mouse) ? Region.Rail : ItemArea(list).Contains(mouse) ? Region.Items : Region.None;
				pressStart = mouse;
				pressScrolling = false;
				dragPlacing = false;
				scrollAtPress = pressRegion == Region.Rail ? railScroll : itemScroll;
				pressedName = pressRegion == Region.Items ? ItemAt(list, mouse) : pressRegion == Region.Rail ? CategoryAt(rail, mouse) : null;
			}

			if (pressRegion == Region.None) return;

			Vector2 delta = mouse - pressStart;
			if (InputHelper.IsMouseHeld(MouseButton.Left))
			{
				if (!pressScrolling && !dragPlacing)
				{
					// Dragging out sideways picks the chip up; dragging along the list scrolls it.
					bool sideways = delta.x > DragThreshold * 1.5f && delta.x > Mathf.Abs(delta.y);
					if (pressRegion == Region.Items && pressedName != null && !blocked.Contains(pressedName) && (sideways || !panel.Contains(mouse)))
					{
						project.controller.CancelEverything();
						project.controller.StartPlacing(pressedName);
						dragPlacing = true;
						ArmedChip = pressedName;
					}
					else if (Mathf.Abs(delta.y) > DragThreshold)
					{
						pressScrolling = true;
					}
				}

				// Upright the board is above the panel, so carrying a chip to it starts like
				// scrolling the list: once the finger leaves the panel, it picks the chip up.
				if (pressScrolling && pressRegion == Region.Items && pressedName != null && !blocked.Contains(pressedName) && !panel.Contains(mouse))
				{
					itemScroll = scrollAtPress;
					pressScrolling = false;
					project.controller.CancelEverything();
					project.controller.StartPlacing(pressedName);
					dragPlacing = true;
					ArmedChip = pressedName;
				}

				if (pressScrolling)
				{
					if (pressRegion == Region.Rail) railScroll = scrollAtPress + delta.y;
					else itemScroll = scrollAtPress + delta.y;
				}
			}

			if (InputHelper.IsMouseUpThisFrame(MouseButton.Left))
			{
				if (dragPlacing)
				{
					// Released over the board: put it there. Released back over the panel: never mind.
					bool overBoard = MobileUI.CanvasRectUI.Contains(mouse);
					if (!overBoard) project.controller.CancelEverything();
					else if (!project.controller.TryFinishPlacing()) MobileUI.ShowNotice("It overlaps something: tap a free spot to place it.");
				}
				else if (!pressScrolling && pressedName != null)
				{
					if (pressRegion == Region.Rail && CategoryAt(rail, mouse) == pressedName)
					{
						if (selectedCategory != pressedName) itemScroll = 0;
						selectedCategory = pressedName;
					}
					else if (pressRegion == Region.Items && ItemAt(list, mouse) == pressedName && blocked.Contains(pressedName))
					{
						MobileUI.ShowNotice($"{pressedName} can't go inside itself. Start a NEW CHIP to use it.");
					}
					else if (pressRegion == Region.Items && ItemAt(list, mouse) == pressedName)
					{
						// Tap: the chip waits for a tap on the board; tapping it again puts it back.
						bool wasArmed = ArmedChip == pressedName && project.controller.IsPlacingElements;
						project.controller.CancelEverything();
						if (!wasArmed)
						{
							project.controller.StartPlacing(pressedName);
							ArmedChip = pressedName;
						}
					}
				}

				pressRegion = Region.None;
				dragPlacing = false;
				pressedName = null;
			}
		}

		static string CategoryAt(Rect rail, Vector2 point)
		{
			if (!rail.Contains(point)) return null;
			int index = Mathf.FloorToInt((rail.yMax + railScroll - point.y) / (RowHeight + RowGap));
			if (index < 0 || index >= categories.Count) return null;
			float rowTop = rail.yMax + railScroll - index * (RowHeight + RowGap);
			return point.y >= rowTop - RowHeight ? categories[index] : null;
		}

		// The scrolling list of chips: below the category title, above MY CHIPS' NEW CHIP button.
		static Rect ItemArea(Rect list)
		{
			float bottom = IsMyChipsCategory(selectedCategory) ? list.yMin + RowHeight + RowGap * 2 : list.yMin;
			return Rect.MinMaxRect(list.xMin, bottom, list.xMax, list.yMax - 2.2f);
		}

		static string ItemAt(Rect list, Vector2 point)
		{
			Rect area = ItemArea(list);
			float top = area.yMax;
			if (!area.Contains(point)) return null;
			int index = Mathf.FloorToInt((top + itemScroll - point.y) / (ItemHeight + ItemGap));
			if (index < 0 || index >= items.Count) return null;
			float itemTop = top + itemScroll - index * (ItemHeight + ItemGap);
			return point.y >= itemTop - ItemHeight ? items[index] : null;
		}

		// The project's collections, then MY CHIPS with every chip the user has made. OTHER, where
		// saved chips go by default, is only listed if it holds built-in chips as well.
		static void CollectCategories(Project project)
		{
			categories.Clear();
			bool otherHasBuiltins = false;
			foreach (ChipCollection collection in project.description.ChipCollections)
			{
				if (collection.Name == OtherCollectionName)
				{
					otherHasBuiltins = collection.Chips.Exists(c => project.chipLibrary.IsBuiltinChip(c));
					continue;
				}

				categories.Add(collection.Name);
			}

			categories.Add(MyChipsCategory);
			if (otherHasBuiltins) categories.Add(OtherCollectionName);

			if (selectedCategory == null || !categories.Contains(selectedCategory))
			{
				// A new chip starts with its pins, so open IN/OUT first.
				selectedCategory = categories.Contains("IN/OUT") ? "IN/OUT" : categories.Count > 0 ? categories[0] : null;
			}
		}

		static void CollectItems(Project project)
		{
			items.Clear();
			blocked.Clear();
			if (selectedCategory == MyChipsCategory)
			{
				foreach (ChipDescription chip in project.chipLibrary.allChips)
				{
					if (chip.ChipType != ChipType.Custom) continue;
					items.Add(chip.Name);
					if (!project.ViewedChip.CanAddSubchip(chip.Name)) blocked.Add(chip.Name);
				}

				items.Sort(System.StringComparer.OrdinalIgnoreCase);
				return;
			}

			foreach (ChipCollection collection in project.description.ChipCollections)
			{
				if (collection.Name != selectedCategory) continue;
				foreach (string chip in collection.Chips)
				{
					if (project.chipLibrary.HasChip(chip) && project.ViewedChip.CanAddSubchip(chip) && !items.Contains(chip)) items.Add(chip);
				}
			}
		}

		const string OtherCollectionName = "OTHER";
		// A key no collection can have, shown as MY CHIPS.
		const string MyChipsCategory = "\u0001MY CHIPS";

		static bool IsMyChipsCategory(string name) => name == MyChipsCategory;

		static string DisplayName(string category) => IsMyChipsCategory(category) ? "MY CHIPS" : category ?? string.Empty;

		static string BlockedReason(Project project, string name) =>
			ChipDescription.NameMatch(name, project.ActiveDevChipName) ? "open now: can't go inside itself" : "uses the open chip";

		// A few words on what each chip is for, so names like 1-4BIT explain themselves.
		static string Describe(Project project, string name)
		{
			if (!project.chipLibrary.TryGetChipDescription(name, out ChipDescription description)) return string.Empty;

			return description.ChipType switch
			{
				ChipType.Nand => "the basic gate",
				ChipType.Clock => "switches on and off by itself",
				ChipType.Pulse => "one short pulse",
				ChipType.Key => "on while held down",
				ChipType.TriStateBuffer => "passes or cuts a signal",
				ChipType.In_1Bit => "input: 1 bit",
				ChipType.In_4Bit => "input: 4 bits",
				ChipType.In_8Bit => "input: 8 bits",
				ChipType.Out_1Bit => "output: 1 bit",
				ChipType.Out_4Bit => "output: 4 bits",
				ChipType.Out_8Bit => "output: 8 bits",
				ChipType.Merge_1To4Bit => "4 wires into a 4-bit bus",
				ChipType.Merge_1To8Bit => "8 wires into an 8-bit bus",
				ChipType.Merge_4To8Bit => "two 4-bit into 8-bit",
				ChipType.Split_4To1Bit => "4-bit bus into 4 wires",
				ChipType.Split_8To4Bit => "8-bit into two 4-bit",
				ChipType.Split_8To1Bit => "8-bit bus into 8 wires",
				ChipType.Bus_1Bit or ChipType.Bus_4Bit or ChipType.Bus_8Bit => "wire across the board",
				ChipType.SevenSegmentDisplay => "shows a digit",
				ChipType.DisplayDot => "dot screen",
				ChipType.DisplayRGB => "colour screen",
				ChipType.DisplayRGB64 => "64 x 64 colour screen",
				ChipType.DisplayLED => "a light",
				ChipType.Rom_256x16 => "program memory",
				ChipType.Buzzer => "makes a sound",
				ChipType.Custom => $"{Count(description.InputPins)} in, {Count(description.OutputPins)} out",
				_ => "built-in chip"
			};

			static int Count(PinDescription[] pins) => pins?.Length ?? 0;
		}
	}
}
