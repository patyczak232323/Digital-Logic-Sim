# Rewired v0.5.2

Rewired v0.5.2 updates itself on Android without the browser, adds copy and paste to the chip editor, and fixes the R in the logo.

## Android

- **Update from the app**: *UPDATE* downloads the new APK inside Rewired, shows the progress in the header and installs it. Rewired then closes; open it again to use the new version. The first time, Android asks to allow installs from Rewired and to confirm the update. After that, Android 12 and newer install updates without asking.

## Chip editor

- **Ctrl+A, Ctrl+C, Ctrl+X, Ctrl+V**: select all, copy, cut and paste the parts of the chip being edited and the wires between them. The copy is a snapshot, so it can be pasted after the originals are deleted, or into another chip or project; chips the target cannot use are left out. *Shift+D* duplication uses the same code.
- **Ctrl+Y** is a second redo shortcut. All new shortcuts can be rebound. Settings saved by an older version get them, unless one of the combinations is already used by another action; that new shortcut then starts unbound.

## Fixes

- The R in the logo on the main menu and in the Android header was drawn twice as thick as intended, and its leg stuck out below the letter. It now matches the app icon.

## Compatibility

- Projects from v0.5.1 open unchanged.
- **Android**: v0.5.1 installs this version over itself, but through the browser as before, because v0.5.1 does not have the new updater yet. From v0.5.2 on, updates are installed by the app.

## Downloads

- Windows x64: `DLSRewired-Windows-x64.zip`
- Linux x86_64: `DLSRewired-Linux-x86_64.zip`
- WebGL (browser): `DLSRewired-WebGL.zip`. Unzip it and serve the folder from any static web host.
- Android ARM64: `DLSRewired-Android-arm64.apk` (experimental)

On Android, allow installing apps from your browser or file manager, then open the APK.
