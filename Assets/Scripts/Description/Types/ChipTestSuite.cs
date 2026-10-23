namespace DLS.Description
{
	// Expected outputs recorded from a combinational chip's truth table.
	// Pin names are stored so a later run can tell that the chip's pins changed.
	public class ChipTestSuite
	{
		public string ChipName;
		public string[] InputNames;
		public string[] OutputNames;
		public uint[][] Inputs;
		public uint[][] Outputs;
	}
}
