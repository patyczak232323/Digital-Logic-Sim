using System;
using System.Collections.Generic;
using System.Linq;
using DLS.Description;
using DLS.Game;
using Seb.Helpers;
using Seb.Types;
using Seb.Vis;
using Seb.Vis.UI;
using UnityEngine;
using static DLS.Graphics.DrawSettings;

namespace DLS.Graphics
{
	public static class SearchPopup
	{
		static readonly UIHandle ID_SearchInput = new("SearchPopup_SearchInput");
		static readonly UIHandle ID_Scrollbar = new("SearchPopup_Scrollbar");
		static readonly Func<string, bool> searchStringValidator = ValidateSearchInput;

		static string[] allChipNames;
		static string[] filteredChipNames;
		static readonly UI.ScrollViewDrawElementFunc drawChipSearchEntry = DrawChipSearchEntry;
		static readonly UI.ScrollViewDrawElementFunc drawTouchChipSearchEntry = DrawTouchChipSearchEntry;
		static int menuOpenedFrame;
		static bool isDraggingScrollbar;

		static readonly List<string> recentChipNames = new();

		public static void DrawMenu()
		{
			if (TouchUILayout.Enabled)
			{
				DrawTouchMenu();
				HandleKeyboardShortcuts();
				return;
			}

			MenuHelper.DrawBackgroundOverlay();
			Draw.ID panelID = UI.ReservePanel();

			using (UI.BeginBoundsScope(true))
			{
				InputFieldTheme inputTheme = MenuHelper.Theme.ChipNameInputField;
				(Vector2 inputFieldSize, float inputFieldTextPad) = ChipSaveMenu.GetTextInputSize();
				inputFieldSize.x += 6f;

				float height = UI.Height * 0.93f;
				float width = inputFieldSize.x;
				Vector2 topLeft = new(UI.Centre.x - width / 2, height);

				// Draw search bar
				UI.InputField(ID_SearchInput, inputTheme, topLeft, inputFieldSize, string.Empty, Anchor.TopLeft, inputFieldTextPad, searchStringValidator, true);
				topLeft = UI.PrevBounds.BottomLeft + Vector2.down * 2;

				// Draw scroll view
				ScrollBarState scrollState = UI.DrawScrollView(ID_Scrollbar, topLeft, new Vector2(width, UI.Height * 0.7f), UILayoutHelper.DefaultSpacing, Anchor.TopLeft, ActiveUITheme.ScrollTheme, drawChipSearchEntry, filteredChipNames.Length);
				isDraggingScrollbar = scrollState.isDragging;

				// Draw background panel
				MenuHelper.DrawReservedMenuPanel(panelID, UI.GetCurrentBoundsScope());
			}

			HandleKeyboardShortcuts();
		}

		static void DrawTouchMenu()
		{
			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			float left = TouchUILayout.SafeLeft + 1.2f;
			float right = TouchUILayout.SafeRight + 1.2f;
			float top = UI.Height - TouchUILayout.SafeTop - 1.0f;
			float bottom = TouchUILayout.BottomBarTotalHeight + 0.8f;
			float width = UI.Width - left - right;
			Color bg = new(0.055f, 0.055f, 0.065f, 0.99f);

			UI.DrawFullscreenPanel(bg);

			UI.DrawText(
				"ADD COMPONENT",
				theme.FontBold,
				theme.FontSizeRegular * 1.12f,
				new Vector2(left, top - 1.0f),
				Anchor.TextCentreLeft,
				Color.white);

			UI.DrawText(
				filteredChipNames == null ? "0 results" : $"{filteredChipNames.Length:N0} results",
				theme.FontRegular,
				theme.FontSizeRegular * 0.72f,
				new Vector2(left, top - 2.45f),
				Anchor.TextCentreLeft,
				Color.white * 0.58f);

			if (UI.Button(
				    "CLOSE",
				    theme.ButtonTheme,
				    new Vector2(UI.Width - right, top - 0.4f),
				    new Vector2(13f, TouchUILayout.TouchButtonHeight),
				    true,
				    false,
				    false,
				    Anchor.TopRight))
			{
				UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);
				return;
			}

			InputFieldTheme inputTheme = MenuHelper.Theme.ChipNameInputField;
			inputTheme.fontSize = theme.FontSizeRegular * 1.0f;
			float inputHeight = TouchUILayout.TouchButtonHeight;
			Vector2 inputTopLeft = new(left, top - 4.5f);
			UI.InputField(
				ID_SearchInput,
				inputTheme,
				inputTopLeft,
				new Vector2(width, inputHeight),
				"Search components...",
				Anchor.TopLeft,
				1.1f,
				searchStringValidator,
				true);

