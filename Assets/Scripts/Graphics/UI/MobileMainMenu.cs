using System;
using System.Linq;
using DLS.Description;
using DLS.Game;
using DLS.Game.Updates;
using DLS.SaveSystem;
using DLS.Simulation;
using Seb.Helpers;
using Seb.Helpers.InputHandling;
using Seb.Types;
using Seb.Vis;
using Seb.Vis.UI;
using UnityEngine;

namespace DLS.Graphics
{
	public static class MobileMainMenu
	{
		enum ScreenKind
		{
			Home,
			ProjectActions,
			ProjectName,
			DeleteProject,
			Settings,
			About
		}

		enum ProjectNameMode
		{
			New,
			Rename,
			Duplicate
		}

		static readonly UIHandle ID_ProjectName = new("MobileMainMenu_ProjectName");
		static readonly UIHandle ID_ProjectScroll = new("MobileMainMenu_ProjectScroll");
		static readonly UI.ScrollViewDrawElementFunc DrawProjectElementFunc = DrawProjectElement;

		static ScreenKind screen = ScreenKind.Home;
		static ProjectNameMode projectNameMode = ProjectNameMode.New;
		static ProjectDescription[] projects = Array.Empty<ProjectDescription>();
		static int selectedProjectIndex = -1;
		static string status = string.Empty;
		static string[] projectWarnings = Array.Empty<string>();

		static bool HasSelectedProject => selectedProjectIndex >= 0 && selectedProjectIndex < projects.Length;
		static string SelectedProjectName => HasSelectedProject ? projects[selectedProjectIndex].ProjectName : string.Empty;

		public static void Draw()
		{
			RewiredEngine.UpdatePaused();
			UI.DrawFullscreenPanel(ColHelper.MakeCol255(23, 24, 28));

			Rect safe = MobileUI.SafeRectUI;
			DrawHeader(safe);

			switch (screen)
			{
				case ScreenKind.Home: DrawHome(safe); break;
				case ScreenKind.ProjectActions: DrawProjectActions(safe); break;
				case ScreenKind.ProjectName: DrawProjectName(safe); break;
				case ScreenKind.DeleteProject: DrawDeleteProject(safe); break;
				case ScreenKind.Settings: DrawMobileSettings(safe); break;
				case ScreenKind.About: DrawAbout(safe); break;
			}
		}

		static void DrawHeader(Rect safe)
		{
			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			Vector2 topLeft = new(safe.xMin, safe.yMax);
			UI.DrawPanel(topLeft, new Vector2(safe.width, 6.4f), RewiredUI.SurfaceRaised, Anchor.TopLeft);
			Bounds2D bar = UI.PrevBounds;
			UI.DrawLine(bar.BottomLeft, bar.BottomRight, 0.08f, RewiredUI.Accent);

			if (screen != ScreenKind.Home)
			{
				if (MobileTheme.IconButton("BACK", MobileIcon.Back, MobileTheme.Neutral, bar.CentreLeft + Vector2.right * 0.6f,
					    new Vector2(11.5f, 4.8f), true, Anchor.CentreLeft))
				{
					NavigateBack();
				}
			}

			float titleX = screen == ScreenKind.Home ? bar.Left + 1.2f : bar.Left + 13.3f;
			const float logoSize = 4.6f;
			RewiredLogo.Draw(new Vector2(titleX + logoSize / 2, bar.Centre.y), logoSize);
			titleX += logoSize + 0.8f;
			UI.DrawText("REWIRED", FontType.Born2bSporty, 4.7f,
				new Vector2(titleX, bar.Centre.y + 0.15f), Anchor.TextCentreLeft, Color.white);
			if (screen == ScreenKind.Home)
			{
				UI.DrawText("DIGITAL LOGIC SIMULATOR", theme.FontBold, theme.FontSizeRegular * 0.8f,
					new Vector2(UI.PrevBounds.Right + 2f, bar.Centre.y), Anchor.TextCentreLeft, RewiredUI.SecondaryText);
			}
			if (AppUpdater.Current == AppUpdater.State.Available)
			{
				if (MobileTheme.Button(AppUpdater.ActionLabel, MobileTheme.Primary, bar.CentreRight + Vector2.left * 0.6f, new Vector2(20f, 4.8f), true, Anchor.CentreRight))
				{
					AppUpdater.RunAction();
				}
			}
			else
			{
				UI.DrawText($"ANDROID  •  v{Main.RewiredVersion}", theme.FontRegular, theme.FontSizeRegular * 0.68f,
					bar.CentreRight + Vector2.left * 1.1f, Anchor.TextCentreRight, RewiredUI.SecondaryText);
			}
		}

