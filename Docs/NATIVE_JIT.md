# Native combinational JIT

This project contains an optional native accelerator for pure Custom Chips.

`Assets/Scripts/Simulation/CombinationalJitCompiler.cs` flattens an acyclic,
single-driver combinational Custom Chip to a compact operation graph, emits IL with
`DynamicMethod`, and lets the Unity/Mono JIT compile that IL to native machine code.
The generated code operates on a compact `uint[]` scratch buffer and bypasses internal
Custom Chip recursion, gate-type dispatch and per-wire event propagation.

Runtime policy:

- Ready full LUT: preferred first.
- Pure combinational Custom Chip without a ready LUT: native JIT.
- Stateful, feedback, multi-driver or unsupported logic: deterministic simulator.
- When a chip is being inspected internally, it is deliberately expanded so internal
  pin/wire state remains visible in the editor.
- Structural runtime edits invalidate the compiled executor for the edited chip and
  all compiled ancestors.
- Platforms without `DynamicMethod`/dynamic-code support automatically fall back to
  the deterministic simulator.

The current Standalone project is configured for the Mono scripting backend, which is
the intended backend for this JIT path.
