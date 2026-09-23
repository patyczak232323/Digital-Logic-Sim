using System;
using System.Collections.Generic;
using System.Linq;
using DLS.Description;
using DLS.Game;
using UnityEngine;

namespace DLS.SaveSystem
{
	/// <summary>
	/// Validates operations that cross the project/global library boundary and
	/// updates closed projects when a shared chip is renamed or removed.
	/// </summary>
	public static class GlobalChipManager
	{
		public static bool ValidateSave(
			ChipDescription description,
			Project project,
			Project.SaveMode mode,
			bool makeGlobal,
			out string error)
		{
			error = string.Empty;
			ChipDescription previous = project.ViewedChip.LastSavedDescription;
			bool updatingExisting = previous != null && mode != Project.SaveMode.SaveAs;
			bool wasGlobal = updatingExisting && project.chipLibrary.IsGlobalChip(previous.Name);

			if (makeGlobal)
			{
				if (!ValidateGlobalName(description.Name, project, previous, updatingExisting, wasGlobal, out error)) return false;
				return ValidateGlobalDependencies(description, project.chipLibrary, out error);
			}

			if (wasGlobal && !CanDemote(previous.Name, project.description.ProjectName, out error)) return false;
			return true;
		}

		static bool ValidateGlobalName(
			string name,
			Project project,
			ChipDescription previous,
			bool updatingExisting,
			bool wasGlobal,
			out string error)
		{
			bool keepsExistingGlobalName = updatingExisting && wasGlobal && previous.NameMatch(name);
			bool globalNameExists = Loader.GlobalChipExists(name) ||
			                        Loader.LoadAllGlobalChipDescriptions().Any(chip => chip.NameMatch(name));
			if (globalNameExists && !keepsExistingGlobalName)
			{
				error = $"A different global chip named '{name}' already exists.";
				return false;
			}

			foreach (ProjectDescription otherProject in Loader.LoadAllProjectDescriptions())
			{
				bool containsName = (otherProject.AllCustomChipNames ?? Array.Empty<string>())
					.Any(localName => ChipDescription.NameMatch(localName, name));
				if (!containsName) continue;

				bool isCurrentLocalBeingPromoted =
					ChipDescription.NameMatch(otherProject.ProjectName, project.description.ProjectName) &&
					updatingExisting && !wasGlobal && previous.NameMatch(name);
				if (isCurrentLocalBeingPromoted) continue;

				error = $"Project '{otherProject.ProjectName}' already has a local chip named '{name}'. Rename it first.";
				return false;
			}

			error = string.Empty;
			return true;
		}

		public static bool ValidateGlobalDependencies(ChipDescription root, ChipLibrary library, out string error)
		{
			HashSet<string> visited = new(ChipDescription.NameComparer);
			return Visit(root, out error);

			bool Visit(ChipDescription current, out string visitError)
			{
				if (!visited.Add(current.Name))
				{
					visitError = string.Empty;
					return true;
				}

				foreach (SubChipDescription subChip in current.SubChips ?? Array.Empty<SubChipDescription>())
				{
					if (!library.TryGetChipDescription(subChip.Name, out ChipDescription dependency))
					{
						visitError = $"Dependency '{subChip.Name}' is missing.";
						return false;
					}

					if (library.IsBuiltinChip(dependency.Name)) continue;
					if (!library.IsGlobalChip(dependency.Name))
					{
						visitError = $"Make dependency '{dependency.Name}' global first.";
						return false;
					}

					if (!Visit(dependency, out visitError)) return false;
				}

				visitError = string.Empty;
				return true;
			}
		}

		static bool CanDemote(string chipName, string currentProjectName, out string error)
		{
			foreach (ChipDescription globalChip in Loader.LoadAllGlobalChipDescriptions())
			{
				if (globalChip.NameMatch(chipName)) continue;
				if (UsesChip(globalChip, chipName))
				{
					error = $"Global chip '{globalChip.Name}' depends on this chip.";
					return false;
				}
			}

			foreach (ProjectDescription project in Loader.LoadAllProjectDescriptions())
			{
				if (ChipDescription.NameMatch(project.ProjectName, currentProjectName)) continue;
				foreach (string localName in project.AllCustomChipNames ?? Array.Empty<string>())
				{
					try
					{
						ChipDescription localChip = Loader.LoadProjectChipDescription(project.ProjectName, localName);
						if (!UsesChip(localChip, chipName)) continue;
						error = $"Project '{project.ProjectName}' uses this global chip in '{localChip.Name}'.";
						return false;
					}
					catch (Exception e)
					{
						error = $"Could not verify project '{project.ProjectName}': {e.Message}";
						return false;
					}
				}
			}

			error = string.Empty;
			return true;
		}

