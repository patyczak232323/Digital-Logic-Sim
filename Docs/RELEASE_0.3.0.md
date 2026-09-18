# Digital Logic Sim Rewired v0.3.0

Release candidate prepared from the current `main` development line.

## Core runtime

- restored compatibility-first routing for stateful and feedback-heavy projects
- retained the fast deterministic/JIT/LUT path for proven pure combinational graphs
- live structural edits conservatively use compatibility timing until the root is rebuilt
- compatibility reason and execution counters are exposed for diagnostics
- paused inspection synchronization and topology recovery guards are preserved

## Experimental native C backend

A native C evaluator is available for the same pure acyclic graphs already accepted by the combinational JIT.

Preferences: Off / NAND only / All supported.

The native backend never bypasses the compatibility classifier.

An optional C/JIT cross-check runs both implementations and compares outputs bit-for-bit. On divergence, the JIT result remains authoritative.

## Diagnostics retained from main

- hot-chip profiler
- raw engine benchmark
- logic-analyzer probes
- transition-compressed waveform capture
- deterministic replay
- non-convergence summaries
- combinational Custom Chip test runner

These diagnostics remain useful on the fast path. Compatibility-mode projects intentionally prioritize original timing behaviour over deterministic-runtime instrumentation.

## Versioning

- Rewired release: **0.3.0**
- upstream DLS project-format version: **2.1.6**
- earliest compatible upstream project version remains unchanged

## Benchmark reference

Controlled GitHub Actions / .NET 8 microbenchmark, 4,096 NAND DAG, 20,000 evaluations, 7-round median:

- Sebastian-style StepChip: ~1099 ms
- compact managed interpreter: ~118 ms
- native C: ~76 ms
- DynamicMethod JIT: ~62 ms

The JIT result is roughly 17.7x faster than the Sebastian-style core baseline in this microbenchmark. Native C remains experimental because DynamicMethod JIT was faster in the stable median test on that environment.

## Safety defaults

For existing projects:

- experimental Native C: Off
- engine diagnostics: Off
- C/JIT cross-check: Off

Feedback, latches, DFFs, registers, counters, clocks and RAM are not forced through the new fixed-point fast path.

## Release gate

The release branch must pass:

1. Engine Audit regressions
2. simulation runtime compile audit
3. executable runtime tests
4. whole-repository C# syntax audit
5. full simulation stress regression
6. 8-bit computer behavioural regression
7. generated computer netlist build/verification
8. whole-program regression
9. native C differential tests

A full Unity Editor/player build is still a separate release step because the repository does not currently have a licensed Unity build runner in CI.
