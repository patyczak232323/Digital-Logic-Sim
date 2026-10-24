# Rewired v0.4.0

Rewired v0.4.0 brings Rewired to Android, adds chips shared across projects and makes saving safer. It also adds automated tests that run the real simulation engine.

## Highlights

- **Android (ARM64)**: starts in the same interface as the desktop sim, driven by touch (tap to place, long-press for menus, pinch to zoom). An optional touch interface, chosen in *SETTINGS*, has its own main menu, project management, chip picker, chip library, chip save flow and RHDL Studio layout. Touch gestures, safe-area and landscape layout, and the Android system keyboard are supported.
- **Global chips**: save a custom chip with `AVAILABILITY` set to `ALL PROJECTS` and it is available in every project. Renames propagate to other projects, and deleting a global chip warns that other projects may depend on it.
- **Readable RHDL schematics**: chips generated from RHDL use a left-to-right block layout with orthogonal wiring, and keep the root pin order from the source.
- **Built-in name conflicts**: if a custom chip has the same name as a newer built-in chip, the project menus now show which chip to rename to use the built-in one.

## Saving and data

- Desktop save data now lives in its own `Rewired` subfolder instead of the folder shared with the original Digital Logic Sim. Existing data is copied there once on first launch; the original folder is left untouched.
- A failed final save step can no longer delete both the new and the previous version of a file, and an interrupted save is recovered on the next load.
- Project and chip names that would resolve outside their save folder are rejected.
- Switching projects waits for the previous simulation thread to stop before starting the next one.

## Testing

- New Unity EditMode tests run the shipped C# engine through `RewiredEngine`: gate-level behaviour (NAND, SR latch, nested chips, oscillator guard, determinism) and every curated RHDL example, including the CPU4 test program.
- The tests run in GitHub Actions next to the existing Python regression suites.

## Downloads

- Windows x64: `DLSRewired-Windows-x64.zip`
- Linux x86_64: `DLSRewired-Linux-x86_64.zip`
- Android ARM64: `DLSRewired-Android-arm64.apk` (experimental)

On Android, allow installing apps from your browser or file manager, then open the APK.

## Compatibility

Projects from v0.3.0 open unchanged. Rewired remains an independent simulator derived from Sebastian Lague's Digital Logic Sim; exact traversal-order, race-condition and timing-dependent behaviour is not guaranteed to match the original runtime.
