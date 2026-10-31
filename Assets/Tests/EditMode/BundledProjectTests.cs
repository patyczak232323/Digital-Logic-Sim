using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DLS.Description;
using DLS.Game;
using DLS.RHDL;
using DLS.SaveSystem;
using DLS.Simulation;
using NUnit.Framework;
using UnityEngine;

namespace DLS.Tests
{
	// The Snake project that comes with Rewired (Assets/Resources/BundledProjects/Snake) is
	// built from Examples/RHDL/PIX6 and snake.pix6. These tests check that the shipped files
	// match those sources and that the project runs.
	//
	// After changing the sources, rewrite the files with:
	//   REWIRED_WRITE_BUNDLED=1 dotnet test Tools/EngineHarness --filter BundledProjectTests
	public class BundledProjectTests
	{
		const string ProjectName = "Snake";
		static string ExamplesRoot => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Examples", "RHDL"));
		static string BundledFolder => FolderOf(ProjectName);
		internal static string FolderOf(string projectName) => Path.Combine(Application.dataPath, "Resources", BundledProjectInstaller.ResourcesFolder, projectName);

		static readonly string[] Sources =
		{
			"Basics/04_DFF.rhdl",
			"Basics/05_REG4.rhdl",
			"PIX6/00_REG8.rhdl",
			"PIX6/01_PIX6.rhdl",
			"PIX6/03_SNAKE.rhdl"
		};

		// Keys of the Snake chip, by what they are wired to: the bits of the KEYS merge
		// (most significant first) and the processor's RESET.
		static readonly char[] KeyByMergeInput = { 'S', 'A', 'W', 'D' };
		const char ResetKey = 'R';

		[TearDown]
		public void TearDown() => RewiredEngine.Reset();

		// Opening the example must not report unsaved changes: the comparison sees the saved chip
		// as the editor describes it after loading (no colour on the chip's own outputs, the
		// colour of every subchip output, none for buses).
		[Test]
		public void SavedChipsMatchWhatTheEditorDescribes()
		{
			(_, ChipDescription[] chips) = CreateSnakeProject();
			ChipLibrary library = TestLibrary.Create();
			foreach (ChipDescription chip in chips) library.NotifyChipSaved(chip, false);

			ChipDescription snake = chips.Single(c => c.Name == "Snake");
			ChipDescription normalized = Saver.WithEditorPinColours(snake, library);
			Assert.AreNotSame(snake, normalized, "the RHDL chip should need the editor's defaults");
			Assert.IsTrue(normalized.OutputPins.All(p => p.Colour == default));

			foreach (SubChipDescription subChip in normalized.SubChips)
			{
				Assert.IsTrue(library.TryGetChipDescription(subChip.Name, out ChipDescription desc));
				if (ChipTypeHelper.IsBusType(desc.ChipType))
				{
					Assert.IsNull(subChip.OutputPinColourInfo, subChip.Name);
					continue;
				}

				CollectionAssert.AreEqual(desc.OutputPins.Select(p => (p.Colour, p.ID)).ToArray(),
					subChip.OutputPinColourInfo.Select(c => (c.PinColour, c.PinID)).ToArray(), subChip.Name);
			}

			// Already in the editor's form: nothing to change, and no copy is made.
			Assert.AreSame(normalized, Saver.WithEditorPinColours(normalized, library));
			Assert.IsFalse(Saver.HasUnsavedChanges(normalized, Saver.WithEditorPinColours(snake, library)));
		}

		public static (ProjectDescription project, ChipDescription[] chips) CreateSnakeProject()
		{
			ChipLibrary library = TestLibrary.Create();
			List<ChipDescription> chips = new();
			foreach (string source in Sources)
			{
				RhdlCompileResult result = RhdlCompiler.Compile(File.ReadAllText(Path.Combine(ExamplesRoot, source)), library);
				Assert.IsTrue(result.Success, $"{source} failed to compile:\n" + string.Join("\n", result.Diagnostics.Select(d => d.ToString())));
				library.NotifyChipSaved(result.Description, false);
				chips.Add(result.Description);
			}

			ChipDescription pix6 = chips.Single(c => c.Name == "Pix6");
			uint[] rom = new uint[256];
			uint[] program = Pix6Assembler.Assemble(File.ReadAllText(Path.Combine(ExamplesRoot, "PIX6", "snake.pix6")));
			Array.Copy(program, rom, program.Length);
			int romIndex = Array.FindIndex(pix6.SubChips, s => s.Name == ChipTypeHelper.GetName(ChipType.Rom_256x16));
			pix6.SubChips[romIndex].InternalData = rom;

			ChipDescription snake = chips.Single(c => c.Name == "Snake");
			BindKeys(snake);

			ProjectDescription project = new()
			{
				ProjectName = ProjectName,
				DLSVersion_LastSaved = Main.DLSVersion.ToString(),
				DLSVersion_EarliestCompatible = Main.DLSVersion_EarliestCompatible.ToString(),
				CreationTime = new DateTime(2026, 10, 30),
				LastSaveTime = new DateTime(2026, 10, 30),
				Prefs_MainPinNamesDisplayMode = 1,
				Prefs_ChipPinNamesDisplayMode = 1,
				// One instruction takes two simulation steps (clock low, clock high).
				Prefs_SimTargetStepsPerSecond = 8000,
				Prefs_SimStepsPerClockTick = 1,
				AllCustomChipNames = chips.Select(c => c.Name).ToArray(),
				StarredList = BuiltinCollectionCreator.GetDefaultStarredList().ToList(),
				ChipCollections = BuiltinCollectionCreator.CreateDefaultChipCollections()
					.Append(new ChipCollection("PIX6", chips.Select(c => c.Name).ToArray()))
					.ToList(),
				StartupChipName = snake.Name
			};

			return (project, chips.ToArray());
		}

