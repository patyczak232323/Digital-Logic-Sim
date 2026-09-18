# Native C fast-engine experiment

Branch: `exp/native-c-fast-engine`

This experiment leaves the upstream-compatible/stateful simulation path unchanged.
Only pure, acyclic, single-driver combinational programs already accepted by
`CombinationalJitCompiler` are eligible.

## Runtime selection

The existing DynamicMethod JIT remains the default.

To opt in to the C backend on Linux:

```bash
Tools/NativeFastEngine/build_linux.sh
DLS_NATIVE_FAST=1 <launch-the-game>
```

If the library is missing, its ABI does not match, or a native evaluation fails, the
same compiled DynamicMethod program is retained and used as the fallback.

## CI differential tests

The native test harness checks:
- NAND truth table,
- tri-state behaviour,
- 8-bit split/merge roundtrip,
- 5,000 randomized evaluations of a 2,048-node NAND DAG against a managed reference,
- all existing engine-audit regressions.

## Benchmark

GitHub Actions Ubuntu runner, .NET 8 benchmark harness, 4,096-node acyclic NAND DAG,
20,000 evaluations:

| implementation | time |
| --- | ---: |
| managed compact interpreter | 165.729 ms |
| DynamicMethod JIT (current fast path style) | 71.104 ms |
| optimized native C | 75.790 ms |

Native C was about **2.19x faster** than the managed interpreter, but about **6.6% slower**
than DynamicMethod JIT on this runner.

Earlier C layouts measured 151.156 ms and 96.339 ms. Compact bytecode plus a direct
NAND hot loop reduced that to 75.790 ms.

This does not prove the result will be identical under Unity's Mono runtime. The native
backend therefore remains opt-in until an actual Unity build is benchmarked.
