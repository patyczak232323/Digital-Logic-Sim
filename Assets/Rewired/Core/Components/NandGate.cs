using System;
using Rewired.Core.Simulation;

namespace Rewired.Core.Components
{
    public sealed class NandGate : ICombinationalComponent
    {
        public NandGate(Signal inputA, Signal inputB, Signal output)
        {
            InputA = inputA ?? throw new ArgumentNullException(nameof(inputA));
            InputB = inputB ?? throw new ArgumentNullException(nameof(inputB));
            Output = output ?? throw new ArgumentNullException(nameof(output));

            if (InputA.Width != 1 || InputB.Width != 1 || Output.Width != 1)
            {
                throw new ArgumentException("NAND pins must be one-bit signals.");
            }
        }

        public Signal InputA { get; }
        public Signal InputB { get; }
        public Signal Output { get; }

        public bool Evaluate()
        {
            return Output.Set((ushort)(InputA.High && InputB.High ? 0 : 1));
        }
    }
}
