# Rewired Documentation

Documentation for **Rewired**, the digital logic simulator, circuit editor and RHDL hardware description environment.

## Start here

- **[Getting Started](GETTING_STARTED.md)** — download, launch and build a first circuit.
- **[RHDL Guide](RHDL_GUIDE.md)** — RHDL v0.6 syntax and hardware-generation workflow.
- **[Examples](../Examples/RHDL/README.md)** — small circuits, RAM4x4 and CPU4.

## Simulation engine

- **[Project Principles](PROJECT_PRINCIPLES.md)** — architectural rules, including that original Digital Logic Sim engine compatibility is not a goal.
- **[Simulation Diagnostics](SIMULATION_DIAGNOSTICS.md)** — diagnostics, debugging and inspection tools.
- **[Rewired Regression Testing](COMPATIBILITY_TESTING.md)** — golden traces, sequential/built-in tests and live-vs-JIT/LUT parity.
- **[Native JIT](NATIVE_JIT.md)** — native acceleration for suitable circuit regions.
- **[Persistent FULL LUT Cache](PERSISTENT_FULL_LUT_CACHE.md)** — combinational LUT caching and persistence.

## Releases

- **[Rewired v0.3.0](RELEASE_0.3.0.md)**

The latest downloadable binaries are published under the repository's GitHub Releases page.

## Project scope

Rewired is an independent digital logic simulator derived from Sebastian Lague's Digital Logic Sim. It targets digital electronics education, logic-gate experimentation, CPU design, computer architecture and larger nested digital circuits.

Compatibility with Sebastian's original simulation engine is **not a project goal**. Rewired defines its own semantics and may intentionally diverge whenever that improves correctness, determinism, performance or architecture. Regression tests protect Rewired's own behavior and parity between its execution backends.
