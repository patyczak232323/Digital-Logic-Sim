using System;
using System.Linq;
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
			NewProject,
			Settings,
			About
		}

		static readonly UIHandle ID_ProjectName = new("MobileMainMenu_ProjectName");
		static readonly UIHandle ID_ProjectScroll = new("MobileMainMenu_ProjectScroll");
		static readonly UI.ScrollViewDrawElementFunc DrawProjectElementFunc = DrawProjectElement;

		static ScreenKind screen = ScreenKind.Home;
		static ProjectDescription[] projects = Array.Empty<ProjectDescription>();
		static string status = string.Empty;

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
				case ScreenKind.NewProject: DrawNewProject(safe); break;
				case ScreenKind.Settings: DrawSettings(safe); break;
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
				if (UI.Button("BACK", theme.MainMenuButtonTheme, bar.CentreLeft + Vector2.right * 0.6f, new Vector2(11.5f, 4.8f), true, false, false, Anchor.CentreLeft))
					screen = ScreenKind.Home;
			}

			float titleX = screen == ScreenKind.Home ? bar.Left + 1.2f : bar.Left + 13.3f;
			UI.DrawText("REWIRED", FontType.Born2bSporty, 4.7f, new Vector2(titleX, bar.Centre.y + 0.15f), Anchor.TextCentreLeft, Color.white);
			UI.DrawText($"ANDROID  •  v{Main.RewiredVersion}", theme.FontRegular, theme.FontSizeRegular * 0.68f, bar.CentreRight + Vector2.left * 1.1f, Anchor.TextCentreRight, RewiredUI.SecondaryText);
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

			UI.DrawText("DIGITAL LOGIC SIMULATOR", theme.FontBold, theme.FontSizeRegular * 0.9f, new Vector2(safe.center.x, y + 2.2f), Anchor.Centre, RewiredUI.SecondaryText);
			y -= 2.2f;

			if (UI.Button("NEW PROJECT", theme.MainMenuButtonTheme, new Vector2(x, y), new Vector2(width, h), true, false, false, Anchor.TopLeft))
			{
				status = string.Empty;
				UI.GetInputFieldState(ID_ProjectName).ClearText();
				screen = ScreenKind.NewProject;
			}
			y -= h + gap;

			if (UI.Button("OPEN PROJECT", theme.MainMenuButtonTheme, new Vector2(x, y), new Vector2(width, h), true, false, false, Anchor.TopLeft))
			{
				RefreshProjects();
				screen = ScreenKind.Projects;
			}
			y -= h + gap;

			if (UI.Button("MOBILE SETTINGS", theme.MainMenuButtonTheme, new Vector2(x, y), new Vector2(width, h), true, false, false, Anchor.TopLeft))
				screen = ScreenKind.Settings;
			y -= h + gap;

			if (UI.Button("ABOUT", theme.MainMenuButtonTheme, new Vector2(x, y), new Vector2(width, h), true, false, false, Anchor.TopLeft))
				screen = ScreenKind.About;
			y -= h + gap;

			if (UI.Button("QUIT", theme.MenuPopupButtonTheme, new Vector2(x, y), new Vector2(width, h), true, false, false, Anchor.TopLeft))
				Application.Quit();

			UI.DrawText("Touch-first interface • no Android system keyboard required", theme.FontRegular, theme.FontSizeRegular * 0.68f,
				new Vector2(safe.center.x, safe.yMin + 2.2f), Anchor.Centre, new Color(1, 1, 1, 0.45f));
		}

		static void DrawProjects(Rect safe)
		{
			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			float top = safe.yMax - 7.6f;
			float bottom = safe.yMin + 1.2f;
			Vector2 pos = new(safe.xMin + 1.2f, top);
			Vector2 size = new(safe.width - 2.4f, Mathf.Max(8f, top - bottom));

			UI.DrawText("PROJECTS", theme.FontBold, theme.FontSizeRegular * 1.1f, pos, Anchor.TextCentreLeft, Color.white);
			pos.y -= 2.3f;
			size.y -= 2.3f;

			if (projects.Length == 0)
			{
				UI.DrawText("No projects yet.", theme.FontRegular, theme.FontSizeRegular, new Vector2(safe.center.x, safe.center.y), Anchor.Centre, RewiredUI.SecondaryText);
				return;
			}

			UI.DrawScrollView(ID_ProjectScroll, pos, size, 0.55f, Anchor.TopLeft, theme.ScrollTheme, DrawProjectElementFunc, projects.Length);
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
			bool pressed = UI.Button(project.ProjectName.ToUpperInvariant(), theme.ProjectSelectionButton, topLeft, new Vector2(width, height), true, false, false, Anchor.TopLeft, true, 1f);
			Bounds2D bounds = UI.PrevBounds;

			UI.DrawText("OPEN", theme.FontBold, theme.FontSizeRegular * 0.66f, bounds.CentreRight + Vector2.left * 1f, Anchor.TextCentreRight, RewiredUI.SecondaryText);
			UI.OverridePreviousBounds(bounds);

			if (pressed)
			{
				TryOpenProject(project.ProjectName);
			}
		}

		static void DrawNewProject(Rect safe)
		{
			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			float width = Mathf.Min(58f, safe.width - 4f);
			Vector2 centre = new(safe.center.x, safe.center.y + 2f);
			Vector2 topLeft = new(centre.x - width / 2f, centre.y + 7f);

			UI.DrawText("NEW PROJECT", theme.FontBold, theme.FontSizeRegular * 1.15f, topLeft, Anchor.TextCentreLeft, Color.white);
			topLeft.y -= 3.1f;

			InputFieldTheme inputTheme = theme.ChipNameInputField;
			InputFieldState field = UI.InputField(ID_ProjectName, inputTheme, topLeft, new Vector2(width, 6.2f), "PROJECT NAME", Anchor.TopLeft, 1.1f, ValidateProjectName, true);
			string name = field.text.Trim();

			bool valid = name.Length > 0 && name.Length <= MainMenu.MaxProjectNameLength && SaveUtils.ValidFileName(name) && !Loader.ProjectExists(name);
			Vector2 buttonPos = UI.PrevBounds.BottomLeft + Vector2.down * 1f;
			float bw = (width - 0.6f) / 2f;

			if (UI.Button("CANCEL", theme.MenuPopupButtonTheme, buttonPos, new Vector2(bw, 5.3f), true, false, false, Anchor.TopLeft))
			{
				field.ClearText();
				status = string.Empty;
				screen = ScreenKind.Home;
			}

			if (UI.Button("CREATE", theme.MainMenuButtonTheme, buttonPos + Vector2.right * (bw + 0.6f), new Vector2(bw, 5.3f), valid, false, false, Anchor.TopLeft))
			{
				field.ClearText();
				Main.CreateOrLoadProject(name);
			}

			string helper = Loader.ProjectExists(name) && name.Length > 0
				? "A project with this name already exists."
				: "Use up to 20 characters.";
			UI.DrawText(helper, theme.FontRegular, theme.FontSizeRegular * 0.68f, buttonPos + Vector2.down * 6.2f, Anchor.TextCentreLeft,
				Loader.ProjectExists(name) && name.Length > 0 ? Color.yellow : RewiredUI.SecondaryText);
		}

		static bool ValidateProjectName(string text)
		{
			if (text.Length > MainMenu.MaxProjectNameLength) return false;
			return !SaveUtils.NameContainsForbiddenChar(text);
		}

		static void DrawSettings(Rect safe)
		{
			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			float width = Mathf.Min(64f, safe.width - 4f);
			float x = safe.center.x - width / 2f;
			float y = safe.yMax - 10f;
			float h = 5.2f;

			UI.DrawText("MOBILE SETTINGS", theme.FontBold, theme.FontSizeRegular * 1.15f, new Vector2(x, y + 2f), Anchor.TextCentreLeft, Color.white);

			y -= 1.5f;
			UI.DrawText("DEFAULT TOUCH MODE", theme.FontBold, theme.FontSizeRegular * 0.72f, new Vector2(x, y), Anchor.TextCentreLeft, RewiredUI.SecondaryText);
			y -= 1.6f;
			float half = (width - 0.6f) / 2f;
			bool panDefault = MobileUI.PanByDefault;
			if (UI.Button("SELECT", panDefault ? theme.MenuButtonTheme : theme.ChipLibraryCollectionToggleOn, new Vector2(x, y), new Vector2(half, h), true, false, false, Anchor.TopLeft))
			{
				MobileUI.SetPanDefault(false);
				MobileUI.SetPanMode(false);
			}
			if (UI.Button("PAN", panDefault ? theme.ChipLibraryCollectionToggleOn : theme.MenuButtonTheme, new Vector2(x + half + 0.6f, y), new Vector2(half, h), true, false, false, Anchor.TopLeft))
			{
				MobileUI.SetPanDefault(true);
				MobileUI.SetPanMode(true);
			}

			y -= h + 2.3f;
			UI.DrawText("FRAME RATE", theme.FontBold, theme.FontSizeRegular * 0.72f, new Vector2(x, y), Anchor.TextCentreLeft, RewiredUI.SecondaryText);
			y -= 1.6f;

			int[] rates = { 60, 90, 120 };
			float gap = 0.45f;
			float third = (width - gap * 2f) / 3f;
			for (int i = 0; i < rates.Length; i++)
			{
				int fps = rates[i];
				ButtonTheme bt = MobileUI.TargetFrameRate == fps ? theme.ChipLibraryCollectionToggleOn : theme.MenuButtonTheme;
				if (UI.Button($"{fps} FPS", bt, new Vector2(x + i * (third + gap), y), new Vector2(third, h), true, false, false, Anchor.TopLeft))
					MobileUI.SetTargetFrameRate(fps);
			}

			y -= h + 2.6f;
			UI.DrawText("GESTURES", theme.FontBold, theme.FontSizeRegular * 0.72f, new Vector2(x, y), Anchor.TextCentreLeft, RewiredUI.SecondaryText);
			y -= 2f;
			UI.DrawText("Pinch: zoom  •  Two fingers: pan  •  Long press: context menu  •  Left-edge swipe: tools",
				theme.FontRegular, theme.FontSizeRegular * 0.72f, new Vector2(x, y), Anchor.TextCentreLeft, Color.white);
		}

		static void DrawAbout(Rect safe)
		{
			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			float width = Mathf.Min(70f, safe.width - 5f);
			float x = safe.center.x - width / 2f;
			float y = safe.yMax - 12f;

			UI.DrawText("REWIRED", FontType.Born2bSporty, 8f, new Vector2(safe.center.x, y + 4f), Anchor.Centre, Color.white);
			UI.DrawText("Independent digital logic simulator", theme.FontRegular, theme.FontSizeRegular, new Vector2(safe.center.x, y), Anchor.Centre, Color.white);
			y -= 4f;
			UI.DrawText($"Version {Main.RewiredVersion} ({Main.RewiredLastUpdatedString})", theme.FontRegular, theme.FontSizeRegular * 0.78f, new Vector2(safe.center.x, y), Anchor.Centre, RewiredUI.SecondaryText);
			y -= 4f;
			UI.DrawText("Android UI is a dedicated touch-first layout; desktop UI remains unchanged.", theme.FontRegular, theme.FontSizeRegular * 0.78f, new Vector2(safe.center.x, y), Anchor.Centre, RewiredUI.SecondaryText);
			y -= 3f;
			UI.DrawText("Based on Digital Logic Sim by Sebastian Lague.", theme.FontRegular, theme.FontSizeRegular * 0.78f, new Vector2(safe.center.x, y), Anchor.Centre, RewiredUI.SecondaryText);
		}

		static void TryOpenProject(string name)
		{
			try
			{
				status = string.Empty;
				Main.CreateOrLoadProject(name);
			}
			catch (Exception e)
			{
				status = e.Message;
				Debug.LogException(e);
			}
		}

		static void RefreshProjects()
		{
			try
			{
				projects = Loader.LoadAllProjectDescriptions();
				status = string.Empty;
			}
			catch (Exception e)
			{
				projects = Array.Empty<ProjectDescription>();
				status = e.Message;
				Debug.LogException(e);
			}
		}

		public static bool HandleBackButton()
		{
			if (screen == ScreenKind.Home) return false;
			MobileInputBridge.DismissKeyboard();
			screen = ScreenKind.Home;
			status = string.Empty;
			return true;
		}

		public static void OnMenuOpened()
		{
			screen = ScreenKind.Home;
			status = string.Empty;
			RefreshProjects();
		}

		public static void Reset()
		{
			screen = ScreenKind.Home;
			projects = Array.Empty<ProjectDescription>();
			status = string.Empty;
		}
	}
}
