# Digital Logic Sim Rewired

**Digital Logic Sim Rewired** is an independently maintained fork of Sebastian Lague's Digital Logic Sim. It keeps the familiar editor and project format while adding a compatibility-first simulation runtime, acceleration for safe combinational logic, diagnostics and regression tooling.

## Release status

Source release candidate: **v0.3.0**

Latest published binary release: **v0.2.0**

The upstream project/save format remains **DLS 2.1.6**. Rewired's own release number is intentionally separate so existing project compatibility is not changed just to version the fork.

## Simulation architecture

Rewired 0.3.0 uses two execution paths.

### Compatibility path

Projects that require timing/state semantics which cannot be represented as a pure combinational function use the original Sebastian-style one-pass-per-tick simulator.

This includes, conservatively:

- feedback loops
- NAND-built latches and flip-flops
- clocks and pulse sources
- RAM and other stateful built-ins
- graphs that cannot be proven safe for combinational acceleration
- live structural edits until the root is rebuilt

This path exists specifically to preserve observable behaviour of existing Digital Logic Sim computers and storage circuits.

### Fast combinational path

Pure, acyclic, single-driver combinational graphs can use the Rewired fast engine:

- compiled netlist topology
- event-driven dirty propagation
- persistent FULL LUT cache
- DynamicMethod native JIT
- optional experimental native C backend

The compatibility classifier runs before the accelerators. Experimental options cannot force a stateful/feedback project onto the combinational fast path.

## Experimental preferences

`MENU -> PREFERENCES -> EXPERIMENTAL`

Available controls:

- **Native C fast engine**: Off / NAND only / All supported
- **Engine diagnostics**: engine path, compatibility reason and execution counters
- **C/JIT cross-check**: evaluates both eligible implementations, reports mismatches and keeps the JIT result authoritative

All experimental options default to **Off** for new and existing projects.

## Diagnostics

The diagnostics panel remains available at `MENU -> SIM DIAGNOSTICS` and includes hot-chip profiling, raw benchmark, logic-analyzer probes, waveform capture, deterministic replay and convergence summaries.

See `Docs/SIMULATION_DIAGNOSTICS.md`.

## Performance reference

A controlled .NET 8 core benchmark using a 4,096-NAND acyclic graph, 20,000 evaluations per round and a 7-round median measured approximately:

| Engine | Median time |
| --- | ---: |
| Sebastian-style StepChip baseline | 1099 ms |
| compact managed interpreter | 118 ms |
| experimental native C | 76 ms |
| DynamicMethod JIT | 62 ms |

The DynamicMethod JIT was about **17.7x faster** than the Sebastian-style core baseline in that microbenchmark. These are not full Unity player numbers and should not be treated as a guarantee of end-user steps/s.

## Compatibility

Existing Digital Logic Sim projects are intended to remain compatible. Stateful and feedback-heavy projects prefer the compatibility path rather than silently changing their timing model.

The fork preserves DLS 2.1.6 project-format semantics. Rewired-specific preference fields are additive; older project files deserialize them as disabled.

## Validation

Release-gate CI covers engine audits, runtime compilation/tests, whole-repo C# syntax parsing, propagation stress tests, 8-bit computer tests, generated computer netlist verification, whole-program regressions and native C differential tests.

See `Docs/RELEASE_0.3.0.md`.

## Credits and license

Original Digital Logic Sim by **Sebastian Lague**.

Rewired fork maintained by **@patyczak232323**.

Licensed under the MIT License. See `LICENSE`.
