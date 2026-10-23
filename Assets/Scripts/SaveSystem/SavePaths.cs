using System;
using System.IO;
using UnityEngine;

namespace DLS.SaveSystem
{
	public static class SavePaths
	{
		const bool UseBuildPathInEditor = false;

		// Unity derives persistentDataPath from the company/product name, which Rewired
		// still shares with the original Digital Logic Sim on desktop. Keep Rewired data in
		// its own subfolder so neither program overwrites the other's projects/settings.
		// Android already has a Rewired-only package directory.
		const string RewiredDataFolderName = "Rewired";

		public const string ProjectFileName = "ProjectDescription.json";
		static readonly string legacyDataPath_Build = Application.persistentDataPath;
		public static readonly string dataPath_Build = Application.platform == RuntimePlatform.Android
			? legacyDataPath_Build
			: Path.Combine(legacyDataPath_Build, RewiredDataFolderName);
		static readonly string dataPath_Editor = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "TestData");
		public static readonly string AllData = Application.isEditor && !UseBuildPathInEditor ? dataPath_Editor : dataPath_Build;

		// Path to save folder for all projects
		public static readonly string ProjectsPath = Path.Combine(AllData, "Projects");
		public static readonly string DeletedProjectsPath = Path.Combine(AllData, "Deleted Projects");
		public static readonly string GlobalChipsPath = Path.Combine(AllData, "Global Chips");
		public static readonly string DeletedGlobalChipsPath = Path.Combine(AllData, "Deleted Global Chips");
		public static readonly string AppSettingsPath = Path.Combine(AllData, "AppSettings.json");

		public static void EnsureDirectoryExists(string directoryPath) => Directory.CreateDirectory(directoryPath);

		// Earlier Rewired builds saved directly into the folder shared with the original
		// Digital Logic Sim. On first launch with the separate folder, copy (never move)
		// that data across so existing users keep their projects and the original app
		// keeps its own copy untouched.
		public static void MigrateLegacyDataIfNeeded()
		{
			if (string.Equals(AllData, legacyDataPath_Build, StringComparison.Ordinal)) return;
			if (!string.Equals(AllData, dataPath_Build, StringComparison.Ordinal)) return;
			if (Directory.Exists(AllData)) return;

			try
			{
				string[] legacyDirectories = { "Projects", "Deleted Projects", "Global Chips", "Deleted Global Chips" };
				bool hasLegacyData = File.Exists(Path.Combine(legacyDataPath_Build, "AppSettings.json"));
				foreach (string name in legacyDirectories) hasLegacyData |= Directory.Exists(Path.Combine(legacyDataPath_Build, name));
				if (!hasLegacyData) return;

				// Copy into a staging folder first so an interrupted migration is retried
				// on the next launch instead of leaving a partial Rewired folder behind.
				string stagingPath = AllData + "_migrating";
				if (Directory.Exists(stagingPath)) Directory.Delete(stagingPath, true);
				Directory.CreateDirectory(stagingPath);
				foreach (string name in legacyDirectories)
				{
					string source = Path.Combine(legacyDataPath_Build, name);
					if (Directory.Exists(source)) SaveUtils.CopyDirectory(source, Path.Combine(stagingPath, name), true);
				}

				foreach (string fileName in new[] { "AppSettings.json", "AppSettings.json.bak" })
				{
					string source = Path.Combine(legacyDataPath_Build, fileName);
					if (File.Exists(source)) File.Copy(source, Path.Combine(stagingPath, fileName));
				}

				Directory.Move(stagingPath, AllData);
				Debug.Log("Copied existing save data into the Rewired data folder: " + AllData);
			}
			catch (Exception e)
			{
				Debug.LogWarning("Could not copy existing save data into the Rewired data folder: " + e.Message);
			}
		}

		// ---- Path to save folder for a specific project ----
		public static string GetProjectPath(string projectName) => CombineContained(ProjectsPath, projectName);
		public static string GetDeletedProjectPath(string projectName) => CombineContained(DeletedProjectsPath, projectName);
		public static string GetChipsPath(string projectName) => Path.Combine(GetProjectPath(projectName), "Chips");
		public static string GetDeletedChipsPath(string projectName) => Path.Combine(GetProjectPath(projectName), "Deleted Chips");
		public static string GetProjectDescriptionPath(string projectName) => Path.Combine(GetProjectPath(projectName), ProjectFileName);
		public static string GetProjectChipPath(string projectName, string chipName) => CombineContained(GetChipsPath(projectName), chipName + ".json");
		public static string GetGlobalChipPath(string chipName) => CombineContained(GlobalChipsPath, chipName + ".json");
		public static string GetExportsPath(string projectName) => Path.Combine(GetProjectPath(projectName), "Exports");
		public static string GetChipTestsPath(string projectName, string chipName) => CombineContained(Path.Combine(GetProjectPath(projectName), "Chip Tests"), chipName + ".json");

		// Project and chip names come from JSON files that users share. Never let such a
		// name (e.g. "..\\..\\x") resolve to a path outside the directory it belongs to.
		public static string CombineContained(string directory, string name)
		{
			if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(new[] { '/', '\\', '\0' }) >= 0)
			{
				throw new InvalidDataException("Invalid save name: " + name);
			}

			string path = Path.Combine(directory, name);
			string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
			string fullPath = Path.GetFullPath(path);
			if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) || fullPath.Length == root.Length)
			{
				throw new InvalidDataException("Save name resolves outside its folder: " + name);
			}

			return path;
		}
	}
}
