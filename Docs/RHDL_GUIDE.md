# RHDL v0.4 — beginner-first guide

RHDL is Rewired's hardware description language. The goal of v0.4 is simple: **code should be understandable by reading it, even before learning HDL syntax**.

RHDL still compiles to ordinary Rewired topology. It does not use a separate simulator or runtime.

## 1. Smallest useful chip

```text
chip Adder:
    input A: 8
    input B: 8

    output Y = A + B
```

That is a complete chip.

- `chip Name:` starts a chip.
- indentation shows that declarations belong to the chip.
- `input` creates an input.
- `output` creates an output.
- `=` describes the logic that drives a signal.
- semicolons are not required.

RHDL Studio uses four spaces per indentation level.

## 2. Signals

Current supported widths are **1, 4 and 8 bits**.

```text
chip Signals:
    input enable
    input A: 8
    input B: 8

    let sum = A + B

    output Y = sum
    output ready = true
```

An input without a width is one bit.

`let` creates an internal signal and lets RHDL infer its width:

```text
let sum = A + B
let same = A == B
```

Outputs can also infer their width:

```text
output Y = A + B
output same = A == B
```

For explicit internal wiring, `wire` is still available.

## 3. Constants

```text
const MASK = 0xFF
const ENABLED = true
```

Accepted logic values:

```text
true   false
high   low
on     off
```

Numeric constants may be decimal, binary or hexadecimal:

```text
42
0b1010
0xFF
```

Underscores in numeric literals are accepted.

## 4. Logic reads like normal code

Preferred readable operators:

```text
let both = A and B
let either = A or B
let different = A xor B
let inverted = not A
```

Symbol forms are also supported:

```text
A & B
A | B
A ^ B
!A
~A
```

Arithmetic and comparisons:

```text
A + B
A - B

A == B
A != B
A < B
A <= B
A > B
A >= B
```

Constant shifts:

```text
A << 1
A >> 2
```

## 5. Choosing between two values

The most readable form is Python-like:

```text
output Y = A if select else B
```

There is also an explicit hardware helper:

```text
output Y = mux(select, A, B)
```

Both generate mux logic.

The older form is still accepted:

```text
output Y = select ? A : B
```

## 6. Bits, slices and joining buses

Read one bit:

```text
let bit0 = A[0]
```

Read a slice:

```text
let high = A[7:4]
```

Join values with the readable helper:

```text
let mixed = concat(A[7:4], B[3:0])
```

`join(...)` is an alias for `concat(...)`.

The older brace form remains valid:

```text
let mixed = {A[7:4], B[3:0]}
```

## 7. Parameters

```text
chip Logic8(WIDTH=8):
    const MASK = 0xFF

    input A: WIDTH
    input B: WIDTH

    output Y: WIDTH = (A and B) xor MASK
```

`param` is still accepted as an alias-style legacy declaration; `const` is preferred for readability.

## 8. Using another chip

Python-like instance syntax:

```text
chip NandExample:
    input A
    input B
    output Y

    n = NAND(A=A, B=B, OUT=Y)
```

For common pins named `IN A`, `IN B`, `IN_A` or `IN_B`, RHDL accepts the shorter aliases `A` and `B`.

Exact pin names remain available when needed.

The older structural form is still supported:

```text
NAND n
connect A -> n.IN_A
connect B -> n.IN_B
connect n.OUT -> Y
```

Use `connect` when you want exact topology rather than concise source code.

## 9. Comments

Preferred Python-style comments:

```text
# this is a comment
output Y = A + B  # this is also a comment
```

Legacy `// comments` are still accepted.

## 10. Full readable example

```text
chip AluMini(WIDTH=8):
    input A: WIDTH
    input B: WIDTH
    input select

    let sum = A + B
    let mixed = concat(A[7:4], B[3:0])

    output Y: WIDTH = sum if select else mixed
    output equal = A == B
    output ready = true
```

## 11. Editor behaviour

RHDL Studio is tuned for the v0.4 syntax:

- Enter after a line ending in `:` automatically indents.
- indentation uses four spaces.
- `Ctrl+/` toggles `#` comments.
- `Ctrl+Shift+F` formats the document.
- `Ctrl+B` or `Ctrl+Enter` builds.
- `F5` builds and opens the generated chip.
- `F8` / `Shift+F8` moves between diagnostics.
- `Tab` / `Shift+Tab` indents and unindents.
- `Alt+Up` / `Alt+Down` moves selected lines.
- `Ctrl+D` duplicates a line or selection.
- `Ctrl+Backspace` / `Ctrl+Delete` deletes by word.
- matching brackets are highlighted.
- clicking the line-number gutter selects a whole line.

## 12. Compatibility with older RHDL

Existing RHDL v0.3 source remains valid.

This is still accepted:

```text
chip Adder {
  input A: 8
  input B: 8
  output Y = A + B
}
```

The preferred v0.4 spelling is:

```text
chip Adder:
    input A: 8
    input B: 8
    output Y = A + B
```

Both lower to the same ordinary Rewired circuit topology.
