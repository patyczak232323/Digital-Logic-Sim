# Architecture

## Goals

The rewrite is organized around four constraints:

1. simulation results do not depend on Unity frame order;
2. the core can be tested and benchmarked without Unity;
3. external devices enter the simulation only at a step boundary;
4. editor, persistence, and platform code cannot mutate simulation internals directly.

## Layers

### Rewired.Core

Owns signals, components, circuit topology, scheduling, and deterministic state transitions. It has no Unity or operating-system references.

A simulation step has three phases:

1. settle existing combinational work;
2. tick clocks, stateful devices, and external adapters once;
3. settle resulting combinational work.

A non-converging circuit fails with an explicit delta-cycle limit instead of depending on traversal order forever.

### Rewired.Platform

Implements interfaces owned by the core. The Windows COM endpoint is one example. It owns its worker thread and queues, while the simulation only observes bytes during ByteSerialAdapter.Tick.

### Rewired.Unity

Owns the Unity lifecycle and presentation integration. Unity frames schedule simulation steps, but do not define logic propagation semantics.

### Future application layer

The next milestone adds immutable project commands, editor tools, undo/redo, save DTOs, and circuit compilation. It will translate user actions into core operations.

## Dependency rules

- Core must not reference UnityEngine.
- Core must not call operating-system APIs.
- Platform assemblies must implement core-owned interfaces.
- UI must not hold mutable references to netlist internals.
- Serializable DTOs must be separate from runtime objects.
- Every external event must be timestamped or injected at a known simulation step.
- No global static simulator state.

## Performance direction

Correctness comes first. Once the graph model is stable, the runtime can compile it into dense arrays:

- integer component and signal IDs;
- contiguous pin/value buffers;
- fan-out ranges instead of object graphs;
- dirty-component queues;
- optional specialized executors behind the same semantics.

Optimizations must pass the same behavioral tests as the reference scheduler.
