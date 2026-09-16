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
- bounded handling of an oscillator/non-convergent loop.

The GitHub Actions workflow also checks that the save/description format and existing GUI implementation are not modified by the simulation fix.
