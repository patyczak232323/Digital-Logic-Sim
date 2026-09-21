# Compatibility testing

Rewired is a separate simulator derived from Digital Logic Sim. Project/file compatibility is useful, but exact legacy traversal-order side effects are not part of the Rewired contract.

The compatibility system therefore tests two things separately:

1. **Rewired semantic stability** — the same circuit and input sequence must keep producing the same externally visible step-by-step behavior.
2. **Reference compatibility** — when a Digital Logic Sim circuit has a known-good trace, Rewired can compare every output at every simulation step against that trace.

## Test layers

### 1. Gate-level solver regression

`Tools/SimulationRegression/regression.py` is the executable specification for the deterministic solver.

It covers:

- NAND truth tables
- long propagation chains
- large fanout
- deeply nested Custom Chips
- randomized combinational DAGs
- SR latches
- D latches
- edge-triggered D flip-flops
- 4/8/16-bit counters
- flat versus nested sequential circuits
- restart determinism
- feedback/oscillation guards
- multiple drivers and target coalescing

Run:

```bash
python3 Tools/SimulationRegression/regression.py
```

### 2. Built-in compatibility contract

`Tools/CompatibilityRegression/regression.py` specifies the timing/state contract for the built-in components that cannot be represented as ordinary combinational NAND networks.

It covers:

- Clock cadence
- Pulse rising-edge detection, duration and retrigger behavior
- RAM reset/write/read semantics
- ROM byte ordering and address masking
- golden-trace comparison
- live-versus-accelerated trace parity contract
- static verification that the C# live-engine suite still contains all required cases

Run:

```bash
python3 Tools/CompatibilityRegression/regression.py
```

This suite runs automatically in GitHub Actions together with the other regression suites. A separate Unity CI job also compiles the project and runs the real-engine self-test before the Linux compatibility build.

### 3. Real Rewired engine self-test

`Assets/Scripts/Simulation/CompatibilitySelfTestSuite.cs` builds real `SimChip` graphs and runs them through `RewiredEngine`.

Current live cases are:

- NAND truth table and propagation
- tri-state disconnect/reconnect
- Pulse
- Clock
- ROM
- RAM
- SR feedback latch
- deep Custom Chip nesting
- live solver versus JIT/FULL LUT parity
- live solver versus feedback-JIT parity

The runner intentionally advances **one actual Rewired simulation step per vector**. Stateful components keep their state between vectors.

From an installed Unity 6000.0.46f1 editor, run the suite headlessly from the repository root:

```bash
Unity \
  -batchmode \
  -quit \
  -projectPath . \
  -executeMethod DLS.EditorTools.CompatibilitySelfTestCommand.Run \
  -logFile -
```

Use the platform-specific Unity executable path if `Unity` is not in `PATH`.

A failed compatibility case throws an exception, so the command exits as a failed CI/build step. In GitHub Actions, `REWIRED_RUN_COMPATIBILITY=1` activates `CompatibilityBuildPreprocessor`, which runs the same suite before the Unity Linux build; a failing case therefore blocks that CI job.

## Golden traces

`CompatibilityTestRunner.RunGolden(...)` compares outputs after every simulation step.

A trace consists of:

- a step name
- input pin states
- expected output pin states
- optional per-output masks

Conceptually:

```text
step 0  IN=00  OUT=01
step 1  IN=01  OUT=11
step 2  IN=11  OUT=10
```

If Rewired produces a different value at step 1, the failure identifies the exact step and output instead of only reporting that the final state is wrong.

This is the preferred way to preserve a behavior observed in:

- a known-good Rewired release
- the original Digital Logic Sim
- a reduced bug-reproduction circuit

For Digital Logic Sim compatibility work, record a small deterministic input/output trace from the reference project and encode those outputs as `CompatibilityStep.ExpectedOutputs`. Do not use circuits whose expected result intentionally depends on undefined races as a correctness oracle.

## Acceleration parity

`CompatibilityTestRunner.RunAccelerationParity(...)` executes the same vector sequence twice:

1. with Custom Chip acceleration recursively disabled
2. with the normal Rewired JIT/FULL LUT/feedback-JIT selection enabled

Every externally visible output is compared step by step.

This guards the core rule:

> acceleration may change performance, but it must not change circuit behavior.

The result also reports whether cache/JIT/feedback-JIT activity was actually observed through engine diagnostics. On a platform where a particular accelerator is unavailable, output parity can still pass while reporting that no accelerated path was selected.

## Adding a regression for a bug

When a compatibility bug is found:

1. reduce it to the smallest practical circuit/input sequence;
2. reproduce the wrong behavior;
3. add a golden-trace or solver regression that fails;
4. fix the engine;
5. keep the regression permanently.

For timing/order bugs, always compare intermediate simulation steps — not only the final state.

## What compatibility does not promise

Rewired does not promise to reproduce every accidental behavior of the original engine, especially behavior caused by:

- unspecified traversal order
- ambiguous active-driver contention
- race conditions with no stable digital interpretation
- legacy initialization side effects

The suite instead makes intended Rewired semantics explicit and provides a mechanism for checking specific Digital Logic Sim circuits where a stable reference trace exists.
