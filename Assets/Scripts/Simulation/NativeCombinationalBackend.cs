using System;
using System.Runtime.InteropServices;
using DLS.Description;

namespace DLS.Simulation
{
    // Optional native C executor for the same pure/acyclic flattened programs that are
    // already accepted by CombinationalJitCompiler. The managed DynamicMethod program is
    // still built and remains the correctness fallback if the native library is absent or
    // rejects an evaluation.
    internal static class NativeCombinationalBackend
    {
        const string LibraryName = "dls_native_fast";
        const uint ExpectedAbiVersion = 1;
        const uint DisconnectedState = 0xFFFF0000u;

        internal static bool Available { get; private set; } = true;
        internal static string LastFailureReason { get; private set; } = string.Empty;

        [StructLayout(LayoutKind.Sequential)]
        internal struct NativeNode
        {
            public uint Op;
            public int In0, In1, In2, In3, In4, In5, In6, In7;
            public int Out0, Out1, Out2, Out3, Out4, Out5, Out6, Out7;

            public void SetInput(int index, int value)
            {
                switch (index)
                {
                    case 0: In0 = value; break;
                    case 1: In1 = value; break;
                    case 2: In2 = value; break;
                    case 3: In3 = value; break;
                    case 4: In4 = value; break;
                    case 5: In5 = value; break;
                    case 6: In6 = value; break;
                    case 7: In7 = value; break;
                    default: throw new ArgumentOutOfRangeException(nameof(index));
                }
            }

            public void SetOutput(int index, int value)
            {
                switch (index)
                {
                    case 0: Out0 = value; break;
                    case 1: Out1 = value; break;
                    case 2: Out2 = value; break;
                    case 3: Out3 = value; break;
                    case 4: Out4 = value; break;
                    case 5: Out5 = value; break;
                    case 6: Out6 = value; break;
                    case 7: Out7 = value; break;
                    default: throw new ArgumentOutOfRangeException(nameof(index));
                }
            }
        }

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        static extern uint dls_native_abi_version();

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr dls_native_create_program(
            [In] NativeNode[] nodes,
            int nodeCount,
            [In] int[] outputRefs,
            int outputCount,
            int scratchCount);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        static extern void dls_native_destroy_program(IntPtr program);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        static extern int dls_native_eval(
            IntPtr program,
            [In, Out] uint[] scratch,
            int scratchCount,
            [Out] uint[] outputs,
            int outputCount);

        internal static NativeCombinationalProgram TryCreate(
            CombinationalJitCompiler.CompiledNode[] nodes,
            int[] topologicalOrder,
            CombinationalJitCompiler.SignalRef[] rootOutputs,
            int scratchCount)
        {
            if (!Available) return null;
            if (Environment.GetEnvironmentVariable("DLS_NATIVE_FAST") == "0") return null;

            try
            {
                uint abi = dls_native_abi_version();
                if (abi != ExpectedAbiVersion)
                {
                    LastFailureReason = $"native ABI mismatch ({abi} != {ExpectedAbiVersion})";
                    Available = false;
                    return null;
                }

                NativeNode[] nativeNodes = new NativeNode[topologicalOrder.Length];
                for (int i = 0; i < topologicalOrder.Length; i++)
                {
                    CombinationalJitCompiler.CompiledNode node = nodes[topologicalOrder[i]];
                    if (!TryMapOp(node.Type, out uint op)) return null;
                    if (node.Inputs.Length > 8 || node.Outputs.Length > 8) return null;

                    NativeNode native = new() { Op = op };
                    for (int input = 0; input < node.Inputs.Length; input++)
                    {
                        if (!TryEncodeSignal(node.Inputs[input], out int encoded)) return null;
                        native.SetInput(input, encoded);
                    }

                    for (int output = 0; output < node.Outputs.Length; output++)
                    {
                        if (node.Outputs[output].IsConstant || node.Outputs[output].Slot < 0) return null;
                        native.SetOutput(output, node.Outputs[output].Slot);
                    }

                    nativeNodes[i] = native;
                }

                int[] outputRefs = new int[rootOutputs.Length];
                for (int i = 0; i < rootOutputs.Length; i++)
                {
                    if (!TryEncodeSignal(rootOutputs[i], out outputRefs[i])) return null;
                }

                IntPtr handle = dls_native_create_program(
                    nativeNodes,
                    nativeNodes.Length,
                    outputRefs,
                    outputRefs.Length,
                    scratchCount);

                if (handle == IntPtr.Zero)
                {
                    LastFailureReason = "native program allocation failed";
                    return null;
                }

                LastFailureReason = string.Empty;
                return new NativeCombinationalProgram(handle, scratchCount, outputRefs.Length);
            }
            catch (DllNotFoundException e)
            {
                LastFailureReason = e.Message;
                Available = false;
                return null;
            }
            catch (EntryPointNotFoundException e)
            {
                LastFailureReason = e.Message;
                Available = false;
                return null;
            }
            catch (BadImageFormatException e)
            {
                LastFailureReason = e.Message;
                Available = false;
                return null;
            }
            catch (Exception e)
            {
                LastFailureReason = e.GetType().Name + ": " + e.Message;
                return null;
            }
        }

