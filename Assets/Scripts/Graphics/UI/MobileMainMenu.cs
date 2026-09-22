using System;
using DLS.Description;
using DLS.Game;
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
			Projects,
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
				case ScreenKind.Projects: DrawProjects(safe); break;
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
				if (UI.Button("BACK", theme.MainMenuButtonTheme, bar.CentreLeft + Vector2.right * 0.6f,
					    new Vector2(11.5f, 4.8f), true, false, false, Anchor.CentreLeft))
				{
					NavigateBack();
				}
			}

			float titleX = screen == ScreenKind.Home ? bar.Left + 1.2f : bar.Left + 13.3f;
			UI.DrawText("REWIRED", FontType.Born2bSporty, 4.7f,
				new Vector2(titleX, bar.Centre.y + 0.15f), Anchor.TextCentreLeft, Color.white);
			UI.DrawText($"ANDROID  •  v{Main.RewiredVersion}", theme.FontRegular, theme.FontSizeRegular * 0.68f,
				bar.CentreRight + Vector2.left * 1.1f, Anchor.TextCentreRight, RewiredUI.SecondaryText);
		}

		static void DrawHome(Rect safe)
		{
			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			float contentTop = safe.yMax - 8.5f;
			float width = Mathf.Min(48f, safe.width - 4f);
			float x = safe.center.x - width / 2f;
			float h = 5.6f;
			float gap = 0.7f;
			float y = contentTop;

			UI.DrawText("DIGITAL LOGIC SIMULATOR", theme.FontBold, theme.FontSizeRegular * 0.9f,
				new Vector2(safe.center.x, y + 2.2f), Anchor.Centre, RewiredUI.SecondaryText);
			y -= 2.2f;

			if (UI.Button("NEW PROJECT", theme.MainMenuButtonTheme, new Vector2(x, y), new Vector2(width, h),
				    true, false, false, Anchor.TopLeft))
			{
				OpenProjectNameScreen(ProjectNameMode.New);
			}
			y -= h + gap;

			if (UI.Button("OPEN PROJECT", theme.MainMenuButtonTheme, new Vector2(x, y), new Vector2(width, h),
				    true, false, false, Anchor.TopLeft))
			{
				RefreshProjects();
				selectedProjectIndex = -1;
				screen = ScreenKind.Projects;
			}
			y -= h + gap;

			if (UI.Button("MOBILE SETTINGS", theme.MainMenuButtonTheme, new Vector2(x, y), new Vector2(width, h),
				    true, false, false, Anchor.TopLeft))
				screen = ScreenKind.Settings;
			y -= h + gap;

			if (UI.Button("ABOUT", theme.MainMenuButtonTheme, new Vector2(x, y), new Vector2(width, h),
				    true, false, false, Anchor.TopLeft))
				screen = ScreenKind.About;
			y -= h + gap;

			if (UI.Button("QUIT", theme.MenuPopupButtonTheme, new Vector2(x, y), new Vector2(width, h),
				    true, false, false, Anchor.TopLeft))
				Application.Quit();

			UI.DrawText("Touch-first interface • no Android system keyboard required",
				theme.FontRegular, theme.FontSizeRegular * 0.68f,
				new Vector2(safe.center.x, safe.yMin + 2.2f), Anchor.Centre, new Color(1, 1, 1, 0.45f));
		}

		static void DrawProjects(Rect safe)
		{
			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			const float pad = 1.2f;
			const float gap = 1.1f;
			float top = safe.yMax - 7.7f;
			float bottom = safe.yMin + 1.1f;
			float totalWidth = safe.width - pad * 2f;
			float listWidth = (totalWidth - gap) * 0.60f;
			float actionWidth = totalWidth - gap - listWidth;
			float height = Mathf.Max(10f, top - bottom);

			Vector2 listTop = new(safe.xMin + pad, top);
			Vector2 actionTop = new(listTop.x + listWidth + gap, top);

			UI.DrawText("PROJECTS", theme.FontBold, theme.FontSizeRegular * 1.0f,
				listTop, Anchor.TextCentreLeft, Color.white);
			listTop.y -= 2.2f;
			float listHeight = height - 2.2f;

			if (projects.Length == 0)
			{
				UI.DrawText("NO PROJECTS", theme.FontRegular, theme.FontSizeRegular,
					new Vector2(listTop.x + listWidth / 2f, safe.center.y), Anchor.Centre, RewiredUI.SecondaryText);
			}
			else
			{
				UI.DrawScrollView(ID_ProjectScroll, listTop, new Vector2(listWidth, listHeight), 0.55f,
					Anchor.TopLeft, theme.ScrollTheme, DrawProjectElementFunc, projects.Length);
			}

			UI.DrawText(HasSelectedProject ? SelectedProjectName.ToUpperInvariant() : "SELECT PROJECT",
				theme.FontBold, theme.FontSizeRegular * 0.92f,
				actionTop, Anchor.TextCentreLeft, Color.white);
			actionTop.y -= 2.4f;

			if (!HasSelectedProject)
			{
				UI.DrawText("Tap a project on the left.", theme.FontRegular, theme.FontSizeRegular * 0.75f,
					actionTop, Anchor.TextCentreLeft, RewiredUI.SecondaryText);
				DrawStatus(safe, theme);
				return;
			}

			(bool compatible, string message) = CanOpenProject(projects[selectedProjectIndex]);
			if (!compatible)
			{
				UI.DrawText(message, theme.FontRegular, theme.FontSizeRegular * 0.68f,
					actionTop, Anchor.TextCentreLeft, Color.yellow);
				actionTop.y -= 3.0f;
			}

			float buttonH = 5.0f;
			float buttonGap = 0.55f;
			if (UI.Button("OPEN", theme.MainMenuButtonTheme, actionTop, new Vector2(actionWidth, buttonH),
				    compatible, false, false, Anchor.TopLeft))
			{
				TryOpenProject(SelectedProjectName);
				return;
			}
			actionTop.y -= buttonH + buttonGap;

			if (UI.Button("DUPLICATE", theme.MenuButtonTheme, actionTop, new Vector2(actionWidth, buttonH),
				    compatible, false, false, Anchor.TopLeft))
			{
				OpenProjectNameScreen(ProjectNameMode.Duplicate);
				return;
			}
			actionTop.y -= buttonH + buttonGap;

			if (UI.Button("RENAME", theme.MenuButtonTheme, actionTop, new Vector2(actionWidth, buttonH),
				    compatible, false, false, Anchor.TopLeft))
			{
				OpenProjectNameScreen(ProjectNameMode.Rename);
				return;
			}
			actionTop.y -= buttonH + buttonGap;

			if (UI.Button("DELETE", theme.MenuPopupButtonTheme, actionTop, new Vector2(actionWidth, buttonH),
				    true, false, false, Anchor.TopLeft))
			{
				status = string.Empty;
				screen = ScreenKind.DeleteProject;
			}

			DrawStatus(safe, theme);
		}

		static void DrawProjectElement(Vector2 topLeft, float width, int index, bool isLayoutOnly)
		{
			const float height = 5.4f;
			Bounds2D entryBounds = Bounds2D.CreateFromTopLeftAndSize(topLeft, new Vector2(width, height));
			if (isLayoutOnly)
			{
				UI.OverridePreviousBounds(entryBounds);
				return;
			}

			ProjectDescription project = projects[index];
			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			bool selected = index == selectedProjectIndex;
			ButtonTheme rowTheme = selected ? theme.ProjectSelectionButtonSelected : theme.ProjectSelectionButton;
			bool pressed = UI.Button(project.ProjectName.ToUpperInvariant(), rowTheme, topLeft,
				new Vector2(width, height), true, false, false, Anchor.TopLeft, true, 1f);
			Bounds2D bounds = UI.PrevBounds;

			bool compatible = CanOpenProject(project).compatible;
			UI.DrawText(compatible ? "READY" : "VERSION",
				theme.FontBold, theme.FontSizeRegular * 0.62f,
				bounds.CentreRight + Vector2.left * 0.9f, Anchor.TextCentreRight,
				compatible ? RewiredUI.SecondaryText : Color.yellow);
			UI.OverridePreviousBounds(bounds);

			if (pressed)
			{
				selectedProjectIndex = index;
				status = string.Empty;
			}
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

			if (UI.Button("CANCEL", theme.MenuPopupButtonTheme, buttonPos, new Vector2(bw, 5.3f),
				    true, false, false, Anchor.TopLeft))
			{
				field.ClearText();
				NavigateBack();
				return;
			}

			if (UI.Button(confirmLabel, theme.MainMenuButtonTheme,
				    buttonPos + Vector2.right * (bw + 0.6f), new Vector2(bw, 5.3f),
				    valid, false, false, Anchor.TopLeft))
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
			if (UI.Button("CANCEL", theme.MenuButtonTheme, new Vector2(x, y), new Vector2(bw, 5.4f),
				    true, false, false, Anchor.TopLeft))
			{
				screen = ScreenKind.Projects;
				return;
			}

			if (UI.Button("DELETE", theme.MenuPopupButtonTheme,
				    new Vector2(x + bw + gap, y), new Vector2(bw, 5.4f),
				    HasSelectedProject, false, false, Anchor.TopLeft))
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
			float h = 5.2f;

			UI.DrawText("MOBILE SETTINGS", theme.FontBold, theme.FontSizeRegular * 1.15f,
				new Vector2(x, y + 2f), Anchor.TextCentreLeft, Color.white);

			y -= 1.5f;
			UI.DrawText("DEFAULT TOUCH MODE", theme.FontBold, theme.FontSizeRegular * 0.72f,
				new Vector2(x, y), Anchor.TextCentreLeft, RewiredUI.SecondaryText);
			y -= 1.6f;
			float half = (width - 0.6f) / 2f;
			bool panDefault = MobileUI.PanByDefault;
			if (UI.Button("SELECT", panDefault ? theme.MenuButtonTheme : theme.ChipLibraryCollectionToggleOn,
				    new Vector2(x, y), new Vector2(half, h), true, false, false, Anchor.TopLeft))
			{
				MobileUI.SetPanDefault(false);
				MobileUI.SetPanMode(false);
			}
			if (UI.Button("PAN", panDefault ? theme.ChipLibraryCollectionToggleOn : theme.MenuButtonTheme,
				    new Vector2(x + half + 0.6f, y), new Vector2(half, h), true, false, false, Anchor.TopLeft))
			{
				MobileUI.SetPanDefault(true);
				MobileUI.SetPanMode(true);
			}

			y -= h + 2.3f;
			UI.DrawText("FRAME RATE", theme.FontBold, theme.FontSizeRegular * 0.72f,
				new Vector2(x, y), Anchor.TextCentreLeft, RewiredUI.SecondaryText);
			y -= 1.6f;

			int[] rates = { 60, 90, 120 };
			float gap = 0.45f;
			float third = (width - gap * 2f) / 3f;
			for (int i = 0; i < rates.Length; i++)
			{
				int fps = rates[i];
				ButtonTheme bt = MobileUI.TargetFrameRate == fps ? theme.ChipLibraryCollectionToggleOn : theme.MenuButtonTheme;
				if (UI.Button($"{fps} FPS", bt, new Vector2(x + i * (third + gap), y),
					    new Vector2(third, h), true, false, false, Anchor.TopLeft))
					MobileUI.SetTargetFrameRate(fps);
			}

			y -= h + 2.6f;
			UI.DrawText("GESTURES", theme.FontBold, theme.FontSizeRegular * 0.72f,
				new Vector2(x, y), Anchor.TextCentreLeft, RewiredUI.SecondaryText);
			y -= 2f;
			UI.DrawText("Pinch: zoom  •  Two fingers: pan  •  Long press: context menu  •  Left-edge swipe: tools",
				theme.FontRegular, theme.FontSizeRegular * 0.72f,
				new Vector2(x, y), Anchor.TextCentreLeft, Color.white);
		}

		static void DrawAbout(Rect safe)
		{
			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			float y = safe.yMax - 12f;

			UI.DrawText("REWIRED", FontType.Born2bSporty, 8f,
				new Vector2(safe.center.x, y + 4f), Anchor.Centre, Color.white);
			UI.DrawText("Independent digital logic simulator", theme.FontRegular, theme.FontSizeRegular,
				new Vector2(safe.center.x, y), Anchor.Centre, Color.white);
			y -= 4f;
			UI.DrawText($"Version {Main.RewiredVersion} ({Main.RewiredLastUpdatedString})",
				theme.FontRegular, theme.FontSizeRegular * 0.78f,
				new Vector2(safe.center.x, y), Anchor.Centre, RewiredUI.SecondaryText);
			y -= 4f;
			UI.DrawText("Android UI is a dedicated touch-first layout; desktop UI remains unchanged.",
				theme.FontRegular, theme.FontSizeRegular * 0.78f,
				new Vector2(safe.center.x, y), Anchor.Centre, RewiredUI.SecondaryText);
			y -= 3f;
			UI.DrawText("Based on Digital Logic Sim by Sebastian Lague.",
				theme.FontRegular, theme.FontSizeRegular * 0.78f,
				new Vector2(safe.center.x, y), Anchor.Centre, RewiredUI.SecondaryText);
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

				bool currentCaseOnlyRename =
					projectNameMode == ProjectNameMode.Rename &&
					i == selectedProjectIndex &&
					!string.Equals(candidate, SelectedProjectName, StringComparison.Ordinal) &&
					string.Equals(candidate, SelectedProjectName, StringComparison.OrdinalIgnoreCase);

				if (!currentCaseOnlyRename) return true;
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
						screen = ScreenKind.Projects;
						break;
					}
					case ProjectNameMode.Duplicate:
						Saver.DuplicateProject(SelectedProjectName, name);
						RefreshProjects();
						SelectProjectByName(name);
						screen = ScreenKind.Projects;
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
				screen = ScreenKind.Projects;
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
				if (selectedProjectIndex >= projects.Length) selectedProjectIndex = -1;
				status = string.Empty;
			}
			catch (Exception e)
			{
				projects = Array.Empty<ProjectDescription>();
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
				screen = ScreenKind.Projects;
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
			selectedProjectIndex = -1;
			status = string.Empty;
		}
	}
}
