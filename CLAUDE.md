# Notes for Claude

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
- Android APKs (releases and unofficial builds) are signed with the release key from the repository
  secrets `ANDROID_*`; see "Android signing" in `CONTRIBUTING.md`.
- To release: update the version (see "Releasing" in `CONTRIBUTING.md`), merge, then run
  `release.yml` on `main` (workflow_dispatch). This environment cannot push tags; the release creates the tag itself.
