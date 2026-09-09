namespace FluxMapper.Tests;

// Analyzer regression coverage for the second source-generator broadening round: FLUX0002/FLUX0003 must
// not fire false positives now that the generator itself recognizes naming-convention-relaxed matches,
// one-level flattening, a registered global converter, and a plain struct destination.
public class AnalyzerBroadenedTests
{
    [Fact]
    public async Task NoFalsePositive_OnAStructDestination()
    {
        var diagnostics = await TestHelpers.GetAnalyzerDiagnosticsAsync("""
            using FluxMapper.Abstractions;
            namespace T;
            public class Order { public int Id { get; set; } }
            [MapFrom(typeof(Order))]
            public partial struct CleanStructDto { public int Id { get; set; } }
            """);
        Assert.DoesNotContain(diagnostics, d => d.Id is "FLUX0001" or "FLUX0002" or "FLUX0003");
    }

    [Fact]
    public async Task NoFalsePositive_OnANamingConventionOnlyMatch()
    {
        var diagnostics = await TestHelpers.GetAnalyzerDiagnosticsAsync("""
            using FluxMapper.Abstractions;
            namespace T;
            public class Order { public string user_name { get; set; } = ""; }
            [MapFrom(typeof(Order), NamingConvention = MapFromNamingConvention.SnakeCase)]
            public partial class CleanSnakeDto { public string UserName { get; set; } = ""; }
            """);
        Assert.DoesNotContain(diagnostics, d => d.Id == "FLUX0002");
    }

    [Fact]
    public async Task FLUX0002_StillReported_WhenNamingConventionIsNotEnabled()
    {
        // Control: without NamingConvention = SnakeCase, "UserName" has no exact-name match against
        // "user_name" -- proving the no-false-positive test above is genuinely exercising the relaxed
        // match, not just always passing regardless of the setting.
        var diagnostics = await TestHelpers.GetAnalyzerDiagnosticsAsync("""
            using FluxMapper.Abstractions;
            namespace T;
            public class Order { public string user_name { get; set; } = ""; }
            [MapFrom(typeof(Order))]
            public partial class MismatchedSnakeDto { public string UserName { get; set; } = ""; }
            """);
        Assert.Contains(diagnostics, d => d.Id == "FLUX0002");
    }

    [Fact]
    public async Task NoFalsePositive_OnAFlatteningOnlyMatch()
    {
        var diagnostics = await TestHelpers.GetAnalyzerDiagnosticsAsync("""
            using FluxMapper.Abstractions;
            namespace T;
            public class Address { public string City { get; set; } = ""; }
            public class Order { public Address Address { get; set; } = new(); }
            [MapFrom(typeof(Order))]
            public partial class CleanFlattenedDto { public string AddressCity { get; set; } = ""; }
            """);
        Assert.DoesNotContain(diagnostics, d => d.Id == "FLUX0002");
    }

    [Fact]
    public async Task NoFalsePositive_OnAConverterOnlyMatch()
    {
        var diagnostics = await TestHelpers.GetAnalyzerDiagnosticsAsync("""
            using FluxMapper.Abstractions;
            [assembly: MapFromConverter(typeof(T.Money), typeof(decimal), typeof(T.MoneyConverter))]
            namespace T;
            public class Money { public decimal Amount { get; set; } }
            public class MoneyConverter : IValueConverter<Money, decimal>
            {
                public decimal Convert(Money source, ResolutionContext context) => source.Amount;
            }
            public class Invoice { public Money Total { get; set; } = new(); }
            [MapFrom(typeof(Invoice))]
            public partial class CleanConverterDto { public decimal Total { get; set; } }
            """);
        Assert.DoesNotContain(diagnostics, d => d.Id == "FLUX0002");
    }

    [Fact]
    public async Task NoFalsePositive_OnADictionaryOnlyMatch()
    {
        var diagnostics = await TestHelpers.GetAnalyzerDiagnosticsAsync("""
            using FluxMapper.Abstractions;
            using System.Collections.Generic;
            namespace T;
            public class Order { public Dictionary<string, int> Counts { get; set; } = new(); }
            [MapFrom(typeof(Order))]
            public partial class CleanDictDto { public IReadOnlyDictionary<string, int> Counts { get; set; } = new Dictionary<string, int>(); }
            """);
        Assert.DoesNotContain(diagnostics, d => d.Id == "FLUX0002");
    }
}
