# RHDL v0.6 — simple hardware description language

RHDL describes hardware. It is intentionally not Python and it is not a sequence of software instructions.

Every equation describes hardware that exists at the same time. RHDL lowers to ordinary Rewired components, pins and wires, so it uses the normal Rewired simulation engine.

## Minimal circuit

```text
circuit Adder
    input A: 8
    input B: 8
    output Y: 8

    Y = A + B
end
```

A file begins with `circuit NAME` and ends with `end`.

Indentation is only for readability. `end` is the actual circuit terminator.

## Core vocabulary

| Syntax | Meaning |
| --- | --- |
| `circuit NAME` | define a circuit |
| `input A: 8` | external input |
| `output Y: 8` | external output |
| `signal temp: 8` | internal signal |
| `constant WIDTH = 8` | compile-time constant |
| `component alu : ALU8(...)` | instantiate another Rewired component |
| `connect A -> gate.A` | exact structural wire |
| `end` | end the circuit |

There is deliberately one canonical spelling for each concept.

## Parallel logic

```text
circuit LogicExample
    input A
    input B

    output both
    output either
    output different

    both      = A and B
    either    = A or B
    different = A xor B
end
```

These equations are concurrent. Their order in the file does not define an execution order.

Supported logic words are `and`, `or`, `xor`, and `not`.

## Arithmetic and comparisons

```text
sum   = A + B
diff  = A - B
same  = A == B
less  = A < B
shift = A << 1
```

## Choosing a value

Use `choose(condition, when_high, when_low)`.

```text
Y = choose(select, A, B)
```

This describes selection hardware. It is intentionally not Python's `A if condition else B`.

Nested selection is valid:

```text
result = choose(do_add, add_result,
         choose(do_sub, sub_result, A))
```

## Bits and buses

Read one bit:

```text
bit0 = A[0]
```

Read a slice:

```text
upper = A[7:4]
```

Join values from high part to low part:

```text
mixed = join(A[7:4], B[3:0])
```

Current Rewired pin widths exposed through RHDL are 1, 4 and 8 bits. Larger architectural values can be represented with multiple buses, as Rewired-8 currently does for 16-bit PC/X/Y/SP.

## Logic levels and constants

```text
constant WIDTH = 8
constant MASK = 0xFF

signal enabled = high
signal disabled = low
```

Numeric literals may be decimal, binary or hexadecimal.

## Components

Existing Rewired components and custom circuits can be instantiated directly:

```text
circuit NandExample
    input A
    input B
    output Y

    component gate : NAND(A=A, B=B, OUT=Y)
end
```

The compact pin list binds pins by name.

For complex or unusual hardware, use explicit structural wiring:

```text
circuit StructuralNand
    input A
    input B
    output Y

    component gate : NAND

    connect A -> gate.A
    connect B -> gate.B
    connect gate.OUT -> Y
end
```

This structural level is the escape hatch that keeps RHDL general: if a topology can be represented by Rewired components and wires, RHDL can describe it. That includes feedback networks, latches, flip-flops, registers, clocks, custom chips and complete CPUs. The current public Rewired palette has no general-purpose RAM primitive; RAM can be attached later as a user-built/custom component.

## Complete example

```text
circuit AluMini
    constant WIDTH = 8

    input A: WIDTH
    input B: WIDTH
    input select

    output Y: WIDTH
    output equal

    signal sum = A + B
    signal mixed = join(A[7:4], B[3:0])

    Y = choose(select, sum, mixed)
    equal = A == B
end
```

## Why this is an HDL

RHDL source is declarative. `A = B + C` means that combinational hardware drives A from B and C; it does not mean “execute this statement now”.

State can be represented directly with explicit feedback topology. The examples build SR latches, D latches, D flip-flops and registers from NAND gates, so they do not rely on a hidden RAM primitive. This keeps the surface language small while still allowing stateful Rewired hardware to be built.

## RHDL Studio

- Enter after a `circuit NAME` line indents the body.
- `Ctrl+/` toggles `#` comments.
- `Ctrl+Shift+F` formats the document.
- `Ctrl+B` / `Ctrl+Enter` builds.
- `F5` builds and opens the circuit.
- `F8` / `Shift+F8` navigates diagnostics.

RHDL is experimental. v0.6 intentionally makes breaking syntax changes instead of carrying compatibility aliases from earlier development versions.

## Example progression

The `Examples/RHDL/Basics` directory now progresses from gates and arithmetic through feedback state:

`AND -> bus ALU -> structural NAND -> half adder -> full adder -> MUX8 -> SR latch -> D latch -> DFF -> Reg4 -> Counter4`

`CPU4` uses those NAND-built registers plus the stock program ROM. `Rewired8` uses the NAND-built `DffBit` for all architectural registers and exposes data memory externally.