		public static void PropagateGlobalRename(string oldName, string newName, string activeProjectName)
		{
			foreach (ChipDescription globalChip in Loader.LoadAllGlobalChipDescriptions())
			{
				if (globalChip.NameMatch(newName)) continue;
				if (ReplaceSubChipName(globalChip, oldName, newName)) Saver.SaveGlobalChip(globalChip);
			}

			foreach (ProjectDescription project in Loader.LoadAllProjectDescriptions())
			{
				if (ChipDescription.NameMatch(project.ProjectName, activeProjectName)) continue;

				bool projectChanged = ReplaceMenuReferences(project, oldName, newName);
				foreach (string localName in project.AllCustomChipNames ?? Array.Empty<string>())
				{
					try
					{
						ChipDescription localChip = Loader.LoadProjectChipDescription(project.ProjectName, localName);
						if (ReplaceSubChipName(localChip, oldName, newName)) Saver.SaveChip(localChip, project.ProjectName);
					}
					catch (Exception e)
					{
						Debug.LogWarning($"Could not update global chip reference in project '{project.ProjectName}': {e.Message}");
					}
				}

				if (projectChanged) Saver.SaveProjectDescription(project);
			}
		}

		public static void RemoveMenuReferencesAfterDemotion(string chipName, string activeProjectName)
		{
			foreach (ProjectDescription project in Loader.LoadAllProjectDescriptions())
			{
				if (ChipDescription.NameMatch(project.ProjectName, activeProjectName)) continue;
				if (RemoveMenuReferences(project, chipName)) Saver.SaveProjectDescription(project);
			}
		}

		static bool UsesChip(ChipDescription parent, string childName) =>
			(parent.SubChips ?? Array.Empty<SubChipDescription>())
			.Any(subChip => ChipDescription.NameMatch(subChip.Name, childName));

		static bool ReplaceSubChipName(ChipDescription parent, string oldName, string newName)
		{
			bool changed = false;
			SubChipDescription[] subChips = parent.SubChips ?? Array.Empty<SubChipDescription>();
			for (int i = 0; i < subChips.Length; i++)
			{
				if (!ChipDescription.NameMatch(subChips[i].Name, oldName)) continue;
				SubChipDescription replacement = subChips[i];
				replacement.Name = newName;
				subChips[i] = replacement;
				changed = true;
			}

			return changed;
		}

		static bool ReplaceMenuReferences(ProjectDescription project, string oldName, string newName)
		{
			bool changed = false;
			for (int i = 0; i < project.StarredList.Count; i++)
			{
				StarredItem item = project.StarredList[i];
				if (item.IsCollection || !ChipDescription.NameMatch(item.Name, oldName)) continue;
				project.StarredList[i] = new StarredItem(newName, false);
				changed = true;
			}

			foreach (ChipCollection collection in project.ChipCollections)
			{
				for (int i = 0; i < collection.Chips.Count; i++)
				{
					if (!ChipDescription.NameMatch(collection.Chips[i], oldName)) continue;
					collection.Chips[i] = newName;
					changed = true;
				}
			}

			return changed;
		}

		static bool RemoveMenuReferences(ProjectDescription project, string chipName)
		{
			int oldStarCount = project.StarredList.Count;
			project.StarredList.RemoveAll(item => !item.IsCollection && ChipDescription.NameMatch(item.Name, chipName));
			bool changed = project.StarredList.Count != oldStarCount;

			foreach (ChipCollection collection in project.ChipCollections)
			{
				int oldCount = collection.Chips.Count;
				collection.Chips.RemoveAll(name => ChipDescription.NameMatch(name, chipName));
				changed |= collection.Chips.Count != oldCount;
			}

			return changed;
		}
	}
}
