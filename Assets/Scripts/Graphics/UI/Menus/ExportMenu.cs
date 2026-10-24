using System;
using System.Collections;
using System.IO;
using DLS.Description;
using DLS.Export;
using DLS.Game;
using DLS.SaveSystem;
using Seb.Helpers;
using Seb.Types;
using Seb.Vis;
using Seb.Vis.UI;
using UnityEngine;

namespace DLS.Graphics
{
	// Exports a chip as Verilog, as an SVG schematic, or the current view as a PNG.
	// Files go to the project's "Exports" folder; Verilog is also copied to the clipboard.
	// On Android a copy goes to Download/Rewired, where other apps can open it, and SHARE
	// sends it through the system share sheet.
	public static class ExportMenu
	{
		static string chipName;
		static string statusLine1;
		static string statusLine2;
		static Color statusColour;

		static string pendingScreenshotPath;
		static int pendingScreenshotFrame;
		static bool screenshotCaptureStarted;
		static bool keepStatusOnOpen;

		public static void Open(string name)
		{
			chipName = name;
			UIDrawer.SetActiveMenu(UIDrawer.MenuType.Export);
		}

		public static void OnMenuOpened()
		{
			// Reopened after a screenshot: keep its result instead of the default hint.
			if (keepStatusOnOpen)
			{
				keepStatusOnOpen = false;
				return;
			}

			string where = AndroidExports.CanPublish ? "Files are saved in " + AndroidExports.FolderName + "." : "Files are saved in the project's Exports folder.";
			SetStatus(where, string.Empty, RewiredUI.SecondaryText);
		}

		public static void DrawMenu()
		{
			MenuHelper.DrawBackgroundOverlay();
			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			Project project = Project.ActiveProject;

			bool isViewedChip = project != null && ChipDescription.NameMatch(project.ActiveDevChipName, chipName);
			bool canExportLogic = project != null && project.chipLibrary.TryGetChipDescription(chipName ?? string.Empty, out ChipDescription d) && d.ChipType == ChipType.Custom;

			float buttonHeight = MobileUI.IsActive ? 4.6f : DrawSettings.ButtonHeight * 1.2f;
			float width = MobileUI.IsActive ? Mathf.Min(MobileUI.SafeRectUI.width - 2f, 60f) : 34f;
			Vector2 topLeft = MobileUI.IsActive
				? new Vector2(MobileUI.SafeRectUI.center.x - width / 2, MobileUI.SafeRectUI.yMax - 6.4f)
				: UI.Centre + new Vector2(-width / 2, 14f);

			Draw.ID panelID = UI.ReservePanel();
			using (UI.BeginBoundsScope(true))
			{
				string title = string.IsNullOrWhiteSpace(chipName) ? "EXPORT" : "EXPORT  /  " + chipName.ToUpperInvariant();
				RewiredUI.DrawSectionHeader(title, topLeft, width, true);
				Vector2 pos = UI.PrevBounds.BottomLeft + Vector2.down * 1f;
				Vector2 size = new(width, buttonHeight);
				const float gap = 0.6f;

				bool verilog = UI.Button("VERILOG (.v)", theme.ButtonTheme, pos, size, canExportLogic, false, false, Anchor.TopLeft);
				pos = UI.PrevBounds.BottomLeft + Vector2.down * gap;
				bool svg = UI.Button("SCHEMATIC (.svg)", theme.ButtonTheme, pos, size, canExportLogic || isViewedChip, false, false, Anchor.TopLeft);
				pos = UI.PrevBounds.BottomLeft + Vector2.down * gap;
				bool png = UI.Button("SCREENSHOT (.png)", theme.ButtonTheme, pos, size, isViewedChip || string.IsNullOrEmpty(chipName), false, false, Anchor.TopLeft);
				pos = UI.PrevBounds.BottomLeft + Vector2.down * (gap * 2);

				RewiredUI.DrawLabel(statusLine1 ?? string.Empty, pos + Vector2.down * 0.6f, false, 0.62f, statusColour);
				RewiredUI.DrawLabel(Shorten(statusLine2, MobileUI.IsActive ? 48 : 64), pos + Vector2.down * 1.9f, false, 0.55f, RewiredUI.DimText);
				pos += Vector2.down * 3f;

				bool share = false;
				if (AndroidExports.HasSharableExport)
				{
					share = UI.Button("SHARE LAST EXPORT", theme.MainMenuButtonTheme, pos, size, true, false, false, Anchor.TopLeft);
					pos = UI.PrevBounds.BottomLeft + Vector2.down * gap;
				}

				bool close = UI.Button("CLOSE", theme.ButtonTheme, pos, size, true, false, false, Anchor.TopLeft);
				MenuHelper.DrawReservedMenuPanel(panelID, UI.GetCurrentBoundsScope());

				if (verilog) ExportVerilog(project);
				else if (svg) ExportSvg(project, isViewedChip);
				else if (png) RequestScreenshot(project);
				else if (share && !AndroidExports.ShareLast(out string shareError)) SetStatus("Could not open the share sheet.", shareError, Color.red);
				else if (close || KeyboardShortcuts.CancelShortcutTriggered) UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);
			}
		}