		// Home: start a new project on the left, open an existing one on the right with one tap.
		static void DrawHome(Rect safe)
		{
			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			const float pad = 1.4f;
			float top = safe.yMax - 6.4f - pad;
			float bottom = safe.yMin + pad;
			float leftWidth = Mathf.Min(30f, safe.width * 0.34f);
			float x = safe.xMin + pad;

			// ---- Left: actions ----
			float y = top;
			if (MobileTheme.Button("NEW PROJECT", MobileTheme.Primary, new Vector2(x, y), new Vector2(leftWidth, 7f), true, Anchor.TopLeft))
			{
				OpenProjectNameScreen(ProjectNameMode.New);
			}
			y -= 7f + 0.8f;

			float half = (leftWidth - 0.6f) / 2;
			if (MobileTheme.Button("SETTINGS", MobileTheme.Neutral, new Vector2(x, y), new Vector2(half, 5f), true, Anchor.TopLeft))
			{
				screen = ScreenKind.Settings;
			}
			if (MobileTheme.Button("ABOUT", MobileTheme.Neutral, new Vector2(x + half + 0.6f, y), new Vector2(half, 5f), true, Anchor.TopLeft))
			{
				screen = ScreenKind.About;
			}
			y -= 5f + 1.6f;

			string[] tips =
			{
				"Build chips from NAND gates up,",
				"then use them inside bigger chips.",
				"",
				"Drag empty space to move around,",
				"pinch to zoom, and hold a finger on",
				"anything to see all its options."
			};
			foreach (string tip in tips)
			{
				UI.DrawText(tip, theme.FontRegular, theme.FontSizeRegular * 0.6f, new Vector2(x, y), Anchor.TextCentreLeft, MobileTheme.DimTextCol);
				y -= 2f;
			}

			if (MobileTheme.Button("QUIT", MobileTheme.Quiet, new Vector2(x, bottom), new Vector2(half, 4.4f), true, Anchor.BottomLeft))
			{
				Application.Quit();
			}

			// ---- Right: projects ----
			float listX = x + leftWidth + 2f;
			float listWidth = safe.xMax - pad - listX;
			UI.DrawText("YOUR PROJECTS", theme.FontBold, theme.FontSizeRegular * 0.72f, new Vector2(listX, top - 1f), Anchor.TextCentreLeft, MobileTheme.SecondaryTextCol);
			float listTop = top - 2.6f;

			if (projects.Length == 0)
			{
				UI.DrawText("No projects yet: tap NEW PROJECT to start one.", theme.FontRegular, theme.FontSizeRegular * 0.66f,
					new Vector2(listX, listTop - 2f), Anchor.TextCentreLeft, MobileTheme.DimTextCol);
			}
			else
			{
				UI.DrawScrollView(ID_ProjectScroll, new Vector2(listX, listTop), new Vector2(listWidth, listTop - bottom), 0.45f,
					Anchor.TopLeft, theme.ScrollTheme, DrawProjectElementFunc, projects.Length);
			}

			DrawStatus(safe, theme);
		}

