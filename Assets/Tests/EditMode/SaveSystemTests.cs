using System;
using System.IO;
using System.Linq;
using DLS.Description;
using DLS.SaveSystem;
using NUnit.Framework;
using static DLS.Tests.ChipBuilder;

namespace DLS.Tests
{
	// Save data safety: atomic saves with a backup, recovery after an interrupted save,
	// names that cannot escape their folder, renames, deletes and global chips.
	public class SaveSystemTests
	{
		string projectName;
		string globalChipName;

		[SetUp]
		public void SetUp()
		{
			string id = Guid.NewGuid().ToString("N").Substring(0, 12);
			projectName = "RewiredSaveTest_" + id;
			globalChipName = "RewiredGlobalTest_" + id;
		}

		[TearDown]
		public void TearDown()
		{
			string projectPath = SavePaths.GetProjectPath(projectName);
			if (Directory.Exists(projectPath)) Directory.Delete(projectPath, true);

			foreach (string directory in new[] { SavePaths.GlobalChipsPath, SavePaths.DeletedGlobalChipsPath })
			{
				if (!Directory.Exists(directory)) continue;
				foreach (string file in Directory.GetFiles(directory, globalChipName + "*")) File.Delete(file);
			}
		}

		static ChipDescription Chip(string name, int extraInputs = 0)
		{
			ChipBuilder b = new(name);
			PinAddress a = b.Input("A");
			for (int i = 0; i < extraInputs; i++) b.Input("X" + i);
			b.Wire(NandOutput(b.Nand(a, a)), b.Output("Y"));
			ChipDescription d = b.Build();
			d.DLSVersion = "2.1.6";
			return d;
		}

		string ChipPath(string name) => SavePaths.GetProjectChipPath(projectName, name);

		[Test]
		public void SavingAgainKeepsThePreviousVersionAsBackup()
		{
			Saver.SaveChip(Chip("Inv"), projectName);
			Assert.IsFalse(File.Exists(ChipPath("Inv") + ".bak"), "first save has nothing to back up");

			Saver.SaveChip(Chip("Inv", extraInputs: 2), projectName);

			Assert.AreEqual(3, Loader.LoadProjectChipDescription(projectName, "Inv").InputPins.Length, "primary holds the new version");
			ChipDescription backup = Serializer.DeserializeChipDescription(File.ReadAllText(ChipPath("Inv") + ".bak"));
			Assert.AreEqual(1, backup.InputPins.Length, "backup holds the previous version");
			Assert.IsFalse(File.Exists(ChipPath("Inv") + ".tmp"), "temporary file left behind");
		}

		[Test]
		public void CorruptPrimaryIsRecoveredFromBackup()
		{
			Saver.SaveChip(Chip("Inv"), projectName);
			Saver.SaveChip(Chip("Inv", extraInputs: 1), projectName);
			File.WriteAllText(ChipPath("Inv"), "{ not json");

			Assert.AreEqual(1, Loader.LoadProjectChipDescription(projectName, "Inv").InputPins.Length, "should load the backup");
		}

		[Test]
		public void InterruptedSaveIsRecoveredFromStagedBackup()
		{
			Saver.SaveChip(Chip("Inv", extraInputs: 1), projectName);

			// Saver copies the old primary to .bak.next and deletes the primary just before
			// moving the new file in. Simulate a crash at exactly that point.
			File.Copy(ChipPath("Inv"), ChipPath("Inv") + ".bak.next");
			File.Delete(ChipPath("Inv"));

			Assert.AreEqual(2, Loader.LoadProjectChipDescription(projectName, "Inv").InputPins.Length);
		}

		[Test]
		public void MissingSaveWithoutBackupIsReported()
		{
			Assert.Throws<FileNotFoundException>(() => Loader.LoadProjectChipDescription(projectName, "Nope"));
		}

		[TestCase("..")]
		[TestCase("../Escape")]
		[TestCase("..\\Escape")]
		[TestCase("a/b")]
		[TestCase("")]
		[TestCase("   ")]
		public void NamesThatLeaveTheirFolderAreRejected(string name)
		{
			Assert.Throws<InvalidDataException>(() => SavePaths.GetProjectChipPath(projectName, name + "x/.."));
			Assert.Throws<InvalidDataException>(() => SavePaths.CombineContained(SavePaths.ProjectsPath, name));
		}

		[Test]
		public void OrdinaryNamesStayInsideTheirFolder()
		{
			string path = Path.GetFullPath(SavePaths.GetProjectChipPath(projectName, "My Chip.v2"));
			StringAssert.StartsWith(Path.GetFullPath(SavePaths.GetChipsPath(projectName)), path);
		}

		[Test]
		public void RenameMovesTheFileAndRemovesTheOldOne()
		{
			Saver.SaveChip(Chip("Old"), projectName);
			ChipDescription renamed = Chip("New");
			Saver.RenameChip("Old", renamed, projectName);

			Assert.IsFalse(File.Exists(ChipPath("Old")));
			Assert.AreEqual("New", Loader.LoadProjectChipDescription(projectName, "New").Name);
		}

		[Test]
		public void CaseOnlyRenameKeepsTheChip()
		{
			Saver.SaveChip(Chip("adder"), projectName);
			Saver.RenameChip("adder", Chip("Adder"), projectName);

			string[] files = Directory.GetFiles(SavePaths.GetChipsPath(projectName), "*.json").Select(Path.GetFileName).ToArray();
			CollectionAssert.AreEqual(new[] { "Adder.json" }, files);
			Assert.AreEqual("Adder", Loader.LoadProjectChipDescription(projectName, "Adder").Name);
		}

		[Test]
		public void DeleteMovesTheChipToDeletedChipsAndDropsItsBackup()
		{
			Saver.SaveChip(Chip("Gone"), projectName);
			Saver.SaveChip(Chip("Gone"), projectName);
			Assert.IsTrue(File.Exists(ChipPath("Gone") + ".bak"));

			Saver.DeleteChip("Gone", projectName);

			Assert.IsFalse(File.Exists(ChipPath("Gone")));
			Assert.IsFalse(File.Exists(ChipPath("Gone") + ".bak"), "a stale backup would resurrect the chip");
			Assert.IsTrue(File.Exists(Path.Combine(SavePaths.GetDeletedChipsPath(projectName), "Gone.json")));
		}

		[Test]
		public void GlobalChipRoundTripAndDelete()
		{
			Assert.IsFalse(Loader.GlobalChipExists(globalChipName));
			Saver.SaveGlobalChip(Chip(globalChipName));

			Assert.IsTrue(Loader.GlobalChipExists(globalChipName));
			Assert.IsTrue(Loader.LoadAllGlobalChipDescriptions().Any(c => c.Name == globalChipName));

			Saver.SaveGlobalChip(Chip(globalChipName, extraInputs: 1));
			Saver.DeleteGlobalChip(globalChipName);

			Assert.IsFalse(Loader.GlobalChipExists(globalChipName), "neither primary nor backup may remain");
			Assert.IsFalse(Loader.LoadAllGlobalChipDescriptions().Any(c => c.Name == globalChipName));
		}

		[Test]
		public void GlobalChipNameThatIsNotAPathDoesNotExist()
		{
			Assert.IsFalse(Loader.GlobalChipExists("../" + globalChipName));
		}
	}
}
