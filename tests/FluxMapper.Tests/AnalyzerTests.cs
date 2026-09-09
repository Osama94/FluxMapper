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
        Assert.DoesNotContain(diagnostics, d => d.Id is "FLUX0001" or "FLUX0002" or "FLUX0003");
    }

    [Fact]
    public async Task FLUX0003_Reported_WhenNoUsableConstructorExists()
    {
        // No public parameterless constructor, and the one parameterized constructor's parameter name
        // matches nothing on the source type (and has no default value to fall back to) -- the generator
        // has no way to construct this type at all.
        var diagnostics = await TestHelpers.GetAnalyzerDiagnosticsAsync("""
            using FluxMapper.Abstractions;
            namespace T;
            public class Order { public int Id { get; set; } }
            [MapFrom(typeof(Order))]
            public partial class NoUsableCtorDto
            {
                public string TotallyDifferentName { get; }
                public NoUsableCtorDto(string TotallyDifferentName) => this.TotallyDifferentName = TotallyDifferentName;
            }
            """);
        Assert.Contains(diagnostics, d => d.Id == "FLUX0003" && d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task NoFalsePositive_OnAPositionalRecordMapFromTarget()
    {
        var diagnostics = await TestHelpers.GetAnalyzerDiagnosticsAsync("""
            using FluxMapper.Abstractions;
            namespace T;
            public class Order { public int Id { get; set; } public decimal Total { get; set; } }
            [MapFrom(typeof(Order))]
            public partial record CleanPositionalDto(int Id, decimal Total);
            """);
        Assert.DoesNotContain(diagnostics, d => d.Id is "FLUX0001" or "FLUX0002" or "FLUX0003");
    }

    [Fact]
    public async Task NoFalsePositive_OnAPlainRecordWithInitOnlyProperties()
    {
        // Regression check for FLUX0001's partial-detection: before this fix it only recognized
        // ClassDeclarationSyntax, so a `partial record` -- correctly partial -- would have been
        // misreported as FLUX0001 ("not declared partial"), since `record` parses as the sibling node
        // RecordDeclarationSyntax rather than ClassDeclarationSyntax.
        var diagnostics = await TestHelpers.GetAnalyzerDiagnosticsAsync("""
            using FluxMapper.Abstractions;
            namespace T;
            public class Order { public int Id { get; set; } }
            [MapFrom(typeof(Order))]
            public partial record CleanInitPropsDto { public int Id { get; init; } }
            """);
        Assert.DoesNotContain(diagnostics, d => d.Id is "FLUX0001" or "FLUX0002" or "FLUX0003");
    }

    [Fact]
    public async Task FLUX0003_NotReported_ForAValueTypeDestination()
    {
        // new T() is always legal for a struct/record struct regardless of what constructors it declares,
        // so FLUX0003 must never fire for one even though it superficially looks like the FLUX0003 case
        // above (a parameter name that matches nothing on the source).
        var diagnostics = await TestHelpers.GetAnalyzerDiagnosticsAsync("""
            using FluxMapper.Abstractions;
            namespace T;
            public class Order { public int Id { get; set; } }
            [MapFrom(typeof(Order))]
            public partial record struct StructDto(int Id)
            {
                public string TotallyDifferentName { get; set; } = "";
            }
            """);
        Assert.DoesNotContain(diagnostics, d => d.Id == "FLUX0003");
    }
}
