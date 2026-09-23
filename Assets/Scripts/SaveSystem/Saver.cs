using System;
using System.IO;
using DLS.Description;
using DLS.Game;

namespace DLS.SaveSystem
{
	public static class Saver
	{
		public static void SaveAppSettings(AppSettings settings)
		{
			string data = Serializer.SerializeAppSettings(settings);
			WriteToFile(data, SavePaths.AppSettingsPath);
		}

		public static void SaveProjectDescription(ProjectDescription projectDescription)
		{
			projectDescription.LastSaveTime = DateTime.Now;
			projectDescription.DLSVersion_LastSaved = Main.DLSVersion.ToString();
			projectDescription.DLSVersion_EarliestCompatible = Main.DLSVersion_EarliestCompatible.ToString();

			string data = Serializer.SerializeProjectDescription(projectDescription);
			WriteToFile(data, SavePaths.GetProjectDescriptionPath(projectDescription.ProjectName));
		}

		public static void RenameProject(string nameOld, string nameNew)
		{
			ProjectDescription desc = Loader.LoadProjectDescription(nameOld);
			desc.ProjectName = nameNew;

			string sourcePath = SavePaths.GetProjectPath(nameOld);
			string destinationPath = SavePaths.GetProjectPath(nameNew);
			MoveDirectorySupportingCaseOnlyRename(sourcePath, destinationPath);
			SaveProjectDescription(desc);
		}

		public static void DuplicateProject(string nameOriginal, string nameDuplicate)
		{
			SaveUtils.CopyDirectory(SavePaths.GetProjectPath(nameOriginal), SavePaths.GetProjectPath(nameDuplicate), true);
			ProjectDescription descNew = Loader.LoadProjectDescription(nameDuplicate);
			descNew.ProjectName = nameDuplicate;
			SaveProjectDescription(descNew);
		}

		public static void SaveChip(ChipDescription chipDescription, string projectName)
		{
			string serializedDescription = CreateSerializedChipDescription(chipDescription);
			WriteToFile(serializedDescription, GetChipFilePath(chipDescription.Name, projectName));
		}

		public static void SaveGlobalChip(ChipDescription chipDescription)
		{
			string serializedDescription = CreateSerializedChipDescription(chipDescription);
			WriteToFile(serializedDescription, SavePaths.GetGlobalChipPath(chipDescription.Name));
		}

		public static void SaveChip(ChipDescription chipDescription, string projectName, bool global)
		{
			if (global) SaveGlobalChip(chipDescription);
			else SaveChip(chipDescription, projectName);
		}

		public static void RenameChip(string oldName, ChipDescription chipDescription, string projectName)
		{
			RenameChip(oldName, chipDescription, projectName, false, false);
		}

		public static void RenameChip(string oldName, ChipDescription chipDescription, string projectName, bool wasGlobal, bool makeGlobal)
		{
			string oldPath = wasGlobal ? SavePaths.GetGlobalChipPath(oldName) : GetChipFilePath(oldName, projectName);
			string newPath = makeGlobal ? SavePaths.GetGlobalChipPath(chipDescription.Name) : GetChipFilePath(chipDescription.Name, projectName);
			string serializedDescription = CreateSerializedChipDescription(chipDescription);

			// A case-only rename maps to the same path on Windows/macOS. First update
			// the contents safely, then use a temporary name to make the casing stick.
			if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
			{
				WriteToFile(serializedDescription, oldPath);
				if (!string.Equals(oldPath, newPath, StringComparison.Ordinal))
				{
					MoveFileSupportingCaseOnlyRename(oldPath, newPath);
					if (File.Exists(oldPath + ".bak"))
					{
						MoveFileSupportingCaseOnlyRename(oldPath + ".bak", newPath + ".bak");
					}
				}

				return;
			}

			// Write the new file before removing the old one, so a failed save never
			// destroys the last usable copy.
			WriteToFile(serializedDescription, newPath);
			if (File.Exists(oldPath)) File.Delete(oldPath);
			// Global chips are discovered by enumerating both primaries and backups.
			// Leaving the old backup behind would resurrect a renamed/demoted chip.
			if (wasGlobal && File.Exists(oldPath + ".bak")) File.Delete(oldPath + ".bak");
		}


		public static ChipDescription CloneChipDescription(ChipDescription desc)
		{
			if (desc == null) return null;
			return Serializer.DeserializeChipDescription(Serializer.SerializeChipDescription(desc));
		}


		public static string CreateSerializedChipDescription(ChipDescription chipDescription) => Serializer.SerializeChipDescription(chipDescription);


