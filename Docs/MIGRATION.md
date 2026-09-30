# Rewrite roadmap

The rewrite is developed on rewrite/clean-architecture. The main branch remains the reference for currently released behavior until the replacement reaches feature parity.

No old source tree is carried into this branch. Existing projects will be treated as input data for a future importer, not as a reason to couple the new runtime to old classes.

## Milestone 1 — foundation

- pure C# deterministic core
- basic logic primitives
- test runner
- Unity runtime host
- byte serial adapter
- Windows COM transport

## Milestone 2 — circuit model and editor

- stable component and pin identifiers
- wires, fan-out compilation, contention rules
- placement, selection, movement, and deletion tools
- command-based undo/redo
- serial component inspector with COM name and baud rate

## Milestone 3 — persistence and custom chips

- versioned save DTOs
- atomic project writes
- custom-chip definitions and instances
- explicit migration/import pipeline
- global library separated from project state

## Milestone 4 — RHDL

- parser and diagnostics independent from UI
- typed intermediate representation
- lowering into the normal circuit graph
- source-to-component traceability

## Milestone 5 — performance and diagnostics

- dense compiled netlist
- dirty scheduling
- profiling and waveforms
- deterministic replay including external serial events
- optional JIT and LUT acceleration validated against the reference scheduler

## Acceptance rule

A milestone is complete only when its behavior is covered by non-Unity tests and the Unity integration compiles. Performance work may not change observable circuit semantics.
