namespace FluxMapper.Tests;

// Analyzer tests -- drive FluxMapper.Analyzers.MapFromAnalyzer against real in-memory compilations via
// TestHelpers' hand-rolled substitute for the Microsoft.CodeAnalysis.Testing package.
public class AnalyzerTests
{
    [Fact]
    public async Task FLUX0001_Reported_ForNonPartialMapFromTarget()
    {
        var diagnostics = await TestHelpers.GetAnalyzerDiagnosticsAsync("""
            using FluxMapper.Abstractions;
            namespace T;
            public class Order { public int Id { get; set; } }
            [MapFrom(typeof(Order))]
            public class NotPartialDto { public int Id { get; set; } }
            """);
        Assert.Contains(diagnostics, d => d.Id == "FLUX0001" && d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task FLUX0002_Reported_WhenNoDestinationMemberMatchesAnySourceMember()
    {
        var diagnostics = await TestHelpers.GetAnalyzerDiagnosticsAsync("""
            using FluxMapper.Abstractions;
            namespace T;
            public class Order { public int Id { get; set; } }
            [MapFrom(typeof(Order))]
            public partial class MismatchedDto { public string TotallyDifferentName { get; set; } = ""; }
            """);
        Assert.Contains(diagnostics, d => d.Id == "FLUX0002" && d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Warning);
    }

    [Fact]
    public async Task NoFalsePositive_OnACorrectPartialMapFromTarget()
    {
        var diagnostics = await TestHelpers.GetAnalyzerDiagnosticsAsync("""
            using FluxMapper.Abstractions;
            namespace T;
            public class Order { public int Id { get; set; } }
            [MapFrom(typeof(Order))]
            public partial class CleanDto { public int Id { get; set; } }
            """);
        Assert.DoesNotContain(diagnostics, d => d.Id is "FLUX0001" or "FLUX0002");
    }
}
