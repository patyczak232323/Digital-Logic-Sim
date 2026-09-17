# Digital Logic Sim 8-bit computer builder

This tool generates a native Digital Logic Sim project containing a complete
8-bit computer. The machine uses only stock simulator primitives and custom
chips composed from NAND gates, splitters, mergers, RAM, ROM, and a clock.

Generate the project and ZIP archive from the repository root:

```bash
python3 Tools/ComputerProjectBuilder/build_project.py
```

Execute the generated hierarchy as a flattened native-chip netlist:

```bash
python3 Tools/ComputerProjectBuilder/verify_netlist.py
```

The diagnostic ROM exercises all 16 opcodes. A successful run finishes with:

```text
PC=FF R14=5A MEM=5A FLAGS=02 IR=C0FF
```

Generated files are written below `Artifacts/` and are intentionally not
tracked by Git.
