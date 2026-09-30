using System;
using Rewired.Core.Components;
using UnityEngine;

namespace Rewired.Platform.Windows
{
    public sealed class WindowsComController : MonoBehaviour
    {
        [SerializeField] string portName = "COM10";
        [SerializeField] int baudRate = 115200;

        WindowsComEndpoint endpoint;

        public ByteSerialAdapter Adapter { get; private set; }
        public bool IsConnected => endpoint != null && endpoint.IsOpen;
        public string LastError => endpoint == null ? string.Empty : endpoint.LastError;

        public void Connect()
        {
            Disconnect();

            endpoint = new WindowsComEndpoint();
            endpoint.Open(portName, baudRate);
            Adapter = new ByteSerialAdapter(endpoint);
        }

        public void Connect(string newPortName, int newBaudRate)
        {
            portName = newPortName;
            baudRate = newBaudRate;
            Connect();
        }

        public void Disconnect()
        {
            Adapter = null;

            if (endpoint != null)
            {
                endpoint.Dispose();
                endpoint = null;
            }
        }

        public SerialAdapterSnapshot Step(byte txData, bool txSend, bool rxRead, bool reset)
        {
            if (Adapter == null)
            {
                return new SerialAdapterSnapshot(false, false, 0, false, LastError);
            }

            Adapter.TxData.Set(txData);
            Adapter.TxSend.Set((ushort)(txSend ? 1 : 0));
            Adapter.RxRead.Set((ushort)(rxRead ? 1 : 0));
            Adapter.Reset.Set((ushort)(reset ? 1 : 0));
            Adapter.Tick();

            return new SerialAdapterSnapshot(
                Adapter.TxReady.High,
                Adapter.RxValid.High,
                (byte)Adapter.RxData.Value,
                Adapter.Connected.High,
                LastError);
        }

        void OnDestroy()
        {
            Disconnect();
        }
    }

    public readonly struct SerialAdapterSnapshot
    {
        public SerialAdapterSnapshot(bool txReady, bool rxValid, byte rxData, bool connected, string error)
        {
            TxReady = txReady;
            RxValid = rxValid;
            RxData = rxData;
            Connected = connected;
            Error = error ?? string.Empty;
        }

        public bool TxReady { get; }
        public bool RxValid { get; }
        public byte RxData { get; }
        public bool Connected { get; }
        public string Error { get; }
    }
}
