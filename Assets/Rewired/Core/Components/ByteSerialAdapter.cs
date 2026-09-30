using System;
using Rewired.Core.Ports;
using Rewired.Core.Simulation;

namespace Rewired.Core.Components
{
    public sealed class ByteSerialAdapter : ITickComponent
    {
        readonly IByteEndpoint endpoint;
        bool previousTxSend;
        bool previousRxRead;
        bool hasReceiveByte;
        byte receiveByte;

        public ByteSerialAdapter(IByteEndpoint endpoint)
        {
            this.endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));

            TxData = new Signal(8);
            TxSend = new Signal(1);
            RxRead = new Signal(1);
            Reset = new Signal(1);

            TxReady = new Signal(1);
            RxData = new Signal(8);
            RxValid = new Signal(1);
            Connected = new Signal(1);
        }

        public Signal TxData { get; }
        public Signal TxSend { get; }
        public Signal RxRead { get; }
        public Signal Reset { get; }

        public Signal TxReady { get; }
        public Signal RxData { get; }
        public Signal RxValid { get; }
        public Signal Connected { get; }

        public ulong TransmittedBytes { get; private set; }
        public ulong ReceivedBytes { get; private set; }
        public ulong RejectedTransmits { get; private set; }

        public void Tick()
        {
            bool txSend = TxSend.High;
            bool rxRead = RxRead.High;

            if (Reset.High)
            {
                endpoint.Clear();
                hasReceiveByte = false;
                receiveByte = 0;
                previousTxSend = txSend;
                previousRxRead = rxRead;
                PublishOutputs();
                return;
            }

            if (rxRead && !previousRxRead && hasReceiveByte)
            {
                hasReceiveByte = false;
            }

            if (!hasReceiveByte && endpoint.TryRead(out byte nextByte))
            {
                receiveByte = nextByte;
                hasReceiveByte = true;
                ReceivedBytes++;
            }

            if (txSend && !previousTxSend)
            {
                if (endpoint.IsOpen && endpoint.CanWrite && endpoint.TryWrite((byte)TxData.Value))
                {
                    TransmittedBytes++;
                }
                else
                {
                    RejectedTransmits++;
                }
            }

            previousTxSend = txSend;
            previousRxRead = rxRead;
            PublishOutputs();
        }

        void PublishOutputs()
        {
            bool open = endpoint.IsOpen;
            Connected.Set((ushort)(open ? 1 : 0));
            TxReady.Set((ushort)(open && endpoint.CanWrite ? 1 : 0));
            RxValid.Set((ushort)(hasReceiveByte ? 1 : 0));
            RxData.Set(hasReceiveByte ? receiveByte : (ushort)0);
        }
    }
}
