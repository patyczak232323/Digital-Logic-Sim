# Experimental engine options

The options in `Preferences -> EXPERIMENTAL` are intentionally separated from the normal simulator controls.

## Native C fast engine

- **Off** — use the existing DynamicMethod JIT/LUT fast path.
- **NAND only** — native C may execute eligible compiled blocks containing only NAND primitives.
- **All supported** — native C may execute any primitive set supported by the native ABI.

Only graphs already approved as pure, acyclic and single-driver are eligible.

## Engine diagnostics

Shows the selected runtime path, compatibility classification and accelerator counters. Diagnostics may add overhead and are disabled by default.

## C/JIT cross-check

For an eligible block:

1. native C computes the output
2. DynamicMethod JIT computes the same output
3. outputs are compared bit-for-bit
4. a mismatch records `native-c-jit-mismatch`
5. the JIT result is retained as the safe authoritative output

This mode is for validation, not performance measurement.

## Compatibility guarantee

These options do not alter the compatibility classifier. A root requiring upstream timing is routed to the compatibility engine before any Native C or combinational JIT decision is made.
