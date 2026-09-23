using System.Collections.Generic;
using System.Linq;
using DLS.Description;

namespace DLS.Game
{
	public class ChipLibrary
	{
		public readonly List<ChipDescription> allChips = new();

		readonly HashSet<string> builtinChipNames = new(ChipDescription.NameComparer);
		readonly HashSet<string> globalChipNames = new(ChipDescription.NameComparer);
		readonly HashSet<string> localChipNames = new(ChipDescription.NameComparer);
		readonly Dictionary<string, ChipDescription> descriptionFromNameLookup = new(ChipDescription.NameComparer);

		readonly List<ChipDescription> hiddenChips = new();

		public ChipLibrary(ChipDescription[] localChips, ChipDescription[] globalChips, ChipDescription[] builtinChips)
		{
			// Add built-in chips to list of all chips
			foreach (ChipDescription chip in builtinChips)
			{
				// Bus terminus chip should not be shown to the user (it is created automatically upon placement of a bus start point)
				bool hidden = ChipTypeHelper.IsBusTerminusType(chip.ChipType);

				AddChipToLibrary(chip, hidden);
				builtinChipNames.Add(chip.Name);
			}

			// Global chips are shared by every project. Local names take precedence when
			// opening old data that predates the global-name collision checks.
			foreach (ChipDescription chip in globalChips)
			{
				AddChipToLibrary(chip);
				globalChipNames.Add(chip.Name);
			}

			foreach (ChipDescription chip in localChips)
			{
				if (globalChipNames.Remove(chip.Name)) allChips.RemoveAll(c => c.NameMatch(chip.Name));
				AddChipToLibrary(chip);
				localChipNames.Add(chip.Name);
			}

			RebuildChipDescriptionLookup();
		}

		void RebuildChipDescriptionLookup()
		{
			descriptionFromNameLookup.Clear();
			foreach (ChipDescription desc in allChips)
			{
				descriptionFromNameLookup.Add(desc.Name, desc);
			}

			foreach (ChipDescription desc in hiddenChips)
			{
				descriptionFromNameLookup.Add(desc.Name, desc);
			}
		}


		public bool IsBuiltinChip(string name) => builtinChipNames.Contains(name);
		public bool IsGlobalChip(string name) => globalChipNames.Contains(name);
		public bool IsLocalChip(string name) => localChipNames.Contains(name);

		public bool HasChip(string name) => TryGetChipDescription(name, out _);

		public ChipDescription GetChipDescription(string name) => descriptionFromNameLookup[name];

		public bool TryGetChipDescription(string name, out ChipDescription description) => descriptionFromNameLookup.TryGetValue(name, out description);

		public void RemoveChip(string chipName)
		{
			allChips.RemoveAll(c => c.NameMatch(chipName));
			globalChipNames.Remove(chipName);
			localChipNames.Remove(chipName);
			RebuildChipDescriptionLookup();
		}

		public void NotifyChipSaved(ChipDescription description)
		{
			NotifyChipSaved(description, IsGlobalChip(description.Name));
		}

		public void NotifyChipSaved(ChipDescription description, bool isGlobal)
		{
			// Replace chip description if already exists
			bool foundChip = false;

			for (int i = 0; i < allChips.Count; i++)
			{
				if (allChips[i].NameMatch(description.Name))
				{
					allChips[i] = description;
					foundChip = true;
					break;
				}
			}

			// Otherwise add as new description
			if (!foundChip) AddChipToLibrary(description);

			if (isGlobal)
			{
				localChipNames.Remove(description.Name);
				globalChipNames.Add(description.Name);
			}
			else
			{
				globalChipNames.Remove(description.Name);
				localChipNames.Add(description.Name);
			}

			RebuildChipDescriptionLookup();
		}

		public void NotifyChipRenamed(ChipDescription description, string nameOld)
		{
			NotifyChipRenamed(description, nameOld, IsGlobalChip(nameOld));
		}

		public void NotifyChipRenamed(ChipDescription description, string nameOld, bool isGlobal)
		{
			// Replace chip description
			for (int i = 0; i < allChips.Count; i++)
			{
				if (allChips[i].NameMatch(nameOld))
				{
					allChips[i] = description;
					break;
				}
			}

			globalChipNames.Remove(nameOld);
			localChipNames.Remove(nameOld);
			if (isGlobal) globalChipNames.Add(description.Name);
			else localChipNames.Add(description.Name);

			RebuildChipDescriptionLookup();
		}

		public string[] GetAllCustomChipNames()
		{
			List<string> customChipNames = new();

			foreach (ChipDescription chip in allChips)
			{
				if (IsLocalChip(chip.Name))
				{
					customChipNames.Add(chip.Name);
				}
			}

			return customChipNames.ToArray();
		}

		// Returns the descriptions of all chips that use the given chip as a direct subchip
		public ChipDescription[] GetDirectParentChips(string chipName)
		{
			List<ChipDescription> parents = new();

			foreach (ChipDescription other in allChips)
			{
				if (other.SubChips == null) continue;
				if (other.SubChips.Any(subchip => ChipDescription.NameMatch(subchip.Name, chipName)))
				{
					parents.Add(other);
				}
			}

			return parents.ToArray();
		}

		void AddChipToLibrary(ChipDescription description, bool hidden = false)
		{
			if (hidden) hiddenChips.Add(description);
			else allChips.Add(description);
		}
	}
}
