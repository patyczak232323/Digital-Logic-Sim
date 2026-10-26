using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DLS.Game;
using DLS.RHDL;
using DLS.Simulation;
using NUnit.Framework;
using UnityEngine;

namespace DLS.Tests
{
	// Compiles every complete RHDL circuit shown in the Markdown documentation, so a syntax
	// change cannot leave README.md, Docs/ or the example READMEs describing code that no
	// longer builds. Fragments (code blocks without a whole "circuit ... end") are skipped.
	public class DocumentationExampleTests
	{
		static readonly Regex CodeBlock = new(@"^```[^\n]*\n(.*?)^```", RegexOptions.Singleline | RegexOptions.Multiline);

		static string RepositoryRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

		[TearDown]
		public void TearDown() => RewiredEngine.Reset();

		public static IEnumerable<TestCaseData> DocumentedCircuits()
		{
			string root = RepositoryRoot;
			IEnumerable<string> files = new[] { Path.Combine(root, "README.md") }
				.Concat(Directory.GetFiles(Path.Combine(root, "Docs"), "*.md"))
				.Concat(Directory.GetFiles(Path.Combine(root, "Examples"), "*.md", SearchOption.AllDirectories));

			foreach (string file in files)
			{
				string relativePath = Path.ChangeExtension(file.Substring(root.Length), null).Replace('\\', '/').TrimStart('/');
				int index = 0;
				foreach (Match match in CodeBlock.Matches(File.ReadAllText(file)))
				{
					string code = match.Groups[1].Value.Trim();
					if (!code.StartsWith("circuit ") || !code.EndsWith("end")) continue;

					index++;
					yield return new TestCaseData(code).SetName($"{relativePath} #{index}");
				}
			}
		}

		[TestCaseSource(nameof(DocumentedCircuits))]
		public void DocumentedCircuitCompiles(string source)
		{
			ChipLibrary library = TestLibrary.Create();
			RhdlCompileResult result = RhdlCompiler.Compile(source, library);
			Assert.IsTrue(result.Success, "Documented circuit failed to compile:\n" + string.Join("\n", result.Diagnostics.Select(d => d.ToString())));
		}

		[Test]
		public void DocumentationContainsCircuits()
		{
			Assert.GreaterOrEqual(DocumentedCircuits().Count(), 5, "the code block scan found too few circuits; did the Markdown layout change?");
		}
	}
}