		static void DrawProjectElement(Vector2 topLeft, float width, int index, bool isLayoutOnly)
		{
			const float height = 5.4f;
			const float manageWidth = 9f;
			Bounds2D entryBounds = Bounds2D.CreateFromTopLeftAndSize(topLeft, new Vector2(width, height));
			if (isLayoutOnly)
			{
				UI.OverridePreviousBounds(entryBounds);
				return;
			}

			ProjectDescription project = projects[index];
			(bool compatible, string message) = CanOpenProject(project);
			string warning = compatible ? (index < projectWarnings.Length ? projectWarnings[index] : string.Empty) : message;

			// Tapping the row opens the project; MANAGE has rename, duplicate and delete.
			bool open = MobileTheme.Button(string.Empty, MobileTheme.Neutral, topLeft, new Vector2(width - manageWidth - 0.4f, height), true, Anchor.TopLeft);
			Bounds2D row = UI.PrevBounds;
			string name = MobileTheme.Fit(project.ProjectName.ToUpperInvariant(), row.Width - 2f, 0.78f, true);
			bool hasWarning = !string.IsNullOrEmpty(warning);
			MobileTheme.Text(name, row.CentreLeft + new Vector2(1.2f, hasWarning ? 0.8f : 0), 0.78f, Color.white, Anchor.TextCentreLeft, true);
			if (hasWarning)
			{
				MobileTheme.Text(MobileTheme.Fit(warning, row.Width - 2f, 0.52f), row.CentreLeft + new Vector2(1.2f, -1.1f), 0.52f, MobileTheme.WarningCol);
			}

			bool manage = MobileTheme.Button("MANAGE", MobileTheme.Quiet, new Vector2(row.Right + 0.4f, topLeft.y), new Vector2(manageWidth, height), true, Anchor.TopLeft);
			UI.OverridePreviousBounds(entryBounds);

			if (open)
			{
				selectedProjectIndex = index;
				TryOpenProject(project.ProjectName);
			}
			else if (manage)
			{
				selectedProjectIndex = index;
				status = string.Empty;
				screen = ScreenKind.ProjectActions;
			}
		}

		static void DrawProjectActions(Rect safe)
		{
			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			if (!HasSelectedProject)
			{
				screen = ScreenKind.Home;
				return;
			}

			float width = Mathf.Min(44f, safe.width - 4f);
			float x = safe.center.x - width / 2f;
			float y = safe.yMax - 10f;

			UI.DrawText(SelectedProjectName.ToUpperInvariant(), theme.FontBold, theme.FontSizeRegular * 1.1f, new Vector2(x, y), Anchor.TextCentreLeft, Color.white);
			y -= 2.6f;

			(bool compatible, string message) = CanOpenProject(projects[selectedProjectIndex]);
			string warning = compatible ? (selectedProjectIndex < projectWarnings.Length ? projectWarnings[selectedProjectIndex] : string.Empty) : message;
			if (!string.IsNullOrEmpty(warning))
			{
				UI.DrawText(warning, theme.FontRegular, theme.FontSizeRegular * 0.62f, new Vector2(x, y), Anchor.TextCentreLeft, MobileTheme.WarningCol);
				y -= 2.4f;
			}

			const float h = 5f;
			const float gap = 0.55f;
			if (MobileTheme.Button("OPEN", MobileTheme.Primary, new Vector2(x, y), new Vector2(width, h), compatible, Anchor.TopLeft))
			{
				TryOpenProject(SelectedProjectName);
				return;
			}
			y -= h + gap;

			if (MobileTheme.Button("RENAME", MobileTheme.Neutral, new Vector2(x, y), new Vector2(width, h), compatible, Anchor.TopLeft))
			{
				OpenProjectNameScreen(ProjectNameMode.Rename);
				return;
			}
			y -= h + gap;

			if (MobileTheme.Button("DUPLICATE", MobileTheme.Neutral, new Vector2(x, y), new Vector2(width, h), compatible, Anchor.TopLeft))
			{
				OpenProjectNameScreen(ProjectNameMode.Duplicate);
				return;
			}
			y -= h + gap;

			if (MobileTheme.Button("DELETE", MobileTheme.Danger, new Vector2(x, y), new Vector2(width, h), true, Anchor.TopLeft))
			{
				status = string.Empty;
				screen = ScreenKind.DeleteProject;
			}

			DrawStatus(safe, theme);
		}

