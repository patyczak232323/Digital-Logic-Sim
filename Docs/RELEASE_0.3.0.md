# Rewired v0.3.0

Rewired v0.3.0 is the current public release of the Rewired digital logic simulator.

This release focuses on the Rewired simulation runtime, diagnostics, acceleration and the RHDL hardware-description workflow.

## Highlights

- deterministic event-driven simulation
- compiled circuit/netlist topology
- dirty-gate scheduling and fixed-point settling
- improved deeply nested Custom Chip propagation
- native combinational JIT
- feedback JIT for supported cyclic gate networks
- persistent FULL LUT caching for suitable combinational circuits
- state materialization for accelerated regions
- simulation diagnostics and profiling
- waveform recording
- deterministic replay
- non-convergence reporting
- unified `RewiredEngine` integration boundary
- RHDL Studio with the current **RHDL v0.6** frontend
- curated RHDL examples including RAM4x4 and CPU4
- automated Windows x64 and Linux x86_64 release builds

## RHDL v0.6

RHDL v0.6 describes concurrent hardware and lowers into ordinary Rewired topology.

The language supports:

- 1, 4 and 8-bit signals
- Boolean logic
- arithmetic and comparisons
- indexing and slicing
- `join(...)`
- `choose(...)`
- reusable components
- explicit structural wiring with `connect`
- stateful/feedback topology built from normal components

See `Docs/RHDL_GUIDE.md` for the current syntax.

## Examples

The repository intentionally keeps the current example set compact:

- AND
- Half Adder
- Full Adder
- MUX4
- D Flip-Flop
- 4-bit Register
- RAM4x4
- CPU4

CPU4 is the largest processor example and uses a 4-bit datapath.

## Compatibility

Rewired is derived from Sebastian Lague's Digital Logic Sim but is maintained as an independent simulator.

Project/file-format compatibility is useful and generally intended, but exact traversal-order, race-condition and timing-dependent behaviour is not guaranteed to match the original runtime.

## Downloads

Prebuilt release packages:

- Windows x64: `DLSRewired-Windows-x64.zip`
- Linux x86_64: `DLSRewired-Linux-x86_64.zip`