		// The keys wired to the KEYS merge steer; the one left over resets. The power-on pulse
		// is made long enough to cover a rising clock edge.
		static void BindKeys(ChipDescription snake)
		{
			string keyName = ChipTypeHelper.GetName(ChipType.Key);
			int mergeId = snake.SubChips.Single(s => s.Name == ChipTypeHelper.GetName(ChipType.Merge_1To4Bit)).ID;

			for (int i = 0; i < snake.SubChips.Length; i++)
			{
				string name = snake.SubChips[i].Name;
				if (name == ChipTypeHelper.GetName(ChipType.Pulse)) snake.SubChips[i].InternalData = new uint[] { PowerOnPulseSteps, 0, 0 };
				if (name != keyName) continue;

				int id = snake.SubChips[i].ID;
				WireDescription[] wires = snake.Wires.Where(w => w.SourcePinAddress.PinOwnerID == id).ToArray();
				WireDescription toMerge = wires.FirstOrDefault(w => w.TargetPinAddress.PinOwnerID == mergeId);
				char key = wires.Length == 1 && toMerge.TargetPinAddress.PinOwnerID == mergeId ? KeyByMergeInput[toMerge.TargetPinAddress.PinID] : ResetKey;
				snake.SubChips[i].InternalData = new uint[] { key };
			}

			Assert.AreEqual(5, snake.SubChips.Count(s => s.Name == keyName));
			CollectionAssert.AreEquivalent("WASDR", snake.SubChips.Where(s => s.Name == keyName).Select(s => (char)s.InternalData[0]));
		}

		const uint PowerOnPulseSteps = 4;

		[Test]
		public void BundledFilesMatchTheSources()
		{
			(ProjectDescription project, ChipDescription[] chips) = CreateSnakeProject();
			CheckBundledFiles(BundledFolder, project, chips);
		}

		// Compares the shipped files with the project built from the sources, or rewrites them
		// when REWIRED_WRITE_BUNDLED=1.
		internal static void CheckBundledFiles(string bundledFolder, ProjectDescription project, ChipDescription[] chips)
		{
			if (Environment.GetEnvironmentVariable("REWIRED_WRITE_BUNDLED") == "1")
			{
				Write(bundledFolder, project, chips);
				Assert.Pass("bundled project written");
			}

			string projectPath = Path.Combine(bundledFolder, "ProjectDescription.json");
			Assert.IsTrue(File.Exists(projectPath), "missing " + projectPath);
			ProjectDescription shipped = Serializer.DeserializeProjectDescription(File.ReadAllText(projectPath));
			Assert.AreEqual(project.StartupChipName, shipped.StartupChipName);
			CollectionAssert.AreEqual(project.AllCustomChipNames, shipped.AllCustomChipNames);
			Assert.AreEqual(project.Prefs_SimTargetStepsPerSecond, shipped.Prefs_SimTargetStepsPerSecond);
			Assert.AreEqual(project.Prefs_SimStepsPerClockTick, shipped.Prefs_SimStepsPerClockTick);

			string[] chipFiles = Directory.GetFiles(Path.Combine(bundledFolder, "Chips"), "*.json");
			CollectionAssert.AreEquivalent(chips.Select(c => c.Name + ".json"), chipFiles.Select(Path.GetFileName));

			// Compare what the chips do, not where their parts are drawn: chip sizes (and with
			// them the layout) depend on whether the editor's fonts are loaded.
			foreach (ChipDescription chip in chips)
			{
				ChipDescription shippedChip = Serializer.DeserializeChipDescription(File.ReadAllText(Path.Combine(bundledFolder, "Chips", chip.Name + ".json")));
				Assert.AreEqual(Logic(chip), Logic(shippedChip), $"{chip.Name} differs from its source; see the comment at the top of BundledProjectTests");
			}
		}

