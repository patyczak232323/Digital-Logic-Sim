# RHDL 1.0 — hardware description with Python syntax

RHDL describes a chip in text. It reads like Python: indentation instead of `end`, `def`-style headers, Python operators, `for` loops and tuple unpacking. What it builds is hardware: every line is a piece of circuitry that exists at the same time as all the others, and the result is an ordinary Rewired chip made of NAND gates and the chips you already have.

## A first circuit

```python
# 8-bit adder
circuit Adder(A: u8, B: u8) -> Y: u8:
    Y = A + B
```

- `circuit Name(inputs) -> outputs:` starts a circuit. The indented lines below it are its body.
- Inputs and outputs are the chip's pins. A pin without a type is one bit; `u4` and `u8` are 4- and 8-bit pins (`bit` is the same as no type).
- Several outputs go in brackets: `-> (SUM, COUT)`. A long header may span lines inside the brackets.
- `#` starts a comment.

## Logic

```python
circuit Logic(A, B, C) -> (both, either, different, inverted, majority):
    both = A & B
    either = A | B
    different = A ^ B
    inverted = ~A
    majority = (A & B) | (A & C) | (B & C)
```

`&`, `|`, `^` and `~` work on every bit of a value, like Python's operators on integers. For single bits, Python's `and`, `or` and `not` can be used as well:

```python
circuit Enable(A, EN, RESET) -> Y:
    Y = A and EN and not RESET
```

The order of the lines does not matter for plain wiring: all of them are hardware that exists at once.

## Names

A line such as `ab = A ^ B` creates a signal called `ab`. Its width comes from the value.

- **A name assigned once is a wire.** It can be used anywhere in the circuit, also above the line that assigns it. This is how feedback is written (see *Memory* below).
- **A name assigned several times is a variable.** Each use reads the latest assignment above it, exactly like Python. This is what makes loops useful.
- Outputs are assigned once. They can also be read inside the circuit.

## Arithmetic and comparisons

```python
circuit Math(A: u8, B: u8) -> (sum: u8, diff: u8, same, less, double: u8):
    sum = A + B
    diff = A - B
    same = A == B
    less = A < B
    double = A << 1
```

- `+` and `-` wrap around at the width of the result: with 8 bits, `200 + 100` is `44`.
- Give the result one bit more to keep the carry: `wide: u9 = A + B`.
- `==`, `!=`, `<`, `>`, `<=` and `>=` compare unsigned values and give one bit.
- `<<` and `>>` shift by a fixed number of bits; the bits shifted in are 0.
- Multiplication and division are not built; `* / %` give an error.

Operator precedence is Python's, from loosest to tightest: `x if c else y`, `or`, `and`, `not`, comparisons, `|`, `^`, `&`, `<<` `>>`, `+` `-`, `~`, then calls, `[...]` and `.`. So `A & B == 0` means `(A & B) == 0`, as in Python.

## Choosing between values

Python's conditional expression builds a multiplexer:

```python
circuit Select(A: u4, B: u4, SEL) -> Y: u4:
    Y = A if SEL else B
```

The condition must be one bit. Chains work as in Python, and long ones read well in brackets:

```python
circuit Alu(A: u4, B: u4, OP: u4) -> Y: u4:
    Y = (
        A + B if OP == 0 else
        A - B if OP == 1 else
        A & B if OP == 2 else
        A | B
    )
```

There is no `if` statement: an `if` block would run in sequence, but hardware runs all at once.

## Bits and buses

```python
circuit Bits(A: u8, B: u8) -> (low: u4, high: u4, top, joined: u8, wide: u8):
    low = A[0:4]
    high = A[4:8]
    top = A[-1]
    joined = cat(A[4:8], B[0:4])
    wide = u8(A[0:4])
```

- `A[3]` is bit 3; bit 0 is the least significant. Negative indexes count from the top, as in Python.
- Slices work like Python's: `A[0:4]` is bits 0, 1, 2 and 3. `A[4:]` is from bit 4 to the top.
- `cat(high, ..., low)` joins values into a bus, most significant part first, the way numbers are written.
- `u4(x)`, `u8(x)` and so on give a value another width: extra top bits are 0, missing ones are dropped.
- Signals inside a circuit can have any width up to 32 bits; only chip pins are limited to 1, 4 and 8.

A signal can also be built bit by bit. Declare its width first (outputs already have one):

```python
circuit Reverse(A: u4) -> Y: u4:
    for i in range(4):
        Y[i] = A[3 - i]
```

## Numbers and constants

`5`, `0b0101`, `0x5` and `True`/`False` are numbers. A number takes the width it is used at. Spelling it in binary or hex also gives it a width: `0b0101` is 4 bits, `0x0F` is 8.

Names assigned a number at the top of the file are constants:

```python
WIDTH = 4

circuit Ones() -> Y: u4:
    Y = (1 << WIDTH) - 1
```

Constants are worked out when the circuit is built, so they can also choose what gets built: `Y = ~A if INVERT else A` builds only one of the two.

## Loops

`for i in range(...)` repeats hardware a fixed number of times. Together with a variable it describes chains such as a ripple-carry adder:

```python
circuit Add4(A: u4, B: u4, CIN) -> (S: u4, COUT):
    carry = CIN
    for i in range(4):
        S[i] = A[i] ^ B[i] ^ carry
        carry = (A[i] & B[i]) | (carry & (A[i] ^ B[i]))
    COUT = carry
```

