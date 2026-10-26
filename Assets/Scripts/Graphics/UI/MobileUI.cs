using System;
using DLS.Description;
using DLS.Game;
using DLS.Simulation;
using Seb.Helpers;
using Seb.Helpers.InputHandling;
using Seb.Types;
using Seb.Vis;
using Seb.Vis.UI;
using UnityEngine;

namespace DLS.Graphics
{
	/// <summary>
	/// Touch interface for phones and tablets (landscape). Desktop keeps the original UI.
	/// Layout: a top bar (menu, parts, chip name, undo/redo, run/step, save), the parts panel on
	/// the left and the board filling the rest. The board shows what to do next (banners), the
	/// selected part's actions, and view buttons. Specialised screens (save, library, truth
	/// table...) keep their own content inside a phone-sized canvas with a BACK button.
	/// </summary>
	public static class MobileUI
	{
		public const float TopBarHeight = 5.4f;
		const float TopButtonHeight = 4.2f;
		const float BannerHeight = 4.6f;
		const float ToolSize = 4.8f;

		const string PrefTargetFps = "rewired.mobile.targetFps";
		const string PrefGuideShown = "rewired.mobile.guideShown.v2";
		const string PrefPaletteOpen = "rewired.mobile.paletteOpen";

		static string notice;
		static float noticeUntil;
		static bool unsavedChanges;
		static float unsavedCheckTime = -1;

		// "-mobile-ui" on the command line shows the touch interface in a desktop build, with the
		// mouse acting as one finger, to try and screenshot it without a phone.
		public static bool PreviewOnDesktop { get; private set; }

		public static bool IsActive => MobileInputBridge.RuntimeEnabled || Application.isMobilePlatform || PreviewOnDesktop;

		// One finger on empty space moves the view; with this on it draws a selection box instead.
		public static bool AreaSelectMode { get; private set; }

		public static bool PaletteOpen
		{
			get => PlayerPrefs.GetInt(PrefPaletteOpen, 1) != 0;
			set
			{
				PlayerPrefs.SetInt(PrefPaletteOpen, value ? 1 : 0);
				PlayerPrefs.Save();
			}
		}

		public static int TargetFrameRate => Mathf.Clamp(PlayerPrefs.GetInt(PrefTargetFps, 60), 30, 120);

		public static void ReadCommandLine()
		{
			PreviewOnDesktop = Array.Exists(Environment.GetCommandLineArgs(), a => string.Equals(a, "-mobile-ui", StringComparison.OrdinalIgnoreCase));
		}

		public static Rect SafeRectUI
		{
			get
			{
				if (Screen.width <= 0) return new Rect(0, 0, UI.Width, UI.Width * 9 / 16f);
				Rect safe = Screen.safeArea;
				float s = UI.Width / Screen.width;
				return new Rect(safe.xMin * s, safe.yMin * s, safe.width * s, safe.height * s);
			}
		}

		public static Rect KeyboardAwareSafeRectUI
		{
			get
			{
				Rect safe = SafeRectUI;
				if (!MobileInputBridge.KeyboardVisible ||
				    MobileInputBridge.KeyboardScreenRect.height <= 0 ||
				    Screen.width <= 0)
				{
					return safe;
				}

				float s = UI.Width / Screen.width;
				float keyboardTop = MobileInputBridge.KeyboardScreenRect.yMax * s;
				float visibleBottom = Mathf.Clamp(keyboardTop + 0.6f, safe.yMin, safe.yMax);
				return Rect.MinMaxRect(safe.xMin, visibleBottom, safe.xMax, safe.yMax);
			}
		}

		static bool PaletteShown
		{
			get
			{
				Project project = Project.ActiveProject;
				return project != null && project.CanEditViewedChip && PaletteOpen;
			}
		}

		// The part of the screen where the circuit can be seen: below the top bar, right of the
		// parts panel.
		public static Rect CanvasRectUI
		{
			get
			{
				Rect safe = SafeRectUI;
				float left = PaletteShown ? safe.xMin + MobilePalette.Width : safe.xMin;
				return Rect.MinMaxRect(left, safe.yMin, safe.xMax, safe.yMax - TopBarHeight);
			}
		}

