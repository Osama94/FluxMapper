namespace FluxMapper.Tests;

using FluxMapper.Core.Configuration;
using FluxMapper.Tests.Model;

// 5) ReverseMap reversibility validation.
public class ReverseMapTests
{
    private static readonly (bool rejectedWithoutOverride, bool acceptedWithOverride) Result = Compute();

    private static (bool, bool) Compute()
    {
        var rejected = MapperConfiguration.Create(cfg => cfg.CreateMap<FlattenSrc, FlattenDst>().ReverseMap());
        var reversePlan = rejected.GetPlan<FlattenDst, FlattenSrc>();
        var rejectedWithoutOverride = reversePlan.Diagnostics.Any(d => d.Code == "MAP0003");

        var accepted = MapperConfiguration.Create(cfg =>
            cfg.CreateMap<FlattenSrc, FlattenDst>()
               .ReverseMap()
               .Map(d => d.N, s => new Nested { Val = s.NVal }));
        var reversePlan2 = accepted.GetPlan<FlattenDst, FlattenSrc>();
        var acceptedWithOverride = reversePlan2.Diagnostics.All(d => d.Code != "MAP0003");

        return (rejectedWithoutOverride, acceptedWithOverride);
    }

    [Fact]
    public void RejectedWithoutExplicitOverride() => Assert.True(Result.rejectedWithoutOverride);

    [Fact]
    public void AcceptedWithExplicitCompensatingOverride() => Assert.True(Result.acceptedWithOverride);
}
