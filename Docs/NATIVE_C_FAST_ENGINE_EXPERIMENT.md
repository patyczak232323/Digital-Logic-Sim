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
20,000 evaluations per round. Final comparison uses the median of 7 rounds and alternates
execution order to reduce turbo/thermal/scheduler bias:

| implementation | median time |
| --- | ---: |
| managed compact interpreter | 113.743 ms |
| DynamicMethod JIT (current fast path style) | 61.720 ms |
| optimized native C | 75.628 ms |

Observed ranges were 113.357–195.075 ms (managed interpreter), 61.612–71.562 ms
(DynamicMethod JIT), and 75.510–77.558 ms (native C).

Native C was about **1.50x faster** than the managed interpreter, but the current
DynamicMethod JIT had about **1.23x the throughput of native C** on this runner.

Earlier native C layouts measured 151.156 ms and 96.339 ms in single-run tests.
Compact bytecode plus a direct connected-NAND hot loop reduced the native result to
a stable ~75–78 ms range.

This does not prove the result will be identical under Unity's Mono runtime. The native
backend therefore remains opt-in until an actual Unity build is benchmarked.
