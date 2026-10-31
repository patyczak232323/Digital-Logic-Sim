# Rewired v0.5.1

Rewired v0.5.1 is the first Android release signed with a permanent key, so later versions install over it. It also lets you hold the phone upright, fixes the in-app update on Linux, and fixes problems found in an audit of v0.5.0.

## Android

- **Signed APK**: the APK is signed with Rewired's own key. Later releases and the unofficial builds install over it, and *DOWNLOAD* in the app updates it. **This version needs one uninstall**: v0.5.0 and earlier were signed with a temporary key, and Android refuses to replace an app signed with a different key ("App not installed"). Uninstalling deletes the projects stored in the app, so export the ones you want to keep first. *EXPORT* saves them to `Download/Rewired`.
- **Hold the phone upright**: in portrait the top bar takes two rows, the *PARTS* panel sits below the board, and the menu and home screen are one column. *Settings > SCREEN* chooses *TURN WITH PHONE*, *SIDEWAYS* or *UPRIGHT*.
- **Turning the phone**: the picture now follows the screen when the phone turns. Before, Rewired fixed its drawing surface to the size the screen had at start-up.
- Upright, *Preferences*, *Simulation diagnostics* and the *Chip library* use one column. Labels no longer run into their values, the logic analyzer is no longer cut off at the right edge, and the library's actions are no longer squeezed into a narrow column.
- Upright, a part can be dragged from *PARTS* up onto the board.
- The home screen's *Settings* fits a phone held sideways.
- **Challenges** have been removed.

## Fixes

- The example *Snake* project and chips built with RHDL no longer show "not saved", or ask about unsaved changes, right after they are opened.
- **Linux**: the in-app update now runs. Before, it looked for `DLSRewired.x86_64`, but the release's program is called `DLSRewired`, so it only opened the release page.
- **Desktop updates**: the download is checked against the size of the release file. If the new files cannot be copied into the installation, Rewired starts again and says so. Before, Windows waited forever behind a hidden window, and Linux quietly left nothing running.
- **RHDL**: very deeply nested expressions, very long operator chains and long chains of wires used before they are assigned could exhaust the stack and close Rewired. The compiler now has far more room, and nesting deeper than 100 levels gets a message. A loop whose body adds nothing (`for i in range(10**12): pass`) no longer freezes the editor.
- Renaming a chip by changing only upper and lower case no longer leaves its old recorded tests behind on Linux and Android.

## Compatibility

- Projects from v0.5.0 open unchanged.
- **Android**: uninstall v0.5.0 once before installing this version (see above). After that, updates install over each other.
- **Linux**: a v0.5.0 installation still opens the release page instead of updating itself. Download this version by hand once. From v0.5.1 on, the update runs in the app.

## Downloads

- Windows x64: `DLSRewired-Windows-x64.zip`
- Linux x86_64: `DLSRewired-Linux-x86_64.zip`
- WebGL (browser): `DLSRewired-WebGL.zip`. Unzip it and serve the folder from any static web host.
- Android ARM64: `DLSRewired-Android-arm64.apk` (experimental)

On Android, allow installing apps from your browser or file manager, then open the APK.
