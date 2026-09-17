# Persistent Full LUT Cache

Custom **combinational** chips can use a full binary lookup table (LUT).

## Modes

- `AUTO` - use a full LUT whenever the chip is safe to cache.
- `NORMAL` - disable LUT caching for the chip.
- `FULL` - request a full LUT whenever it is mathematically safe.

A feedback/stateful circuit (for example an SR latch, D latch, D flip-flop or register built from feedback gates) cannot be represented correctly by a table keyed only by the current external inputs. Such a chip therefore stays on the normal deterministic simulation path. This is not treated as a simulation/cache failure: any safe combinational custom chips nested inside the stateful hierarchy can still use their own LUTs (hybrid caching).

## Persistent cache

Generated LUTs are stored outside the chip JSON in the project's runtime data folder:

`Projects/<ProjectName>/Cache/FullLUT/<ChipName>.dlscache`

The file contains a versioned header, a SHA-256 logic fingerprint and the dense `uint[]` LUT. On load, a matching file is read directly into RAM. Missing, stale or corrupt files are rebuilt automatically.

The fingerprint includes the chip's functional topology and recursively includes custom-chip dependencies. Changing nested logic therefore invalidates affected parent LUTs without relying on timestamps.

## Non-blocking build/load

LUT file loading, exhaustive LUT generation and disk writes are serialized on a background worker. The live simulator continues to use its normal flattened deterministic topology until a LUT is ready. When the LUT finishes, the deterministic simulator notices the cache generation change and rebuilds its compiled topology so the ready LUT can be used.

This avoids the old behaviour where opening/saving a chip could block the whole program while all input combinations were evaluated or a large LUT was read/written.

## Limits

- Maximum binary input bits per full LUT: 24
- Maximum estimated LUT RAM per chip: 512 MiB
- High-impedance / tristate input cases are not part of the binary LUT and fall back to normal combinational evaluation.
