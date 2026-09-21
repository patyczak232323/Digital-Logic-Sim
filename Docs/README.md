# Rewired Documentation

Documentation for **Rewired**, the digital logic simulator, circuit editor and RHDL hardware description environment.

## Start here

- **[Getting Started](GETTING_STARTED.md)** — download, launch and build a first circuit.
- **[RHDL Guide](RHDL_GUIDE.md)** — RHDL v0.6 syntax and hardware-generation workflow.
- **[Examples](../Examples/RHDL/README.md)** — small circuits, RAM4x4 and CPU4.

## Simulation engine

- **[Simulation Diagnostics](SIMULATION_DIAGNOSTICS.md)** — diagnostics, debugging and inspection tools.
- **[Compatibility Testing](COMPATIBILITY_TESTING.md)** — golden traces, sequential/built-in tests and live-vs-JIT/LUT parity.
- **[Native JIT](NATIVE_JIT.md)** — native acceleration for suitable circuit regions.
- **[Persistent FULL LUT Cache](PERSISTENT_FULL_LUT_CACHE.md)** — combinational LUT caching and persistence.

## Releases

- **[Rewired v0.3.0](RELEASE_0.3.0.md)**

The latest downloadable binaries are published under the repository's GitHub Releases page.

## Project scope

Rewired is an independent digital logic simulator derived from Sebastian Lague's Digital Logic Sim. It targets digital electronics education, logic-gate experimentation, CPU design, computer architecture and larger nested digital circuits.

Project/file compatibility with Digital Logic Sim remains useful, but exact timing and traversal-order behaviour are not guaranteed to match the original simulator. The compatibility test system makes intended Rewired behavior explicit and can validate specific Digital Logic Sim circuits against recorded step-by-step reference traces.