        static bool TryEncodeSignal(CombinationalJitCompiler.SignalRef signal, out int encoded)
        {
            if (!signal.IsConstant)
            {
                encoded = signal.Slot;
                return encoded >= 0;
            }

            // The existing resolver only synthesizes disconnected constants. Refuse any
            // future constant kind until the native ABI explicitly defines it.
            if (signal.Constant == DisconnectedState)
            {
                encoded = -1;
                return true;
            }

            encoded = 0;
            return false;
        }

        static bool TryMapOp(ChipType type, out uint op)
        {
            op = type switch
            {
                ChipType.Nand => 1u,
                ChipType.TriStateBuffer => 2u,
                ChipType.Split_4To1Bit => 3u,
                ChipType.Split_8To1Bit => 4u,
                ChipType.Merge_1To4Bit => 5u,
                ChipType.Merge_1To8Bit => 6u,
                ChipType.Merge_4To8Bit => 7u,
                ChipType.Split_8To4Bit => 8u,
                ChipType.Bus_1Bit or ChipType.Bus_4Bit or ChipType.Bus_8Bit => 9u,
                _ => 0u
            };

            return op != 0;
        }

        internal static bool TryEvaluate(
            IntPtr handle,
            uint[] scratch,
            int scratchCount,
            uint[] outputs,
            int outputCount)
        {
            try
            {
                return dls_native_eval(handle, scratch, scratchCount, outputs, outputCount) != 0;
            }
            catch (Exception e) when (
                e is DllNotFoundException ||
                e is EntryPointNotFoundException ||
                e is BadImageFormatException)
            {
                LastFailureReason = e.Message;
                Available = false;
                return false;
            }
        }

        internal static void Destroy(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return;
            try { dls_native_destroy_program(handle); }
            catch { }
        }
    }

    internal sealed class NativeCombinationalProgram
    {
        IntPtr handle;
        readonly int scratchCount;
        readonly int outputCount;

        public NativeCombinationalProgram(IntPtr handle, int scratchCount, int outputCount)
        {
            this.handle = handle;
            this.scratchCount = scratchCount;
            this.outputCount = outputCount;
        }

        ~NativeCombinationalProgram()
        {
            NativeCombinationalBackend.Destroy(handle);
            handle = IntPtr.Zero;
        }

        public bool TryRun(uint[] scratch, uint[] outputs)
        {
            IntPtr local = handle;
            if (local == IntPtr.Zero) return false;
            return NativeCombinationalBackend.TryEvaluate(
                local,
                scratch,
                Math.Min(scratch.Length, scratchCount),
                outputs,
                Math.Min(outputs.Length, outputCount));
        }
    }
}