`range(stop)`, `range(start, stop)` and `range(start, stop, step)` work as in Python.

## Using other chips

Any chip from the library, and every circuit you have built, can be called like a function. Inputs are given in order, or by name:

```python
circuit TwoNands(A, B, C) -> Y:
    t = NAND(A, B)
    Y = NAND(t, C)
```

- A chip with one output gives its value directly.
- A chip with several outputs is unpacked like a Python tuple: `s, c = HalfAdder(A, B)`. Use `_` for an output you do not need.
- Name a chip to reach its pins later: `h = HalfAdder(A, B)` then `h.SUM` and `h.CARRY`. Its inputs can also be connected afterwards with `h.A = x`.
- Pin names are written without spaces: the NAND pins `IN A` and `IN B` are `IN_A` and `IN_B` (or just `A` and `B`).

Built-in chips are called `NAND`, `TRISTATE`, `CLOCK`, `PULSE`, `ROM`, `KEY`, `LED`, `SEVEN_SEGMENT`, `RGB_DISPLAY`, `RGB_DISPLAY_64`, `DOT_DISPLAY` and `BUZZER`.

`RGB_DISPLAY_64` is a 64 x 64 screen with 4 bits per colour. Its inputs are `X`, `Y` (8 bits, the low 6 are used; `(0, 0)` is the bottom-left pixel), `RED`, `GREEN`, `BLUE` (4 bits each), `RESET`, `WRITE`, `REFRESH` and `CLOCK`. On a rising clock edge `RESET` clears the hidden picture, otherwise `WRITE` draws the colour at `(X, Y)` into it; `REFRESH` then shows the hidden picture. Its outputs `R_OUT`, `G_OUT` and `B_OUT` give the colour shown at `(X, Y)`.

Several `TRISTATE` buffers may drive the same signal, which is how a shared bus is made:

```python
circuit SharedBus(A, B, SEL) -> Y:
    bus = TRISTATE(A, SEL)
    bus = TRISTATE(B, ~SEL)
    Y = bus
```

## Memory

A latch is two NAND gates that feed each other. Because a name can be used above the line that assigns it, the circuit is written the way it is drawn:

```python
circuit SrLatch(S_N, R_N) -> (Q, QN):
    Q = NAND(S_N, QN)
    QN = NAND(R_N, Q)
```

Every `NAND(...)` written in the source is exactly one gate, kept as written. The `Examples/RHDL` folder builds a D flip-flop, a 4-bit register, a small RAM and a CPU this way.

## What gets built

Rewired's only logic gate is the NAND, and RHDL builds everything from it:

| Expression | NAND gates |
| --- | --- |
| `~a` | 1 |
| `a & b` | 2 |
| `a \| b` | 3 |
| `a ^ b` | 4 |
| `a if s else b` | 3 per bit, plus 1 for `~s` |
| full adder (one bit of `+`) | 9 |

Constants are folded, identical gates are shared and gates nobody reads are removed, so `A & 0b1111` is just a wire. Multi-bit pins are split into bits only where single bits are needed.

The generated chip is drawn like a hand-made schematic: signals flow from left to right, each bit of a bus operation sits on its own row with the most significant bit at the top (as on Rewired's pins), wires run horizontally into pins and their vertical parts get separate tracks, and feedback loops back over the parts it skips.

## Messages

Mistakes are reported with the line and a hint, for example:

- `Unknown name 'totl'. Did you mean 'total'?`
- `This value is 4 bits but 'Y' is 8. Write u8(...) to add zero bits at the top.`
- `'t' is used before it is assigned. Names that are assigned more than once are read in order, like Python variables.`
- `'if' blocks would run in sequence, but hardware runs all at once. Choose between values with 'a if select else b'.`

## RHDL Studio

- Enter after a line ending in `:` indents the next line.
- `Ctrl+/` toggles `#` comments.
- `Ctrl+Shift+F` formats the document.
- `Ctrl+B` / `Ctrl+Enter` builds.
- `F5` builds and opens the circuit.
- `F8` / `Shift+F8` navigates diagnostics.

Write one circuit per source. Build it, then use it by name from the next one.

## Coming from RHDL 0.6

RHDL 1.0 replaces the earlier syntax:

| 0.6 | 1.0 |
| --- | --- |
| `circuit X` ... `input A: 4` ... `end` | `circuit X(A: u4) -> ...:` and an indented body |
| `signal t: 4` | `t: u4` (or just assign `t = ...`) |
| `and`, `or`, `xor`, `not` on buses | `&`, `\|`, `^`, `~` |
| `choose(s, a, b)` | `a if s else b` |
| `A[7:4]` | `A[4:8]` |
| `join(a, b)` | `cat(a, b)` |
| `component g : NAND(IN_A=a, IN_B=b, OUT=y)` | `y = NAND(a, b)` |
| `connect a -> g.IN_A` | `g.IN_A = a` |
| `constant N = 4` | `N = 4` at the top of the file |

## Examples

The curated `Examples/RHDL/` set progresses through `And2 -> HalfAdder -> FullAdder -> Mux4 -> DffBit -> Reg4`, then adds a separate `Ram4x4` memory example, `CPU4`, a 4-bit accumulator processor, and `PIX6`, a small computer that draws on the 64 x 64 screen.
