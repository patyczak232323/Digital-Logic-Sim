using System.Diagnostics;

// Standalone benchmark that mirrors the hot path of Sebastian Lague's
// Digital-Logic-Sim upstream commit 7aeb66ddff44f916ff7fafb043ab4476ef4409fe.
// The relevant logic is copied structurally from Simulator.StepChip,
// ProcessBuiltinChip, SimChip.Sim_PropagateInputs/Outputs and SimPin.ReceiveInput.

internal static class Program
{
    const uint SingleBitMask = 1u | (1u << 16);
    const uint Disconnected = 0xFFFF0000u;

    enum ChipType
    {
        Custom,
        Nand,
        TriStateBuffer,
        Split4To1,
        Split8To1,
        Merge1To4,
        Merge1To8,
        Merge4To8,
        Split8To4,
        Bus
    }

    sealed class Pin
    {
        public readonly Chip Parent;
        public readonly bool IsInput;
        public uint State = Disconnected;
        public Pin[] Targets = Array.Empty<Pin>();
        public int NumInputConnections;
        public int NumInputsReceivedThisFrame;
        public int LastUpdatedFrame;

        public Pin(Chip parent, bool isInput)
        {
            Parent = parent;
            IsInput = isInput;
        }

        public void PropagateSignal()
        {
            Pin[] targets = Targets;
            for (int i = 0; i < targets.Length; i++)
                targets[i].ReceiveInput(this);
        }

        void ReceiveInput(Pin source)
        {
            if (LastUpdatedFrame != Upstream.SimulationFrame)
            {
                LastUpdatedFrame = Upstream.SimulationFrame;
                NumInputsReceivedThisFrame = 0;
            }

            // Benchmark graphs are deliberately single-driver, matching the fast-engine
            // comparison. This is exactly the first-source branch in upstream SimPin.
            State = source.State;
            NumInputsReceivedThisFrame++;

            if (IsInput && NumInputsReceivedThisFrame == NumInputConnections)
                Parent.NumInputsReady++;
        }
    }

    sealed class Chip
    {
        public readonly ChipType Type;
        public readonly bool IsBuiltin;
        public Pin[] Inputs;
        public Pin[] Outputs;
        public Chip[] SubChips = Array.Empty<Chip>();
        public int NumConnectedInputs;
        public int NumInputsReady;

        public Chip(ChipType type, int inputCount, int outputCount)
        {
            Type = type;
            IsBuiltin = type != ChipType.Custom;
            Inputs = new Pin[inputCount];
            Outputs = new Pin[outputCount];
            for (int i = 0; i < inputCount; i++) Inputs[i] = new Pin(this, true);
            for (int i = 0; i < outputCount; i++) Outputs[i] = new Pin(this, false);
        }

        public void PropagateInputs()
        {
            Pin[] pins = Inputs;
            for (int i = 0; i < pins.Length; i++) pins[i].PropagateSignal();
        }

        public void PropagateOutputs()
        {
            Pin[] pins = Outputs;
            for (int i = 0; i < pins.Length; i++) pins[i].PropagateSignal();
            NumInputsReady = 0;
        }

        public bool IsReady() => NumInputsReady == NumConnectedInputs;
    }

    static class Upstream
    {
        public static int SimulationFrame;
        public static bool CanDynamicReorderThisFrame;

        public static void RunSimulationStep(Chip root)
        {
            CanDynamicReorderThisFrame = SimulationFrame % 100 == 0;
            SimulationFrame++;
            StepChip(root);
        }

        public static void StepChip(Chip chip)
        {
            chip.PropagateInputs();

            for (int i = chip.SubChips.Length - 1; i >= 0; i--)
            {
                Chip next = chip.SubChips[i];

                // In a correctly ordered acyclic graph every gate is ready here, so the
                // upstream dynamic race reordering branch is not entered. This is the
                // steady-state hot path after StepChipReorder has sorted the graph.
                if (next.IsBuiltin) ProcessBuiltin(next);
                else StepChip(next);

                next.PropagateOutputs();
            }
        }

