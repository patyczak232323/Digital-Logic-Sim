using System;
using System.Collections.Generic;
using System.Linq;
using DLS.Description;
using DLS.Game;
using DLS.RHDL;
using DLS.SaveSystem;
using DLS.Simulation;
using NUnit.Framework;
using static DLS.Tests.ChipBuilder;

namespace DLS.Tests
{
	// The 64 x 64 screen through the real engine: writes go to the back buffer, appear only on a
	// refresh, and the R/G/B outputs read back the shown pixel at (X, Y).
	public class DisplayRgb64Tests
	{
		const string ScreenName = "RGB DISPLAY 64";

		[TearDown]
		public void TearDown() => RewiredEngine.Reset();

		static TestCircuit BuildScreenCircuit()
		{
			ChipBuilder b = new("ScreenTest");
			PinAddress x = b.Input("X", PinBitCount.Bit8);
			PinAddress y = b.Input("Y", PinBitCount.Bit8);
			PinAddress red = b.Input("RED", PinBitCount.Bit4);
			PinAddress green = b.Input("GREEN", PinBitCount.Bit4);
			PinAddress blue = b.Input("BLUE", PinBitCount.Bit4);
			PinAddress reset = b.Input("RESET");
			PinAddress write = b.Input("WRITE");
			PinAddress refresh = b.Input("REFRESH");
			PinAddress clock = b.Input("CLK");
			PinAddress rOut = b.Output("R", PinBitCount.Bit4);
			PinAddress gOut = b.Output("G", PinBitCount.Bit4);
			PinAddress bOut = b.Output("B", PinBitCount.Bit4);

			int screen = b.Chip(ScreenName);
			PinAddress[] inputs = { x, y, red, green, blue, reset, write, refresh, clock };
			for (int i = 0; i < inputs.Length; i++) b.Wire(inputs[i], Pin(screen, i));
			b.Wire(Pin(screen, 9), rOut);
			b.Wire(Pin(screen, 10), gOut);
			b.Wire(Pin(screen, 11), bOut);

			return new TestCircuit(b.Build(), TestLibrary.Create());
		}

		static void Write(TestCircuit c, uint x, uint y, uint r, uint g, uint b, bool refresh = false)
		{
			c.Set("X", x);
			c.Set("Y", y);
			c.Set("RED", r);
			c.Set("GREEN", g);
			c.Set("BLUE", b);
			c.Set("WRITE", 1);
			c.Set("REFRESH", refresh ? 1u : 0u);
			c.Tick();
			c.Set("WRITE", 0);
			c.Set("REFRESH", 0);
		}

		static void Refresh(TestCircuit c)
		{
			c.Set("REFRESH", 1);
			c.Tick();
			c.Set("REFRESH", 0);
		}

		static (uint r, uint g, uint b) Read(TestCircuit c, uint x, uint y)
		{
			c.Set("X", x);
			c.Set("Y", y);
			c.Step();
			return (c.Get("R"), c.Get("G"), c.Get("B"));
		}

		[Test]
		public void WritesAppearOnlyAfterRefresh()
		{
			TestCircuit c = BuildScreenCircuit();

			Write(c, 3, 5, 15, 7, 1);
			Assert.AreEqual((0u, 0u, 0u), Read(c, 3, 5), "not shown before refresh");

			Refresh(c);
			Assert.AreEqual((15u, 7u, 1u), Read(c, 3, 5), "shown after refresh");
			Assert.AreEqual((0u, 0u, 0u), Read(c, 5, 3), "other pixels stay black");
		}

		[Test]
		public void WriteWithRefreshShowsOnSameEdge()
		{
			TestCircuit c = BuildScreenCircuit();
			Write(c, 63, 63, 1, 2, 3, refresh: true);
			Assert.AreEqual((1u, 2u, 3u), Read(c, 63, 63));
		}

		[Test]
		public void NothingHappensWithoutClockEdge()
		{
			TestCircuit c = BuildScreenCircuit();
			c.Set("X", 1);
			c.Set("Y", 1);
			c.Set("RED", 9);
			c.Set("WRITE", 1);
			c.Set("REFRESH", 1);
			c.Set("CLK", 0);
			c.Step(3);
			Assert.AreEqual(0u, c.Get("R"));

			// Holding the clock high is one edge, not one per step.
			c.Set("CLK", 1);
			c.Step();
			Assert.AreEqual(9u, c.Get("R"));
			c.Set("RED", 4);
			c.Step(3);
			Assert.AreEqual(9u, c.Get("R"));
		}

		[Test]
		public void CoordinatesUseLowSixBits()
		{
			TestCircuit c = BuildScreenCircuit();
			Write(c, 64 + 2, 128 + 64 + 7, 5, 6, 7, refresh: true);
			Assert.AreEqual((5u, 6u, 7u), Read(c, 2, 7));
		}

