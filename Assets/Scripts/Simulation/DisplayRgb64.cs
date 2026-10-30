using System;

namespace DLS.Simulation
{
	// 64 x 64 colour screen with 4 bits per channel, double-buffered like the 16 x 16 RGB display.
	//
	// Pins (index): X 8-bit (0), Y 8-bit (1), RED (2), GREEN (3), BLUE (4), RESET (5), WRITE (6),
	// REFRESH (7), CLOCK (8); outputs R OUT, G OUT, B OUT give the shown colour at (X, Y).
	// X and Y use their low 6 bits; (0, 0) is the bottom-left pixel, as on the RGB display.
	//
	// On a rising CLOCK edge: RESET clears the back buffer, otherwise WRITE stores the colour at
	// (X, Y) in the back buffer; then REFRESH shows the back buffer. Writes only reach the screen
	// on a refresh, so a picture can be drawn over many ticks and appear at once.
	public static class DisplayRgb64
	{
		public const int Width = 64;
		public const int Height = 64;
		public const int PixelCount = Width * Height;

		// Internal state: shown pixels, back buffer, previous clock level, and whether the back
		// buffer differs from what is shown (so a refresh with nothing new copies nothing).
		public const int FrontOffset = 0;
		public const int BackOffset = PixelCount;
		public const int ClockIndex = PixelCount * 2;
		public const int BackDirtyIndex = ClockIndex + 1;
		public const int StateLength = BackDirtyIndex + 1;

		const int PinX = 0;
		const int PinY = 1;
		const int PinRed = 2;
		const int PinGreen = 3;
		const int PinBlue = 4;
		const int PinReset = 5;
		const int PinWrite = 6;
		const int PinRefresh = 7;
		const int PinClock = 8;

		public static int PixelIndex(uint xPinState, uint yPinState) =>
			(PinState.GetBitStates(yPinState) & (Height - 1)) * Width + (PinState.GetBitStates(xPinState) & (Width - 1));

		public static uint PackColour(uint red, uint green, uint blue) =>
			(PinState.GetBitStates(red) & 0xFu) | ((PinState.GetBitStates(green) & 0xFu) << 4) | ((PinState.GetBitStates(blue) & 0xFu) << 8);

		// Applies the clock. Returns true when the shown picture changed.
		public static bool Advance(SimChip chip)
		{
			uint[] state = chip.InternalState;
			SimPin[] pins = chip.InputPins;

			bool clockHigh = PinState.FirstBitHigh(pins[PinClock].State);
			bool risingEdge = clockHigh && state[ClockIndex] == 0;
			state[ClockIndex] = clockHigh ? 1u : 0u;
			if (!risingEdge) return false;

			if (PinState.FirstBitHigh(pins[PinReset].State))
			{
				Array.Clear(state, BackOffset, PixelCount);
				state[BackDirtyIndex] = 1;
			}
			else if (PinState.FirstBitHigh(pins[PinWrite].State))
			{
				int index = BackOffset + PixelIndex(pins[PinX].State, pins[PinY].State);
				uint colour = PackColour(pins[PinRed].State, pins[PinGreen].State, pins[PinBlue].State);
				if (state[index] != colour)
				{
					state[index] = colour;
					state[BackDirtyIndex] = 1;
				}
			}

			if (!PinState.FirstBitHigh(pins[PinRefresh].State) || state[BackDirtyIndex] == 0) return false;

			Array.Copy(state, BackOffset, state, FrontOffset, PixelCount);
			state[BackDirtyIndex] = 0;
			return true;
		}

		// Colour shown at the pixel the X and Y pins point to.
		public static uint ShownColour(SimChip chip) =>
			chip.InternalState[FrontOffset + PixelIndex(chip.InputPins[PinX].State, chip.InputPins[PinY].State)];
	}
}
