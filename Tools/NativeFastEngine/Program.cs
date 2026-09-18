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
        DifferentialRandomDag();
        BenchmarkRandomDag();
        Console.WriteLine("ALL NATIVE FAST ENGINE TESTS PASSED");
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

        for (int i = 0; i < inputs; i++)
        {
            uint bit = (uint)(i & 1);
            managed[i] = bit;
            jitScratch[i] = bit;
            native[i] = bit;
        }

        // Warm all implementations.
        for (int i = 0; i < 200; i++)
        {
            ManagedEval(graph.Nodes, managed);
            foreach (JitBlock block in jit) block(jitScratch);
            dls_native_eval(p, native, native.Length, output, 1);
        }

        double managedMs = Time(iterations, () => ManagedEval(graph.Nodes, managed));
        double jitMs = Time(iterations, () =>
        {
            for (int i = 0; i < jit.Length; i++) jit[i](jitScratch);
        });
        double nativeMs = Time(iterations, () =>
        {
            if (dls_native_eval(p, native, native.Length, output, 1) == 0)
                throw new Exception("native eval failed");
        });

        dls_native_destroy_program(p);

        if (native[graph.OutputRef] != jitScratch[graph.OutputRef] ||
            output[0] != jitScratch[graph.OutputRef])
            throw new Exception("benchmark implementations diverged");

        Console.WriteLine($"BENCH nodes={nodes} iterations={iterations}");
        Console.WriteLine($"managed_interpreter_ms={managedMs:F3}");
        Console.WriteLine($"dynamic_jit_ms={jitMs:F3}");
        Console.WriteLine($"native_c_ms={nativeMs:F3}");
        Console.WriteLine($"native_vs_managed_speedup={managedMs / nativeMs:F3}x");
        Console.WriteLine($"native_vs_dynamic_jit_speedup={jitMs / nativeMs:F3}x");
    }

    static double Time(int iterations, Action action)
    {
        Stopwatch sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++) action();
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds;
    }
}