		// The board without the view buttons and the room the selection bar takes at the bottom,
		// so FIT leaves the whole circuit visible.
		public static Rect FitRectUI
		{
			get
			{
				Rect canvas = CanvasRectUI;
				return Rect.MinMaxRect(canvas.xMin + 1f, canvas.yMin + 7f, canvas.xMax - ToolSize - 2f, canvas.yMax - 1f);
			}
		}

		public static void ApplyRuntimePreferences()
		{
			Application.targetFrameRate = TargetFrameRate;
		}

		public static void SetTargetFrameRate(int fps)
		{
			fps = Mathf.Clamp(fps, 30, 120);
			PlayerPrefs.SetInt(PrefTargetFps, fps);
			PlayerPrefs.Save();
			Application.targetFrameRate = fps;
		}

		// A short message shown at the top of the board for a few seconds.
		public static void ShowNotice(string text)
		{
			notice = text;
			noticeUntil = Time.unscaledTime + 3.5f;
		}

		// Called before the world handles input, so a touch on the top bar or the parts panel
		// does not reach the circuit underneath.
		public static bool IsScreenPointOverPersistentChrome(Vector2 screenPosition)
		{
			if (!IsActive || Screen.width <= 0) return false;
			if (UIDrawer.ActiveMenu == UIDrawer.MenuType.BottomBarMenuPopup) return true;
			if (UIDrawer.ActiveMenu != UIDrawer.MenuType.None) return false;

			Vector2 point = screenPosition * (UI.Width / Screen.width);
			Rect safe = SafeRectUI;
			if (point.y >= safe.yMax - TopBarHeight) return true;
			return PaletteShown && point.x <= safe.xMin + MobilePalette.Width;
		}

		public static void Draw()
		{
			if (UIDrawer.ActiveMenu == UIDrawer.MenuType.MainMenu)
			{
				MobileMainMenu.Draw();
				return;
			}

			Project project = Project.ActiveProject;
			if (project == null)
			{
				UIDrawer.SetActiveMenu(UIDrawer.MenuType.MainMenu);
				return;
			}

			DrawProjectMenus(project);
		}

		static void DrawProjectMenus(Project project)
		{
			UIDrawer.MenuType menu = UIDrawer.ActiveMenu;

			if (menu is UIDrawer.MenuType.None or UIDrawer.MenuType.BottomBarMenuPopup)
			{
				// Show the short guide the first time a project is opened.
				if (PlayerPrefs.GetInt(PrefGuideShown, 0) == 0)
				{
					PlayerPrefs.SetInt(PrefGuideShown, 1);
					PlayerPrefs.Save();
					UIDrawer.SetActiveMenu(UIDrawer.MenuType.MobileHelp);
					return;
				}

				DrawEditor(project);
				ContextMenu.Update();
				return;
			}

			// Full-screen tools keep their own content on a phone-sized canvas, with a large
			// BACK button in the corner.
			switch (menu)
			{
				case UIDrawer.MenuType.ChipSave: ChipSaveMenu.DrawMenu(); break;
				case UIDrawer.MenuType.ChipLibrary: ChipLibraryMenu.DrawMenu(); break;
				case UIDrawer.MenuType.ChipCustomization: ChipCustomizationMenu.DrawMenu(); break;
				case UIDrawer.MenuType.Preferences: PreferencesMenu.DrawMenu(project); break;
				case UIDrawer.MenuType.SimulationDiagnostics: SimulationDiagnosticsMenu.DrawMenu(); break;
				case UIDrawer.MenuType.RhdlStudio: RhdlStudioMenu.DrawMenu(); break;
				case UIDrawer.MenuType.PinRename: PinEditMenu.DrawMenu(); break;
				case UIDrawer.MenuType.RebindKeyChip: RebindKeyChipMenu.DrawMenu(); break;
				case UIDrawer.MenuType.RomEdit: RomEditMenu.DrawMenu(); break;
				case UIDrawer.MenuType.UnsavedChanges: UnsavedChangesPopup.DrawMenu(); break;
				case UIDrawer.MenuType.Search: SearchPopup.DrawMenu(); break;
				case UIDrawer.MenuType.ChipLabelPopup: ChipLabelMenu.DrawMenu(); break;
				case UIDrawer.MenuType.PulseEdit: PulseEditMenu.DrawMenu(); break;
				case UIDrawer.MenuType.TruthTable: TruthTableMenu.DrawMenu(); break;
				case UIDrawer.MenuType.Export: ExportMenu.DrawMenu(); break;
				case UIDrawer.MenuType.Breakpoint: BreakpointMenu.DrawMenu(); break;
				case UIDrawer.MenuType.MobileHelp: MobileHelpOverlay.Draw(); break;
				case UIDrawer.MenuType.Challenges: ChallengesMenu.DrawMenu(); break;
			}

			DrawModalBackButton(menu);
			ContextMenu.Update();
		}

