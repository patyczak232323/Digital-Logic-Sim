using System.IO;
using System.Linq;
using DLS.Description;
using DLS.Export;
using DLS.Game;
using DLS.RHDL;
using NUnit.Framework;
using UnityEngine;

namespace DLS.Tests
{
	public class SchematicSvgTests
	{
		static string ExamplesRoot => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Examples", "RHDL"));

		ChipLibrary library;

		[SetUp]
		public void SetUp() => library = TestLibrary.Create();

		[Test]
		public void SchematicSvgIsWellFormedAndDrawsEveryElement()
		{
			BuildExample("Basics/01_HALF_ADDER.rhdl");
			ChipDescription fullAdder = BuildExample("Basics/02_FULL_ADDER.rhdl");
			DevChipInstance chip = DevChipInstance.LoadFromDescriptionTest(fullAdder, library).devChip;

			string svg = SchematicSvgExporter.Export(chip, "Full <Adder>");
			System.Xml.XmlDocument document = new();
			document.LoadXml(svg);

			System.Xml.XmlNamespaceManager ns = new(document.NameTable);
			ns.AddNamespace("s", "http://www.w3.org/2000/svg");
			int pins = fullAdder.InputPins.Length + fullAdder.OutputPins.Length;
			int subChipPins = chip.GetSubchips().Sum(c => c.AllPins.Length);
			// Background + one body per subchip + one handle per dev pin.
			Assert.AreEqual(1 + fullAdder.SubChips.Length + pins, document.SelectNodes("//s:rect", ns).Count);
			Assert.AreEqual(pins + subChipPins, document.SelectNodes("//s:circle", ns).Count);
			Assert.AreEqual(chip.Wires.Count(w => w.WirePointCount >= 2), document.SelectNodes("//s:polyline", ns).Count);
			StringAssert.Contains("Full &lt;Adder&gt;", svg);
		}

		ChipDescription BuildExample(string relativePath)
		{
			RhdlCompileResult result = RhdlCompiler.Compile(File.ReadAllText(Path.Combine(ExamplesRoot, relativePath)), library);
			Assert.IsTrue(result.Success, relativePath + " failed to compile");
			library.NotifyChipSaved(result.Description, false);
			return result.Description;
		}
	}
}
