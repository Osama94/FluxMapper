namespace FluxMapper.Tests;

using FluxMapper.Abstractions;
using Microsoft.CodeAnalysis.Diagnostics;

// Shared by AnalyzerTests: compiles `source` in-memory and runs FluxMapper.Analyzers.MapFromAnalyzer
// against it, returning whatever diagnostics the analyzer itself reports (compiler diagnostics are
// excluded so a deliberately-broken snippet's own compile errors don't get mixed in). This is a
// hand-rolled substitute for the Microsoft.CodeAnalysis.Testing package.
internal static class TestHelpers
{
    public static async Task<System.Collections.Immutable.ImmutableArray<Microsoft.CodeAnalysis.Diagnostic>> GetAnalyzerDiagnosticsAsync(string source)
    {
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source);

        var runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        var references = new List<Microsoft.CodeAnalysis.MetadataReference>
        {
            Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(typeof(MapFromAttribute).Assembly.Location),
            Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll")),
            Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Private.CoreLib.dll")),
            Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "netstandard.dll")),
        };

        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
            "AnalyzerTest",
            new[] { tree },
            references,
            new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));

        var analyzers = System.Collections.Immutable.ImmutableArray.Create<Microsoft.CodeAnalysis.Diagnostics.DiagnosticAnalyzer>(
            new FluxMapper.Analyzers.MapFromAnalyzer());
        var withAnalyzers = compilation.WithAnalyzers(analyzers);
        return await withAnalyzers.GetAnalyzerDiagnosticsAsync();
    }
}
