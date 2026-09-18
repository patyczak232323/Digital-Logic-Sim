# Digital Logic Sim Rewired v0.3.0

Release candidate based on the current `main` development line.

## Core runtime

- all projects use the Rewired deterministic engine
- removed the legacy Sebastian timing fallback
- pure combinational Custom Chips can use LUT / DynamicMethod JIT
- cyclic feedback Custom Chips can use Feedback JIT
- structural edits retain deterministic topology recovery
- paused inspection synchronization and feedback-state materialization are preserved

## Registers and feedback

The release keeps the fully Rewired register path rather than routing latches/DFFs/registers through the original simulator.

Feedback JIT deliberately accepts cyclic gate graphs. It uses separate current/next buffers per sweep, synchronizes only after the live deterministic network has settled, and materializes authoritative state before deoptimization or inspection.

Runtime tests include:

- cross-coupled NAND latch
- unchanged-input zero-sweep feedback fast path
- feedback state materialization
- active/dormant feedback ownership
- 8-bit feedback latch bank
- nested NAND DFF register writes
- 4/8/16-bit counters
- generated 8-bit computer netlist

## Experimental Native C backend

Native C remains available for eligible **acyclic combinational** programs only:

- Off
- NAND only
- All supported

Feedback/stateful acceleration remains the responsibility of the Rewired deterministic engine and Feedback JIT.

An optional C/JIT cross-check compares both combinational implementations and retains JIT output as authoritative on validation.

## Versioning

- Rewired release: **0.3.0**
- upstream DLS project-format version: **2.1.6**

## Benchmark reference

Controlled GitHub Actions / .NET 8 microbenchmark, 4,096 NAND DAG, 20,000 evaluations, 7-round median:

- Sebastian-style StepChip: ~1099 ms
- compact managed interpreter: ~118 ms
- Native C: ~76 ms
- DynamicMethod JIT: ~62 ms

## Release gate

The release branch must pass:

1. Engine Audit regressions
2. simulation runtime compile audit
3. executable runtime tests
4. whole-repository C# syntax audit
5. deterministic propagation stress regression
6. 8-bit computer behavioural regression
7. generated computer netlist build/verification
8. whole-program regression
9. Native C differential tests

A licensed Unity Editor/player build is still a separate release step because the repository does not currently have a Unity build runner in CI.
