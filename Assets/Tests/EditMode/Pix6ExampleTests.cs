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

		TestCircuit Start(uint[] program)
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
			return c;
		}

		TestCircuit Run(uint[] program, int maxTicks)
		{
			TestCircuit c = Start(program);
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

		// ---- Snake (Examples/RHDL/PIX6/snake.pix6) ----

		const uint Right = 1, Up = 2, Left = 4, Down = 8;
		static readonly uint Head = Rgb(15, 15, 0);
		static readonly uint Food = Rgb(15, 0, 0);
		static readonly uint Wall = Rgb(4, 4, 4);
		static readonly uint Crash = Rgb(15, 0, 15);

		sealed class SnakeGame
		{
			public readonly TestCircuit Circuit;
			readonly int frameAddress;

			public SnakeGame(Pix6ExampleTests owner)
			{
				uint[] program = Pix6Assembler.Assemble(File.ReadAllText(Path.Combine(ExamplesRoot, "PIX6/snake.pix6")), out var labels);
				frameAddress = labels["frame"];
				// Shortest delay, so the test needs few instructions per frame.
				Assert.AreEqual(0x0C00u, program[frameAddress] & 0xFF00u, "the frame should start with SETT");
				program[frameAddress] = 0x0C01;
				Circuit = owner.Start(program);
			}

			public uint[] Picture => ShownPicture(Circuit.Root);

			// Runs until the next frame starts (the game shows the previous one just before).
			public void NextFrame(uint keys = 0, int maxTicks = 20000)
			{
				Circuit.Set("KEYS", keys);
				for (int i = 0; i < maxTicks; i++)
				{
					Circuit.Tick();
					if (Circuit.Get("PC") == frameAddress) return;
				}
				Assert.Fail("no new frame started");
			}

			public (int x, int y) Find(uint colour)
			{
				uint[] picture = Picture;
				int index = Array.IndexOf(picture, colour);
				return index < 0 ? (-1, -1) : (index % 64, index / 64);
			}

			public int Length => Picture.Count(p => p == Head || (p & 0xCFFu) == Rgb(0, 15, 0));
		}

		[Test]
		public void SnakeAssemblesIntoTheRom()
		{
			uint[] program = Pix6Assembler.Assemble(File.ReadAllText(Path.Combine(ExamplesRoot, "PIX6/snake.pix6")));
			Assert.LessOrEqual(program.Length, 256);
			TestContext.WriteLine($"{program.Length} words");
		}

		[Test]
		public void SnakeStartsInTheMiddleAndMovesRight()
		{
			SnakeGame game = new(this);
			game.NextFrame();

			uint[] picture = game.Picture;
			for (int i = 0; i < 64; i++)
			{
				Assert.AreEqual(Wall, picture[i], "bottom wall");
				Assert.AreEqual(Wall, picture[63 * 64 + i], "top wall");
				Assert.AreEqual(Wall, picture[i * 64], $"left wall at y={i}");
				Assert.AreEqual(Wall, picture[i * 64 + 63], "right wall");
			}
			Assert.AreEqual((16, 32), game.Find(Head));
			Assert.AreNotEqual((-1, -1), game.Find(Food), "food is placed");

			for (int i = 1; i <= 6; i++)
			{
				game.NextFrame();
				Assert.AreEqual((16 + i, 32), game.Find(Head), $"head after {i} moves");
			}
			Assert.AreEqual(5, game.Length, "the snake grows to 5 at the start");
		}

		[Test]
		public void SnakeTurnsButCannotReverse()
		{
			SnakeGame game = new(this);
			game.NextFrame();
			game.NextFrame();
			game.NextFrame(Up);
			Assert.AreEqual((17, 33), game.Find(Head), "turned up");
			game.NextFrame(Down);
			Assert.AreEqual((17, 34), game.Find(Head), "down would reverse, so it keeps going up");
			game.NextFrame(Left);
			Assert.AreEqual((16, 34), game.Find(Head), "turned left");
		}

		[Test]
		public void SnakeCrashesIntoTheWallAndRestartsOnAKey()
		{
			SnakeGame game = new(this);
			int ticks = 0;
			game.Circuit.Set("KEYS", 0);
			while (game.Find(Crash).x < 0)
			{
				Assert.Less(ticks++, 100000, "the snake should reach the wall");
				game.Circuit.Tick();
			}
			Assert.AreEqual((63, 32), game.Find(Crash), "crash shown where the head hit the wall");

			game.NextFrame(Right, 100000);
			Assert.AreEqual(-1, game.Find(Crash).x, "a new game started");
			Assert.AreEqual((16, 32), game.Find(Head));
		}

		[Test]
		public void SnakeBotEatsAndGrows()
		{
			SnakeGame game = new(this);
			game.NextFrame();
			for (int i = 0; i < 6; i++) game.NextFrame();
			Assert.AreEqual(5, game.Length);

			int eaten = 0;
			(int x, int y) lastFood = game.Find(Food);
			uint heading = Right;
			for (int frame = 0; frame < 2000 && eaten < 4; frame++)
			{
				(int hx, int hy) = game.Find(Head);
				(int fx, int fy) = game.Find(Food);
				Assert.AreEqual(-1, game.Find(Crash).x, "the bot should not crash");

				// Head for the food without reversing.
				uint want = fx > hx ? Right : fx < hx ? Left : fy > hy ? Up : Down;
				bool reverse = (want | heading) is (Right | Left) or (Up | Down);
				if (reverse) want = fy > hy ? Up : fy < hy ? Down : (heading is Up or Down ? Right : Up);
				heading = want;
				game.NextFrame(want);

				(int x, int y) food = game.Find(Food);
				if (food != lastFood)
				{
					eaten++;
					lastFood = food;
				}
			}

			Assert.AreEqual(4, eaten, "food eaten");
			for (int i = 0; i < 4; i++) game.NextFrame(heading);
			Assert.AreEqual(5 + 3 * eaten, game.Length, "each food adds three segments");
		}
	}
}