		// ---- Editor ----

		static void DrawEditor(Project project)
		{
			Rect safe = SafeRectUI;
			Rect canvas = CanvasRectUI;
			bool drawerOpen = UIDrawer.ActiveMenu == UIDrawer.MenuType.BottomBarMenuPopup;

			if (!drawerOpen)
			{
				DrawBoardOverlays(project, canvas);
			}

			if (PaletteShown) MobilePalette.Draw(project, safe);
			DrawTopBar(project, safe);

			if (drawerOpen)
			{
				DrawMenuDrawer(project, safe);
			}
		}

		static void DrawTopBar(Project project, Rect safe)
		{
			UI.DrawPanel(new Vector2(safe.xMin, safe.yMax), new Vector2(safe.width, TopBarHeight), MobileTheme.BarCol, Anchor.TopLeft);
			Bounds2D bar = UI.PrevBounds;
			UI.DrawLine(bar.BottomLeft, bar.BottomRight, 0.07f, MobileTheme.LineCol);

			const float gap = 0.4f;
			float y = bar.Centre.y + TopButtonHeight / 2;
			float x = bar.Left + 0.5f;
			bool viewingInside = project.chipViewStack.Count > 1;
			bool drawerOpen = UIDrawer.ActiveMenu == UIDrawer.MenuType.BottomBarMenuPopup;

			// ---- Left: menu (or back out of a chip being looked inside), parts panel ----
			if (viewingInside)
			{
				if (MobileTheme.IconButton("BACK", MobileIcon.Back, MobileTheme.Neutral, new Vector2(x, y), new Vector2(10.5f, TopButtonHeight)))
				{
					project.ReturnToPreviousViewedChip();
				}
				x += 10.5f + gap;
			}
			else
			{
				if (MobileTheme.IconButton("MENU", MobileIcon.Menu, MobileTheme.Toggle(drawerOpen), new Vector2(x, y), new Vector2(10.5f, TopButtonHeight)))
				{
					ToggleDrawer();
				}
				x += 10.5f + gap;

				if (MobileTheme.Button("PARTS", MobileTheme.Toggle(PaletteOpen), new Vector2(x, y), new Vector2(9.5f, TopButtonHeight), true, Anchor.TopLeft))
				{
					PaletteOpen = !PaletteOpen;
					if (drawerOpen) UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);
				}
				x += 9.5f + gap;
			}

			// ---- Right, from the edge: save, step, run/pause, redo, undo ----
			float right = bar.Right - 0.5f;
			bool canEdit = project.CanEditViewedChip;
			bool paused = project.simPaused;

			right -= 9f;
			ButtonTheme saveTheme = canEdit && HasUnsavedChanges(project) ? MobileTheme.Primary : MobileTheme.Neutral;
			if (MobileTheme.Button("SAVE", saveTheme, new Vector2(right, y), new Vector2(9f, TopButtonHeight), canEdit, Anchor.TopLeft))
			{
				project.controller.CancelEverything();
				UIDrawer.SetActiveMenu(UIDrawer.MenuType.ChipSave);
			}

