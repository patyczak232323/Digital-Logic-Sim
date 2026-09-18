using System.Diagnostics;
using System.Reflection.Emit;
using System.Runtime.InteropServices;

internal static class Program
{
    const string Library = "dls_native_fast";
    const uint Disconnected = 0xFFFF0000u;

    [StructLayout(LayoutKind.Sequential)]
    struct NativeNode
    {
        public uint Op;
        public int In0, In1, In2, In3, In4, In5, In6, In7;
        public int Out0, Out1, Out2, Out3, Out4, Out5, Out6, Out7;
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern uint dls_native_abi_version();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr dls_native_create_program(
        [In] NativeNode[] nodes, int nodeCount,
        [In] int[] outputRefs, int outputCount,
        int scratchCount);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern void dls_native_destroy_program(IntPtr program);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern int dls_native_eval(
        IntPtr program,
        [In, Out] uint[] scratch, int scratchCount,
        [Out] uint[] outputs, int outputCount);

    delegate void JitBlock(uint[] scratch);

    static int Main()
    {
        if (dls_native_abi_version() != 1)
            throw new Exception("native ABI mismatch");

        TestNandTruthTable();
        TestTriState();
        TestSplitMergeRoundTrips();
        TestSebastianNandTruthTable();
        TestSebastianTriState();
        TestSebastianSplitMergeRoundTrip();
        TestSebastianFeedbackLatchOrdering();
        DifferentialRandomDag();
        DifferentialSebastianRandomDag();
        BenchmarkRandomDag();
        Console.WriteLine("ALL NATIVE + SEBASTIAN BASELINE TESTS PASSED");
        return 0;
    }

    static NativeNode Nand(int a, int b, int o) => new()
    {
        Op = 1,
        In0 = a,
        In1 = b,
        Out0 = o
    };

    static uint EvalSingle(NativeNode node, uint[] inputs)
    {
        int maxRef = Math.Max(node.Out0, Math.Max(node.In0, node.In1));
        uint[] scratch = new uint[Math.Max(maxRef + 1, inputs.Length)];
        Array.Copy(inputs, scratch, inputs.Length);
        uint[] outputs = new uint[1];
        IntPtr p = dls_native_create_program(new[] { node }, 1, new[] { node.Out0 }, 1, scratch.Length);
        if (p == IntPtr.Zero) throw new Exception("native allocation failed");
        try
        {
            if (dls_native_eval(p, scratch, scratch.Length, outputs, 1) == 0)
                throw new Exception("native eval failed");
            return outputs[0];
        }
        finally { dls_native_destroy_program(p); }
    }

    static void TestNandTruthTable()
    {
        NativeNode n = Nand(0, 1, 2);
        uint[] expected = { 1, 1, 1, 0 };
        int k = 0;
        for (uint a = 0; a <= 1; a++)
        for (uint b = 0; b <= 1; b++)
        {
            uint actual = EvalSingle(n, new[] { a, b });
            if (actual != expected[k++])
                throw new Exception($"NAND mismatch for {a},{b}: {actual}");
        }
        Console.WriteLine("PASS nand_truth_table");
    }

    static void TestTriState()
    {
        NativeNode n = new() { Op = 2, In0 = 0, In1 = 1, Out0 = 2 };
        uint data = 0x000000A5u;
        uint disabled = EvalSingle(n, new[] { data, 0u });
        uint enabled = EvalSingle(n, new[] { data, 1u });
        if (disabled != Disconnected || enabled != data)
            throw new Exception("tristate mismatch");
        Console.WriteLine("PASS tristate");
    }