		static void DrawProjectName(Rect safe)
		{
			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			float width = Mathf.Min(62f, safe.width - 4f);
			Rect visible = MobileUI.KeyboardAwareSafeRectUI;
			float centreY = MobileInputBridge.KeyboardVisible ? visible.center.y + 0.5f : safe.center.y + 2f;
			float formTop = Mathf.Min(safe.yMax - 7.1f, centreY + 7f);
			Vector2 topLeft = new(safe.center.x - width / 2f, formTop);

			string title = projectNameMode switch
			{
				ProjectNameMode.New => "NEW PROJECT",
				ProjectNameMode.Rename => "RENAME PROJECT",
				ProjectNameMode.Duplicate => "DUPLICATE PROJECT",
				_ => "PROJECT"
			};
			string confirmLabel = projectNameMode switch
			{
				ProjectNameMode.New => "CREATE",
				ProjectNameMode.Rename => "RENAME",
				ProjectNameMode.Duplicate => "DUPLICATE",
				_ => "CONFIRM"
			};

			UI.DrawText(title, theme.FontBold, theme.FontSizeRegular * 1.15f,
				topLeft, Anchor.TextCentreLeft, Color.white);
			topLeft.y -= 3.1f;

			InputFieldState field = UI.InputField(
				ID_ProjectName,
				theme.ChipNameInputField,
				topLeft,
				new Vector2(width, 6.2f),
				"PROJECT NAME",
				Anchor.TopLeft,
				1.1f,
				ValidateProjectName,
				true);
			string name = field.text.Trim();

			bool conflict = ProjectNameConflicts(name);
			bool sameExactName = projectNameMode == ProjectNameMode.Rename &&
			                     HasSelectedProject &&
			                     string.Equals(name, SelectedProjectName, StringComparison.Ordinal);
			bool valid = name.Length > 0 &&
			             name.Length <= MainMenu.MaxProjectNameLength &&
			             SaveUtils.ValidFileName(name) &&
			             !conflict &&
			             !sameExactName;

			Vector2 buttonPos = UI.PrevBounds.BottomLeft + Vector2.down * 1f;
			float bw = (width - 0.6f) / 2f;

			if (MobileTheme.Button("CANCEL", theme.MenuPopupButtonTheme, buttonPos, new Vector2(bw, 5.3f),
				    true, Anchor.TopLeft))
			{
				field.ClearText();
				NavigateBack();
				return;
			}

			if (MobileTheme.Button(confirmLabel, theme.MainMenuButtonTheme,
				    buttonPos + Vector2.right * (bw + 0.6f), new Vector2(bw, 5.3f),
				    valid, Anchor.TopLeft))
			{
				PerformProjectNameAction(name);
				return;
			}

			string helper = conflict
				? "A project with this name already exists."
				: sameExactName
					? "Enter a different name."
					: "Use up to 20 characters.";
			UI.DrawText(helper, theme.FontRegular, theme.FontSizeRegular * 0.68f,
				buttonPos + Vector2.down * 6.2f, Anchor.TextCentreLeft,
				(conflict || sameExactName) ? Color.yellow : RewiredUI.SecondaryText);

			DrawStatus(safe, theme);
		}

		static void DrawDeleteProject(Rect safe)
		{
			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			float width = Mathf.Min(58f, safe.width - 4f);
			float x = safe.center.x - width / 2f;
			float y = safe.center.y + 7f;

			UI.DrawText("DELETE PROJECT", theme.FontBold, theme.FontSizeRegular * 1.15f,
				new Vector2(x, y), Anchor.TextCentreLeft, Color.white);
			y -= 4f;

			UI.DrawText($"Move \"{SelectedProjectName}\" to deleted projects?",
				theme.FontRegular, theme.FontSizeRegular * 0.9f,
				new Vector2(x, y), Anchor.TextCentreLeft, Color.yellow);
			y -= 4.2f;

			float gap = 0.6f;
			float bw = (width - gap) / 2f;
			if (MobileTheme.Button("CANCEL", theme.MenuButtonTheme, new Vector2(x, y), new Vector2(bw, 5.4f),
				    true, Anchor.TopLeft))
			{
				screen = ScreenKind.ProjectActions;
				return;
			}

			if (MobileTheme.Button("DELETE", MobileTheme.Danger,
				    new Vector2(x + bw + gap, y), new Vector2(bw, 5.4f),
				    HasSelectedProject, Anchor.TopLeft))
			{
				DeleteSelectedProject();
			}

			DrawStatus(safe, theme);
		}