		static string Logic(ChipDescription chip)
		{
			IEnumerable<string> lines = new[] { chip.Name }
				.Concat(chip.InputPins.Select(p => $"in {p.ID} {p.Name} {p.BitCount}"))
				.Concat(chip.OutputPins.Select(p => $"out {p.ID} {p.Name} {p.BitCount}"))
				.Concat(chip.SubChips.Select(s => $"sub {s.ID} {s.Name} {string.Join(",", s.InternalData ?? Array.Empty<uint>())}"))
				.Concat(chip.Wires.Select(w => $"wire {w.SourcePinAddress.PinOwnerID}.{w.SourcePinAddress.PinID} {w.TargetPinAddress.PinOwnerID}.{w.TargetPinAddress.PinID}"));
			return string.Join("\n", lines.OrderBy(l => l, StringComparer.Ordinal));
		}

		static void Write(string bundledFolder, ProjectDescription project, ChipDescription[] chips)
		{
			string chipsFolder = Path.Combine(bundledFolder, "Chips");
			Directory.CreateDirectory(chipsFolder);
			foreach (string old in Directory.GetFiles(chipsFolder, "*.json")) File.Delete(old);

			File.WriteAllText(Path.Combine(bundledFolder, "ProjectDescription.json"), Serializer.SerializeProjectDescription(project));
			foreach (ChipDescription chip in chips)
			{
				File.WriteAllText(Path.Combine(chipsFolder, chip.Name + ".json"), Serializer.SerializeChipDescription(chip));
			}

			// Unity needs a .meta file next to every asset and folder.
			string resources = Path.Combine(Application.dataPath, "Resources");
			foreach (string folder in new[] { resources, Path.GetDirectoryName(bundledFolder), bundledFolder, chipsFolder }) WriteMeta(folder, true);
			foreach (string file in Directory.GetFiles(bundledFolder, "*.json", SearchOption.AllDirectories)) WriteMeta(file, false);
		}

		static void WriteMeta(string path, bool folder)
		{
			string meta = path + ".meta";
			if (File.Exists(meta)) return;
			string guid = Guid.NewGuid().ToString("N");
			File.WriteAllText(meta, folder
				? $"fileFormatVersion: 2\nguid: {guid}\nfolderAsset: yes\nDefaultImporter:\n  externalObjects: {{}}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n"
				: $"fileFormatVersion: 2\nguid: {guid}\nTextScriptImporter:\n  externalObjects: {{}}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n");
		}

		[Test]
		public void SnakeBootsWithoutPressingReset()
		{
			(ProjectDescription _, ChipDescription[] chips) = CreateSnakeProject();
			ChipLibrary library = TestLibrary.Create(chips);
			ChipDescription snake = chips.Single(c => c.Name == "Snake");

			RewiredEngine.Reset();
			RewiredEngine.StepsPerClockTransition = 1;
			SimChip root = RewiredEngine.BuildChip(snake, library);
			RewiredEngine.EnsureInitialized(root, Array.Empty<DevPinInstance>(), null);

			SimChip screen = Find(root, ChipType.DisplayRGB64);
			uint wall = DisplayRgb64.PackColour(4, 4, 4);
			for (int i = 0; i < 4000 && screen.InternalState[DisplayRgb64.FrontOffset] != wall; i++)
			{
				RewiredEngine.RunStep(root, Array.Empty<DevPinInstance>(), null);
			}

			Assert.AreEqual(wall, screen.InternalState[DisplayRgb64.FrontOffset], "the walls should be shown");
			Assert.AreEqual(wall, screen.InternalState[DisplayRgb64.FrontOffset + 63 * 64 + 63]);
		}

		[Test]
		public void InstallerWritesTheProjectOnce()
		{
			(ProjectDescription project, ChipDescription[] chips) = CreateSnakeProject();
			string name = "Snake install test " + Guid.NewGuid().ToString("N");
			try
			{
				string projectJson = Serializer.SerializeProjectDescription(project);
				string[] chipJsons = chips.Select(Serializer.SerializeChipDescription).ToArray();

				Assert.IsTrue(BundledProjectInstaller.Install(name, projectJson, chipJsons));
				Assert.IsFalse(BundledProjectInstaller.Install(name, projectJson, chipJsons), "an existing project is left alone");

				ProjectDescription loaded = Loader.LoadProjectDescription(name);
				Assert.AreEqual(name, loaded.ProjectName);
				Assert.AreEqual("Snake", loaded.StartupChipName);
				foreach (ChipDescription chip in chips) Assert.IsTrue(File.Exists(SavePaths.GetProjectChipPath(name, chip.Name)), chip.Name);
			}
			finally
			{
				string path = SavePaths.GetProjectPath(name);
				if (Directory.Exists(path)) Directory.Delete(path, true);
			}
		}

		internal static SimChip Find(SimChip chip, ChipType type)
		{
			if (chip.ChipType == type) return chip;
			foreach (SimChip sub in chip.SubChips)
			{
				SimChip found = sub == null ? null : Find(sub, type);
				if (found != null) return found;
			}
			return null;
		}
	}
}
