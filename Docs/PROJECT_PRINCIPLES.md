# Rewired project principles

These rules are architectural constraints for the Rewired project.

## 1. Rewired is an independent simulator

Rewired is derived from Sebastian Lague's Digital Logic Sim, but it is a separate project with its own simulation semantics, runtime architecture and development direction.

**Compatibility with the original Digital Logic Sim engine is not a project goal.**

Rewired does not have to reproduce the original engine's:

- traversal order
- race-condition outcomes
- propagation quirks
- initialization side effects
- timing-dependent behavior
- internal implementation choices

If preserving legacy behavior conflicts with correctness, determinism, performance, maintainability or a cleaner Rewired architecture, Rewired behavior takes priority.

Inherited editor/UI/project-format behavior may be retained when useful, but it must not constrain the Rewired simulation engine.

## 2. Rewired defines its own semantic contract

The canonical behavior of the project is the behavior intentionally defined and regression-tested by Rewired itself.

Tests should verify:

- deterministic signal propagation
- sequential state behavior
- Clock and Pulse semantics
- RAM and ROM behavior
- feedback convergence/non-convergence handling
- deep Custom Chip nesting
- consistency across execution backends

The original Sebastian engine must not be used as the correctness oracle.

## 3. Optimizations must preserve Rewired behavior

The live deterministic Rewired solver is the reference path for acceleration correctness.

JIT, FULL LUT, feedback JIT and future acceleration methods may change performance and implementation strategy, but must preserve the externally visible Rewired result for circuits where those accelerators are valid.

## 4. Internal compatibility means Rewired-to-Rewired consistency

When documentation or tests use terms such as compatibility, parity or regression, they refer to compatibility between Rewired execution paths, versions or explicitly defined Rewired semantics — not compatibility with Sebastian Lague's original engine.

## 5. Architecture may evolve freely

Development may change internal data structures, simulation scheduling, caching, compilation, RHDL lowering and other implementation details without preserving legacy-engine behavior, provided the intended Rewired semantic contract remains coherent and tested.