		static bool ValidateProjectName(string text)
		{
			if (text.Length > MainMenu.MaxProjectNameLength) return false;
			return !SaveUtils.NameContainsForbiddenChar(text);
		}

		static void DrawMobileSettings(Rect safe)
		{
			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			float width = Mathf.Min(64f, safe.width - 4f);
			float x = safe.center.x - width / 2f;
			float y = safe.yMax - 10f;
			const float h = 5.2f;

			UI.DrawText("SETTINGS", theme.FontBold, theme.FontSizeRegular * 1.15f,
				new Vector2(x, y + 2f), Anchor.TextCentreLeft, Color.white);

			y -= 1.5f;
			UI.DrawText("FRAME RATE", theme.FontBold, theme.FontSizeRegular * 0.72f,
				new Vector2(x, y), Anchor.TextCentreLeft, RewiredUI.SecondaryText);
			y -= 1.6f;

			int[] rates = { 60, 90, 120 };
			const float gap = 0.45f;
			float third = (width - gap * 2f) / 3f;
			for (int i = 0; i < rates.Length; i++)
			{
				int fps = rates[i];
				if (MobileTheme.Button($"{fps} FPS", MobileTheme.Toggle(MobileUI.TargetFrameRate == fps), new Vector2(x + i * (third + gap), y),
					    new Vector2(third, h), true, Anchor.TopLeft))
					MobileUI.SetTargetFrameRate(fps);
			}

			y -= h + 2.6f;
			UI.DrawText("UPDATES", theme.FontBold, theme.FontSizeRegular * 0.72f,
				new Vector2(x, y), Anchor.TextCentreLeft, RewiredUI.SecondaryText);
			y -= 1.6f;
			float half = (width - gap) / 2f;
			bool checkAtStart = !Main.ActiveAppSettings.DisableUpdateCheck;
			if (MobileTheme.Button("CHECK AT START", MobileTheme.Toggle(checkAtStart), new Vector2(x, y), new Vector2(half, h), true, Anchor.TopLeft))
			{
				AppSettings settings = Main.ActiveAppSettings;
				settings.DisableUpdateCheck = checkAtStart;
				Main.SaveAppSettings(settings);
			}
			string updateLabel = AppUpdater.ActionLabel ?? AppUpdater.StatusText;
			if (MobileTheme.Button(updateLabel, MobileTheme.Neutral, new Vector2(x + half + gap, y), new Vector2(half, h), !AppUpdater.Busy, Anchor.TopLeft))
			{
				AppUpdater.RunAction();
			}

			y -= h + 2.6f;
			UI.DrawText("TOUCH", theme.FontBold, theme.FontSizeRegular * 0.72f,
				new Vector2(x, y), Anchor.TextCentreLeft, RewiredUI.SecondaryText);
			y -= 2f;
			string[] lines =
			{
				"Tap a part to select it; drag it to move it.",
				"Drag empty space to move around; pinch to zoom.",
				"Hold a finger on anything to see all its options."
			};
			foreach (string line in lines)
			{
				UI.DrawText(line, theme.FontRegular, theme.FontSizeRegular * 0.68f, new Vector2(x, y), Anchor.TextCentreLeft, Color.white);
				y -= 2.2f;
			}
		}

