using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Rewired.Core.Ports;

namespace Rewired.Platform.Windows
{
    public sealed class WindowsComEndpoint : IByteEndpoint
    {
        const int QueueCapacity = 256;
        const uint GenericRead = 0x80000000;
        const uint GenericWrite = 0x40000000;
        const uint OpenExisting = 3;
        const uint FileAttributeNormal = 0x00000080;
        const uint PurgeTxAbort = 0x0001;
        const uint PurgeRxAbort = 0x0002;
        const uint PurgeTxClear = 0x0004;
        const uint PurgeRxClear = 0x0008;

        readonly object lifecycleLock = new object();
        readonly object receiveLock = new object();
        readonly object transmitLock = new object();
        readonly Queue<byte> receiveQueue = new Queue<byte>();
        readonly Queue<byte> transmitQueue = new Queue<byte>();

        SafeFileHandle handle;
        Thread worker;
        int cancelRequested;
        int open;
        string lastError = string.Empty;

        public bool IsOpen => Volatile.Read(ref open) == 1;

        public bool CanWrite
        {
            get
            {
                if (!IsOpen) return false;
                lock (transmitLock)
                {
                    return transmitQueue.Count < QueueCapacity;
                }
            }
        }

        public string LastError
        {
            get
            {
                lock (lifecycleLock)
                {
                    return lastError;
                }
            }
        }

        public void Open(string portName, int baudRate)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                throw new PlatformNotSupportedException("System COM transport is available only on Windows.");
            }

            if (string.IsNullOrWhiteSpace(portName))
            {
                throw new ArgumentException("COM port name is required.", nameof(portName));
            }

