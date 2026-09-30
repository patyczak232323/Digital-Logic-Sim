using System;
using System.Collections.Generic;
using Rewired.Core.Components;
using Rewired.Core.Ports;
using Rewired.Core.Simulation;

namespace Rewired.Core.Tests
{
    static class Program
    {
        static int failures;

        static int Main()
        {
            Run("NAND truth table", NandTruthTable);
            Run("Engine settles combinational logic", EngineSettles);
            Run("Serial TX uses rising edge", SerialTxUsesRisingEdge);
            Run("Serial RX uses valid/read handshake", SerialRxHandshake);
            Run("Serial reset clears pending bytes", SerialResetClearsQueues);

            Console.WriteLine(failures == 0 ? "All clean-core tests passed." : failures + " test(s) failed.");
            return failures == 0 ? 0 : 1;
        }

        static void NandTruthTable()
        {
            var a = new Signal(1);
            var b = new Signal(1);
            var output = new Signal(1);
            var gate = new NandGate(a, b, output);

            gate.Evaluate();
            Equal((ushort)1, output.Value);

            a.Set(1);
            gate.Evaluate();
            Equal((ushort)1, output.Value);

            b.Set(1);
            gate.Evaluate();
            Equal((ushort)0, output.Value);
        }

        static void EngineSettles()
        {
            var a = new Signal(1, 1);
            var b = new Signal(1, 1);
            var intermediate = new Signal(1);
            var output = new Signal(1);
            var engine = new SimulationEngine();

            engine.Add(new NandGate(a, b, intermediate));
            engine.Add(new NandGate(intermediate, intermediate, output));
            engine.RunStep();

            Equal((ushort)1, output.Value);
            Equal((ulong)1, engine.StepNumber);
        }

        static void SerialTxUsesRisingEdge()
        {
            var endpoint = new FakeEndpoint();
            var serial = new ByteSerialAdapter(endpoint);

            serial.TxData.Set(0x41);
            serial.TxSend.Set(1);
            serial.Tick();
            serial.Tick();

            Equal(1, endpoint.Written.Count);
            Equal((byte)0x41, endpoint.Written.Dequeue());

            serial.TxSend.Set(0);
            serial.Tick();
            serial.TxData.Set(0x42);
            serial.TxSend.Set(1);
            serial.Tick();

            Equal((byte)0x42, endpoint.Written.Dequeue());
            Equal((ulong)2, serial.TransmittedBytes);
        }

        static void SerialRxHandshake()
        {
            var endpoint = new FakeEndpoint();
            endpoint.Incoming.Enqueue(0x5A);
            var serial = new ByteSerialAdapter(endpoint);

            serial.Tick();
            Equal((ushort)1, serial.RxValid.Value);
            Equal((ushort)0x5A, serial.RxData.Value);

            serial.RxRead.Set(1);
            serial.Tick();
            Equal((ushort)0, serial.RxValid.Value);
            Equal((ulong)1, serial.ReceivedBytes);
        }

        static void SerialResetClearsQueues()
        {
            var endpoint = new FakeEndpoint();
            endpoint.Incoming.Enqueue(0x10);
            endpoint.Written.Enqueue(0x20);
            var serial = new ByteSerialAdapter(endpoint);

            serial.Reset.Set(1);
            serial.Tick();

            Equal(0, endpoint.Incoming.Count);
            Equal(0, endpoint.Written.Count);
            Equal((ushort)0, serial.RxValid.Value);
        }

        static void Run(string name, Action test)
        {
            try
            {
                test();
                Console.WriteLine("PASS " + name);
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine("FAIL " + name + ": " + exception.Message);
            }
        }

        static void Equal<T>(T expected, T actual) where T : IEquatable<T>
        {
            if (!expected.Equals(actual))
            {
                throw new InvalidOperationException("Expected " + expected + ", got " + actual + ".");
            }
        }

        sealed class FakeEndpoint : IByteEndpoint
        {
            public readonly Queue<byte> Incoming = new Queue<byte>();
            public readonly Queue<byte> Written = new Queue<byte>();

            public bool IsOpen { get; set; } = true;
            public bool CanWrite => IsOpen;
            public string LastError => string.Empty;

            public bool TryRead(out byte value)
            {
                if (Incoming.Count > 0)
                {
                    value = Incoming.Dequeue();
                    return true;
                }

                value = 0;
                return false;
            }

            public bool TryWrite(byte value)
            {
                if (!CanWrite) return false;
                Written.Enqueue(value);
                return true;
            }

            public void Clear()
            {
                Incoming.Clear();
                Written.Clear();
            }

            public void Dispose()
            {
                IsOpen = false;
                Clear();
            }
        }
    }
}
