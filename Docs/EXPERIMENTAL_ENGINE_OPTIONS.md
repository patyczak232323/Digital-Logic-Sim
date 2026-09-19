# Experimental engine options

The options in `Preferences -> EXPERIMENTAL` affect accelerators and diagnostics, not the core engine selection. Rewired's deterministic runtime is always the simulation engine.

## Native C fast engine

- **Off** — use the existing DynamicMethod JIT/LUT combinational fast path.
- **NAND only** — Native C may execute eligible acyclic compiled blocks containing only NAND primitives.
- **All supported** — Native C may execute any acyclic primitive set supported by the native ABI.

Cyclic feedback/register blocks are not sent to Native C; they remain on the deterministic engine / Feedback JIT path.

## Engine diagnostics

Shows the active Rewired engine, Feedback JIT availability, Native C status, engine-step count, C/JIT evaluation counters and latest engine event.

## C/JIT cross-check

For an eligible acyclic block:

1. Native C computes the output
2. DynamicMethod JIT computes the same output
3. outputs are compared bit-for-bit
4. a mismatch records `native-c-jit-mismatch`
5. the JIT result remains authoritative

This mode is for validation, not performance measurement.
