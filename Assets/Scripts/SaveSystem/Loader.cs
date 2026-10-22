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

		static HashSet<string> builtinChipNames;

		// Custom chips of the project whose names hide a built-in chip added in a newer version.
		public static string[] FindBuiltinNameConflicts(ProjectDescription projectDescription)
		{
			builtinChipNames ??= new HashSet<string>(BuiltinChipCreator.CreateAllBuiltinChipDescriptions().Select(c => c.Name), ChipDescription.NameComparer);
			string[] customNames = projectDescription.AllCustomChipNames ?? Array.Empty<string>();
			return customNames.Where(builtinChipNames.Contains).Distinct(ChipDescription.NameComparer).ToArray();
		}

		public static string BuiltinNameConflictMessage(string[] conflicts)
		{
			if (conflicts == null || conflicts.Length == 0) return string.Empty;
			return conflicts.Length == 1
				? $"Chip '{conflicts[0]}' hides a new built-in chip. Rename it to use the built-in one."
				: $"Chips {string.Join(", ", conflicts.Select(n => $"'{n}'"))} hide new built-in chips. Rename them to use the built-in ones.";
		}

		public static bool ProjectExists(string projectName)
		{
			if (!TryGetPath(() => SavePaths.GetProjectDescriptionPath(projectName), out string path)) return false;
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
			if (!Directory.Exists(SavePaths.ProjectsPath)) return Array.Empty<ProjectDescription>();

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

		public static ChipDescription LoadProjectChipDescription(string projectName, string chipName)
		{
			string path = SavePaths.GetProjectChipPath(projectName, chipName);
			return NormalizeChipDescription(LoadWithBackup(path, Serializer.DeserializeChipDescription), path);
		}

		public static ChipDescription LoadGlobalChipDescription(string chipName)
		{
			string path = SavePaths.GetGlobalChipPath(chipName);
			return NormalizeChipDescription(LoadWithBackup(path, Serializer.DeserializeChipDescription), path);
		}

		public static bool GlobalChipExists(string chipName)
		{
			if (!TryGetPath(() => SavePaths.GetGlobalChipPath(chipName), out string path)) return false;
			return File.Exists(path) || File.Exists(path + ".bak");
		}

		// Existence checks also run on names the user is still typing; a name that
		// cannot form a valid save path simply does not exist yet.
		static bool TryGetPath(Func<string> getPath, out string path)
		{
			try
			{
				path = getPath();
				return true;
			}
			catch (InvalidDataException)
			{
				path = null;
				return false;
			}
		}

		public static ChipDescription[] LoadAllGlobalChipDescriptions()
		{
			if (!Directory.Exists(SavePaths.GlobalChipsPath)) return Array.Empty<ChipDescription>();

			List<ChipDescription> chips = new();
			HashSet<string> names = new(ChipDescription.NameComparer);
			IEnumerable<string> primaryPaths = Directory.EnumerateFiles(SavePaths.GlobalChipsPath, "*.json");
			IEnumerable<string> backupPrimaryPaths = Directory.EnumerateFiles(SavePaths.GlobalChipsPath, "*.json.bak")
				.Select(path => path[..^4]);
			foreach (string path in primaryPaths.Concat(backupPrimaryPaths).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
			{
				try
				{
					ChipDescription chip = NormalizeChipDescription(
						LoadWithBackup(path, Serializer.DeserializeChipDescription),
						path);
					if (!names.Add(chip.Name))
					{
						Debug.LogWarning("Ignoring duplicate global chip name: " + chip.Name);
						continue;
					}

					chips.Add(chip);
				}
				catch (Exception e)
				{
					// One damaged shared chip must not make every project impossible to open.
					Debug.LogWarning("Could not load global chip '" + path + "': " + e.Message);
				}
			}

			return chips.ToArray();
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
				string chipPath = SavePaths.GetProjectChipPath(projectDescription.ProjectName, projectDescription.AllCustomChipNames[i]);
				ChipDescription chipDesc = NormalizeChipDescription(
					LoadWithBackup(chipPath, Serializer.DeserializeChipDescription),
					chipPath);
				loadedChips[i] = chipDesc;
				customChipNameHashset.Add(chipDesc.Name);
			}

			ChipDescription[] globalChips = LoadAllGlobalChipDescriptions()
				.Where(chip => !customChipNameHashset.Contains(chip.Name))
				.ToArray();
			foreach (ChipDescription chip in globalChips) customChipNameHashset.Add(chip.Name);

			// If built-in chip name conflicts with a custom chip, the built-in chip must have been added in a newer version.
			// In that case, simply exclude the built-in chip. The project menus show the same conflicts (see FindBuiltinNameConflicts).
			string[] shadowedBuiltins = builtinChips.Where(b => customChipNameHashset.Contains(b.Name)).Select(b => b.Name).ToArray();
			if (shadowedBuiltins.Length > 0)
			{
				Debug.LogWarning($"Project '{projectDescription.ProjectName}': custom chip(s) {string.Join(", ", shadowedBuiltins)} hide the built-in chip(s) of the same name. Rename them to use the built-in version.");
			}
			builtinChips = builtinChips.Where(b => !customChipNameHashset.Contains(b.Name)).ToArray();

			UpgradeHelper.ApplyVersionChanges(loadedChips.Concat(globalChips).ToArray(), builtinChips);
			return new ChipLibrary(loadedChips, globalChips, builtinChips);
		}

		static ChipDescription NormalizeChipDescription(ChipDescription chipDesc, string path)
		{
			if (chipDesc == null || string.IsNullOrWhiteSpace(chipDesc.Name))
			{
				throw new InvalidDataException("Invalid chip description at " + path);
			}

			chipDesc.InputPins ??= Array.Empty<PinDescription>();
			chipDesc.OutputPins ??= Array.Empty<PinDescription>();
			chipDesc.SubChips ??= Array.Empty<SubChipDescription>();
			chipDesc.Wires ??= Array.Empty<WireDescription>();
			chipDesc.Displays ??= Array.Empty<DisplayDescription>();
			return chipDesc;
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

			// Saver keeps the previous primary here while it swaps in a new file. If that
			// swap was interrupted, this copy is newer than the regular backup.
			string stagedBackupPath = path + ".bak.next";
			if (!File.Exists(path) && File.Exists(stagedBackupPath))
			{
				try
				{
					T recovered = deserialize(File.ReadAllText(stagedBackupPath));
					Debug.LogWarning("Recovered save data from interrupted save: " + stagedBackupPath);
					return recovered;
				}
				catch (Exception)
				{
					// Fall back to the regular backup below.
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