        public static void ProcessBuiltin(Chip chip)
        {
            switch (chip.Type)
            {
                case ChipType.Nand:
                {
                    uint nandOp = 1u ^ (chip.Inputs[0].State & chip.Inputs[1].State);
                    chip.Outputs[0].State = nandOp & 1u;
                    break;
                }
                case ChipType.TriStateBuffer:
                {
                    if ((chip.Inputs[1].State & 1u) == 1u)
                        chip.Outputs[0].State = chip.Inputs[0].State;
                    else
                        chip.Outputs[0].State = Disconnected;
                    break;
                }
                case ChipType.Split4To1:
                {
                    uint s = chip.Inputs[0].State;
                    chip.Outputs[0].State = (s >> 3) & SingleBitMask;
                    chip.Outputs[1].State = (s >> 2) & SingleBitMask;
                    chip.Outputs[2].State = (s >> 1) & SingleBitMask;
                    chip.Outputs[3].State = s & SingleBitMask;
                    break;
                }
                case ChipType.Split8To1:
                {
                    uint s = chip.Inputs[0].State;
                    for (int i = 0; i < 8; i++)
                        chip.Outputs[i].State = (s >> (7 - i)) & SingleBitMask;
                    break;
                }
                case ChipType.Merge1To4:
                {
                    uint a = chip.Inputs[3].State & SingleBitMask;
                    uint b = chip.Inputs[2].State & SingleBitMask;
                    uint c = chip.Inputs[1].State & SingleBitMask;
                    uint d = chip.Inputs[0].State & SingleBitMask;
                    chip.Outputs[0].State = a | (b << 1) | (c << 2) | (d << 3);
                    break;
                }
                case ChipType.Merge1To8:
                {
                    uint value = 0;
                    for (int bit = 0; bit < 8; bit++)
                        value |= (chip.Inputs[7 - bit].State & SingleBitMask) << bit;
                    chip.Outputs[0].State = value;
                    break;
                }
                case ChipType.Merge4To8:
                {
                    uint high = chip.Inputs[0].State;
                    uint low = chip.Inputs[1].State;
                    ushort bits = (ushort)((low & 0xFFFFu) | ((high & 0xFFFFu) << 4));
                    ushort tri = (ushort)(((low >> 16) & 0xFu) | (((high >> 16) & 0xFu) << 4));
                    chip.Outputs[0].State = (uint)(bits | ((uint)tri << 16));
                    break;
                }
                case ChipType.Split8To4:
                {
                    uint s = chip.Inputs[0].State;
                    chip.Outputs[0].State = ((s >> 4) & 0xFu) | (((s >> 20) & 0xFu) << 16);
                    chip.Outputs[1].State = (s & 0xFu) | (((s >> 16) & 0xFu) << 16);
                    break;
                }
                case ChipType.Bus:
                    chip.Outputs[0].State = chip.Inputs[0].State;
                    break;
            }
        }
    }

    sealed class Dag
    {
        public Chip Root;
        public Chip[] Nodes;
        public Pin[] RootInputs;
        public Pin Output;
    }

    static int Main()
    {
        TestNandTruthTable();
        TestTriState();
        TestSplitMergeRoundTrips();
        DifferentialRandomDag();
        TestCrossCoupledNandLatchUpstreamSemantics();
        BenchmarkRandomDag();
        Console.WriteLine("ALL UPSTREAM ENGINE TESTS PASSED");
        return 0;
    }

    static void Connect(Pin source, Pin target)
    {
        int n = source.Targets.Length;
        Array.Resize(ref source.Targets, n + 1);
        source.Targets[n] = target;
        target.NumInputConnections++;
        if (target.NumInputConnections == 1)
            target.Parent.NumConnectedInputs++;
    }

    static Chip Single(ChipType type, int inputs, int outputs) => new(type, inputs, outputs);

    static void TestNandTruthTable()
    {
        uint[] expected = { 1, 1, 1, 0 };
        int k = 0;
        for (uint a = 0; a <= 1; a++)
        for (uint b = 0; b <= 1; b++)
        {
            Chip n = Single(ChipType.Nand, 2, 1);
            n.Inputs[0].State = a;
            n.Inputs[1].State = b;
            Upstream.ProcessBuiltin(n);
            if (n.Outputs[0].State != expected[k++])
                throw new Exception($"NAND mismatch for {a},{b}");
        }
        Console.WriteLine("PASS upstream_nand_truth_table");
    }

    static void TestTriState()
    {
        Chip t = Single(ChipType.TriStateBuffer, 2, 1);
        t.Inputs[0].State = 0xA5;
        t.Inputs[1].State = 0;
        Upstream.ProcessBuiltin(t);
        if (t.Outputs[0].State != Disconnected) throw new Exception("tristate disabled mismatch");
        t.Inputs[1].State = 1;
        Upstream.ProcessBuiltin(t);
        if (t.Outputs[0].State != 0xA5) throw new Exception("tristate enabled mismatch");
        Console.WriteLine("PASS upstream_tristate");
    }

    static void TestSplitMergeRoundTrips()
    {
        Chip split = Single(ChipType.Split8To1, 1, 8);
        Chip merge = Single(ChipType.Merge1To8, 8, 1);

        for (uint value = 0; value < 256; value++)
        {
            split.Inputs[0].State = value;
            Upstream.ProcessBuiltin(split);
            for (int i = 0; i < 8; i++) merge.Inputs[i].State = split.Outputs[i].State;
            Upstream.ProcessBuiltin(merge);
            if ((merge.Outputs[0].State & 0xFFu) != value)
                throw new Exception($"split/merge mismatch at {value}");
        }

        Console.WriteLine("PASS upstream_split_merge_roundtrip");
    }

