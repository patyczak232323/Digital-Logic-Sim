# RHDL v0.5 — language guide

RHDL is Rewired's source language for describing digital circuits.

The v0.5 design goal is deliberately narrow: **one obvious word for one concept**. The source should read like a description of hardware rather than like compiler jargon.

RHDL compiles to ordinary Rewired components, pins and wires. It does not use a separate simulation engine.

## Smallest circuit

```text
circuit Adder:
    input A: 8
    input B: 8

    output Y = A + B
```

A source file contains one `circuit`.

The body uses four-space indentation. Semicolons and braces are not part of the language.

## Core vocabulary

| Word | Meaning |
| --- | --- |
| `circuit` | the circuit being defined |
| `input` | a value entering the circuit |
| `output` | a value leaving the circuit |
| `signal` | an internal value used inside the circuit |
| `constant` | a compile-time fixed value |
| `component` | an instance of another Rewired circuit/component |
| `connect` | an explicit structural connection |

Example:

```text
circuit Example:
    constant WIDTH = 8

    input A: WIDTH
    input B: WIDTH
    input select

    signal sum = A + B

    output Y = sum if select else A
```

## Signal widths

RHDL currently supports 1, 4 and 8-bit values.

```text
input enable
input nibble: 4
input byte: 8
```

A signal without an explicit width is one bit when it is an input. Internal signals and outputs can infer their width from the expression driving them.

```text
signal sum = A + B
output Y = sum
```

Use `NAME: WIDTH` for an explicit width. Brackets are reserved for reading bits and slices.

## Logic

Use words for logic:

```text
signal both = A and B
signal either = A or B
signal different = A xor B
signal inverted = not A
```

Logic levels are named `high` and `low`:

```text
output ready = high
output disabled = low
```

Arithmetic and comparisons use familiar operators:

```text
A + B
A - B
A == B
A != B
A < B
A <= B
A > B
A >= B
A << 1
A >> 2
```

## Choosing between values

A hardware selector is written as a readable conditional expression:

```text
output Y = A if select else B
```

This synthesizes selection logic. There is no separate `mux` keyword to learn.

Nested choices are valid:

```text
signal result = add_value if do_add else (sub_value if do_sub else A)
```

## Bits, slices and joining values

Read one bit:

```text
signal lowest = A[0]
```

Read a slice:

```text
signal high_nibble = A[7:4]
```

Join values from left to right:

```text
signal mixed = join(A[7:4], B[3:0])
```

The leftmost argument becomes the high part of the result.

## Constants

```text
constant WIDTH = 8
constant MASK = 0xFF
constant ENABLED = high
```

Numbers may be decimal, binary or hexadecimal:

```text
42
0b1010
0xFF
```

Underscores in numeric literals are accepted.

## Components

Instantiate another circuit or builtin component explicitly with `component`:

```text
circuit NandExample:
    input A
    input B
    output Y

    component nand_gate = NAND(A=A, B=B, OUT=Y)
```

The name before `=` is the local instance name. The name after `=` is the component type.

Common pins such as `IN A` and `IN B` can be addressed as `A` and `B` where that alias is unambiguous.

For exact structural wiring, declare the component first and use `connect`:

```text
circuit StructuralNand:
    input A
    input B
    output Y

    component nand_gate = NAND

    connect A -> nand_gate.A
    connect B -> nand_gate.B
    connect nand_gate.OUT -> Y
```

## Comments

Comments start with `#`.

```text
# Add the two input buses.
output Y = A + B
```

## Complete example

```text
circuit AluMini:
    constant WIDTH = 8

    input A: WIDTH
    input B: WIDTH
    input select

    signal sum = A + B
    signal mixed = join(A[7:4], B[3:0])

    output Y: WIDTH = sum if select else mixed
    output equal = A == B
    output ready = high
```

## RHDL Studio

The editor follows the language directly: Enter after `:` indents by four spaces, `Ctrl+/` toggles `#` comments, `Ctrl+Shift+F` formats the document, `Ctrl+B` or `Ctrl+Enter` builds, `F5` builds and opens the generated circuit, and `F8` / `Shift+F8` moves through diagnostics.

The current language is intentionally allowed to make breaking changes while RHDL is experimental. Clarity is preferred over preserving older development syntax.