		// Delete chip save file, with option to keep backup in a DeletedChips folder.
		public static void DeleteChip(string chipName, string projectName, bool backupInDeletedFolder = true)
		{
			string filePath = GetChipFilePath(chipName, projectName);
			if (!File.Exists(filePath)) return;
			if (backupInDeletedFolder)
			{
				string deletedChipDirectoryPath = SavePaths.GetDeletedChipsPath(projectName);
				string deletedFilePath = SaveUtils.EnsureUniqueFileName(Path.Combine(deletedChipDirectoryPath, chipName + ".json"));
				SavePaths.EnsureDirectoryExists(Path.GetDirectoryName(deletedFilePath));
				File.Move(filePath, deletedFilePath);
			}
			else
			{
				File.Delete(filePath);
			}
		}

		public static void DeleteGlobalChip(string chipName, bool backupInDeletedFolder = true)
		{
			string filePath = SavePaths.GetGlobalChipPath(chipName);
			string backupPath = filePath + ".bak";
			bool primaryExists = File.Exists(filePath);
			bool backupExists = File.Exists(backupPath);
			if (!primaryExists && !backupExists) return;

			if (backupInDeletedFolder)
			{
				SavePaths.EnsureDirectoryExists(SavePaths.DeletedGlobalChipsPath);
				string deletedPath = SaveUtils.EnsureUniqueFileName(Path.Combine(SavePaths.DeletedGlobalChipsPath, chipName + ".json"));
				File.Move(primaryExists ? filePath : backupPath, deletedPath);
			}
			else if (primaryExists) File.Delete(filePath);

			// Unlike project chips, global chips have no manifest. An orphaned backup
			// would therefore be loaded as if the deleted chip still existed.
			if (File.Exists(backupPath)) File.Delete(backupPath);
		}

		public static void DeleteProject(string projectName, bool backupInDeletedFolder = true)
		{
			string projectPath = SavePaths.GetProjectPath(projectName);

			if (backupInDeletedFolder)
			{
				SavePaths.EnsureDirectoryExists(SavePaths.DeletedProjectsPath);
				string deletedPath = Path.Combine(SavePaths.DeletedProjectsPath, projectName);
				deletedPath = SaveUtils.EnsureUniqueDirectoryName(deletedPath);
				Directory.Move(projectPath, deletedPath);
			}
			else
			{
				Directory.Delete(projectPath, true);
			}
		}

		public static bool HasUnsavedChanges(ChipDescription lastSaved, ChipDescription current)
		{
			string jsonA = CreateSerializedChipDescription(lastSaved);
			string jsonB = CreateSerializedChipDescription(current);
			return !UnsavedChangeDetector.IsEquivalentJson(jsonA, jsonB);
		}

		static void WriteToFile(string data, string path)
		{
			string directory = Path.GetDirectoryName(path);
			Directory.CreateDirectory(directory);

			string temporaryPath = path + ".tmp";
			string backupPath = path + ".bak";
			string stagedBackupPath = backupPath + ".next";

			try
			{
				using (FileStream stream = new(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
				using (StreamWriter writer = new(stream))
				{
					writer.Write(data);
					writer.Flush();
					stream.Flush(true);
				}

				if (File.Exists(path))
				{
					// Do not overwrite the last backup until the new primary is in place.
					// This guarantees that an interruption leaves at least one complete copy.
					File.Copy(path, stagedBackupPath, true);
					if (File.Exists(path)) File.Delete(path);
					File.Move(temporaryPath, path);
					if (File.Exists(backupPath)) File.Delete(backupPath);
					File.Move(stagedBackupPath, backupPath);
				}
				else
				{
					File.Move(temporaryPath, path);
				}
			}
			finally
			{
				if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
				if (File.Exists(stagedBackupPath)) File.Delete(stagedBackupPath);
			}
		}

		static void MoveDirectorySupportingCaseOnlyRename(string sourcePath, string destinationPath)
		{
			if (string.Equals(sourcePath, destinationPath, StringComparison.Ordinal)) return;

			if (!string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase))
			{
				Directory.Move(sourcePath, destinationPath);
				return;
			}

			string temporaryPath = SaveUtils.EnsureUniqueDirectoryName(sourcePath + "_rename");
			Directory.Move(sourcePath, temporaryPath);
			try
			{
				Directory.Move(temporaryPath, destinationPath);
			}
			catch
			{
				if (Directory.Exists(temporaryPath)) Directory.Move(temporaryPath, sourcePath);
				throw;
			}
		}

		static void MoveFileSupportingCaseOnlyRename(string sourcePath, string destinationPath)
		{
			if (string.Equals(sourcePath, destinationPath, StringComparison.Ordinal)) return;

			string temporaryPath = SaveUtils.EnsureUniqueFileName(sourcePath + ".rename");
			File.Move(sourcePath, temporaryPath);
			try
			{
				File.Move(temporaryPath, destinationPath);
			}
			catch
			{
				if (File.Exists(temporaryPath)) File.Move(temporaryPath, sourcePath);
				throw;
			}
		}

		static string GetChipFilePath(string chipName, string projectName)
		{
			string saveDirectoryPath = SavePaths.GetChipsPath(projectName);
			return Path.Combine(saveDirectoryPath, chipName + ".json");
		}
	}
}
