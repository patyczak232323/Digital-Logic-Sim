# Rewired v0.5.0

Rewired v0.5.0 adds a 64 x 64 colour screen and a small computer that plays **Snake** on it, rewrites RHDL with Python syntax, makes the simulator faster, gives Rewired a new logo and lets it update itself from GitHub Releases.

## Highlights

- **64 x 64 colour screen**: the new built-in chip *RGB DISPLAY 64* (in RHDL `RGB_DISPLAY_64`) in the *DISPLAY* collection. It has 4 bits per colour channel and is double-buffered: on a rising clock edge *WRITE* draws a pixel into a hidden picture, and *REFRESH* shows it all at once. The *R/G/B OUT* pins read back the colour shown at *X, Y*. Rows of equal colour are drawn as one shape, so a large screen stays cheap to draw.
- **Snake, ready to play**: on first start Rewired installs a *Snake* project. Open it and play with **W A S D** (**R** restarts). On a phone, hold a finger on the key chips. The game runs on *PIX6*, a small processor built from NAND gates in RHDL, from a 70-instruction program in its ROM. The screen is the game's only memory: every body pixel stores the direction to the next segment, and the tail follows those directions. Deleting the project does not bring it back on the next start.
- **PIX6 example computer** (`Examples/RHDL/PIX6`): a processor with pen registers, a colour register, keys, screen read-back, random numbers and loop counters, documented with its instruction set and example programs.
- **RHDL 1.0**: circuits are written with Python syntax, for example `circuit Adder(A: u8, B: u8) -> Y: u8:` with an indented body. It supports `& | ^ ~`, `a if s else b`, slices, `for` loops and chips called like functions with tuple unpacking. Error messages name the line and suggest a fix. Generated chips use far fewer gates (CPU4: 1121 -> 354 NANDs) and are laid out as readable left-to-right schematics without diagonal wires or wires crossing chips. See *Coming from RHDL 0.6* in the [RHDL guide](RHDL_GUIDE.md).
- **Updates from GitHub**: Rewired checks for a newer release when it starts (this can be turned off in Settings).
  - **Windows and Linux**: *UPDATE* in the main menu's bottom bar downloads the release, replaces the installed files and restarts Rewired.
  - **Android**: *DOWNLOAD* opens the new APK in the browser.
- **New logo**: a chip with a gold R, used as the app icon and next to the title in the menus.

## Faster simulation

- Pins inside custom chips that have a single driver are resolved at once, without going through the scheduling queue, and the hot loop uses arrays and a ring buffer. Engine benchmarks run about 20-30 % faster.
- FULL LUTs are built with the chip's native program on several threads. Building an 18-bit ALU's LUT takes 0.4 s instead of 127 s, and truth tables use the same path.
- Registers and memory built by hand in the editor now stay on the native feedback path instead of falling back to gate-by-gate evaluation. On a hand-built 8-bit datapath this raises the speed from about 149k to 180k steps per second.

## Compatibility

- Projects from v0.4.0 open unchanged. Old projects get the new screen in their *DISPLAY* collection. If a project already has its own chip named *RGB DISPLAY 64*, that chip keeps the name and the built-in screen is hidden in that project.
- RHDL 0.6 sources need to be rewritten in the 1.0 syntax; the guide has a table of what changed. Chips already built from old sources are ordinary saved chips and keep working.
- Projects that use the new screen or PIX6 cannot be opened correctly by v0.4.0.
- **Android**: release APKs are not yet signed with a permanent key, so this APK may not install over an earlier one ("App not installed"). Export the projects you want to keep (*EXPORT*, saved to `Download/Rewired`), uninstall the old version and install this one. Once releases are signed with a fixed key, later updates will install over each other.
- **Desktop**: v0.4.0 has no updater, so download this version by hand once. From v0.5.0 on, Rewired offers newer releases itself.

## Testing

- New tests drive the screen through the engine. They cover buffering, a single clock edge, coordinate masking, reset, RHDL use, name collisions and the collection upgrade.
- Both PIX6 example programs are checked pixel by pixel.
- A bot plays Snake. It eats four times, the snake grows by three segments for each, and the game crashes into the wall and restarts as expected.
- The bundled Snake project is checked against its RHDL and assembly sources, and boots without pressing reset.
- Release parsing, version comparison, the asset names in `release.yml` and the desktop install scripts are tested.

## Downloads

- Windows x64: `DLSRewired-Windows-x64.zip`
- Linux x86_64: `DLSRewired-Linux-x86_64.zip`
- WebGL (browser): `DLSRewired-WebGL.zip`. Unzip it and serve the folder from any static web host.
- Android ARM64: `DLSRewired-Android-arm64.apk` (experimental)

On Android, allow installing apps from your browser or file manager, then open the APK.
