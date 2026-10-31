# Contributing to Rewired

Thanks for helping. Bug reports with a small project or RHDL file that reproduces the problem are the most useful contribution; see [Reporting bugs](#reporting-bugs).

## Requirements

- **Unity** — the exact editor version is in `ProjectSettings/ProjectVersion.txt` (currently Unity 6000.0.46f1). Open the repository root as a Unity project.
- **.NET 8 SDK** — runs the engine tests and benchmarks without Unity.
- **Python 3** — runs the regression suites in `Tools/`.
- **Icarus Verilog** (optional) — when `iverilog` is on the path, the Verilog export tests also simulate the exported designs.

## Repository layout

| Path | Contents |
| --- | --- |
| `Assets/Scripts/Simulation` | Simulation engine: `RewiredEngine` (the single entry point), the deterministic simulator, native JIT, feedback JIT, FULL LUT cache, truth tables, breakpoints, diagnostics |
| `Assets/Scripts/RHDL` | RHDL frontend: `RhdlSyntax` parses the source, `RhdlElaborator` builds a NAND netlist from it, `RhdlLayout` draws the schematic and `RhdlCompiler` ties them together |
| `Assets/Scripts/Description` | Chip and project data types and their JSON serialization |
| `Assets/Scripts/SaveSystem` | Loading, saving, save paths, global chips, WebGL and Android storage |
| `Assets/Scripts/Game` | Editor model: projects, chip instances, interaction, undo |
| `Assets/Scripts/Graphics` | Desktop and touch UI, world drawing |
| `Assets/Scripts/Export` | Verilog and SVG exporters |
| `Assets/Scripts/Seb` | UI, drawing and text helpers from the original Digital Logic Sim |
| `Assets/Tests/EditMode` | Unity EditMode tests (also run by the engine harness) |
| `Assets/Dev` | Development-only tools, excluded from Android builds |
| `Examples/RHDL` | Curated RHDL examples, compiled and simulated by the tests |
| `Docs` | User documentation and release notes |
| `Tools` | Engine harness, Python regression suites, self-hosted runner setup |

## Running the tests

From the repository root:

```bash
# Engine tests (EditMode tests on plain .NET), then the benchmarks
dotnet test Tools/EngineHarness --filter "Category!=Benchmark"
dotnet test Tools/EngineHarness --filter "Category=Benchmark"

# Regression suites
python3 Tools/SimulationRegression/regression.py
python3 Tools/ComputerRegression/regression.py
python3 Tools/ProgramRegression/regression.py
```

In the Unity editor, *Window → General → Test Runner → EditMode* runs the full EditMode suite, including the few tests that need real editor objects and are left out of the engine harness.

What runs in CI:

| Workflow | When | What |
| --- | --- | --- |
| `engine-harness.yml` | pull requests, `main`, `fix/**`, `perf/**`, `exp/**` | engine tests and benchmarks on .NET, Verilog simulation with Icarus |
| `simulation-regression.yml` | same | the three Python suites |
| `unity-tests.yml` | pull requests and `main` that touch `Assets`, `Packages`, `ProjectSettings` or `Examples` | EditMode tests in Unity on the self-hosted runners (not for pull requests from forks) |
| `unofficial-build.yml` | `main` | all platforms, published as the `android-unofficial` pre-release |
| `release.yml` | `v*` tags | the tests above, then all platforms, published as a GitHub Release |

## Making changes

- Match the surrounding code: tabs in C#, braces on their own lines, comments only where the reason is not obvious from the code. `.editorconfig` sets the basics.
- Commit the `.meta` file of every new file under `Assets/`; Unity identifies assets by the GUID inside it. A new `.meta` needs a fresh random GUID.
- Engine code (`Description`, `Simulation`, `RHDL`, the exporters, the save system) is also compiled by `Tools/EngineHarness` against small stand-ins for Unity types in `Tools/EngineHarness/Stubs/Unity.cs`. If engine code starts using another Unity type, add the smallest stand-in that compiles.
- Add or update a test with every behaviour change. Engine and RHDL tests go in `Assets/Tests/EditMode`, so both Unity and the harness run them.
- Every complete RHDL circuit in the Markdown documentation is compiled by `DocumentationExampleTests`. When the RHDL syntax changes, update the docs in the same change.
- The benchmarks fail when gate evaluations or propagations per step grow more than 25% over `Tools/EngineHarness/benchmark-baseline.json`. Lower the baseline after an optimisation; raise it only with a reason in the commit message.
- Keep pull requests focused, and describe what changed for users and how you tested it.

## Trying the touch interface without a phone

A desktop build started with `-mobile-ui` shows the Android interface, and the left mouse button acts as one finger: tap, drag, and hold to long-press; the mouse wheel zooms and Escape works as Android's Back button. The *UI preview build* workflow (`ui-preview.yml`, run by hand) builds the Linux player from any branch and attaches it to the `ui-preview` pre-release, so the interface can be run under Xvfb and screenshotted:

```bash
./DLSRewired -mobile-ui -screen-fullscreen 0 -screen-width 2400 -screen-height 1080
```

## Releasing

1. Set the new version in `VERSION`, `Main.RewiredVersion` (`Assets/Scripts/Game/Main/Main.cs`, with the date in `RewiredLastUpdatedString`) and `bundleVersion` in `ProjectSettings/ProjectSettings.asset`; increase `AndroidBundleVersionCode` there by one.
2. Write `Docs/RELEASE_<version>.md`, and update the version in `README.md` and the release list in `Docs/README.md`. `Tools/ProgramRegression` fails until all of these agree.
3. Merge to `main`, then push a tag `v<version>`. `release.yml` checks that the tag matches `VERSION`, runs the tests, builds every platform and publishes the release with the notes from `Docs/RELEASE_<version>.md`.

## Reporting bugs

Open an issue with the version, platform, what you did, what you expected and what happened. For simulation bugs, attach the smallest project folder or RHDL file that shows the problem; sequential logic, feedback networks, large nested chips, CPU designs and projects imported from Digital Logic Sim are especially useful.

Security problems should not go in a public issue; see [SECURITY.md](SECURITY.md).

## License

By contributing you agree that your contributions are licensed under the [MIT License](LICENSE).