			float listTop = UI.PrevBounds.Bottom - 0.8f;
			float listHeight = Mathf.Max(8f, listTop - bottom);
			ScrollBarState scrollState = UI.DrawScrollView(
				ID_Scrollbar,
				new Vector2(left, listTop),
				new Vector2(width, listHeight),
				TouchUILayout.TouchGap,
				Anchor.TopLeft,
				theme.ScrollTheme,
				drawTouchChipSearchEntry,
				filteredChipNames.Length);
			isDraggingScrollbar = scrollState.isDragging;
		}

		static void DrawTouchChipSearchEntry(Vector2 topLeft, float width, int index, bool isLayoutPass)
		{
			float rowHeight = TouchUILayout.TouchButtonHeight + 0.7f;
			Bounds2D entryBounds = Bounds2D.CreateFromTopLeftAndSize(topLeft, new Vector2(width, rowHeight));
			bool offscreen = entryBounds.Top < 0 || entryBounds.Bottom > UI.Height;

			if (!isLayoutPass && !offscreen)
			{
				string chipName = filteredChipNames[index];
				float gap = TouchUILayout.TouchGap;
				float actionWidth = Mathf.Min(15f, width * 0.16f);
				float nameWidth = width - actionWidth * 3 - gap * 3;
				bool canPlaceChip = Project.ActiveProject.ViewedChip.CanAddSubchip(chipName);
				bool canOpenChip = !Project.ActiveProject.chipLibrary.IsBuiltinChip(chipName);
				bool isStarred = Project.ActiveProject.description.IsStarred(chipName, false);

				ButtonTheme nameTheme = ActiveUITheme.ChipLibraryChipToggleOn;
				UI.Button(
					chipName,
					nameTheme,
					topLeft,
					new Vector2(nameWidth, rowHeight),
					true,
					false,
					false,
					Anchor.TopLeft,
					true,
					1f,
					true);

				Vector2 actionPos = topLeft + Vector2.right * (nameWidth + gap);
				if (UI.Button("USE", ActiveUITheme.ButtonTheme, actionPos, new Vector2(actionWidth, rowHeight), canPlaceChip, false, false, Anchor.TopLeft))
				{
					UseChip(chipName);
				}
				actionPos.x += actionWidth + gap;

				if (UI.Button("OPEN", ActiveUITheme.ButtonTheme, actionPos, new Vector2(actionWidth, rowHeight), canOpenChip, false, false, Anchor.TopLeft))
				{
					OpenChip(chipName);
				}
				actionPos.x += actionWidth + gap;

				if (UI.Button(isStarred ? "UNSTAR" : "STAR", ActiveUITheme.ButtonTheme, actionPos, new Vector2(actionWidth, rowHeight), true, false, false, Anchor.TopLeft))
				{
					Project.ActiveProject.SetStarred(chipName, !isStarred, false);
				}
			}

			UI.OverridePreviousBounds(entryBounds);
		}

