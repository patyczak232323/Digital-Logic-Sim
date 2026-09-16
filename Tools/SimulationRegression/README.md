# Simulation propagation regression

`regression.py` is an executable solver-level specification for the deterministic runtime.
It reproduces the old single-sweep SR-latch ordering failure and then exercises the replacement delta-cycle model with:

- NAND truth tables through hierarchy depths 0-8,
- a 100-gate chain,
- 100 parallel custom-chip instances,
- SR and D latches,
- 10,000 edge-triggered DFF cycles,
- 4-, 8-, and 16-bit counters for 100,000 clocks each,
- flat-vs-nested equivalence,
- creation-order and restart determinism,
- bounded handling of an oscillator/non-convergent loop,
- deterministic multi-driver conflict handling,
- coalescing of simultaneous updates to a shared target.

The runtime compiles the editable hierarchy into a dense, indexed netlist whenever
the topology changes. Per-step propagation uses precomputed fan-in/fan-out arrays,
integer queues, and boolean dirty flags. It does not perform hash lookups or allocate
collections in the steady-state hot path. Single-driver targets use a direct-copy fast
path; multi-driver targets are resolved once per propagation wave.

The suite also reports two structural performance checks:

- one changed input in a bank of 10,000 independent gates evaluates only the affected gate,
- 2,048 simultaneous drivers of one net cause one target resolution rather than 2,048 repeated full fan-in scans.

The GitHub Actions workflow also checks that the save/description format and existing GUI implementation are not modified by the simulation fix.