			right -= 9.5f + gap * 2.5f;
			if (MobileTheme.IconButton("STEP", MobileIcon.Step, MobileTheme.Neutral, new Vector2(right, y), new Vector2(9.5f, TopButtonHeight), paused))
			{
				project.advanceSingleSimStep = true;
			}

			right -= 11f + gap;
			if (MobileTheme.IconButton(paused ? "RUN" : "PAUSE", paused ? MobileIcon.Play : MobileIcon.Pause, MobileTheme.Toggle(paused), new Vector2(right, y), new Vector2(11f, TopButtonHeight)))
			{
				project.description.Prefs_SimPaused = !paused;
				project.SaveCurrentProjectDescription();
			}

			right -= 5.6f + gap * 2.5f;
			UndoController undo = project.ViewedChip.UndoController;
			if (MobileTheme.IconButton(null, MobileIcon.Redo, MobileTheme.Neutral, new Vector2(right, y), new Vector2(5.6f, TopButtonHeight), canEdit && undo.CanRedo))
			{
				undo.TryRedo();
			}

			right -= 5.6f + gap;
			if (MobileTheme.IconButton(null, MobileIcon.Undo, MobileTheme.Neutral, new Vector2(right, y), new Vector2(5.6f, TopButtonHeight), canEdit && undo.CanUndo))
			{
				undo.TryUndo();
			}

			// ---- Middle: what is being edited ----
			float titleWidth = right - x - 1.5f;
			string chipName = string.IsNullOrWhiteSpace(project.ActiveDevChipName) ? "NEW CHIP" : project.ActiveDevChipName.ToUpperInvariant();
			string title = viewingInside ? "INSIDE " + chipName : chipName;
			string subtitle = viewingInside ? "view only" : project.description.ProjectName + (canEdit && HasUnsavedChanges(project) ? "  •  not saved" : string.Empty);
			MobileTheme.Text(MobileTheme.Fit(title, titleWidth, 0.86f, true), new Vector2(x + 0.8f, bar.Centre.y + 0.85f), 0.86f, Color.white, Anchor.TextCentreLeft, true);
			MobileTheme.Text(MobileTheme.Fit(subtitle, titleWidth, 0.58f), new Vector2(x + 0.8f, bar.Centre.y - 1.15f), 0.58f, MobileTheme.SecondaryTextCol);
		}

		// Comparing the chip with its saved version builds a description, so not every frame.
		static bool HasUnsavedChanges(Project project)
		{
			if (Time.unscaledTime - unsavedCheckTime > 0.5f || unsavedCheckTime < 0)
			{
				unsavedCheckTime = Time.unscaledTime;
				unsavedChanges = project.ActiveChipHasUnsavedChanges();
			}

			return unsavedChanges;
		}

		// ---- Board: hints, view buttons, selection actions ----

		static void DrawBoardOverlays(Project project, Rect canvas)
		{
			ChipInteractionController controller = project.controller;

			DrawViewTools(project, canvas);

			if (!DrawBanner(project, controller, canvas) && project.ViewedChip.Elements.Count == 0 && project.CanEditViewedChip && !controller.IsPlacingElements)
			{
				DrawEmptyBoardCard(canvas);
			}

			MobileSelectionBar.Draw(project, canvas);
		}

