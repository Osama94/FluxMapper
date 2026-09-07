namespace FluxMapper.Tests;

using FluxMapper.Core.Configuration;
using FluxMapper.Tests.Model;

// 2) Ambiguity detection: two equally-good flattening candidates
//    For the same destination member must fail, never silently pick one.
public class AmbiguityTests
{
    private static readonly (bool notBuildable, bool hasMap2007, bool listsBothCandidates) Result = Compute();

    private static (bool, bool, bool) Compute()
    {
        var config = MapperConfiguration.Create(cfg => cfg.CreateMap<AmbiguousRoot, AmbiguousDto>());
        var plan = config.GetPlan<AmbiguousRoot, AmbiguousDto>();
        var ambiguousDiag = plan.Diagnostics.FirstOrDefault(d => d.Code == "MAP2007");

        return (
            !plan.IsBuildable,
            plan.Diagnostics.Any(d => d.Code == "MAP2007"),
            ambiguousDiag is not null && ambiguousDiag.Candidates.Count == 2);
    }

    [Fact]
    public void Plan_IsNotBuildable() => Assert.True(Result.notBuildable);

    [Fact]
    public void MAP2007Diagnostic_IsPresent() => Assert.True(Result.hasMap2007);

    [Fact]
    public void Diagnostic_ListsBothCandidates() => Assert.True(Result.listsBothCandidates);
}