    static Dag CreateRandomDag(int inputCount, int nodeCount, int seed)
    {
        Random rng = new(seed);
        Chip root = new(ChipType.Custom, inputCount, 0);
        Chip[] topo = new Chip[nodeCount];
        Pin[] sources = new Pin[inputCount + nodeCount];

        for (int i = 0; i < inputCount; i++) sources[i] = root.Inputs[i];

        for (int i = 0; i < nodeCount; i++)
        {
            Chip gate = new(ChipType.Nand, 2, 1);
            int sourceLimit = inputCount + i;
            Pin a = sources[rng.Next(sourceLimit)];
            Pin b = sources[rng.Next(sourceLimit)];
            Connect(a, gate.Inputs[0]);
            Connect(b, gate.Inputs[1]);
            topo[i] = gate;
            sources[inputCount + i] = gate.Outputs[0];
        }

        // Upstream StepChip walks the array from end to start. The first reorder pass
        // leaves acyclic graphs in reverse visitation order.
        root.SubChips = topo.Reverse().ToArray();

        return new Dag
        {
            Root = root,
            Nodes = topo,
            RootInputs = root.Inputs,
            Output = topo[^1].Outputs[0]
        };
    }

    static uint ManagedReference(int inputCount, int nodeCount, int seed, uint[] inputs)
    {
        Random rng = new(seed);
        uint[] values = new uint[inputCount + nodeCount];
        Array.Copy(inputs, values, inputCount);

        for (int i = 0; i < nodeCount; i++)
        {
            int sourceLimit = inputCount + i;
            int a = rng.Next(sourceLimit);
            int b = rng.Next(sourceLimit);
            values[inputCount + i] = (1u ^ (values[a] & values[b])) & 1u;
        }

        return values[^1];
    }

    static void DifferentialRandomDag()
    {
        const int inputs = 16;
        const int nodes = 2048;
        const int seed = 123456;
        Dag dag = CreateRandomDag(inputs, nodes, seed);
        Random rng = new(42);
        uint[] inputValues = new uint[inputs];

        Upstream.SimulationFrame = 0;
        for (int sample = 0; sample < 5000; sample++)
        {
            for (int i = 0; i < inputs; i++)
            {
                inputValues[i] = (uint)rng.Next(0, 2);
                dag.RootInputs[i].State = inputValues[i];
            }

            Upstream.RunSimulationStep(dag.Root);
            uint expected = ManagedReference(inputs, nodes, seed, inputValues);
            if (dag.Output.State != expected)
                throw new Exception($"upstream random DAG mismatch at sample {sample}: {dag.Output.State} != {expected}");
        }

        Console.WriteLine("PASS upstream_random_dag_differential_5000");
    }

    static void TestCrossCoupledNandLatchUpstreamSemantics()
    {
        Chip root = new(ChipType.Custom, 2, 0);
        Chip q = new(ChipType.Nand, 2, 1);
        Chip qb = new(ChipType.Nand, 2, 1);

        // Sbar/Rbar are held high. Feedback is cross-coupled.
        Connect(root.Inputs[0], q.Inputs[0]);
        Connect(qb.Outputs[0], q.Inputs[1]);
        Connect(root.Inputs[1], qb.Inputs[0]);
        Connect(q.Outputs[0], qb.Inputs[1]);

        // Desired immediate one-pass visitation: q, then qb.
        root.SubChips = new[] { qb, q };
        root.Inputs[0].State = 1;
        root.Inputs[1].State = 1;
        q.Outputs[0].State = 0;
        qb.Outputs[0].State = 0;

        Upstream.SimulationFrame = 0;
        Upstream.RunSimulationStep(root);

        if (!((q.Outputs[0].State == 1 && qb.Outputs[0].State == 0) ||
              (q.Outputs[0].State == 0 && qb.Outputs[0].State == 1)))
            throw new Exception($"upstream latch failed to resolve: {q.Outputs[0].State}/{qb.Outputs[0].State}");

        Console.WriteLine($"PASS upstream_cross_coupled_nand_latch state={q.Outputs[0].State}/{qb.Outputs[0].State}");
    }

    static void BenchmarkRandomDag()
    {
        const int inputs = 16;
        const int nodes = 4096;
        const int iterations = 20000;
        const int rounds = 7;
        Dag dag = CreateRandomDag(inputs, nodes, 987654);

        for (int i = 0; i < inputs; i++) dag.RootInputs[i].State = (uint)(i & 1);

        // Warm up CLR JIT and the object graph.
        Upstream.SimulationFrame = 0;
        for (int i = 0; i < 500; i++) Upstream.RunSimulationStep(dag.Root);

        double[] times = new double[rounds];
        for (int r = 0; r < rounds; r++)
            times[r] = Time(iterations, () => Upstream.RunSimulationStep(dag.Root));

        double median = Median(times);
        Console.WriteLine($"BENCH_UPSTREAM nodes={nodes} iterations={iterations} rounds={rounds} statistic=median");
        Console.WriteLine($"upstream_stepchip_ms={median:F3}");
        Console.WriteLine($"upstream_range_ms={times.Min():F3}..{times.Max():F3}");
        Console.WriteLine($"upstream_evals_per_second={(nodes * (double)iterations) / (median / 1000.0):F0}");
        Console.WriteLine($"upstream_steps_per_second={iterations / (median / 1000.0):F0}");
    }

    static double Median(double[] values)
    {
        double[] copy = (double[])values.Clone();
        Array.Sort(copy);
        return copy[copy.Length / 2];
    }

    static double Time(int iterations, Action action)
    {
        Stopwatch sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++) action();
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds;
    }
}