		static void HandleKeyboardShortcuts()
		{
			if (KeyboardShortcuts.ConfirmShortcutTriggered)
			{
				foreach (string chipName in filteredChipNames)
				{
					if ((InputHelper.ShiftIsHeld || InputHelper.CtrlIsHeld) && !Project.ActiveProject.chipLibrary.IsBuiltinChip(chipName))
					{
						OpenChip(chipName);
						return;
					}

					if (Project.ActiveProject.ViewedChip.CanAddSubchip(chipName))
					{
						UseChip(chipName);
						return;
					}
				}
			}
			else if (KeyboardShortcuts.CancelShortcutTriggered || (KeyboardShortcuts.SearchShortcutTriggered && Time.frameCount > menuOpenedFrame))
			{
				UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);
			}
		}

		static void DrawChipSearchEntry(Vector2 topLeft, float width, int index, bool isLayoutPass)
		{
			Bounds2D entryBounds = Bounds2D.CreateFromTopLeftAndSize(topLeft, new Vector2(width, ButtonHeight));
			bool offscreen = entryBounds.Top < 0 || entryBounds.Bottom > UI.Height;

			if (!isLayoutPass && !offscreen)
			{
				string chipName = filteredChipNames[index];
				const float nameWidth = 22f;

				// Draw chip name (drawn as non-interactive button)
				ButtonTheme nameTheme = ActiveUITheme.ChipLibraryChipToggleOn;
				UI.Button(chipName, nameTheme, topLeft, new Vector2(nameWidth, ButtonHeight), true, false, false, Anchor.TopLeft, true, 1, true);

				// Draw buttons
				Vector2 buttonsTopLeft = topLeft + Vector2.right * (nameWidth + DefaultButtonSpacing);
				float buttonsWidth = width - (buttonsTopLeft.x - topLeft.x);
				bool canPlaceChip = Project.ActiveProject.ViewedChip.CanAddSubchip(chipName);
				bool canOpenChip = !Project.ActiveProject.chipLibrary.IsBuiltinChip(chipName);


				bool isStarred = Project.ActiveProject.description.IsStarred(chipName, false);
				int buttonIndex = MenuHelper.DrawButtonTriplet("USE", "OPEN", isStarred ? "UN-STAR" : "STAR", buttonsTopLeft, buttonsWidth, false, canPlaceChip, canOpenChip, true);


				if (buttonIndex == 0) UseChip(chipName);
				else if (buttonIndex == 1) OpenChip(chipName);
				else if (buttonIndex == 2)
				{
					Project.ActiveProject.SetStarred(chipName, !isStarred, false);
				}
			}

			// Override bounds so can skip drawing if offscreen or in layout pass
			UI.OverridePreviousBounds(entryBounds);
		}

		static void CreateFilteredChipsList(string searchString)
		{
			// Empty search string, so show all chips
			if (string.IsNullOrWhiteSpace(searchString))
			{
				// Remove deleted/renamed chip names from recent chips list
				for (int i = recentChipNames.Count - 1; i >= 0; i--)
				{
					if (!Project.ActiveProject.chipLibrary.HasChip(recentChipNames[i])) recentChipNames.RemoveAt(i);
				}

				// Get all chip names, with recently opened chips (or those used from the search menu) orderered first; thereafter alphabetically
				HashSet<string> remainingChipNames = new(allChipNames);
				remainingChipNames.ExceptWith(recentChipNames);

				List<string> sortedList = remainingChipNames.ToList();
				sortedList.Sort();
				sortedList.Reverse();
				sortedList.AddRange(recentChipNames);
				sortedList.Reverse();
				filteredChipNames = sortedList.ToArray();
				return;
			}

			string searchString_Lenient = LenientString(searchString);

			// Priority 1) exact match from start of name
			HashSet<string> startsWith = new(allChipNames.Where(s => s.StartsWith(searchString, ChipDescription.NameComparison)));
			// Priority 2) match from start of name, but more lenient by ignoring things like spaces and dashes
			HashSet<string> startsWith_Lenient = new(allChipNames.Where(s => LenientString(s).StartsWith(searchString_Lenient, ChipDescription.NameComparison)));
			// Priority 3) exact match from anywhere in name
			HashSet<string> contains = new(allChipNames.Where(s => s.Contains(searchString, ChipDescription.NameComparison)));
			// Todo: fuzzy search?

			startsWith_Lenient.ExceptWith(startsWith);
			contains.ExceptWith(startsWith);
			contains.ExceptWith(startsWith_Lenient);

			List<string> all = ToSortedList(startsWith);
			all.AddRange(ToSortedList(startsWith_Lenient));
			all.AddRange(ToSortedList(contains));
			filteredChipNames = all.ToArray();

			static string LenientString(string s)
			{
				// Ignore easily misremembered aspects of name, like spaces and dashes
				return s.Replace("-", "").Replace(" ", "");
			}

			static List<string> ToSortedList(HashSet<string> set)
			{
				List<string> list = set.ToList();
				list.Sort((a, b) => a.Length.CompareTo(b.Length));
				return list;
			}
		}

		static bool ValidateSearchInput(string text)
		{
			if (Time.frameCount == menuOpenedFrame) return false;
			if (!ChipSaveMenu.ValidateChipNameInput(text)) return false;

			CreateFilteredChipsList(text.Trim());
			return true;
		}

		public static void OnMenuOpened()
		{
			menuOpenedFrame = Time.frameCount;
			InputFieldState inputField = UI.GetInputFieldState(ID_SearchInput);
			inputField.ClearText();

			allChipNames = Project.ActiveProject.chipLibrary.allChips.Select(c => c.Name).ToArray();
			CreateFilteredChipsList(string.Empty);
		}

		static void UseChip(string chipName)
		{
			AddRecentChip(chipName);
			Project.ActiveProject.controller.StartPlacing(chipName);
			UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);
		}


		static void OpenChip(string chipName)
		{
			Project project = Project.ActiveProject;

			if (project.ActiveChipHasUnsavedChanges())
			{
				UnsavedChangesPopup.OpenPopup(OpenChipIfConfirmed);
			}
			else
			{
				OpenChipIfConfirmed(true);
			}

			void OpenChipIfConfirmed(bool confirm)
			{
				if (confirm)
				{
					project.LoadDevChipOrCreateNewIfDoesntExist(chipName);
					UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);
				}
			}
		}

		public static void AddRecentChip(string chipName)
		{
			recentChipNames.Remove(chipName);
			recentChipNames.Add(chipName);
		}

		public static void ClearRecentChips() => recentChipNames.Clear();

		public static void Reset()
		{
		}
	}
}