		// FIT, area select and zoom, stacked down the right edge of the board.
		static void DrawViewTools(Project project, Rect canvas)
		{
			const float gap = 0.4f;
			float x = canvas.xMax - 0.7f - ToolSize;
			float y = canvas.yMax - 0.7f;
			Vector2 size = new(ToolSize, ToolSize);

			if (MobileTheme.IconButton(null, MobileIcon.Fit, MobileTheme.Quiet, new Vector2(x, y), size))
			{
				CameraController.FitViewToChip();
			}
			y -= ToolSize + gap;

			if (project.CanEditViewedChip)
			{
				if (MobileTheme.IconButton(null, MobileIcon.Select, MobileTheme.Toggle(AreaSelectMode), new Vector2(x, y), size))
				{
					AreaSelectMode = !AreaSelectMode;
				}
				y -= ToolSize + gap;
			}

			Vector2 zoomCentre = UI.UIToScreenSpace(canvas.center);
			if (MobileTheme.IconButton(null, MobileIcon.Plus, MobileTheme.Quiet, new Vector2(x, y), size))
			{
				CameraController.ZoomAround(zoomCentre, 0.8f);
			}
			y -= ToolSize + gap;

			if (MobileTheme.IconButton(null, MobileIcon.Minus, MobileTheme.Quiet, new Vector2(x, y), size))
			{
				CameraController.ZoomAround(zoomCentre, 1.25f);
			}
		}

		// Says what the next tap will do, with a way out. Returns whether one was shown.
		static bool DrawBanner(Project project, ChipInteractionController controller, Rect canvas)
		{
			string text = null;
			Color textCol = Color.white;
			string buttonA = null, buttonB = null;
			Action actionA = null, actionB = null;

			if (controller.IsCreatingWire)
			{
				text = "TAP A PIN TO CONNECT  •  TAP EMPTY SPACE TO BEND";
				buttonA = "UNDO BEND";
				actionA = controller.RemoveLastWirePoint;
				buttonB = "CANCEL";
				actionB = controller.CancelEverything;
			}
			else if (controller.IsEditingWire)
			{
				text = "DRAG THE WIRE'S CORNERS  •  TAP THE WIRE TO ADD ONE";
				buttonB = "DONE";
				actionB = controller.FinishWireEdit;
			}
			else if (controller.IsPlacingElements && !MobilePalette.IsDragPlacing)
			{
				text = controller.IsPlacingCopies ? "TAP WHERE THE COPY SHOULD GO" : $"TAP THE BOARD TO PLACE {PlacingName(controller)}";
				buttonB = "CANCEL";
				actionB = controller.CancelEverything;
			}
			else if (AreaSelectMode)
			{
				text = "DRAG OVER PARTS TO SELECT THEM";
				buttonB = "DONE";
				actionB = () => AreaSelectMode = false;
			}
			else if (project.simPaused && project.lastBreakpointHit != null)
			{
				text = "STOPPED: " + project.lastBreakpointHit.ToUpperInvariant();
				textCol = MobileTheme.WarningCol;
				buttonA = "STEP";
				actionA = () => project.advanceSingleSimStep = true;
				buttonB = "RUN";
				actionB = () =>
				{
					project.description.Prefs_SimPaused = false;
					project.SaveCurrentProjectDescription();
				};
			}
			else if (notice != null && Time.unscaledTime < noticeUntil)
			{
				text = notice.ToUpperInvariant();
				textCol = MobileTheme.WarningCol;
			}
			else if (project.simPaused)
			{
				text = "SIMULATION PAUSED  •  STEP ADVANCES ONE TICK";
				textCol = MobileTheme.SecondaryTextCol;
			}

			if (text == null) return false;

			const float textScale = 0.6f;
			ButtonTheme theme = MobileTheme.Neutral;
			float buttonsWidth = BannerButtonWidth(buttonA, theme) + BannerButtonWidth(buttonB, theme);
			float maxWidth = canvas.width - ToolSize - 3f;
			float textWidth = Mathf.Min(MobileTheme.TextWidth(text, textScale, true), maxWidth - buttonsWidth - 2f);
			float width = textWidth + buttonsWidth + 2f;

			Vector2 topCentre = new(canvas.center.x - (ToolSize + 1f) / 2, canvas.yMax - 0.7f);
			Bounds2D banner = MobileTheme.Card(topCentre, new Vector2(width, BannerHeight), Anchor.CentreTop);
			MobileTheme.Text(MobileTheme.Fit(text, textWidth, textScale, true), banner.CentreLeft + Vector2.right * 1f, textScale, textCol, Anchor.TextCentreLeft, true);

			float x = banner.Right - 0.4f;
			Action pressed = null;
			if (buttonB != null)
			{
				x -= BannerButtonWidth(buttonB, theme) - 0.4f;
				if (MobileTheme.Button(buttonB, theme, new Vector2(x, banner.Centre.y), new Vector2(BannerButtonWidth(buttonB, theme) - 0.4f, BannerHeight - 0.8f), true, Anchor.CentreLeft)) pressed = actionB;
				x -= 0.4f;
			}

			if (buttonA != null)
			{
				x -= BannerButtonWidth(buttonA, theme) - 0.4f;
				if (MobileTheme.Button(buttonA, MobileTheme.Quiet, new Vector2(x, banner.Centre.y), new Vector2(BannerButtonWidth(buttonA, theme) - 0.4f, BannerHeight - 0.8f), true, Anchor.CentreLeft)) pressed = actionA;
			}

			UI.OverridePreviousBounds(banner);
			pressed?.Invoke();
			return true;
		}

