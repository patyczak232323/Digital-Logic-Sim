# Digital Logic Sim Rewired

A fork of Sebastian Lague's **Digital Logic Sim** with a rewritten deterministic simulation engine focused on reliable propagation, nested custom chips, feedback-heavy circuits, and large CPU-scale designs.

## Download

The supported binaries are published in GitHub Releases:

- **Windows x64:** `DLSRewired-Windows-x64.zip`
- **Linux x86_64:** `DLSRewired-Linux-x86_64.zip`

Latest release: **v0.1.0**

https://github.com/patyczak232323/Digital-Logic-Sim/releases/tag/v0.1.0

## Main changes

- deterministic event-driven simulation engine
- improved propagation through deeply nested Custom Chips
- fixes for feedback loops, registers, counters and flip-flops
- isolated state for parallel instances of the same custom chip
- better behaviour on large circuits and CPU-scale projects
- Linux/Windows standalone compatibility fixes

## Compatibility

The project keeps the original Digital Logic Sim editor and project format wherever possible. Existing projects are intended to remain compatible, but **v0.1.0 is still an early release** and unusual circuits may expose bugs.

## Source

This repository contains the Unity project used to build the release. Development-only diagnostic tools and temporary CI workflows are intentionally excluded from the release branch.

## Credits and license

Based on [Sebastian Lague's Digital Logic Sim](https://github.com/SebLague/Digital-Logic-Sim).

Licensed under the MIT License. See `LICENSE`.
