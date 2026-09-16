namespace DLS.Graphics
{
	// Keep paused-state audio updates on the same runtime that executes logic.
	public static class Simulator
	{
		public static void UpdateInPausedState() => DLS.Simulation.DeterministicSimulator.UpdateInPausedState();
	}
}