    static void TestSplitMergeRoundTrips()
    {
        // 8-bit split to eight one-bit values, then merge them back.
        NativeNode split = new() { Op = 4, In0 = 0, Out0 = 1, Out1 = 2, Out2 = 3, Out3 = 4, Out4 = 5, Out5 = 6, Out6 = 7, Out7 = 8 };
        NativeNode merge = new() { Op = 6, In0 = 1, In1 = 2, In2 = 3, In3 = 4, In4 = 5, In5 = 6, In6 = 7, In7 = 8, Out0 = 9 };
        IntPtr p = dls_native_create_program(new[] { split, merge }, 2, new[] { 9 }, 1, 10);
        if (p == IntPtr.Zero) throw new Exception("native allocation failed");
        try
        {
            uint[] scratch = new uint[10];
            uint[] output = new uint[1];
            for (uint value = 0; value < 256; value++)
            {
                scratch[0] = value;
                if (dls_native_eval(p, scratch, scratch.Length, output, 1) == 0 || (output[0] & 0xFFu) != value)
                    throw new Exception($"split/merge mismatch at {value}");
            }
        }
        finally { dls_native_destroy_program(p); }
        Console.WriteLine("PASS split_merge_roundtrip");
    }

    static (NativeNode[] Nodes, int ScratchCount, int OutputRef) CreateRandomDag(int inputCount, int nodeCount, int seed)
    {
        Random rng = new(seed);
        NativeNode[] nodes = new NativeNode[nodeCount];
        for (int i = 0; i < nodeCount; i++)
        {
            int output = inputCount + i;
            int a = rng.Next(0, output);
            int b = rng.Next(0, output);
            nodes[i] = Nand(a, b, output);
        }
        return (nodes, inputCount + nodeCount, inputCount + nodeCount - 1);
    }

    static void ManagedEval(NativeNode[] nodes, uint[] scratch)
    {
        for (int i = 0; i < nodes.Length; i++)
        {
            NativeNode n = nodes[i];
            scratch[n.Out0] = (1u ^ (scratch[n.In0] & scratch[n.In1])) & 1u;
        }
    }

