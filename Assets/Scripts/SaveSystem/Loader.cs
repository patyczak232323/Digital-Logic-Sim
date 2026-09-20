using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DLS.Description;
using DLS.Game;
using UnityEngine;

namespace DLS.SaveSystem
{
	public static class Loader
	{
		public static AppSettings LoadAppSettings()
		{
			if (!File.Exists(SavePaths.AppSettingsPath) && !File.Exists(SavePaths.AppSettingsPath + ".bak"))
			{
				return AppSettings.Default();
			}

			try
			{
				return AppSettings.Normalize(LoadWithBackup(SavePaths.AppSettingsPath, Serializer.DeserializeAppSettings));
			}
			catch (Exception e)
			{
				Debug.LogWarning("Could not load app settings; defaults will be used. " + e.Message);
				return AppSettings.Default();
			}
		}

		public static Project LoadProject(string projectName)
		{
			ProjectDescription projectDescription = LoadProjectDescription(projectName);
			ChipLibrary chipLibrary = LoadChipLibrary(projectDescription);
			return new Project(projectDescription, chipLibrary);
		}

		public static bool ProjectExists(string projectName)
		{
			string path = SavePaths.GetProjectDescriptionPath(projectName);
			return File.Exists(path) || File.Exists(path + ".bak");
		}

		public static ProjectDescription LoadProjectDescription(string projectName)
		{
			string path = SavePaths.GetProjectDescriptionPath(projectName);
			ProjectDescription desc = LoadWithBackup(path, Serializer.DeserializeProjectDescription);
			desc.ProjectName = projectName; // Enforce name = directory name (in case player modifies manually -- operations like deleting projects rely on this)
			desc.AllCustomChipNames ??= Array.Empty<string>();
			desc.StarredList ??= new List<StarredItem>();
			desc.ChipCollections ??= new List<ChipCollection>();

			for (int i = 0; i < desc.StarredList.Count; i++)
			{
				StarredItem starred = desc.StarredList[i];
				starred.CacheDisplayStrings();
				desc.StarredList[i] = starred;
			}

			foreach (ChipCollection collection in desc.ChipCollections)
			{
				collection.UpdateDisplayStrings();
			}

			return desc;
		}

		// Get list of saved project descriptions (ordered by last save time)
		public static ProjectDescription[] LoadAllProjectDescriptions()
		{
			List<ProjectDescription> projectDescriptions = new();

			foreach (string dir in Directory.EnumerateDirectories(SavePaths.ProjectsPath))
			{
				try
				{
					string projectName = Path.GetFileName(dir);
					projectDescriptions.Add(LoadProjectDescription(projectName));
				}
				catch (Exception)
				{
					// Ignore invalid project directory
				}
			}

			projectDescriptions.Sort((a, b) => b.LastSaveTime.CompareTo(a.LastSaveTime));
			return projectDescriptions.ToArray();
		}

		static ChipLibrary LoadChipLibrary(ProjectDescription projectDescription)
		{
			string chipDirectoryPath = SavePaths.GetChipsPath(projectDescription.ProjectName);
			ChipDescription[] loadedChips = new ChipDescription[projectDescription.AllCustomChipNames.Length];

			if (!Directory.Exists(chipDirectoryPath) && loadedChips.Length > 0) throw new DirectoryNotFoundException(chipDirectoryPath);

			ChipDescription[] builtinChips = BuiltinChipCreator.CreateAllBuiltinChipDescriptions();
			HashSet<string> customChipNameHashset = new(ChipDescription.NameComparer);

			for (int i = 0; i < loadedChips.Length; i++)
			{
				string chipPath = Path.Combine(chipDirectoryPath, projectDescription.AllCustomChipNames[i] + ".json");
				ChipDescription chipDesc = LoadWithBackup(chipPath, Serializer.DeserializeChipDescription);
				if (chipDesc == null || string.IsNullOrWhiteSpace(chipDesc.Name))
				{
					throw new InvalidDataException("Invalid chip description at " + chipPath);
				}

				chipDesc.InputPins ??= Array.Empty<PinDescription>();
				chipDesc.OutputPins ??= Array.Empty<PinDescription>();
				chipDesc.SubChips ??= Array.Empty<SubChipDescription>();
				chipDesc.Wires ??= Array.Empty<WireDescription>();
				chipDesc.Displays ??= Array.Empty<DisplayDescription>();
				loadedChips[i] = chipDesc;
				customChipNameHashset.Add(chipDesc.Name);
			}


			// If built-in chip name conflicts with a custom chip, the built-in chip must have been added in a newer version.
			// In that case, simply exclude the built-in chip. TODO: warn player that they should rename their chip if they want access to new builtin version
			builtinChips = builtinChips.Where(b => !customChipNameHashset.Contains(b.Name)).ToArray();

			UpgradeHelper.ApplyVersionChanges(loadedChips, builtinChips);
			return new ChipLibrary(loadedChips, builtinChips);
		}

		static T LoadWithBackup<T>(string path, Func<string, T> deserialize)
		{
			Exception primaryError = null;

			if (File.Exists(path))
			{
				try
				{
					return deserialize(File.ReadAllText(path));
				}
				catch (Exception e)
				{
					primaryError = e;
				}
			}

			string backupPath = path + ".bak";
			if (File.Exists(backupPath))
			{
				try
				{
					T recovered = deserialize(File.ReadAllText(backupPath));
					Debug.LogWarning("Recovered save data from backup: " + backupPath);
					return recovered;
				}
				catch (Exception backupError)
				{
					Exception primaryFailure = primaryError ?? new FileNotFoundException("Primary save data is missing.", path);
					throw new InvalidDataException("Both the primary save and backup are invalid: " + path, new AggregateException(primaryFailure, backupError));
				}
			}

			if (primaryError != null) throw new InvalidDataException("Save data is invalid and no backup exists: " + path, primaryError);
			throw new FileNotFoundException("No save data or backup found.", path);
		}
	}
}
