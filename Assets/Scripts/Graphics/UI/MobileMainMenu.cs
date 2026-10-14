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
			UI.DrawPanel(new Vector2(safe.xMin, safe.yMax), new Vector2(safe.width, 6.4f), RewiredUI.SurfaceRaised, Anchor.TopLeft);
			Bounds2D bar = UI.PrevBounds;
			if (screen != ScreenKind.Home && UI.Button("BACK", theme.MenuButtonTheme,
				bar.CentreLeft + Vector2.right * 1.2f, new Vector2(10f, 4.8f), true, false, false, Anchor.CentreLeft))
				NavigateBack();

			string title = screen switch
			{
				ScreenKind.Projects => "Projects",
				ScreenKind.Settings => "Settings",
				ScreenKind.About => "About Rewired",
				_ => "Rewired"
			};
			float x = bar.Left + (screen == ScreenKind.Home ? 2f : 12.5f);
			MobileUI.DrawLabel(title, new Vector2(x, bar.Centre.y), bar.Right - x - 16f, 1.3f, Color.white, true);
			UI.DrawText($"v{Main.RewiredVersion}", theme.FontRegular, theme.FontSizeRegular * 0.8f,
				bar.CentreRight + Vector2.left * 1.5f, Anchor.TextCentreRight, RewiredUI.SecondaryText);
		}


		static void DrawHome(Rect safe)
		{
			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			float width = safe.width - 4.8f;
			float left = safe.xMin + 2.4f;
			float top = safe.yMax - 9f;
			float heroWidth = width * 0.46f;
			float actionWidth = width - heroWidth - 2f;
			float actionX = left + heroWidth + 2f;

			UI.DrawText("Build.\nConnect.\nExplore.", theme.FontBold, 4f,
				new Vector2(left, top - 0.5f), Anchor.TopLeft, Color.white);
			MobileUI.DrawParagraph("Your ideas, made into digital circuits.", new Vector2(left, top - 14.5f), heroWidth, 1f);
			MobileUI.DrawParagraph("Pinch to zoom. Use two fingers to move around.", new Vector2(left, top - 21f), heroWidth, 0.82f);

			if (UI.Button("NEW PROJECT", theme.MainMenuButtonTheme, new Vector2(actionX, top), new Vector2(actionWidth, 6.2f), true, false, false, Anchor.TopLeft))
			{
				OpenProjectNameScreen(ProjectNameMode.New);
				return;
			}
			float y = top - 7f;
			if (UI.Button("OPEN PROJECT", theme.MenuButtonTheme, new Vector2(actionX, y), new Vector2(actionWidth, 6.2f), true, false, false, Anchor.TopLeft))
			{
				RefreshProjects();
				selectedProjectIndex = -1;
				screen = ScreenKind.Projects;
				return;
			}
			y -= 8f;
			float gap = 0.65f;
			float third = (actionWidth - gap * 2f) / 3f;
			string[] labels = { "SETTINGS", "ABOUT", "QUIT" };
			for (int i = 0; i < labels.Length; i++)
			{
				if (!UI.Button(labels[i], theme.MenuButtonTheme, new Vector2(actionX + i * (third + gap), y), new Vector2(third, 5.2f), true, false, false, Anchor.TopLeft)) continue;
				if (i == 0) screen = ScreenKind.Settings;
				else if (i == 1) screen = ScreenKind.About;
				else Application.Quit();
				return;
			}
		}


		static void DrawProjects(Rect safe)
		{
			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			float top = safe.yMax - 8.2f;
			float bottom = safe.yMin + (string.IsNullOrWhiteSpace(status) ? 1.2f : 3.6f);
			float width = safe.width - 3.2f;
			float listWidth = width * 0.57f;
			float actionWidth = width - listWidth - 1.5f;
			Vector2 listTop = new(safe.xMin + 1.6f, top);
			Vector2 actionTop = new(listTop.x + listWidth + 1.5f, top);

			MobileUI.DrawLabel($"{projects.Length} projects", listTop, listWidth, 0.85f);
			listTop.y -= 2.5f;
			if (projects.Length == 0)
			{
				MobileUI.DrawParagraph("No projects yet. Create your first circuit to get started.", listTop + Vector2.down * 3f, listWidth - 1f, 1f);
				if (UI.Button("NEW PROJECT", theme.MainMenuButtonTheme, listTop + Vector2.down * 10f, new Vector2(listWidth, 5.8f), true, false, false, Anchor.TopLeft))
					OpenProjectNameScreen(ProjectNameMode.New);
			}
			else
				UI.DrawScrollView(ID_ProjectScroll, listTop, new Vector2(listWidth, Mathf.Max(1f, listTop.y - bottom)), 0.7f, Anchor.TopLeft, theme.ScrollTheme, DrawProjectElementFunc, projects.Length);

			MobileUI.DrawLabel(HasSelectedProject ? SelectedProjectName : "Choose a project", actionTop, actionWidth, 1.05f, Color.white, true);
			actionTop.y -= 3f;
			if (!HasSelectedProject)
			{
				MobileUI.DrawParagraph("Tap a project to open it or manage its saved copy.", actionTop, actionWidth, 0.9f);
				DrawStatus(safe, theme);
				return;
			}

			(bool compatible, string message) = CanOpenProject(projects[selectedProjectIndex]);
			if (!compatible)
			{
				MobileUI.DrawParagraph(message, actionTop, actionWidth, 0.78f, Color.yellow);
				actionTop.y -= 4.2f;
			}
			float h = 5.3f;
			if (UI.Button("OPEN PROJECT", theme.MainMenuButtonTheme, actionTop, new Vector2(actionWidth, h), compatible, false, false, Anchor.TopLeft))
			{
				TryOpenProject(SelectedProjectName);
				return;
			}
			actionTop.y -= h + 0.7f;
			float half = (actionWidth - 0.6f) / 2f;
			if (UI.Button("RENAME", theme.MenuButtonTheme, actionTop, new Vector2(half, h), compatible, false, false, Anchor.TopLeft))
			{
				OpenProjectNameScreen(ProjectNameMode.Rename);
				return;
			}
			if (UI.Button("DUPLICATE", theme.MenuButtonTheme, actionTop + Vector2.right * (half + 0.6f), new Vector2(half, h), compatible, false, false, Anchor.TopLeft))
			{
				OpenProjectNameScreen(ProjectNameMode.Duplicate);
				return;
			}
			actionTop.y -= h + 0.7f;
			if (UI.Button("DELETE PROJECT", theme.MenuPopupButtonTheme, actionTop, new Vector2(actionWidth, h), true, false, false, Anchor.TopLeft))
			{
				status = string.Empty;
				screen = ScreenKind.DeleteProject;
			}
			DrawStatus(safe, theme);
		}


		static void DrawProjectElement(Vector2 topLeft, float width, int index, bool isLayoutOnly)
		{
			const float height = 6.2f;
			Bounds2D bounds = Bounds2D.CreateFromTopLeftAndSize(topLeft, new Vector2(width, height));
			if (isLayoutOnly)
			{
				UI.OverridePreviousBounds(bounds);
				return;
			}
			ProjectDescription project = projects[index];
			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			ButtonTheme row = index == selectedProjectIndex ? theme.ProjectSelectionButtonSelected : theme.ProjectSelectionButton;
			bool pressed = UI.Button(string.Empty, row, topLeft, new Vector2(width, height), true, false, false, Anchor.TopLeft);
			MobileUI.DrawLabel(project.ProjectName, bounds.TopLeft + new Vector2(1.2f, -1.8f), width - 2.4f, 1.05f, Color.white, true);
			bool compatible = CanOpenProject(project).compatible;
			MobileUI.DrawLabel(compatible ? "Ready to open" : "Different project version", bounds.BottomLeft + new Vector2(1.2f, 1.5f), width - 2.4f, 0.75f, compatible ? RewiredUI.SecondaryText : Color.yellow);
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

			MobileUI.DrawParagraph($"Move \"{SelectedProjectName}\" to deleted projects?", new Vector2(x, y), width, 0.9f, Color.yellow);
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
			float width = (safe.width - 6f) / 2f;
			float left = safe.xMin + 2f;
			float right = left + width + 2f;
			float y = safe.yMax - 9f;
			MobileUI.DrawLabel("Default touch mode", new Vector2(left, y), width, 1.1f, Color.white, true);
			y -= 2.5f;
			float half = (width - 0.6f) / 2f;
			bool pan = MobileUI.PanByDefault;
			if (UI.Button("EDIT", pan ? theme.MenuButtonTheme : theme.ChipLibraryCollectionToggleOn, new Vector2(left, y), new Vector2(half, 5.3f), true, false, false, Anchor.TopLeft))
			{
				MobileUI.SetPanDefault(false);
				MobileUI.SetPanMode(false);
			}
			if (UI.Button("MOVE", pan ? theme.ChipLibraryCollectionToggleOn : theme.MenuButtonTheme, new Vector2(left + half + 0.6f, y), new Vector2(half, 5.3f), true, false, false, Anchor.TopLeft))
			{
				MobileUI.SetPanDefault(true);
				MobileUI.SetPanMode(true);
			}
			y -= 8.5f;
			MobileUI.DrawLabel("Frame rate", new Vector2(left, y), width, 1.1f, Color.white, true);
			y -= 2.5f;
			int[] rates = { 60, 90, 120 };
			float third = (width - 1.2f) / 3f;
			for (int i = 0; i < rates.Length; i++)
			{
				ButtonTheme bt = MobileUI.TargetFrameRate == rates[i] ? theme.ChipLibraryCollectionToggleOn : theme.MenuButtonTheme;
				if (UI.Button($"{rates[i]} FPS", bt, new Vector2(left + i * (third + 0.6f), y), new Vector2(third, 5.3f), true, false, false, Anchor.TopLeft))
					MobileUI.SetTargetFrameRate(rates[i]);
			}

			y = safe.yMax - 9f;
			MobileUI.DrawLabel("Touch controls", new Vector2(right, y), width, 1.1f, Color.white, true);
			string[] gestures = { "Pinch to zoom", "Two fingers to move", "Hold to open chip actions", "Swipe from the left for tools" };
			for (int i = 0; i < gestures.Length; i++)
			{
				y -= 4.5f;
				UI.DrawCircle(new Vector2(right + 0.35f, y), 0.25f, RewiredUI.Accent);
				MobileUI.DrawLabel(gestures[i], new Vector2(right + 1.5f, y), width - 1.5f, 0.95f);
			}
		}


		static void DrawAbout(Rect safe)
		{
			float width = Mathf.Min(72f, safe.width - 5f);
			Vector2 pos = new(safe.center.x - width * 0.5f, safe.yMax - 10f);
			MobileUI.DrawLabel("Digital logic, in your hands.", pos, width, 1.6f, Color.white, true);
			pos.y -= 4.5f;
			MobileUI.DrawParagraph("Build logic gates, memory and your own processors. Save reusable chips or generate circuits with RHDL.", pos, width, 1f);
			pos.y -= 9f;
			MobileUI.DrawParagraph("Based on Digital Logic Sim by Sebastian Lague.", pos, width, 0.9f);
			pos.y -= 6f;
			MobileUI.DrawLabel($"Version {Main.RewiredVersion} ({Main.RewiredLastUpdatedString})", pos, width, 0.8f);
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
			MobileUI.DrawLabel(status, new Vector2(safe.xMin + 1.6f, safe.yMin + 1.5f), safe.width - 3.2f, 0.82f, Color.yellow);
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
