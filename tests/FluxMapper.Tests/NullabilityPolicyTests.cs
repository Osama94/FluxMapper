namespace FluxMapper.Tests;

using FluxMapper.Abstractions;
using FluxMapper.Core.Configuration;
using FluxMapper.Core.Execution;
using FluxMapper.Tests.Model;

// 3) Nullability policy: nullable source into non-nullable
//    Destination with no configured policy is a validation error; an explicit NullSubstitute fixes it.
public class NullabilityPolicyTests
{
    private static readonly (bool unconfiguredIsError, bool configuredIsBuildable, string substituteValue, string passthroughValue) Result = Compute();

    private static (bool, bool, string, string) Compute()
    {
        var unconfigured = MapperConfiguration.Create(cfg => cfg.CreateMap<Person, PersonDto>());
        var plan = unconfigured.GetPlan<Person, PersonDto>();
        var unconfiguredIsError = plan.Diagnostics.Any(d => d.Code == "MAP0001" && d.Severity == DiagnosticSeverity.Error);

        var configured = MapperConfiguration.Create(cfg =>
            cfg.CreateMap<Person, PersonDto>().NullSubstitute(d => d.Nickname, "N/A"));
        var fixedPlan = configured.GetPlan<Person, PersonDto>();

        var mapper = new Mapper(configured);
        var dto = mapper.Map<PersonDto>(new Person { Nickname = null });
        var dto2 = mapper.Map<PersonDto>(new Person { Nickname = "Ada" });

        return (unconfiguredIsError, fixedPlan.IsBuildable, dto.Nickname, dto2.Nickname);
    }

    [Fact]
    public void UnconfiguredNullableToNonNullable_IsAnError() => Assert.True(Result.unconfiguredIsError);

    [Fact]
    public void NullSubstitute_ClearsTheError() => Assert.True(Result.configuredIsBuildable);

    [Fact]
    public void SubstituteValue_IsActuallyUsedAtRuntime() => Assert.Equal("N/A", Result.substituteValue);

    [Fact]
    public void NonNullValue_PassesThroughUnchanged() => Assert.Equal("Ada", Result.passthroughValue);
}