		static void DrawAbout(Rect safe)
		{
			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			// The header already shows the logo.
			float y = safe.yMax - 11f;

			UI.DrawText("Independent digital logic simulator", theme.FontRegular, theme.FontSizeRegular,
				new Vector2(safe.center.x, y), Anchor.Centre, Color.white);
			y -= 4f;
			UI.DrawText($"Version {Main.RewiredVersion} ({Main.RewiredLastUpdatedString})",
				theme.FontRegular, theme.FontSizeRegular * 0.78f,
				new Vector2(safe.center.x, y), Anchor.Centre, RewiredUI.SecondaryText);
			y -= 4f;
			UI.DrawText("Build logic chips from NAND gates up, then use them inside bigger chips.",
				theme.FontRegular, theme.FontSizeRegular * 0.78f,
				new Vector2(safe.center.x, y), Anchor.Centre, RewiredUI.SecondaryText);
			y -= 3f;
			UI.DrawText("Based on Digital Logic Sim by Sebastian Lague.",
				theme.FontRegular, theme.FontSizeRegular * 0.78f,
				new Vector2(safe.center.x, y), Anchor.Centre, RewiredUI.SecondaryText);

			if (!AppUpdater.Supported) return;
			y -= 6f;
			UI.DrawText(AppUpdater.StatusText, theme.FontRegular, theme.FontSizeRegular * 0.78f,
				new Vector2(safe.center.x, y), Anchor.Centre, Color.white);
			y -= 4.5f;
			string label = AppUpdater.ActionLabel;
			if (label != null && MobileTheme.Button(label, MobileTheme.Primary, new Vector2(safe.center.x, y), new Vector2(26f, 5.2f), true, Anchor.Centre))
			{
				AppUpdater.RunAction();
			}
			if (AppUpdater.Current == AppUpdater.State.Available && AppUpdater.Platform == UpdatePlatform.Android)
			{
				y -= 5f;
				UI.DrawText("The browser downloads the new APK. Open it to update; Android may ask to allow installs from the browser.",
					theme.FontRegular, theme.FontSizeRegular * 0.62f, new Vector2(safe.center.x, y), Anchor.Centre, RewiredUI.SecondaryText);
			}
		}

		static void OpenProjectNameScreen(ProjectNameMode mode)
		{
			projectNameMode = mode;
			status = string.Empty;
			InputFieldState field = UI.GetInputFieldState(ID_ProjectName);
			field.ClearText();

			if (mode == ProjectNameMode.Rename && HasSelectedProject)
			{
				field.SetText(SelectedProjectName);
			}
			else if (mode == ProjectNameMode.Duplicate && HasSelectedProject)
			{
				string suggestion = SelectedProjectName + " COPY";
				if (suggestion.Length > MainMenu.MaxProjectNameLength)
					suggestion = suggestion.Substring(0, MainMenu.MaxProjectNameLength);
				field.SetText(suggestion);
			}

			screen = ScreenKind.ProjectName;
		}

		static bool ProjectNameConflicts(string candidate)
		{
			if (string.IsNullOrWhiteSpace(candidate)) return false;

			for (int i = 0; i < projects.Length; i++)
			{
				if (!string.Equals(projects[i].ProjectName, candidate, StringComparison.OrdinalIgnoreCase))
					continue;

				// The project being renamed never conflicts with itself: a case-only change is
				// allowed, and the unchanged name is reported as "Enter a different name".
				bool isProjectBeingRenamed = projectNameMode == ProjectNameMode.Rename && i == selectedProjectIndex;
				if (!isProjectBeingRenamed) return true;
			}

			return false;
		}

		static void PerformProjectNameAction(string name)
		{
			try
			{
				status = string.Empty;
				switch (projectNameMode)
				{
					case ProjectNameMode.New:
						UI.GetInputFieldState(ID_ProjectName).ClearText();
						Main.CreateOrLoadProject(name);
						return;
					case ProjectNameMode.Rename:
					{
						string oldName = SelectedProjectName;
						Saver.RenameProject(oldName, name);
						RefreshProjects();
						SelectProjectByName(name);
						screen = ScreenKind.ProjectActions;
						break;
					}
					case ProjectNameMode.Duplicate:
						Saver.DuplicateProject(SelectedProjectName, name);
						RefreshProjects();
						SelectProjectByName(name);
						screen = ScreenKind.ProjectActions;
						break;
				}

				UI.GetInputFieldState(ID_ProjectName).ClearText();
			}
			catch (Exception e)
			{
				status = e.Message;
				Debug.LogException(e);
			}
		}

