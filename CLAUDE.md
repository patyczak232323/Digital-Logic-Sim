# Notes for Claude

## Reminders for the owner (bring these up at the start of a session)

- **Android release signing is not set up yet.** Ask whether the four repository secrets
  `ANDROID_KEYSTORE_BASE64`, `ANDROID_KEYSTORE_PASS`, `ANDROID_KEYALIAS_NAME` (value `rewired`)
  and `ANDROID_KEYALIAS_PASS` have been added (generated in Termux with
  `keytool -genkeypair -storetype JKS -keystore rewired.keystore -alias rewired ...` and set with `gh secret set`).
  Until then release APKs are signed with Unity's throwaway debug key, so Android refuses to
  install a new APK over an installed one and the in-app update cannot work on Android.
  Once the secrets exist:
  1. In `.github/workflows/release.yml`, pass them to `game-ci/unity-builder` for the Android job
     (`androidKeystoreName`, `androidKeystoreBase64`, `androidKeystorePass`, `androidKeyaliasName`,
     `androidKeyaliasPass`), fail the job with a clear error when they are missing, and set
     `androidVersionCode` so it grows with every release (e.g. from the version number).
  2. Release the next version (v0.5.0 is out, published 30 Oct 2026, its APK still debug-signed):
     bump `VERSION`, `Main.RewiredVersion` (and its date), `bundleVersion` and the README version,
     add `Docs/RELEASE_<version>.md`, merge, then run `release.yml` on `main` (workflow_dispatch;
     this environment cannot push tags, the release creates the tag itself).
  3. Remind the owner that switching from the debug key needs one uninstall on Android, which
     deletes projects stored in the app: export the ones to keep first (they go to Download/Rewired).
  Remove this reminder when it is done.

## Working on this repository

- The owner writes in Polish; answer in Polish.
- After a change: commit, push, open a PR to `main`, wait for CI (including the Unity
  "EditMode tests" job on the self-hosted runner) and squash-merge it without asking.
- Engine, RHDL, save-system and example tests run without Unity:
  `dotnet test Tools/EngineHarness --filter "Category!=Benchmark"`, plus `python3 Tools/*/regression.py`.
  UI code (`Assets/Scripts/Graphics`, `Main.cs`) only compiles in the Unity CI job.
- New `ChipType` values go at the end of the enum (saved projects store the number).
- The bundled Snake project (`Assets/Resources/BundledProjects/Snake`) is generated from
  `Examples/RHDL/PIX6`; see `Assets/Tests/EditMode/BundledProjectTests.cs` to rebuild it.
