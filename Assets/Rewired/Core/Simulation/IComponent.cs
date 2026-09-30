namespace Rewired.Core.Simulation
{
    public interface ICombinationalComponent
    {
        bool Evaluate();
    }

    public interface ITickComponent
    {
        void Tick();
    }
}
