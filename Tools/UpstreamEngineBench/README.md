# Sebastian Lague upstream engine benchmark

Reference upstream commit:
`SebLague/Digital-Logic-Sim@7aeb66ddff44f916ff7fafb043ab4476ef4409fe` (2.1.6 era).

This standalone harness mirrors the hot-path semantics used by the original engine:
- `Simulator.StepChip`,
- relevant cases of `ProcessBuiltinChip`,
- `SimChip.Sim_PropagateInputs/Outputs`,
- the single-driver path of `SimPin.ReceiveInput`.

The 4096-NAND benchmark represents the steady-state path after the original
`StepChipReorder` has established a valid traversal order. It deliberately uses the same
logical random DAG shape, node count, iteration count, round count, and median statistic as
the native/JIT benchmark.

It is not a Unity player benchmark. Unity/Mono overhead outside the simulation core is not
included, and the harness runs on .NET 8 so the absolute timing should be treated as a
relative core-engine comparison.
