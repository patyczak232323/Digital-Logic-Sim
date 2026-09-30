using System;

namespace Rewired.Core.Ports
{
    public interface IByteEndpoint : IDisposable
    {
        bool IsOpen { get; }
        bool CanWrite { get; }
        string LastError { get; }

        bool TryRead(out byte value);
        bool TryWrite(byte value);
        void Clear();
    }
}
