# Engine harness

Builds Rewired's engine code without Unity and runs it with plain .NET and NUnit:

- the Unity EditMode tests in `Assets/Tests/EditMode` (except the few that need real editor
  objects, listed in `EngineHarness.csproj`), in about half a minute instead of a Unity
  editor start;
- the Verilog export tests against Icarus Verilog, when `iverilog` is installed;
- the engine benchmarks in `Benchmarks/`.

It compiles `Description`, `Simulation`, `RHDL`, the Verilog exporter, the chip library
and the save system straight from `Assets/Scripts`, against the small stand-ins for Unity
and editor types in `Stubs/Unity.cs`. When engine code starts using another Unity or
editor type, add the smallest stand-in that compiles.

## Running

```
dotnet test Tools/EngineHarness --filter "Category!=Benchmark"   # tests
dotnet test Tools/EngineHarness --filter "Category=Benchmark"    # benchmarks
```

Requires the .NET 8 SDK. CI runs both in `.github/workflows/engine-harness.yml`.

## Benchmarks

Each workload runs through specific execution paths (plain gate simulation, native JIT,
feedback JIT) and reports steps per second, gate evaluations per step and signal
propagations per step. Results go to `benchmark-results.json` and the CI job summary.

Speed depends on the machine, so it is only reported. The work counters do not, so they
are checked against `benchmark-baseline.json`: a run fails when one grows more than 25%,
which catches an accelerator that stops engaging or a scheduler doing redundant work.
After an optimisation, lower the baseline to the new values.
