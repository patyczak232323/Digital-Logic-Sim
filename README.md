# Digital Logic Sim Rewired

**Digital Logic Sim Rewired** is an independently maintained fork of Sebastian Lague's Digital Logic Sim with a rebuilt simulation runtime focused on reliable large-scale digital circuits while keeping the familiar editor and project format.

The project is aimed especially at designs that are difficult for traditional gate-level simulators: deeply nested Custom Chips, feedback loops, latches, flip-flops, registers, counters, large fan-out networks and complete CPU-scale circuits.

## Simulation engine

Rewired replaces the original runtime propagation model with an event-driven engine built around compiled netlist topology, dirty-gate scheduling and fixed-point settling.

Key goals of the engine are:

- correct propagation through deeply nested Custom Chips
- stable behaviour for feedback-heavy circuits such as latches and flip-flops
- deterministic normal simulation after circuit initialization
- isolated state for multiple instances of the same Custom Chip
- efficient handling of large fan-out and CPU-scale designs
- explicit convergence limits instead of silently leaving partially propagated state
- preservation of the original Digital Logic Sim editor workflow and project format wherever possible

Feedback-based storage elements do not require arbitrary gate outputs to be randomized during normal operation. Circuit initialization is treated separately from normal deterministic simulation so that gate logic itself always remains logically correct.

## Downloads

Prebuilt releases are available for:

- **Windows x64:** `DLSRewired-Windows-x64.zip`
- **Linux x86_64:** `DLSRewired-Linux-x86_64.zip`

Latest release: **v0.1.0**

https://github.com/patyczak232323/Digital-Logic-Sim/releases/tag/v0.1.0

## Compatibility

Existing Digital Logic Sim projects are intended to remain compatible. The editor, Custom Chip workflow and project format are kept as close to the original as practical while the simulation runtime is replaced underneath.

The project is still under active development, so unusual circuits are worth reporting with a minimal reproducible project.

## Repository policy

The canonical project is maintained by **@patyczak232323**. External contributors should use forks and pull requests. Direct write access to the canonical repository is not intended for third parties.

## Credits and license

Based on [Sebastian Lague's Digital Logic Sim](https://github.com/SebLague/Digital-Logic-Sim).

Licensed under the MIT License. See `LICENSE`.