		[Test]
		public void ResetClearsBackBufferUntilRefresh()
		{
			TestCircuit c = BuildScreenCircuit();
			Write(c, 10, 20, 8, 8, 8, refresh: true);
			Write(c, 11, 20, 4, 4, 4, refresh: true);

			c.Set("RESET", 1);
			c.Tick();
			c.Set("RESET", 0);
			Assert.AreEqual((8u, 8u, 8u), Read(c, 10, 20), "reset only clears the back buffer");

			Refresh(c);
			Assert.AreEqual((0u, 0u, 0u), Read(c, 10, 20));
			Assert.AreEqual((0u, 0u, 0u), Read(c, 11, 20));
		}

		[Test]
		public void FullFrameRoundTrips()
		{
			TestCircuit c = BuildScreenCircuit();
			for (uint y = 0; y < 64; y += 9)
			{
				for (uint x = 0; x < 64; x += 7)
				{
					Write(c, x, y, x & 15, y & 15, (x ^ y) & 15);
				}
			}
			Refresh(c);

			for (uint y = 0; y < 64; y += 9)
			{
				for (uint x = 0; x < 64; x += 7)
				{
					Assert.AreEqual((x & 15, y & 15, (x ^ y) & 15), Read(c, x, y), $"pixel ({x}, {y})");
				}
			}
		}

		[Test]
		public void RhdlCanDriveAndReadTheScreen()
		{
			ChipLibrary library = TestLibrary.Create();
			RhdlCompileResult result = RhdlCompiler.Compile(@"
circuit Paint(X: u8, Y: u8, C: u4, CLK) -> (R: u4, G: u4, B: u4):
    s = RGB_DISPLAY_64(X, Y, C, C, C, 0, 1, 1, CLK)
    R = s.R_OUT
    G = s.G_OUT
    B = s.B_OUT
", library);
			Assert.IsTrue(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));

			TestCircuit c = new(result.Description, library);
			c.Set("X", 4);
			c.Set("Y", 9);
			c.Set("C", 12);
			c.Tick();
			Assert.AreEqual((12u, 12u, 12u), (c.Get("R"), c.Get("G"), c.Get("B")));
		}

		[Test]
		public void PixelIndexHasOriginBottomLeftRowMajor()
		{
			Assert.AreEqual(0, DisplayRgb64.PixelIndex(0, 0));
			Assert.AreEqual(1, DisplayRgb64.PixelIndex(1, 0));
			Assert.AreEqual(64, DisplayRgb64.PixelIndex(0, 1));
			Assert.AreEqual(DisplayRgb64.PixelCount - 1, DisplayRgb64.PixelIndex(63, 63));
		}

		[Test]
		public void BuiltinDescriptionAndEnumValue()
		{
			ChipDescription desc = BuiltinChipCreator.CreateAllBuiltinChipDescriptions().Single(d => d.ChipType == ChipType.DisplayRGB64);
			Assert.AreEqual(ScreenName, desc.Name);
			Assert.AreEqual(9, desc.InputPins.Length);
			Assert.AreEqual(3, desc.OutputPins.Length);
			Assert.AreEqual(30, (int)ChipType.Buzzer, "saved chip type numbers stay the same");
			Assert.AreEqual(31, (int)ChipType.DisplayRGB64);
		}

		[Test]
		public void ProjectChipWithSameNameHidesBuiltin()
		{
			ChipDescription own = new ChipBuilder(ScreenName).Build();
			ChipLibrary library = new(Array.Empty<ChipDescription>(), new[] { own }, BuiltinChipCreator.CreateAllBuiltinChipDescriptions());

			Assert.AreEqual(ChipType.Custom, library.GetChipDescription(ScreenName).ChipType);
			Assert.IsFalse(library.IsBuiltinChip(ScreenName));
		}

		[Test]
		public void OldProjectsGetScreenInDisplayCollection()
		{
			ProjectDescription project = new()
			{
				AllCustomChipNames = Array.Empty<string>(),
				ChipCollections = new List<ChipCollection> { new("Basic", "NAND"), new("display", "RGB DISPLAY") }
			};

			Loader.AddNewBuiltinChipsToCollections(project);
			CollectionAssert.AreEqual(new[] { "RGB DISPLAY", ScreenName }, project.ChipCollections[1].Chips);

			Loader.AddNewBuiltinChipsToCollections(project);
			Assert.AreEqual(1, project.ChipCollections.Sum(col => col.Chips.Count(n => n == ScreenName)), "added once");
		}

		[Test]
		public void OldProjectWithOwnScreenChipIsLeftAlone()
		{
			ProjectDescription project = new()
			{
				AllCustomChipNames = new[] { "rgb display 64" },
				ChipCollections = new List<ChipCollection> { new("DISPLAY", "RGB DISPLAY") }
			};

			Loader.AddNewBuiltinChipsToCollections(project);
			CollectionAssert.AreEqual(new[] { "RGB DISPLAY" }, project.ChipCollections[0].Chips);
		}
	}
}
