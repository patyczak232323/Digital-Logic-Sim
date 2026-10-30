using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using DLS.Description;
using DLS.Game;
using DLS.RHDL;
using DLS.Simulation;
using NUnit.Framework;
using UnityEngine;

namespace DLS.Tests
{
	// The PIX6 example computer from Examples/RHDL/PIX6 running the programs from its README
	// and drawing on the 64 x 64 screen.
	public class Pix6ExampleTests
	{
		static string ExamplesRoot => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Examples", "RHDL"));

		ChipLibrary library;
		ChipDescription pix6;
		ChipDescription computer;

		[SetUp]
		public void SetUp()
		{
			library = TestLibrary.Create();
			Build("Basics/04_DFF.rhdl");
			Build("Basics/05_REG4.rhdl");
			Build("PIX6/00_REG8.rhdl");
			pix6 = Build("PIX6/01_PIX6.rhdl");
			computer = Build("PIX6/02_PIX6_COMPUTER.rhdl");
		}

		[TearDown]
		public void TearDown() => RewiredEngine.Reset();

		ChipDescription Build(string relativePath)
		{
			string source = File.ReadAllText(Path.Combine(ExamplesRoot, relativePath));
			RhdlCompileResult result = RhdlCompiler.Compile(source, library);
			Assert.IsTrue(result.Success, $"{relativePath} failed to compile:\n" + string.Join("\n", result.Diagnostics.Select(d => d.ToString())));
			library.NotifyChipSaved(result.Description, false);
			return result.Description;
		}

		TestCircuit Run(uint[] program, int maxTicks)
		{
			uint[] rom = new uint[256];
			Array.Copy(program, rom, program.Length);
			string romName = ChipTypeHelper.GetName(ChipType.Rom_256x16);
			int romIndex = Array.FindIndex(pix6.SubChips, s => ChipDescription.NameMatch(s.Name, romName));
			Assert.GreaterOrEqual(romIndex, 0, "PIX6 should contain the stock ROM");
			pix6.SubChips[romIndex].InternalData = rom;

			TestCircuit c = new(computer, library);
			c.Set("RESET", 1);
			c.Tick();
			c.Set("RESET", 0);

			Stopwatch watch = Stopwatch.StartNew();
			int ticks = 0;
			while (c.Get("HALTED") == 0 && ticks < maxTicks)
			{
				c.Tick();
				ticks++;
			}
			TestContext.WriteLine($"{ticks} instructions in {watch.ElapsedMilliseconds} ms");
			return c;
		}

		static uint[] ShownPicture(SimChip root)
		{
			SimChip screen = Find(root, ChipType.DisplayRGB64);
			Assert.IsNotNull(screen, "the computer should contain the screen");
			uint[] picture = new uint[DisplayRgb64.PixelCount];
			Array.Copy(screen.InternalState, DisplayRgb64.FrontOffset, picture, 0, picture.Length);
			return picture;
		}

		static SimChip Find(SimChip chip, ChipType type)
		{
			if (chip.ChipType == type) return chip;
			foreach (SimChip sub in chip.SubChips)
			{
				SimChip found = sub == null ? null : Find(sub, type);
				if (found != null) return found;
			}
			return null;
		}

		static uint Rgb(uint r, uint g, uint b) => DisplayRgb64.PackColour(r, g, b);

		[Test]
		public void PatternAndDiagonalProgram()
		{
			// Program 1 from Examples/RHDL/PIX6/README.md.
			uint[] program =
			{
				0xB000, // 0: PAT
				0xE000, // 1: PLOTX
				0x9000, // 2: JXNZ 0
				0x7000, // 3: INCY
				0xA000, // 4: JYNZ 0
				0x3FFF, // 5: SETC FFF
				0x5000, // 6: PLOT
				0x6000, // 7: INCX
				0x7000, // 8: INCY
				0x9006, // 9: JXNZ 6
				0xC000, // A: SHOW
				0xF000  // B: HLT
			};

			TestCircuit c = Run(program, 20000);
			Assert.AreEqual(1u, c.Get("HALTED"), "program should finish");

			uint[] picture = ShownPicture(c.Root);
			for (uint y = 0; y < 64; y++)
			{
				for (uint x = 0; x < 64; x++)
				{
					uint expected = x == y ? Rgb(15, 15, 15) : Rgb(x >> 2, y >> 2, (x ^ y) >> 2);
					Assert.AreEqual(expected, picture[y * 64 + x], $"pixel ({x}, {y})");
				}
			}
		}

		[Test]
		public void MovingDotProgram()
		{
			// Program 2 from Examples/RHDL/PIX6/README.md: one frame per loop.
			uint[] program =
			{
				0x3F80, // 0: SETC F80
				0xD000, // 1: CLS
				0x5000, // 2: PLOT
				0xC000, // 3: SHOW
				0x6000, // 4: INCX
				0x7000, // 5: INCY
				0x8001  // 6: JMP 1
			};

			// Reset, SETC, then three frames of six instructions; the dot of the third frame
			// is at (2, 2) and is shown alone.
			TestCircuit c = Run(program, 1 + 6 * 2 + 3);
			uint[] picture = ShownPicture(c.Root);
			Assert.AreEqual(Rgb(15, 8, 0), picture[2 * 64 + 2], "dot at (2, 2)");
			Assert.AreEqual(1, picture.Count(p => p != 0), "only the current dot is shown");
		}
	}
}