		static void DeleteSelectedProject()
		{
			if (!HasSelectedProject) return;

			try
			{
				Saver.DeleteProject(SelectedProjectName);
				RefreshProjects();
				selectedProjectIndex = -1;
				status = string.Empty;
				screen = ScreenKind.Home;
			}
			catch (Exception e)
			{
				status = e.Message;
				Debug.LogException(e);
			}
		}

		static void TryOpenProject(string name)
		{
			try
			{
				if (HasSelectedProject)
				{
					(bool compatible, string message) = CanOpenProject(projects[selectedProjectIndex]);
					if (!compatible)
					{
						status = message;
						return;
					}
				}

				status = string.Empty;
				Main.CreateOrLoadProject(name);
			}
			catch (Exception e)
			{
				status = e.Message;
				Debug.LogException(e);
			}
		}

		static (bool compatible, string message) CanOpenProject(ProjectDescription project)
		{
			try
			{
				Main.Version earliestCompatible = Main.Version.Parse(project.DLSVersion_EarliestCompatible);
				bool compatible = Main.DLSVersion.ToInt() >= earliestCompatible.ToInt();
				return (compatible, compatible ? string.Empty : $"Requires version {earliestCompatible} or later.");
			}
			catch
			{
				return (false, "Unrecognized project format.");
			}
		}

		static void RefreshProjects()
		{
			try
			{
				projects = Loader.LoadAllProjectDescriptions();
				projectWarnings = projects.Select(p => Loader.BuiltinNameConflictMessage(Loader.FindBuiltinNameConflicts(p))).ToArray();
				if (selectedProjectIndex >= projects.Length) selectedProjectIndex = -1;
				status = string.Empty;
			}
			catch (Exception e)
			{
				projects = Array.Empty<ProjectDescription>();
				projectWarnings = Array.Empty<string>();
				selectedProjectIndex = -1;
				status = e.Message;
				Debug.LogException(e);
			}
		}

		static void SelectProjectByName(string name)
		{
			selectedProjectIndex = -1;
			for (int i = 0; i < projects.Length; i++)
			{
				if (string.Equals(projects[i].ProjectName, name, StringComparison.Ordinal))
				{
					selectedProjectIndex = i;
					return;
				}
			}
		}

		static void DrawStatus(Rect safe, DrawSettings.UIThemeDLS theme)
		{
			if (string.IsNullOrWhiteSpace(status)) return;
			UI.DrawText(status, theme.FontRegular, theme.FontSizeRegular * 0.68f,
				new Vector2(safe.center.x, safe.yMin + 1.0f), Anchor.Centre, Color.yellow);
		}

		static void NavigateBack()
		{
			MobileInputBridge.DismissKeyboard();
			status = string.Empty;

			if (screen == ScreenKind.DeleteProject ||
			    (screen == ScreenKind.ProjectName && projectNameMode != ProjectNameMode.New))
			{
				screen = ScreenKind.ProjectActions;
				return;
			}

			screen = ScreenKind.Home;
		}

		public static bool HandleBackButton()
		{
			if (screen == ScreenKind.Home) return false;
			NavigateBack();
			return true;
		}

		public static void OnMenuOpened()
		{
			screen = ScreenKind.Home;
			projectNameMode = ProjectNameMode.New;
			selectedProjectIndex = -1;
			status = string.Empty;
			RefreshProjects();
		}

		public static void Reset()
		{
			screen = ScreenKind.Home;
			projectNameMode = ProjectNameMode.New;
			projects = Array.Empty<ProjectDescription>();
			projectWarnings = Array.Empty<string>();
			selectedProjectIndex = -1;
			status = string.Empty;
		}
	}
}
