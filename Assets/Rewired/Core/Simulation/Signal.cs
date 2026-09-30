using System;

namespace Rewired.Core.Simulation
{
    public sealed class Signal
    {
        readonly ushort mask;
        ushort value;

        public Signal(int width, ushort initialValue = 0)
        {
            if (width < 1 || width > 16)
            {
                throw new ArgumentOutOfRangeException(nameof(width), "Signal width must be between 1 and 16 bits.");
            }

            Width = width;
            mask = width == 16 ? ushort.MaxValue : (ushort)((1u << width) - 1u);
            value = (ushort)(initialValue & mask);
        }

        public int Width { get; }
        public ushort Value => value;
        public bool High => (value & 1) != 0;

        public bool Set(ushort nextValue)
        {
            nextValue = (ushort)(nextValue & mask);
            if (nextValue == value)
            {
                return false;
            }

            value = nextValue;
            return true;
        }
    }
}
