# Digital Logic Sim Rewired v0.2.0

Version 0.2.0 adds the first persistent combinational cache and experimental native JIT execution path while preserving the deterministic simulator as the correctness fallback.

## Changes

- Persistent FULL LUT cache for safe combinational Custom Chips.
- Cache fingerprinting based on logic topology and nested dependencies.
- Background LUT loading, generation and disk writes to avoid blocking the UI/simulation thread.
- Stateful and feedback-based circuits remain on the deterministic simulation path; nested safe combinational chips can still use their own caches.
- Experimental native JIT for pure acyclic combinational Custom Chips using `DynamicMethod`/Mono JIT.
- JIT code is invalidated when edited logic or an ancestor hierarchy changes.
- LUT remains preferred when a ready FULL LUT exists; JIT is used as an acceleration path when appropriate.
- Added cache/JIT documentation and cache-directory ignore rules.

## Notes

The native JIT currently targets combinational logic. Registers, latches, flip-flops and other feedback/stateful structures continue to use the deterministic engine, so workloads dominated by stateful logic may not show a large steps-per-second increase yet.
