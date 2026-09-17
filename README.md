# Digital Logic Sim Rewired

**Digital Logic Sim Rewired** is an independently maintained fork of Sebastian Lague's Digital Logic Sim, focused on making the simulation engine more deterministic, reliable and scalable while preserving the familiar editor and project format.

The rewritten runtime is designed for circuits that are particularly demanding for logic simulators: deeply nested Custom Chips, feedback loops, registers, counters, flip-flops, large fan-out networks and CPU-scale designs.

## Downloads

Prebuilt releases are available for:

- **Windows x64:** `DLSRewired-Windows-x64.zip`
- **Linux x86_64:** `DLSRewired-Linux-x86_64.zip`

Latest release: **v0.1.0**

https://github.com/patyczak232323/Digital-Logic-Sim/releases/tag/v0.1.0

## Highlights

- rewritten deterministic event-driven simulation engine
- reliable propagation through deeply nested Custom Chips
- improved handling of feedback-heavy sequential circuits
- isolated per-instance state for repeated custom chips
- improved behaviour on large circuits and CPU-scale projects
- compatibility fixes for Linux and Windows standalone builds
- original editor workflow and project format retained wherever possible

## Compatibility

Existing Digital Logic Sim projects are intended to remain compatible. The current release is still early software, so unusual or extremely large circuits may expose bugs.

## Repository policy

The canonical project is maintained by **@patyczak232323**. External contributors should use forks and pull requests. Direct write access to the canonical repository is not intended for third parties.

## Credits and license

Based on [Sebastian Lague's Digital Logic Sim](https://github.com/SebLague/Digital-Logic-Sim).

Licensed under the MIT License. See `LICENSE`.
