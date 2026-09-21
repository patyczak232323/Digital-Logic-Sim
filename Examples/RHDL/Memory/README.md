# RAM4x4

A small RAM example built entirely from normal RHDL components.

## Organization

- 4 addresses
- 4 bits per word
- 16 stored bits total
- address inputs: `A1:A0`
- data input: `D[3:0]`
- data output: `Q[3:0]`
- synchronous write on the rising edge of `CLK`
- synchronous clear through `RESET`

Build first:

1. `../Basics/04_DFF.rhdl`
2. `../Basics/05_REG4.rhdl`
3. `00_RAM4x4.rhdl`

## Addresses

| A1 | A0 | Word |
| --- | --- | --- |
| 0 | 0 | 0 |
| 0 | 1 | 1 |
| 1 | 0 | 2 |
| 1 | 1 | 3 |

When `WE=1`, the selected word stores `D` on the next rising clock edge.

Reading is combinational: changing `A1:A0` immediately selects the corresponding stored 4-bit word on `Q`.