		// Called every frame: the screenshot is taken once the menu has closed, so it shows
		// the chip rather than this window, then the menu reopens to report where it went.
		public static void Update()
		{
			if (pendingScreenshotPath == null) return;

			if (Time.frameCount >= pendingScreenshotFrame && !screenshotCaptureStarted)
			{
				screenshotCaptureStarted = true;
				UnityMain.instance.StartCoroutine(CaptureScreenshot(pendingScreenshotPath));
			}
		}

		// ScreenCapture.CaptureScreenshot(path) treats the path as relative to the persistent
		// data folder on mobile, so the image is captured as a texture and written directly.
		static IEnumerator CaptureScreenshot(string path)
		{
			yield return new WaitForEndOfFrame();

			Texture2D texture = null;
			try
			{
				texture = ScreenCapture.CaptureScreenshotAsTexture(MobileUI.IsActive ? 1 : 2);
				File.WriteAllBytes(path, texture.EncodeToPNG());
				WebStorage.MarkDirty();
				ReportExport("Screenshot saved.", path, "image/png");
			}
			catch (Exception e)
			{
				SetStatus("Could not take the screenshot.", e.Message, Color.red);
			}
			finally
			{
				if (texture != null) UnityEngine.Object.Destroy(texture);
				pendingScreenshotPath = null;
				screenshotCaptureStarted = false;
				keepStatusOnOpen = true;
				UIDrawer.SetActiveMenu(UIDrawer.MenuType.Export);
			}
		}

		// Reports where an export went; on Android it is also copied to Download/Rewired.
		static void ReportExport(string message, string path, string mimeType)
		{
			if (AndroidExports.Publish(path, mimeType, out string error))
			{
				SetStatus(message + " Find it in " + AndroidExports.FolderName + ".", Path.GetFileName(path), RewiredUI.Accent);
			}
			else if (error != null)
			{
				SetStatus(message + " Copy to Downloads failed.", error, Color.yellow);
			}
			else
			{
				SetStatus(message, path, RewiredUI.Accent);
			}
		}

		static void ExportVerilog(Project project)
		{
			try
			{
				ChipDescription description = project.chipLibrary.GetChipDescription(chipName);
				string verilog = VerilogExporter.Export(description, project.chipLibrary);
				string path = WriteExport(project, chipName, ".v", verilog);
				InputHelper.CopyToClipboard(verilog);
				ReportExport("Verilog saved and copied.", path, "text/plain");
			}
			catch (Exception e)
			{
				SetStatus("Verilog export failed.", e.Message, Color.red);
			}
		}

		static void ExportSvg(Project project, bool isViewedChip)
		{
			try
			{
				DevChipInstance chip = isViewedChip
					? project.ViewedChip
					: DevChipInstance.LoadFromDescriptionTest(project.chipLibrary.GetChipDescription(chipName), project.chipLibrary).devChip;

				string name = string.IsNullOrWhiteSpace(chipName) ? "Untitled" : chipName;
				string path = WriteExport(project, name, ".svg", SchematicSvgExporter.Export(chip, name));
				ReportExport("Schematic saved.", path, "image/svg+xml");
			}
			catch (Exception e)
			{
				SetStatus("Schematic export failed.", e.Message, Color.red);
			}
		}

		static void RequestScreenshot(Project project)
		{
			string name = string.IsNullOrWhiteSpace(chipName) ? "Untitled" : chipName;
			try
			{
				pendingScreenshotPath = UniquePath(project, name, ".png");
				pendingScreenshotFrame = Time.frameCount + 2;
				screenshotCaptureStarted = false;
				UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);
			}
			catch (Exception e)
			{
				pendingScreenshotPath = null;
				SetStatus("Could not take the screenshot.", e.Message, Color.red);
			}
		}

		// Re-exporting a chip replaces its previous .v/.svg file.
		static string WriteExport(Project project, string name, string extension, string contents)
		{
			string path = ExportPath(project, name, extension);
			File.WriteAllText(path, contents);
			return path;
		}

		static string UniquePath(Project project, string name, string extension) => SaveUtils.EnsureUniqueFileName(ExportPath(project, name, extension));

		static string ExportPath(Project project, string name, string extension)
		{
			string directory = SavePaths.GetExportsPath(project.description.ProjectName);
			Directory.CreateDirectory(directory);
			string fileName = SaveUtils.ValidFileName(name) ? name : "Chip";
			return Path.Combine(directory, fileName + extension);
		}

		// Long paths keep their end, which names the file.
		static string Shorten(string text, int maxLength)
		{
			if (string.IsNullOrEmpty(text) || text.Length <= maxLength) return text ?? string.Empty;
			return "..." + text.Substring(text.Length - (maxLength - 3));
		}

		static void SetStatus(string line1, string line2, Color colour)
		{
			statusLine1 = line1;
			statusLine2 = line2;
			statusColour = colour;
		}
	}
}
