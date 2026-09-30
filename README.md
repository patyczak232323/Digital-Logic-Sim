# Rewired Next

Rewired Next is a from-scratch digital logic simulator architecture. The rewrite keeps the project name and product goals, but no simulator, editor, UI, save-system, or runtime source file from the previous implementation is present in this branch.

The current branch is an architectural foundation, not yet a replacement release.

## Current milestone

Implemented:

- Unity 6 project shell
- engine-independent core assembly
- deterministic settle/tick/settle simulation cycle
- width-checked digital signals
- NAND primitive
- byte-oriented serial adapter
- Windows COM transport using Win32 directly
- 256-byte receive and transmit queues
- edge-triggered TX/RX handshake
- tests that run without Unity
- CI for the core and Windows transport compilation

Still to rebuild:

- circuit graph and wires
- graphical editor
- project persistence
- reusable custom chips
- RHDL
- diagnostics and waveform viewer
- performance compilation and LUT cache
- Android UI

## Repository layout

- Assets/Rewired/Core — pure C# simulation domain; no Unity dependency
- Assets/Rewired/Platform — operating-system adapters
- Assets/Rewired/Unity — Unity lifecycle integration
- tests — fast tests and platform compile checks
- Docs — architecture and protocol documentation

Dependencies point inward:

    Unity/UI -> application services -> core <- platform adapters

The core never opens files, COM ports, windows, or Unity objects directly.

## Byte serial adapter

The serial adapter exposes these circuit pins:

| Pin | Direction | Width | Meaning |
| --- | --- | ---: | --- |
| TX DATA | input | 8 | byte to transmit |
| TX SEND | input | 1 | rising edge queues TX DATA |
| TX READY | output | 1 | transport can accept a byte |
| RX DATA | output | 8 | current received byte |
| RX VALID | output | 1 | RX DATA is valid |
| RX READ | input | 1 | rising edge acknowledges RX DATA |
| RESET | input | 1 | clears adapter and COM queues |
| CONNECTED | output | 1 | COM endpoint is open |

For PuTTY, create a virtual COM pair such as COM10 <-> COM11. Rewired opens COM10 and PuTTY opens COM11. Both applications must not open the same side of the pair.

Recommended PuTTY configuration is 115200 baud, 8 data bits, no parity, one stop bit, and no flow control.

See Docs/SERIAL_COM.md.

## Development

Required Unity version:

    6000.0.46f1

Run the core tests:

    dotnet run --project tests/Rewired.Core.Tests/Rewired.Core.Tests.csproj

Compile-check the Windows COM implementation:

    dotnet build tests/Rewired.Platform.Windows.Compile/Rewired.Platform.Windows.Compile.csproj

See Docs/ARCHITECTURE.md and Docs/MIGRATION.md before adding features.
