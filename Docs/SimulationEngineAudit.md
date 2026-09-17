# Simulation engine audit

Date: 2026-09-16  
Scope: `main`, deterministic runtime introduced after the upstream import.

## Result

The runtime remains an event-driven, deterministic delta-cycle simulator, but its
hot path now operates on a compiled dense netlist. The editable `SimChip` hierarchy
is traversed only when the topology changes.

## Findings and fixes

### 1. Hash-based scheduling in the hot path — fixed

Every signal and gate transition previously performed several `HashSet` and
`Dictionary` operations. Large CPUs amplify that constant cost across millions of
small propagation events.

The topology compiler now assigns dense integer indices to pins and combinational
chips. Runtime scheduling uses integer queues, precomputed arrays, and `bool[]`
membership flags.

Adjacency is stored in compressed sparse row (CSR) form: one offsets array and one
contiguous edge array in each direction. This removes the managed array previously
allocated for every pin and improves locality while walking fan-out and driver lists.

### 2. Repeated multi-driver resolution — fixed

When many outputs changed together and drove the same target, the old queue resolved
that target once per changed source. Every resolution rescanned every driver, making
the work quadratic in the number of simultaneous drivers.

The new queue stores unique target indices. All source changes in the same wave are
coalesced, and the target's complete driver set is resolved once. Single-driver nets
use a direct-copy fast path.

### 3. Repeated external-pin address lookup — fixed

Each simulation step previously searched the chip hierarchy for every user-controlled
input and used exceptions as a normal edit-race fallback. Bindings are now cached and
rebuilt only when the topology or input-instance list changes.

### 4. Steady-state allocations — fixed

Queue and work-list capacity is reserved during topology compilation. The normal
simulation step reuses all scheduling buffers and does not create collections.

### 5. CI did not run on `main` pushes — fixed

The regression workflow listened only to the old feature branch. It now also runs on
`main`.

### 6. Oscillator work scaled with the whole project — fixed

The global delta-cycle limit is intentionally proportional to the number of gates so
very deep valid paths can settle. That allowed a tiny oscillating loop inside a large
CPU project to consume a large amount of work before the global limit fired. Each
gate now also has a per-settle evaluation budget tracked with allocation-free epochs.
A local oscillator is stopped after 256 evaluations regardless of unrelated project
size, while ordinary deep acyclic paths remain unaffected.

### 7. Empty editor input snapshots could crash the simulation — fixed

Null input arrays and temporarily missing editor pins are now treated as absent
inputs during topology rebinding and state application. This covers the short-lived
main-thread/simulation-thread mismatch that can occur while editing a project.

## Correctness invariants retained

- Gate outputs are staged and committed simultaneously within a delta cycle.
- Custom-chip boundaries have zero simulated delay.
- Sequential components advance at most once per simulation step, after the first
  combinational settle and before the second.
- Conflicting active drivers resolve deterministically low and can emit diagnostics.
- Non-convergent combinational loops stop at a bounded delta-cycle limit.
- Power-on feedback seeding is deterministic and independent of creation order.
- Save data, chip descriptions, and existing GUI code are unchanged.

## Verification

`python3 Tools/SimulationRegression/regression.py` covers NAND truth tables, hierarchy
depth 0–8, long chains, 100 parallel custom-chip instances, SR/D latches, 10,000 DFF
cycles, 100,000 clocks for 4/8/16-bit counters, flat-versus-nested equivalence,
creation-order and restart determinism, oscillator bounding, deterministic driver
contention, shared-target coalescing, CSR integration guards, and local oscillator
bounding in a 10,000-gate unrelated netlist.

Structural benchmark results from the suite:

| Workload | Compiled runtime model | Previous-work reference | Ratio |
|---|---:|---:|---:|
| One active gate in a 10,000-gate bank, 2,000 transitions | 2,000 gate evaluations | 20,000,000 full-sweep evaluations | 0.000100 |
| 2,048 drivers changing into one shared target | 2,048 driver scans | 4,194,304 repeated scans | 0.000488 |

These are operation-count checks, not Unity wall-clock claims. A release-build Unity
Profiler capture on representative user CPU projects remains the correct final
measurement of frame time.

## Remaining risks

- Very large topology edits still require a full netlist rebuild; this is intentional
  and occurs outside steady-state propagation.
- Deliberately oscillating combinational loops are bounded rather than modeled as an
  analog oscillator.
- The regression harness is a solver-level executable specification. GitHub CI does
  not currently launch the Unity Editor, so a Unity compile/build job would provide an
  additional integration layer when a Unity license runner is available.