		static string PlacingName(ChipInteractionController controller)
		{
			if (controller.SelectedElements.Count == 0) return "IT";
			return controller.SelectedElements[0] switch
			{
				SubChipInstance subchip when controller.SelectedElements.Count == 1 || subchip.IsBus => subchip.Description.Name.ToUpperInvariant(),
				DevPinInstance pin when controller.SelectedElements.Count == 1 => pin.IsInputPin ? "THE INPUT" : "THE OUTPUT",
				_ => "THEM"
			};
		}

		static float BannerButtonWidth(string label, ButtonTheme theme) =>
			label == null ? 0 : Mathf.Max(8f, UI.CalculateTextSize(label, theme.fontSize, theme.font).x + 2.4f) + 0.4f;

		static void DrawEmptyBoardCard(Rect canvas)
		{
			(string step, string text)[] steps =
			{
				("1", "Drag IN-1 from PARTS onto the board, twice"),
				("2", "Open BASIC and drag a NAND, then add an OUT-1"),
				("3", "Tap a pin, then another pin, to wire them"),
				("4", "Tap an input's round button to switch it on")
			};

			const float lineHeight = 3.4f;
			float width = Mathf.Min(canvas.width - ToolSize - 4f, 58f);
			float height = 4.4f + steps.Length * lineHeight + 6f;
			Vector2 centre = new(canvas.center.x - (ToolSize + 1f) / 2, canvas.center.y);
			Bounds2D card = MobileTheme.Card(centre, new Vector2(width, height), Anchor.Centre);

			MobileTheme.Text("START HERE", card.TopLeft + new Vector2(1.4f, -2.2f), 0.72f, MobileTheme.Accent, Anchor.TextCentreLeft, true);
			float y = card.Top - 5.2f;
			foreach ((string step, string text) in steps)
			{
				UI.DrawCircle(new Vector2(card.Left + 2.4f, y), 1.25f, MobileTheme.Accent * 0.85f);
				MobileTheme.Text(step, new Vector2(card.Left + 2.4f, y), 0.6f, Color.white, Anchor.TextCentre, true);
				MobileTheme.Text(MobileTheme.Fit(text, width - 6f, 0.62f), new Vector2(card.Left + 4.4f, y), 0.62f, Color.white);
				y -= lineHeight;
			}

			if (MobileTheme.Button("HOW IT WORKS", MobileTheme.Neutral, new Vector2(card.Centre.x, card.Bottom + 0.9f), new Vector2(18f, 4f), true, Anchor.CentreBottom))
			{
				UIDrawer.SetActiveMenu(UIDrawer.MenuType.MobileHelp);
			}
		}

		// ---- Menu drawer ----