            if (baudRate <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(baudRate));
            }

            Close();

            SafeFileHandle newHandle = CreateFileW(
                NormalizePortName(portName),
                GenericRead | GenericWrite,
                0,
                IntPtr.Zero,
                OpenExisting,
                FileAttributeNormal,
                IntPtr.Zero);

            if (newHandle == null || newHandle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                newHandle?.Dispose();
                throw new Win32Exception(error, "Could not open " + portName + ".");
            }

            try
            {
                Configure(newHandle, baudRate);
            }
            catch
            {
                newHandle.Dispose();
                throw;
            }

            lock (lifecycleLock)
            {
                handle = newHandle;
                lastError = string.Empty;
                Volatile.Write(ref cancelRequested, 0);
                Volatile.Write(ref open, 1);

                worker = new Thread(WorkerLoop)
                {
                    IsBackground = true,
                    Name = "Rewired COM worker"
                };
                worker.Start();
            }
        }

        public bool TryRead(out byte value)
        {
            lock (receiveLock)
            {
                if (receiveQueue.Count > 0)
                {
                    value = receiveQueue.Dequeue();
                    return true;
                }
            }

            value = 0;
            return false;
        }

        public bool TryWrite(byte value)
        {
            if (!IsOpen) return false;

            lock (transmitLock)
            {
                if (!IsOpen || transmitQueue.Count >= QueueCapacity)
                {
                    return false;
                }

                transmitQueue.Enqueue(value);
                return true;
            }
        }

        public void Clear()
        {
            lock (receiveLock)
            {
                receiveQueue.Clear();
            }

            lock (transmitLock)
            {
                transmitQueue.Clear();
            }

            SafeFileHandle current = handle;
            if (IsOpen && current != null && !current.IsInvalid && !current.IsClosed)
            {
                PurgeComm(current, PurgeTxAbort | PurgeRxAbort | PurgeTxClear | PurgeRxClear);
            }
        }

        public void Close()
        {
            Thread currentWorker;
            SafeFileHandle currentHandle;

            lock (lifecycleLock)
            {
                Volatile.Write(ref cancelRequested, 1);
                currentWorker = worker;
                currentHandle = handle;
            }

            if (currentWorker != null && currentWorker != Thread.CurrentThread)
            {
                currentWorker.Join(1000);
            }

            lock (lifecycleLock)
            {
                Volatile.Write(ref open, 0);
                worker = null;
                handle = null;
                currentHandle?.Dispose();
            }

            lock (receiveLock)
            {
                receiveQueue.Clear();
            }

            lock (transmitLock)
            {
                transmitQueue.Clear();
            }
        }

        public void Dispose()
        {
            Close();
        }

        void WorkerLoop()
        {
            var readBuffer = new byte[64];
            var writeBuffer = new byte[1];

            try
            {
                while (Volatile.Read(ref cancelRequested) == 0)
                {
                    bool didWork = false;
                    byte nextWrite = 0;
                    bool hasWrite;

                    lock (transmitLock)
                    {
                        hasWrite = transmitQueue.Count > 0;
                        if (hasWrite)
                        {
                            nextWrite = transmitQueue.Dequeue();
                        }
                    }

                    if (hasWrite)
                    {
                        writeBuffer[0] = nextWrite;
                        if (!WriteFile(handle, writeBuffer, 1, out int written, IntPtr.Zero))
                        {
                            throw LastWin32Exception("COM write failed.");
                        }

                        if (written != 1)
                        {
                            throw new IOException("COM write completed without writing the queued byte.");
                        }

                        didWork = true;
                    }

                    if (!ReadFile(handle, readBuffer, readBuffer.Length, out int read, IntPtr.Zero))
                    {
                        throw LastWin32Exception("COM read failed.");
                    }

                    if (read > 0)
                    {
                        lock (receiveLock)
                        {
                            for (int i = 0; i < read && receiveQueue.Count < QueueCapacity; i++)
                            {
                                receiveQueue.Enqueue(readBuffer[i]);
                            }
                        }

                        didWork = true;
                    }

                    if (!didWork)
                    {
                        Thread.Sleep(1);
                    }
                }
            }
            catch (Exception exception)
            {
                lock (lifecycleLock)
                {
                    lastError = exception.Message;
                }
            }
            finally
            {
                Volatile.Write(ref open, 0);
            }
        }

        static void Configure(SafeFileHandle portHandle, int baudRate)
        {
            SetupComm(portHandle, QueueCapacity, QueueCapacity);

            var dcb = new Dcb
            {
                DCBlength = (uint)Marshal.SizeOf<Dcb>()
            };

            if (!GetCommState(portHandle, ref dcb))
            {
                throw LastWin32Exception("Could not read COM configuration.");
            }

            dcb.BaudRate = (uint)baudRate;
            dcb.Flags |= 0x00000001;
            dcb.Flags &= ~0x00000002u;
            dcb.ByteSize = 8;
            dcb.Parity = 0;
            dcb.StopBits = 0;

            if (!SetCommState(portHandle, ref dcb))
            {
                throw LastWin32Exception("Could not apply 8N1 COM configuration.");
            }

            var timeouts = new CommTimeouts
            {
                ReadIntervalTimeout = 5,
                ReadTotalTimeoutMultiplier = 0,
                ReadTotalTimeoutConstant = 5,
                WriteTotalTimeoutMultiplier = 0,
                WriteTotalTimeoutConstant = 100
            };

            if (!SetCommTimeouts(portHandle, ref timeouts))
            {
                throw LastWin32Exception("Could not configure COM timeouts.");
            }

            PurgeComm(portHandle, PurgeTxAbort | PurgeRxAbort | PurgeTxClear | PurgeRxClear);
        }

        static string NormalizePortName(string portName)
        {
            string trimmed = portName.Trim();
            return trimmed.StartsWith(@"\\.\", StringComparison.Ordinal)
                ? trimmed
                : @"\\.\" + trimmed;
        }

        static Win32Exception LastWin32Exception(string message)
        {
            return new Win32Exception(Marshal.GetLastWin32Error(), message);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct Dcb
        {
            public uint DCBlength;
            public uint BaudRate;
            public uint Flags;
            public ushort wReserved;
            public ushort XonLim;
            public ushort XoffLim;
            public byte ByteSize;
            public byte Parity;
            public byte StopBits;
            public byte XonChar;
            public byte XoffChar;
            public byte ErrorChar;
            public byte EofChar;
            public byte EvtChar;
            public ushort wReserved1;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct CommTimeouts
        {
            public uint ReadIntervalTimeout;
            public uint ReadTotalTimeoutMultiplier;
            public uint ReadTotalTimeoutConstant;
            public uint WriteTotalTimeoutMultiplier;
            public uint WriteTotalTimeoutConstant;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFileW(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetupComm(SafeFileHandle file, int inputBufferSize, int outputBufferSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetCommState(SafeFileHandle file, ref Dcb dcb);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetCommState(SafeFileHandle file, ref Dcb dcb);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetCommTimeouts(SafeFileHandle file, ref CommTimeouts timeouts);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool PurgeComm(SafeFileHandle file, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool ReadFile(
            SafeFileHandle file,
            byte[] buffer,
            int bytesToRead,
            out int bytesRead,
            IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool WriteFile(
            SafeFileHandle file,
            byte[] buffer,
            int bytesToWrite,
            out int bytesWritten,
            IntPtr overlapped);
    }
}
