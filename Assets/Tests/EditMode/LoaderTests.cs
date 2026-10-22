using DLS.Description;
using DLS.SaveSystem;
using NUnit.Framework;

namespace DLS.Tests
{
	public class LoaderTests
	{
		[Test]
		public void BuiltinNameConflictsAreCaseInsensitive()
		{
			ProjectDescription project = new() { AllCustomChipNames = new[] { "nand", "Adder", "Clock" } };

			string[] conflicts = Loader.FindBuiltinNameConflicts(project);

			CollectionAssert.AreEquivalent(new[] { "nand", "Clock" }, conflicts);
			StringAssert.Contains("'nand'", Loader.BuiltinNameConflictMessage(conflicts));
		}

		[Test]
		public void NoConflictsGiveNoMessage()
		{
			ProjectDescription project = new() { AllCustomChipNames = new[] { "Adder" } };

			Assert.IsEmpty(Loader.FindBuiltinNameConflicts(project));
			Assert.IsEmpty(Loader.BuiltinNameConflictMessage(Loader.FindBuiltinNameConflicts(project)));
			Assert.IsEmpty(Loader.FindBuiltinNameConflicts(new ProjectDescription()));
		}
	}
}