    static JitBlock[] BuildJit(NativeNode[] nodes, int nodesPerBlock)
    {
        List<JitBlock> blocks = new();
        for (int start = 0; start < nodes.Length; start += nodesPerBlock)
        {
            int count = Math.Min(nodesPerBlock, nodes.Length - start);
            DynamicMethod method = new(
                $"bench_{start}",
                typeof(void),
                new[] { typeof(uint[]) },
                typeof(Program).Module,
                true);

            ILGenerator il = method.GetILGenerator();
            for (int i = 0; i < count; i++)
            {
                NativeNode n = nodes[start + i];
                il.Emit(OpCodes.Ldarg_0);
                EmitInt(il, n.Out0);

                il.Emit(OpCodes.Ldarg_0);
                EmitInt(il, n.In0);
                il.Emit(OpCodes.Ldelem_U4);

                il.Emit(OpCodes.Ldarg_0);
                EmitInt(il, n.In1);
                il.Emit(OpCodes.Ldelem_U4);

                il.Emit(OpCodes.And);
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Xor);
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.And);
                il.Emit(OpCodes.Stelem_I4);
            }
            il.Emit(OpCodes.Ret);
            blocks.Add((JitBlock)method.CreateDelegate(typeof(JitBlock)));
        }
        return blocks.ToArray();
    }

    static void EmitInt(ILGenerator il, int value)
    {
        if (value >= sbyte.MinValue && value <= sbyte.MaxValue)
            il.Emit(OpCodes.Ldc_I4_S, (sbyte)value);
        else
            il.Emit(OpCodes.Ldc_I4, value);
    }

    static void DifferentialRandomDag()
    {
        const int inputs = 16;
        var graph = CreateRandomDag(inputs, 2048, 123456);
        IntPtr p = dls_native_create_program(graph.Nodes, graph.Nodes.Length, new[] { graph.OutputRef }, 1, graph.ScratchCount);
        if (p == IntPtr.Zero) throw new Exception("native allocation failed");

        try
        {
            Random rng = new(42);
            uint[] managed = new uint[graph.ScratchCount];
            uint[] native = new uint[graph.ScratchCount];
            uint[] output = new uint[1];

            for (int sample = 0; sample < 5000; sample++)
            {
                for (int i = 0; i < inputs; i++)
                {
                    uint bit = (uint)rng.Next(0, 2);
                    managed[i] = bit;
                    native[i] = bit;
                }

                ManagedEval(graph.Nodes, managed);
                if (dls_native_eval(p, native, native.Length, output, 1) == 0)
                    throw new Exception("native eval failed");

                if (output[0] != managed[graph.OutputRef])
                    throw new Exception($"differential mismatch at sample {sample}");
            }
        }
        finally { dls_native_destroy_program(p); }

        Console.WriteLine("PASS random_dag_differential_5000");
    }

    static void BenchmarkRandomDag()
    {
        const int inputs = 16;
        const int nodes = 4096;
        const int iterations = 20000;
        var graph = CreateRandomDag(inputs, nodes, 987654);
        JitBlock[] jit = BuildJit(graph.Nodes, 256);

        IntPtr p = dls_native_create_program(graph.Nodes, graph.Nodes.Length, new[] { graph.OutputRef }, 1, graph.ScratchCount);
        if (p == IntPtr.Zero) throw new Exception("native allocation failed");

        uint[] managed = new uint[graph.ScratchCount];
        uint[] jitScratch = new uint[graph.ScratchCount];
        uint[] native = new uint[graph.ScratchCount];
        uint[] output = new uint[1];
        SebastianBaseline sebastian = SebastianBaseline.BuildNandDag(inputs, graph.Nodes);

        for (int i = 0; i < inputs; i++)
        {
            uint bit = (uint)(i & 1);
            managed[i] = bit;
            jitScratch[i] = bit;
            native[i] = bit;
            sebastian.SetInput(i, bit);
        }

        // Warm all implementations.
        for (int i = 0; i < 200; i++)
        {
            ManagedEval(graph.Nodes, managed);
            foreach (JitBlock block in jit) block(jitScratch);
            dls_native_eval(p, native, native.Length, output, 1);
            sebastian.Step();
        }

        const int rounds = 7;
        double[] managedTimes = new double[rounds];
        double[] jitTimes = new double[rounds];
        double[] nativeTimes = new double[rounds];
        double[] sebastianTimes = new double[rounds];

        Action managedAction = () => ManagedEval(graph.Nodes, managed);
        Action jitAction = () =>
        {
            for (int i = 0; i < jit.Length; i++) jit[i](jitScratch);
        };
        Action nativeAction = () =>
        {
            if (dls_native_eval(p, native, native.Length, output, 1) == 0)
                throw new Exception("native eval failed");
        };
        Action sebastianAction = () => sebastian.Step();

        for (int round = 0; round < rounds; round++)
        {
            // Alternate order so turbo/thermal/scheduler drift does not systematically
            // favour the same implementation.
            if ((round & 1) == 0)
            {
                managedTimes[round] = Time(iterations, managedAction);
                jitTimes[round] = Time(iterations, jitAction);
                nativeTimes[round] = Time(iterations, nativeAction);
                sebastianTimes[round] = Time(iterations, sebastianAction);
            }
            else
            {
                sebastianTimes[round] = Time(iterations, sebastianAction);
                nativeTimes[round] = Time(iterations, nativeAction);
                jitTimes[round] = Time(iterations, jitAction);
                managedTimes[round] = Time(iterations, managedAction);
            }
        }

        dls_native_destroy_program(p);

        if (native[graph.OutputRef] != jitScratch[graph.OutputRef] ||
            output[0] != jitScratch[graph.OutputRef])
            throw new Exception("benchmark implementations diverged");

        double managedMs = Median(managedTimes);
        double jitMs = Median(jitTimes);
        double nativeMs = Median(nativeTimes);
        double sebastianMs = Median(sebastianTimes);

        Console.WriteLine($"BENCH nodes={nodes} iterations={iterations} rounds={rounds} statistic=median");
        Console.WriteLine($"managed_interpreter_ms={managedMs:F3}");
        Console.WriteLine($"dynamic_jit_ms={jitMs:F3}");
        Console.WriteLine($"native_c_ms={nativeMs:F3}");
        Console.WriteLine($"sebastian_object_engine_ms={sebastianMs:F3}");
        Console.WriteLine($"native_vs_managed_speedup={managedMs / nativeMs:F3}x");
        Console.WriteLine($"jit_vs_sebastian_speedup={sebastianMs / jitMs:F3}x");
        Console.WriteLine($"native_vs_sebastian_speedup={sebastianMs / nativeMs:F3}x");
        Console.WriteLine($"native_vs_dynamic_jit_speedup={jitMs / nativeMs:F3}x");
        Console.WriteLine($"managed_range_ms={managedTimes.Min():F3}..{managedTimes.Max():F3}");
        Console.WriteLine($"dynamic_jit_range_ms={jitTimes.Min():F3}..{jitTimes.Max():F3}");
        Console.WriteLine($"native_c_range_ms={nativeTimes.Min():F3}..{nativeTimes.Max():F3}");
        Console.WriteLine($"sebastian_range_ms={sebastianTimes.Min():F3}..{sebastianTimes.Max():F3}");
    }


    static void TestSebastianNandTruthTable()
    {
        uint[] expected = { 1, 1, 1, 0 };
        int k = 0;
        for (uint a = 0; a <= 1; a++)
        for (uint b = 0; b <= 1; b++)
        {
            var graph = CreateRandomDag(2, 1, 1);
            graph.Nodes[0] = Nand(0, 1, 2);
            SebastianBaseline engine = SebastianBaseline.BuildNandDag(2, graph.Nodes);
            engine.SetInput(0, a);
            engine.SetInput(1, b);
            engine.Step();
            uint actual = engine.Output;
            if (actual != expected[k++])
                throw new Exception($"Sebastian NAND mismatch for {a},{b}: {actual}");
        }
        Console.WriteLine("PASS sebastian_nand_truth_table");
    }

    static void TestSebastianTriState()
    {
        uint data = 0xA5u;
        uint enabled = SebastianPrimitive.TriState(data, 1);
        uint disabled = SebastianPrimitive.TriState(data, 0);
        if (enabled != data || disabled != Disconnected)
            throw new Exception("Sebastian tri-state mismatch");
        Console.WriteLine("PASS sebastian_tristate");
    }

    static void TestSebastianSplitMergeRoundTrip()
    {
        for (uint value = 0; value < 256; value++)
        {
            uint[] bits = SebastianPrimitive.Split8To1(value);
            uint merged = SebastianPrimitive.Merge1To8(bits);
            if ((merged & 0xFFu) != value)
                throw new Exception($"Sebastian split/merge mismatch at {value}");
        }
        Console.WriteLine("PASS sebastian_split_merge_roundtrip");
    }

    static void TestSebastianFeedbackLatchOrdering()
    {
        SebastianBaseline latch = SebastianBaseline.BuildCrossCoupledNandLatch();
        latch.SetInput(0, 1);
        latch.SetInput(1, 1);

        // Upstream's immediate one-pass traversal resolves the symmetric startup into
        // one of the complementary states instead of doing a simultaneous 00<->11 batch.
        latch.Step();
        (uint q, uint qb) = latch.LatchOutputs;
        if (!((q == 1 && qb == 0) || (q == 0 && qb == 1)))
            throw new Exception($"Sebastian latch failed to resolve: Q={q} QB={qb}");

        for (int i = 0; i < 20; i++)
        {
            latch.Step();
            (q, qb) = latch.LatchOutputs;
            if (q == qb)
                throw new Exception($"Sebastian latch lost complementary state at step {i}: Q={q} QB={qb}");
        }

        Console.WriteLine("PASS sebastian_feedback_latch_ordering");
    }

    static void DifferentialSebastianRandomDag()
    {
        const int inputs = 16;
        var graph = CreateRandomDag(inputs, 2048, 123456);
        SebastianBaseline engine = SebastianBaseline.BuildNandDag(inputs, graph.Nodes);
        Random rng = new(42);
        uint[] reference = new uint[graph.ScratchCount];

        for (int sample = 0; sample < 5000; sample++)
        {
            for (int i = 0; i < inputs; i++)
            {
                uint bit = (uint)rng.Next(0, 2);
                reference[i] = bit;
                engine.SetInput(i, bit);
            }

            ManagedEval(graph.Nodes, reference);
            engine.Step();

            if (engine.Output != reference[graph.OutputRef])
                throw new Exception($"Sebastian differential mismatch at sample {sample}");
        }

        Console.WriteLine("PASS sebastian_random_dag_differential_5000");
    }

    sealed class SebastianBaseline
    {
        sealed class Pin
        {
            public uint State = Disconnected;
            public Pin[] Targets = Array.Empty<Pin>();
            public Gate Parent;
            public bool IsInput;
            public int LastUpdatedFrame;
            public int NumInputConnections;
            public int NumInputsReceivedThisFrame;

            public void Propagate(int frame, Random rng)
            {
                for (int i = 0; i < Targets.Length; i++)
                    Targets[i].Receive(this, frame, rng);
            }

            void Receive(Pin source, int frame, Random rng)
            {
                if (LastUpdatedFrame != frame)
                {
                    LastUpdatedFrame = frame;
                    NumInputsReceivedThisFrame = 0;
                }

                if (NumInputsReceivedThisFrame > 0)
                {
                    uint orValue = source.State | State;
                    uint andValue = source.State & State;
                    ushort bitsNew = (ushort)((rng.Next() & 1) == 0 ? orValue : andValue);
                    ushort mask = (ushort)(orValue >> 16);
                    bitsNew = (ushort)((bitsNew & ~mask) | ((ushort)orValue & mask));
                    ushort tristateNew = (ushort)(andValue >> 16);
                    State = (uint)(bitsNew | ((uint)tristateNew << 16));
                }
                else
                {
                    State = source.State;
                }

                NumInputsReceivedThisFrame++;
                if (IsInput &&
                    Parent != null &&
                    NumInputsReceivedThisFrame == NumInputConnections)
                {
                    Parent.NumInputsReady++;
                }
            }
        }

        sealed class Gate
        {
            public readonly Pin[] Inputs = { new Pin { IsInput = true }, new Pin { IsInput = true } };
            public readonly Pin Output = new();
            public int NumConnectedInputs;
            public int NumInputsReady;

            public Gate()
            {
                Inputs[0].Parent = this;
                Inputs[1].Parent = this;
                Output.Parent = this;
            }

            public bool IsReady() => NumInputsReady == NumConnectedInputs;

            public void ProcessNand()
            {
                uint nandOp = 1u ^ (Inputs[0].State & Inputs[1].State);
                Output.State = nandOp & 1u;
            }

            public void PropagateOutput(int frame, Random rng)
            {
                Output.Propagate(frame, rng);
                NumInputsReady = 0;
            }
        }

        readonly Pin[] rootInputs;
        readonly Gate[] traversal;
        readonly Gate[] logicalGates;
        readonly Random rng = new(0x5EED);
        int frame;

        public uint Output => logicalGates[^1].Output.State;
        public (uint Q, uint QB) LatchOutputs => (logicalGates[0].Output.State, logicalGates[1].Output.State);

        SebastianBaseline(Pin[] rootInputs, Gate[] traversal, Gate[] logicalGates)
        {
            this.rootInputs = rootInputs;
            this.traversal = traversal;
            this.logicalGates = logicalGates;
        }

        public static SebastianBaseline BuildNandDag(int inputCount, NativeNode[] nodes)
        {
            Pin[] roots = new Pin[inputCount];
            for (int i = 0; i < roots.Length; i++) roots[i] = new Pin();

            Gate[] logical = new Gate[nodes.Length];
            for (int i = 0; i < logical.Length; i++) logical[i] = new Gate();

            Pin Resolve(int reference)
            {
                if (reference < inputCount) return roots[reference];
                return logical[reference - inputCount].Output;
            }

            for (int i = 0; i < nodes.Length; i++)
            {
                NativeNode n = nodes[i];
                Connect(Resolve(n.In0), logical[i].Inputs[0], logical[i]);
                Connect(Resolve(n.In1), logical[i].Inputs[1], logical[i]);
            }

            // Upstream stores the steady-state visitation order in reverse and StepChip
            // walks SubChips from Length-1 down to zero.
            Gate[] traversal = new Gate[logical.Length];
            for (int i = 0; i < logical.Length; i++)
                traversal[logical.Length - 1 - i] = logical[i];

            return new SebastianBaseline(roots, traversal, logical);
        }

        public static SebastianBaseline BuildCrossCoupledNandLatch()
        {
            Pin[] roots = { new Pin(), new Pin() };
            Gate[] logical = { new Gate(), new Gate() };

            // NAND0 = NAND(S, QB), NAND1 = NAND(R, Q).
            Connect(roots[0], logical[0].Inputs[0], logical[0]);
            Connect(logical[1].Output, logical[0].Inputs[1], logical[0]);
            Connect(roots[1], logical[1].Inputs[0], logical[1]);
            Connect(logical[0].Output, logical[1].Inputs[1], logical[1]);

            Gate[] traversal = { logical[1], logical[0] };
            return new SebastianBaseline(roots, traversal, logical);
        }

        static void Connect(Pin source, Pin target, Gate targetGate)
        {
            Pin[] targets = source.Targets;
            Array.Resize(ref targets, targets.Length + 1);
            targets[^1] = target;
            source.Targets = targets;

            target.NumInputConnections++;
            if (target.NumInputConnections == 1)
                targetGate.NumConnectedInputs++;
        }

        public void SetInput(int index, uint state) => rootInputs[index].State = state;

        public void Step()
        {
            frame++;
            bool canDynamicReorder = ((frame - 1) % 100) == 0;

            for (int i = 0; i < rootInputs.Length; i++)
                rootInputs[i].Propagate(frame, rng);

            for (int i = traversal.Length - 1; i >= 0; i--)
            {
                Gate next = traversal[i];

                if (canDynamicReorder && i > 0 && !next.IsReady() && (rng.Next() & 1) != 0)
                {
                    Gate potential = traversal[i - 1];
                    next = potential;
                    (traversal[i], traversal[i - 1]) = (traversal[i - 1], traversal[i]);
                }

                next.ProcessNand();
                next.PropagateOutput(frame, rng);
            }
        }
    }

    static class SebastianPrimitive
    {
        public static uint TriState(uint data, uint enable)
            => (enable & 1u) != 0 ? data : Disconnected;

        public static uint[] Split8To1(uint input)
        {
            uint mask = 0x00010001u;
            return new[]
            {
                (input >> 7) & mask,
                (input >> 6) & mask,
                (input >> 5) & mask,
                (input >> 4) & mask,
                (input >> 3) & mask,
                (input >> 2) & mask,
                (input >> 1) & mask,
                input & mask,
            };
        }

        public static uint Merge1To8(uint[] input)
        {
            uint mask = 0x00010001u;
            uint a = input[7] & mask;
            uint b = input[6] & mask;
            uint c = input[5] & mask;
            uint d = input[4] & mask;
            uint e = input[3] & mask;
            uint f = input[2] & mask;
            uint g = input[1] & mask;
            uint h = input[0] & mask;
            return a | (b << 1) | (c << 2) | (d << 3) |
                   (e << 4) | (f << 5) | (g << 6) | (h << 7);
        }
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
