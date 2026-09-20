# RHDL v0.3 — quick guide

RHDL is Rewired's source language for generating ordinary circuit topology. It does not use a separate simulation engine: source is lowered to normal Rewired chips, pins, subchips and wires.

## 1. Smallest useful chip

```text
chip And2 {
  input A, B
  output Y

  Y = A & B
}
```

A source file contains one `chip` declaration. Statements do not require semicolons.

## 2. Signals and widths

Supported signal widths are currently **1, 4 and 8 bits**.

```text
input enable
input A: 8
input B[8]
output Y: 8
wire temp: 8
```

`A: 8` and `A[8]` both declare an 8-bit signal.

Do not use `A[7:0]` in a declaration. Ranges are expression slices:

```text
wire high: 4
high = A[7:4]
```

Parameters can be used for widths:

```text
chip Example(WIDTH=8) {
  input A: WIDTH
  output Y: WIDTH
  Y = A
}
```

## 3. Expressions

RHDL supports readable combinational expressions:

```text
sum = A + B
diff = A - B
logic = (A & B) ^ 0xFF
equal = A == B
Y = select ? sum : diff
```

Operators:

- logic: `& | ^ ! ~` or `AND OR XOR NOT`
- arithmetic: `+ -`
- compare: `== != < > <= >=`
- shift: `<< >>` with a compile-time constant/parameter
- mux: `condition ? whenTrue : whenFalse`

Constants may be decimal, binary or hexadecimal:

```text
0
42
0b1010
0xFF
```

## 4. Bits, slices and concatenation

```text
bit0 = A[0]
nibble = A[7:4]
mixed = {A[7:4], B[3:0]}
```

The final width must still be 1, 4 or 8 bits.

## 5. Internal wires

Use a `wire` when an intermediate result is reused:

```text
wire sum: 8
sum = A + B

Y = select ? sum : A
carryLike = sum > A
```

Inputs are sources. Outputs are final destinations. A reusable internal value should normally be a wire.

## 6. Existing chips / structural RHDL

You can instantiate existing builtin or custom chips:

```text
NAND n
connect A -> n.IN_A
connect B -> n.IN_B
connect n.OUT -> Y
```

Named bindings are shorter:

```text
NAND n(IN_A=A, IN_B=B, OUT=Y)
```

Pin names containing spaces can be written with underscores, e.g. `IN_A` resolves to `IN A`.

## 7. Editor shortcuts

RHDL Studio includes:

- `Ctrl+S` — save draft
- `Ctrl+B` — build
- `Ctrl+Z` / `Ctrl+Y` — undo / redo
- `Ctrl+Shift+Z` — redo
- `Ctrl+/` — comment/uncomment selected lines
- `Ctrl+D` — duplicate current/selected lines
- `Tab` / `Shift+Tab` — indent / unindent
- `Home` — jump between indentation and true line start
- automatic `{}`, `()`, `[]` pairing
- `Tab` or `Enter` between `{}` — expand an indented block
- smart Backspace for indentation and empty bracket pairs

After a failed build, RHDL Studio marks diagnostic lines and moves the caret to the first source error.

## 8. Typical mistakes

### Width declaration vs slice

Wrong declaration:

```text
input A[7:0]
```

Correct:

```text
input A: 8
```

Then slice it in an expression:

```text
wire low: 4
low = A[3:0]
```

### Driving an input

Wrong:

```text
input A
A = B
```

Inputs are driven from outside the chip. Assign to an output or wire instead.

### Reusing an output as internal logic

Prefer:

```text
wire result: 8
result = A + B
Y = result
flag = result == 0
```

rather than treating an output as an internal source.

## 9. Examples

Start with:

- `Examples/RHDL/Basics/00_AND.rhdl`
- `Examples/RHDL/Basics/01_BUS_ALU.rhdl`
- `Examples/RHDL/Basics/02_STRUCTURAL_NAND.rhdl`

The larger `Examples/RHDL/Rewired8/` set demonstrates CPU-scale RHDL.
