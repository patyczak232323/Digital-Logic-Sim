# Rewired regression testing

Rewired is an independent simulator. **Matching Sebastian Lague's original Digital Logic Sim engine is not a goal.**

This test system protects Rewired's own semantic contract and checks that optimization backends do not change Rewired behavior.

## What is the reference?

For Rewired, the intended deterministic live simulation semantics are the reference.

The regression system checks:

- deterministic signal propagation
- sequential state behavior
- Clock and Pulse semantics
- RAM and ROM behavior
- feedback convergence and non-convergence handling
- deep Custom Chip nesting
- repeatability across restarts
- parity between the live Rewired solver and acceleration paths

The original Digital Logic Sim engine is deliberately **not** used as a correctness oracle.

## 1. Gate-level solver regression

`Tools/SimulationRegression/regression.py` is the executable specification for core gate-level and delta-cycle behavior.

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

## 2. Built-in semantic regression

`Tools/CompatibilityRegression/regression.py` specifies the intended Rewired behavior of built-in components that cannot be represented as ordinary combinational NAND networks.

It covers:

- Clock cadence
- Pulse rising-edge detection, duration and retrigger behavior
- RAM reset/write/read semantics
- ROM byte ordering and address masking
- golden-trace comparison
- live-versus-accelerated trace parity
- static verification that required C# live-engine cases remain present

Run:

```bash
python3 Tools/CompatibilityRegression/regression.py
```

## 3. Real Rewired engine self-test

`Assets/Scripts/Simulation/CompatibilitySelfTestSuite.cs` builds real `SimChip` graphs and drives them through `RewiredEngine`.

Current live cases include:

- NAND propagation
- tri-state disconnect/reconnect
- Pulse
- Clock
- ROM
- RAM
- SR feedback latch
- deep Custom Chip nesting
- live solver versus native JIT
- live solver versus FULL LUT
- live solver versus feedback JIT

The runner advances one actual Rewired simulation step per vector, so stateful components preserve state between vectors.

## Golden traces

`CompatibilityTestRunner.RunGolden(...)` compares Rewired output after every simulation step against an explicitly defined Rewired expectation.

Example:

```text
step 0  IN=00  OUT=01
step 1  IN=01  OUT=11
step 2  IN=11  OUT=10
```

If a later engine change produces a different value at step 1, the regression identifies that exact step and output.

Golden traces should come from an intentionally defined Rewired behavior or a known-good Rewired revision, not from the original Sebastian engine.

## Acceleration parity

`CompatibilityTestRunner.RunAccelerationParity(...)` executes the same vector sequence twice:

1. with Custom Chip acceleration recursively disabled
2. with the normal Rewired JIT/FULL LUT/feedback-JIT selection enabled

Every externally visible output is compared step by step.

Core rule:

> acceleration may change performance, but it must not change Rewired behavior.

The result also reports whether cache, JIT or feedback-JIT activity was actually observed.

## CI

The lightweight regression suites run in GitHub Actions.

A separate Unity CI job compiles the project and runs the real-engine self-test with `REWIRED_RUN_COMPATIBILITY=1`. A failed semantic regression therefore blocks that job.

The suite can also be run headlessly from an installed Unity 6000.0.46f1 editor:

```bash
Unity \
  -batchmode \
  -quit \
  -projectPath . \
  -executeMethod DLS.EditorTools.CompatibilitySelfTestCommand.Run \
  -logFile -
```

## Adding a regression for a bug

When a Rewired engine bug is found:

1. reduce it to the smallest practical circuit/input sequence;
2. reproduce the incorrect Rewired behavior;
3. define the intended Rewired behavior;
4. add a regression that fails;
5. fix the engine;
6. keep the regression permanently.

For timing/order bugs, compare intermediate simulation steps rather than only the final state.

## Non-goal: Sebastian engine compatibility

No regression should be added merely to force Rewired to reproduce an original Digital Logic Sim timing quirk, traversal order, race outcome or initialization side effect.

If an old project behaves differently, the question is whether Rewired's own semantics are coherent and correct — not whether the original engine produced the same accidental result.

See **[Project Principles](PROJECT_PRINCIPLES.md)**.
