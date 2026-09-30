using System;
using System.Collections.Generic;

namespace Rewired.Core.Simulation
{
    public sealed class SimulationEngine
    {
        readonly List<ICombinationalComponent> combinational = new List<ICombinationalComponent>();
        readonly List<ITickComponent> ticked = new List<ITickComponent>();

        public SimulationEngine(int maxDeltaCycles = 1024)
        {
            if (maxDeltaCycles < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxDeltaCycles));
            }

            MaxDeltaCycles = maxDeltaCycles;
        }

        public int MaxDeltaCycles { get; }
        public ulong StepNumber { get; private set; }

        public void Add(ICombinationalComponent component)
        {
            if (component == null) throw new ArgumentNullException(nameof(component));
            combinational.Add(component);
        }

        public void Add(ITickComponent component)
        {
            if (component == null) throw new ArgumentNullException(nameof(component));
            ticked.Add(component);
        }

        public void RunStep()
        {
            Settle();

            for (int i = 0; i < ticked.Count; i++)
            {
                ticked[i].Tick();
            }

            Settle();
            StepNumber++;
        }

        public int Settle()
        {
            for (int delta = 0; delta < MaxDeltaCycles; delta++)
            {
                bool changed = false;

                for (int i = 0; i < combinational.Count; i++)
                {
                    changed |= combinational[i].Evaluate();
                }

                if (!changed)
                {
                    return delta;
                }
            }

            throw new InvalidOperationException(
                "Combinational network did not converge within " + MaxDeltaCycles + " delta cycles.");
        }
    }
}
