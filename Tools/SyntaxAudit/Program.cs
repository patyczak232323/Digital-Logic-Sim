using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

string root = args.Length > 0 ? Path.GetFullPath(args[0]) : Directory.GetCurrentDirectory();
string[] roots =
{
    Path.Combine(root, "Assets", "Scripts"),
    Path.Combine(root, "Tools")
};

int fileCount = 0;
int errorCount = 0;
CSharpParseOptions options = new(LanguageVersion.Preview, DocumentationMode.None, SourceCodeKind.Regular);

foreach (string scanRoot in roots)
{
    if (!Directory.Exists(scanRoot)) continue;

    foreach (string file in Directory.EnumerateFiles(scanRoot, "*.cs", SearchOption.AllDirectories))
    {
        // Third-party Newtonsoft test sources are intentionally shipped as package
        // fixtures and are not part of the DLS runtime/editor code we modify.
        if (file.Contains($"{Path.DirectorySeparatorChar}Description{Path.DirectorySeparatorChar}Serialization{Path.DirectorySeparatorChar}Newtonsoft{Path.DirectorySeparatorChar}Tests{Path.DirectorySeparatorChar}"))
            continue;

        fileCount++;
        string text = File.ReadAllText(file);
        SyntaxTree tree = CSharpSyntaxTree.ParseText(text, options, file);

        foreach (Diagnostic diagnostic in tree.GetDiagnostics())
        {
            if (diagnostic.Severity != DiagnosticSeverity.Error) continue;
            errorCount++;
            Console.Error.WriteLine(diagnostic.ToString());
        }
    }
}

Console.WriteLine($"Parsed {fileCount} C# files; syntax errors: {errorCount}");
return errorCount == 0 ? 0 : 1;