		static void DrawMenuDrawer(Project project, Rect safe)
		{
			// Above the parts panel's text, which is drawn in a later pass of the same layer.
			UI.StartNewLayer();

			// Dim the board; tapping it closes the drawer.
			UI.DrawPanel(new Vector2(safe.xMin, safe.yMin), new Vector2(UI.Width, safe.height - TopBarHeight), new Color(0, 0, 0, 0.55f), Anchor.BottomLeft);
			bool tappedOutside = InputHelper.IsMouseDownThisFrame(MouseButton.Left);

			const float columnWidth = 17.5f;
			const float rowHeight = 4f;
			const float rowGap = 0.4f;
			const float pad = 1f;
			string current = project.ActiveDevChipName;
			bool isSavedCustomChip = !string.IsNullOrWhiteSpace(current) &&
			                         project.chipLibrary.TryGetChipDescription(current, out ChipDescription description) &&
			                         description.ChipType == ChipType.Custom;
			int breakpoints = SimulationBreakpoints.Count;
			bool canEdit = project.CanEditViewedChip;

			(string header, (string label, bool enabled, Action action)[] rows)[] columns =
			{
				("CHIP", new (string, bool, Action)[]
				{
					("NEW CHIP", canEdit, () => CreateNewChip(project)),
					("SAVE CHIP", canEdit, () => UIDrawer.SetActiveMenu(UIDrawer.MenuType.ChipSave)),
					("CHIP LIBRARY", true, () => UIDrawer.SetActiveMenu(UIDrawer.MenuType.ChipLibrary)),
					("TRUTH TABLE", isSavedCustomChip, () => TruthTableMenu.Open(current)),
					("EXPORT", true, () => ExportMenu.Open(current))
				}),
				("TOOLS", new (string, bool, Action)[]
				{
					("CHALLENGES", true, ChallengesMenu.Open),
					("RHDL STUDIO", true, () => UIDrawer.SetActiveMenu(UIDrawer.MenuType.RhdlStudio)),
					("DIAGNOSTICS", true, () => UIDrawer.SetActiveMenu(UIDrawer.MenuType.SimulationDiagnostics)),
					(breakpoints > 0 ? $"CLEAR {breakpoints} BREAKPOINT{(breakpoints == 1 ? "" : "S")}" : "NO BREAKPOINTS", breakpoints > 0, SimulationBreakpoints.Clear)
				}),
				("APP", new (string, bool, Action)[]
				{
					("SETTINGS", true, () => UIDrawer.SetActiveMenu(UIDrawer.MenuType.Preferences)),
					("HOW IT WORKS", true, () => UIDrawer.SetActiveMenu(UIDrawer.MenuType.MobileHelp)),
					("MAIN MENU", true, () => ExitToMainMenu(project))
				})
			};

			int maxRows = 0;
			foreach (var column in columns) maxRows = Mathf.Max(maxRows, column.rows.Length);
			float width = pad * 2 + columns.Length * columnWidth + (columns.Length - 1) * 0.8f;
			float height = pad + 2.6f + maxRows * (rowHeight + rowGap) + pad;
			Vector2 topLeft = new(safe.xMin + 0.5f, safe.yMax - TopBarHeight - 0.5f);
			Bounds2D sheet = MobileTheme.Card(topLeft, new Vector2(width, height), Anchor.TopLeft);

			Action pressed = null;
			for (int c = 0; c < columns.Length; c++)
			{
				float x = sheet.Left + pad + c * (columnWidth + 0.8f);
				MobileTheme.Text(columns[c].header, new Vector2(x + 0.2f, sheet.Top - pad - 1f), 0.58f, MobileTheme.SecondaryTextCol, Anchor.TextCentreLeft, true);
				float y = sheet.Top - pad - 2.6f;
				foreach ((string label, bool enabled, Action action) in columns[c].rows)
				{
					ButtonTheme theme = label == "MAIN MENU" ? MobileTheme.Quiet : MobileTheme.Neutral;
					if (MobileTheme.Button(label, theme, new Vector2(x, y), new Vector2(columnWidth, rowHeight), enabled, Anchor.TopLeft)) pressed = action;
					y -= rowHeight + rowGap;
				}
			}

			bool insideSheet = UI.MouseInsideBounds(sheet);
			UI.OverridePreviousBounds(sheet);

			if (pressed != null)
			{
				UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);
				project.controller.CancelEverything();
				pressed();
			}
			else if (tappedOutside && !insideSheet && !IsOverTopBar())
			{
				UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);
			}

			bool IsOverTopBar() => UI.ScreenToUISpace(InputHelper.MousePos).y >= safe.yMax - TopBarHeight;
		}

		static void DrawModalBackButton(UIDrawer.MenuType menu)
		{
			// UnsavedChanges owns an explicit decision and should not be visually obscured;
			// the guide has its own GOT IT button.
			if (menu is UIDrawer.MenuType.UnsavedChanges or UIDrawer.MenuType.MobileHelp) return;

			Rect safe = SafeRectUI;
			UI.StartNewLayer();
			Vector2 pos = new(safe.xMin + 0.6f, safe.yMax - 0.6f);
			if (MobileTheme.IconButton("BACK", MobileIcon.Back, MobileTheme.Neutral, pos, new Vector2(11.5f, 4.8f)))
			{
				MobileInputBridge.DismissKeyboard();
				if (MobileRuntime.IsRunning) MobileRuntime.ScheduleVirtualKey(KeyCode.Escape);
				else KeyboardShortcuts.ScheduleCancel();
			}
		}

		static void CreateNewChip(Project project)
		{
			if (project.ActiveChipHasUnsavedChanges()) UnsavedChangesPopup.OpenPopup(Confirm);
			else Confirm(true);

			void Confirm(bool ok)
			{
				if (ok) project.CreateBlankDevChip();
			}
		}

		static void ExitToMainMenu(Project project)
		{
			if (project.ActiveChipHasUnsavedChanges()) UnsavedChangesPopup.OpenPopup(Confirm);
			else Confirm(true);

			void Confirm(bool ok)
			{
				if (!ok) return;
				project.NotifyExit();
				UIDrawer.SetActiveMenu(UIDrawer.MenuType.MainMenu);
			}
		}

		public static void ToggleDrawer()
		{
			if (!IsActive) return;
			UIDrawer.SetActiveMenu(UIDrawer.ActiveMenu == UIDrawer.MenuType.BottomBarMenuPopup
				? UIDrawer.MenuType.None
				: UIDrawer.MenuType.BottomBarMenuPopup);
		}

		// Android Back: closes whatever is open, one step at a time.
		public static bool HandleBackButton()
		{
			if (!IsActive) return false;

			if (UIDrawer.ActiveMenu == UIDrawer.MenuType.MainMenu)
				return MobileMainMenu.HandleBackButton();

			if (UIDrawer.ActiveMenu == UIDrawer.MenuType.BottomBarMenuPopup)
			{
				UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);
				return true;
			}

			// Specialized menus already define their own Escape/cancel semantics.
			// Returning false lets Android Back flow through the existing shortcut path,
			// preserving save/discard/rollback behavior instead of force-closing state.
			if (UIDrawer.ActiveMenu != UIDrawer.MenuType.None)
				return false;

			if (ContextMenu.IsOpen)
			{
				ContextMenu.CloseContextMenu();
				return true;
			}

			Project project = Project.ActiveProject;
			if (project == null) return false;

			ChipInteractionController controller = project.controller;
			if (controller.IsPlacingOrMovingElementOrCreatingWire || controller.IsEditingWire || controller.SelectedElements.Count > 0)
			{
				controller.CancelEverything();
				return true;
			}

			if (AreaSelectMode)
			{
				AreaSelectMode = false;
				return true;
			}

			if (project.chipViewStack.Count > 1)
			{
				project.ReturnToPreviousViewedChip();
				return true;
			}

			return false;
		}

		public static void OnMainMenuOpened()
		{
			MobileMainMenu.OnMenuOpened();
			AreaSelectMode = false;
		}

		public static void Reset()
		{
			AreaSelectMode = false;
			notice = null;
			MobilePalette.Reset();
			MobileMainMenu.Reset();
		}
	}
}
