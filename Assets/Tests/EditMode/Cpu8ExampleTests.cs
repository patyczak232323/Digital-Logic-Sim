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
	// CPU8 from Examples/RHDL/CPU8: an 8-bit single-cycle processor built chip by chip, the way it
	// would be built by hand (gates, adders, multiplexers, decoders, registers, RAM, ALU, control
	// unit). The programs below are assembled with Cpu8Asm and run on the real engine.
	//
	// The CPU8 project that comes with Rewired (Assets/Resources/BundledProjects/CPU8) is built from
	// these sources with SheetProgram in the ROM. After changing them, rewrite its files with:
	//   REWIRED_WRITE_BUNDLED=1 dotnet test Tools/EngineHarness --filter Cpu8ExampleTests
	public class Cpu8ExampleTests
	{
		static string ExamplesRoot => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Examples", "RHDL"));

		public static readonly string[] Sources =
		{
			"Basics/04_DFF.rhdl",
			"CPU8/00_NOT.rhdl",
			"CPU8/01_AND.rhdl",
			"CPU8/02_OR.rhdl",
			"CPU8/03_XOR.rhdl",
			"CPU8/04_OR4.rhdl",
			"CPU8/05_MUX.rhdl",
			"CPU8/06_FULL_ADDER.rhdl",
			"CPU8/07_ADD8.rhdl",
			"CPU8/08_MUX2X8.rhdl",
			"CPU8/09_MUX4X8.rhdl",
			"CPU8/10_MUX8X8.rhdl",
			"CPU8/11_MUX16X8.rhdl",
			"CPU8/12_DEC2.rhdl",
			"CPU8/13_DEC3.rhdl",
			"CPU8/14_DEC4.rhdl",
			"CPU8/15_DEC5.rhdl",
			"CPU8/16_IS_ZERO8.rhdl",
			"CPU8/17_REGISTER8.rhdl",
			"CPU8/18_RAM16X8.rhdl",
			"CPU8/19_RAM256X8.rhdl",
			"CPU8/20_REGISTER_FILE.rhdl",
			"CPU8/21_ALU.rhdl",
			"CPU8/22_FLAGS.rhdl",
			"CPU8/23_PROGRAM_COUNTER.rhdl",
			"CPU8/24_PROGRAM_ROM.rhdl",
			"CPU8/25_CONTROL_UNIT.rhdl",
			"CPU8/26_CPU8.rhdl",
			"CPU8/27_CPU8_COMPUTER.rhdl"
		};

		[TearDown]
		public void TearDown() => RewiredEngine.Reset();

		public static (ChipLibrary library, List<ChipDescription> chips) BuildAll()
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

			return (library, chips);
		}

		// Puts the program into the eight ROMs of ProgramRom: ROM n holds addresses n*256 .. n*256+255.
		public static void LoadProgram(ChipLibrary library, uint[] program)
		{
			ChipDescription programRom = library.GetChipDescription("ProgramRom");
			string romName = ChipTypeHelper.GetName(ChipType.Rom_256x16);
			int[] roms = Enumerable.Range(0, programRom.SubChips.Length).Where(i => ChipDescription.NameMatch(programRom.SubChips[i].Name, romName)).ToArray();
			Assert.AreEqual(8, roms.Length, "ProgramRom should contain eight ROMs");
			for (int bank = 0; bank < 8; bank++)
			{
				uint[] data = new uint[256];
				for (int i = 0; i < 256 && bank * 256 + i < program.Length; i++) data[i] = program[bank * 256 + i];
				programRom.SubChips[roms[RomBankOrder(programRom, library)[bank]]].InternalData = data;
			}
		}

		// Index (among the ROMs, in SubChips order) of the ROM that feeds input In of the output multiplexers.
		static int[] RomBankOrder(ChipDescription programRom, ChipLibrary library)
		{
			PinDescription[] muxInputs = library.GetChipDescription("Mux8x8").InputPins;
			string romName = ChipTypeHelper.GetName(ChipType.Rom_256x16);
			SubChipDescription[] roms = programRom.SubChips.Where(s => ChipDescription.NameMatch(s.Name, romName)).ToArray();
			SubChipDescription mux = programRom.SubChips.First(s => s.Name == "Mux8x8");
			int[] order = new int[8];
			for (int bank = 0; bank < 8; bank++)
			{
				WireDescription wire = programRom.Wires.First(w => w.TargetPinAddress.PinOwnerID == mux.ID && w.TargetPinAddress.PinID == muxInputs[bank].ID && roms.Any(r => r.ID == w.SourcePinAddress.PinOwnerID));
				order[bank] = Array.FindIndex(roms, r => r.ID == wire.SourcePinAddress.PinOwnerID);
			}

			return order;
		}

		TestCircuit Run(string program, int ticks)
		{
			(ChipLibrary library, List<ChipDescription> chips) = BuildAll();
			LoadProgram(library, Cpu8Asm.Assemble(program));
			TestCircuit c = new(chips.Single(c => c.Name == "Cpu8"), library);
			c.Set("RESET", 1);
			c.Tick();
			c.Set("RESET", 0);
			for (int i = 0; i < ticks; i++) c.Tick();
			return c;
		}

		static uint Pc(TestCircuit c) => c.Get("PC_LO") | (c.Get("PC_HI") << 8);

		[Test]
		public void AssemblerEncodesEveryFormat()
		{
			CollectionAssert.AreEqual(new uint[]
			{
				0x0000, 0x0800, 0x1180, 0x1AAA, 0x2420, 0x2A20, 0x3240, 0x3B20, 0x4120, 0x4919,
				0x7300, 0xD800, 0xE00A, 0xE200, 0xFFFF
			}, Cpu8Asm.Assemble(@"
				NOP
				HLT
				MOV R1, R4
				LDI R2, 0xAA
				LD R4, [0x20]
				ST [0x20], R2
				LDR R2, [R2]
				STR [R3], R1
				ADD R1, R1
				ADDI R1, 25
				INC R3
				JMP 0
				JZ 0x00A
				JZ 0x200
				JNC 0x7FF
			"));
		}

		// The test program from the instruction sheet (Examples/RHDL/CPU8/test_program.asm): a
		// countdown loop, then a RAM write and read back, compared with CMP. It ends in a loop at
		// 0x00B (success); HLT at 0x009 means failure.
		public static string SheetProgram => File.ReadAllText(Path.Combine(ExamplesRoot, "CPU8", "test_program.asm"));

		// The words listed in Examples/RHDL/CPU8/README.md (and printed by cpu8asm.py).
		[Test]
		public void SheetProgramAssemblesToTheDocumentedWords()
		{
			CollectionAssert.AreEqual(new uint[]
			{
				0x1805, 0x1901, 0x5820, 0xE802, 0x1AAA, 0x2A20, 0x2320, 0xCA60, 0xE00A, 0x0800, 0x0000, 0xD80B
			}, Cpu8Asm.Assemble(SheetProgram));
		}

		[Test]
		public void RunsTheProgramFromTheInstructionSheet()
		{
			TestCircuit c = Run(SheetProgram, 40);

			Assert.AreEqual(0u, c.Get("R0"), "R0 counted down to 0");
			Assert.AreEqual(1u, c.Get("R1"));
			Assert.AreEqual(0xAAu, c.Get("R2"));
			Assert.AreEqual(0xAAu, c.Get("R3"), "R3 read back from RAM[0x20]");
			Assert.AreEqual(1u, c.Get("Z"), "CMP of equal values sets Z");
			Assert.AreEqual(0u, c.Get("C"), "no borrow");
			Assert.AreEqual(0u, c.Get("HALTED"), "the test must not reach the HLT at 0x009");
			Assert.AreEqual(0x00Bu, Pc(c));
		}

		// Every instruction once, checked after each clock tick: (instruction, register, value, Z, C).
		[Test]
		public void ExecutesEveryInstruction()
		{
			(string asm, string reg, uint value, uint z, uint c)[] steps =
			{
				("NOP", "R0", 0, 0, 0),
				("LDI R1, 200", "R1", 200, 0, 0),
				("LDI R2, 100", "R2", 100, 0, 0),
				("MOV R3, R1", "R3", 200, 0, 0),
				("ADD R3, R2", "R3", 44, 0, 1), // 200 + 100 = 300: carry
				("ADC R3, R2", "R3", 145, 0, 0), // 44 + 100 + 1
				("ADDI R3, 111", "R3", 0, 1, 1), // 145 + 111 = 256
				("SUB R3, R2", "R3", 156, 0, 1), // 0 - 100: borrow
				("SBC R3, R2", "R3", 55, 0, 0), // 156 - 100 - 1
				("SUBI R3, 55", "R3", 0, 1, 0),
				("DEC R3", "R3", 255, 0, 1),
				("INC R3", "R3", 0, 1, 1),
				("LDI R4, 0b11001010", "R4", 0xCA, 1, 1), // LDI leaves the flags alone
				("AND R4, R2", "R4", 0xCA & 100, 0, 1), // logic leaves C alone
				("ANDI R4, 0x0F", "R4", 0x0, 1, 1),
				("OR R4, R1", "R4", 200, 0, 1),
				("ORI R4, 0x01", "R4", 201, 0, 1),
				("XOR R4, R2", "R4", 201 ^ 100, 0, 1),
				("XORI R4, 0xFF", "R4", (~(201u ^ 100)) & 0xFF, 0, 1),
				("NOT R4", "R4", 201 ^ 100, 0, 1),
				("SHL R1", "R1", 144, 0, 1), // 200 << 1, bit 7 into C
				("SHR R1", "R1", 72, 0, 0), // bit 0 into C
				("SHR R2", "R2", 50, 0, 0),
				("CMP R1, R2", "R1", 72, 0, 0), // 72 - 50: no borrow, R1 unchanged
				("CMPI R2, 51", "R2", 50, 0, 1), // 50 - 51: borrow
				("CMPI R2, 50", "R2", 50, 1, 0),
				("ST [0x80], R1", "R1", 72, 1, 0),
				("LDI R5, 0x80", "R5", 0x80, 1, 0),
				("LDR R6, [R5]", "R6", 72, 1, 0),
				("LDI R7, 0x81", "R7", 0x81, 1, 0),
				("STR [R7], R2", "R7", 0x81, 1, 0),
				("LD R0, [0x81]", "R0", 50, 1, 0)
			};

			string program = string.Join("\n", steps.Select(s => s.asm)) + "\nHLT";
			(ChipLibrary library, List<ChipDescription> chips) = BuildAll();
			LoadProgram(library, Cpu8Asm.Assemble(program));
			TestCircuit c = new(chips.Single(c => c.Name == "Cpu8"), library);
			c.Set("RESET", 1);
			c.Tick();
			c.Set("RESET", 0);

			for (int i = 0; i < steps.Length; i++)
			{
				c.Tick();
				(string asm, string reg, uint value, uint z, uint cf) = steps[i];
				Assert.AreEqual(value, c.Get(reg), $"{reg} after {asm}");
				Assert.AreEqual(z, c.Get("Z"), $"Z after {asm}");
				Assert.AreEqual(cf, c.Get("C"), $"C after {asm}");
				Assert.AreEqual((uint)i + 1, Pc(c), $"PC after {asm}");
			}

			c.Tick();
			c.Tick();
			Assert.AreEqual(1u, c.Get("HALTED"));
			Assert.AreEqual((uint)steps.Length, Pc(c), "HLT keeps the PC on itself");
		}

		[Test]
		public void ConditionalJumpsFollowTheFlags()
		{
			TestCircuit c = Run(@"
				LDI R0, 0
				LDI R1, 0
				CMPI R1, 0      ; Z=1, C=0
				JNZ fail
				JC fail
				JZ z_ok
				HLT
			z_ok:
				JNC c_ok
				HLT
			c_ok:
				CMPI R1, 1      ; Z=0, C=1
				JZ fail
				JNC fail
				JNZ nz_ok
				HLT
			nz_ok:
				JC done
				HLT
			done:
				LDI R0, 1
				HLT
			fail:
				LDI R0, 0xEE
				HLT
			", 30);

			Assert.AreEqual(1u, c.Get("R0"));
			Assert.AreEqual(1u, c.Get("HALTED"));
		}

		// Jumps use all 11 address bits: the program continues in the other ROMs and the PC wraps
		// from 0x7FF to 0x000.
		[Test]
		public void JumpsReachEveryRom()
		{
			uint[] program = new uint[2048];
			uint[] start = Cpu8Asm.Assemble("JMP 0x105");
			Array.Copy(start, program, start.Length);
			Array.Copy(Cpu8Asm.Assemble("LDI R1, 0x11\nJMP 0x7FE"), 0, program, 0x105, 2);
			Array.Copy(Cpu8Asm.Assemble("LDI R2, 0x22\nLDI R3, 0x33"), 0, program, 0x7FE, 2);
			// Wraps to 0x000, which jumps again: after the second pass R1 and R2 stay set.

			(ChipLibrary library, List<ChipDescription> chips) = BuildAll();
			LoadProgram(library, program);
			TestCircuit c = new(chips.Single(c => c.Name == "Cpu8"), library);
			c.Set("RESET", 1);
			c.Tick();
			c.Set("RESET", 0);

			c.Tick();
			Assert.AreEqual(0x105u, Pc(c));
			c.Tick();
			Assert.AreEqual(0x11u, c.Get("R1"));
			c.Tick();
			Assert.AreEqual(0x7FEu, Pc(c));
			c.Tick();
			c.Tick();
			Assert.AreEqual(0x22u, c.Get("R2"));
			Assert.AreEqual(0x33u, c.Get("R3"));
			Assert.AreEqual(0x000u, Pc(c), "the PC wraps around after 0x7FF");
		}

		// Sum of the bytes 1..10 kept in RAM, with a pointer register and a loop: R0 = 55.
		[Test]
		public void RunsALoopOverRam()
		{
			TestCircuit c = Run(@"
				LDI R1, 0x40    ; pointer
				LDI R2, 1       ; value
			fill:
				STR [R1], R2
				INC R1
				INC R2
				CMPI R2, 11
				JNZ fill
				LDI R0, 0       ; sum
				LDI R1, 0x40
			sum:
				LDR R3, [R1]
				ADD R0, R3
				INC R1
				CMPI R1, 0x4A
				JNZ sum
				HLT
			", 120);

			Assert.AreEqual(1u, c.Get("HALTED"));
			Assert.AreEqual(55u, c.Get("R0"));
		}
	
		// ---- The bundled CPU8 project ----

		const string ProjectName = "CPU8";

		public static (ProjectDescription project, ChipDescription[] chips) CreateCpu8Project()
		{
			(ChipLibrary library, List<ChipDescription> chips) = BuildAll();
			LoadProgram(library, Cpu8Asm.Assemble(SheetProgram));

			ChipDescription computer = chips.Single(c => c.Name == "Cpu8Computer");
			for (int i = 0; i < computer.SubChips.Length; i++)
			{
				string name = computer.SubChips[i].Name;
				// The power-on pulse is long enough to cover a rising clock edge.
				if (name == ChipTypeHelper.GetName(ChipType.Pulse)) computer.SubChips[i].InternalData = new uint[] { 4, 0, 0 };
				if (name == ChipTypeHelper.GetName(ChipType.Key)) computer.SubChips[i].InternalData = new uint[] { 'R' };
			}

			ProjectDescription project = new()
			{
				ProjectName = ProjectName,
				DLSVersion_LastSaved = Main.DLSVersion.ToString(),
				DLSVersion_EarliestCompatible = Main.DLSVersion_EarliestCompatible.ToString(),
				CreationTime = new DateTime(2026, 10, 31),
				LastSaveTime = new DateTime(2026, 10, 31),
				Prefs_MainPinNamesDisplayMode = 1,
				Prefs_ChipPinNamesDisplayMode = 1,
				// One instruction takes two simulation steps: 10 instructions per second, slow
				// enough to watch the registers change.
				Prefs_SimTargetStepsPerSecond = 20,
				Prefs_SimStepsPerClockTick = 1,
				AllCustomChipNames = chips.Select(c => c.Name).ToArray(),
				StarredList = BuiltinCollectionCreator.GetDefaultStarredList().ToList(),
				ChipCollections = BuiltinCollectionCreator.CreateDefaultChipCollections()
					.Append(new ChipCollection("CPU8", chips.Select(c => c.Name).ToArray()))
					.ToList(),
				StartupChipName = computer.Name
			};

			return (project, chips.ToArray());
		}

		[Test]
		public void BundledFilesMatchTheSources()
		{
			(ProjectDescription project, ChipDescription[] chips) = CreateCpu8Project();
			BundledProjectTests.CheckBundledFiles(BundledProjectTests.FolderOf(ProjectName), project, chips);
		}

		[Test]
		public void BundledProjectIsInstalledWithRewired()
		{
			CollectionAssert.Contains(BundledProjectInstaller.ProjectNames, ProjectName);
		}

		// The project runs the sheet program by itself: the power-on pulse resets the CPU, the
		// clock runs it, and it ends in the loop at 0x00B.
		[Test]
		public void ComputerRunsTheSheetProgramWithoutPressingReset()
		{
			(ProjectDescription _, ChipDescription[] chips) = CreateCpu8Project();
			ChipLibrary library = TestLibrary.Create(chips);
			ChipDescription computer = chips.Single(c => c.Name == "Cpu8Computer");

			RewiredEngine.Reset();
			RewiredEngine.StepsPerClockTransition = 1;
			SimChip root = RewiredEngine.BuildChip(computer, library);
			RewiredEngine.EnsureInitialized(root, Array.Empty<DevPinInstance>(), null);
			for (int i = 0; i < 200; i++) RewiredEngine.RunStep(root, Array.Empty<DevPinInstance>(), null);

			uint Output(string name)
			{
				int id = computer.OutputPins.Single(p => p.Name == name).ID;
				return PinState.GetBitStates(root.OutputPins.Single(p => p.ID == id).State);
			}

			Assert.AreEqual(0xAAu, Output("R2"));
			Assert.AreEqual(0xAAu, Output("R3"));
			Assert.AreEqual(0x0Bu, Output("PC_LO"));
			Assert.AreEqual(0u, Output("HALTED"));
		}
	}
}
