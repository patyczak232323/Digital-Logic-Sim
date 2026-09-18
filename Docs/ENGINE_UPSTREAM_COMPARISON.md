# Engine comparison: Sebastian upstream vs new fast engine

Reference upstream:
`SebLague/Digital-Logic-Sim@7aeb66ddff44f916ff7fafb043ab4476ef4409fe`.

## Correctness suite

The upstream-equivalent harness ran the same categories used for the new fast engine:

- NAND truth table: PASS
- tri-state buffer: PASS
- 8-bit split/merge roundtrip: PASS
- 5,000 randomized evaluations of a 2,048-node acyclic NAND DAG: PASS
- cross-coupled NAND latch: PASS, resolved to 1/0 using upstream immediate traversal semantics

The fast-engine/native suite also passes its matching differential tests and all Engine Audit
regressions.

## 4,096-NAND steady-state benchmark

GitHub Actions Ubuntu runner, .NET 8 harnesses, 20,000 evaluations per round, 7 rounds,
median statistic.

| implementation | median time | approximate full-graph steps/s |
| --- | ---: | ---: |
| Sebastian upstream-style `StepChip` | 1099.062 ms | 18,197 |
| compact managed interpreter | 117.825 ms | 169,743 |
| optimized native C | 76.228 ms | 262,371 |
| DynamicMethod JIT | 62.239 ms | 321,342 |

Relative to Sebastian upstream-style `StepChip` on this benchmark:

- compact managed interpreter: ~9.33x faster
- optimized native C: ~14.42x faster
- DynamicMethod JIT: ~17.66x faster

The upstream benchmark measured about 74.5 million NAND evaluations/s while still paying
the original object/pin propagation and per-tick traversal overhead.

## Methodology and limits

The upstream harness mirrors the hot-path structure of upstream
`Simulator.StepChip`, relevant `ProcessBuiltinChip` cases,
`SimChip.Sim_PropagateInputs/Outputs`, and the single-driver branch of
`SimPin.ReceiveInput`.

For the acyclic benchmark the graph is already in the steady-state reverse visitation order
that upstream uses after `StepChipReorder`. This avoids unfairly charging the one-time order pass
on every benchmark iteration.

The logical graph size, random DAG construction style, iteration count and number of benchmark
rounds match the fast-engine benchmark. Internal representations intentionally differ because
that is the performance difference being measured: upstream uses object/pin propagation while
the fast engine collapses the combinational graph into a compact executable program.

These are simulation-core microbenchmarks, not full Unity player measurements. Absolute numbers
under Unity/Mono can differ, so the ratios should be confirmed in a built player before treating
them as end-user FPS/steps-per-second guarantees.
