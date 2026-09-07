namespace FluxMapper.Tests;

using FluxMapper.Core.Configuration;
using FluxMapper.Core.Execution;
using FluxMapper.Tests.Model;

// Immutable collections -- List<T> source into ImmutableArray/
// ImmutableList/ImmutableHashSet destinations.
public class ImmutableCollectionTests
{
    private static readonly (int arrayLength, string first, string second, int listCount, int setCount, bool setHasAda, bool setHasGrace) Result = Compute();

    private static (int, string, string, int, int, bool, bool) Compute()
    {
        var config = MapperConfiguration.Create(cfg => cfg.CreateMap<Roster, RosterDto>());
        config.AssertConfigurationIsValid();
        var mapper = new Mapper(config);

        var dto = mapper.Map<RosterDto>(new Roster
        {
            Names = ["Ada", "Grace", "Ada"],
            NamesAsList = ["Ada", "Grace", "Ada"],
            NamesAsSet = ["Ada", "Grace", "Ada"],
        });

        return (dto.Names.Length, dto.Names[0], dto.Names[1], dto.NamesAsList.Count, dto.NamesAsSet.Count,
            dto.NamesAsSet.Contains("Ada"), dto.NamesAsSet.Contains("Grace"));
    }

    [Fact]
    public void ImmutableArray_ElementCount() => Assert.Equal(3, Result.arrayLength);

    [Fact]
    public void ImmutableArray_ElementValue()
    {
        Assert.Equal("Ada", Result.first);
        Assert.Equal("Grace", Result.second);
    }

    [Fact]
    public void ImmutableList_ElementCount() => Assert.Equal(3, Result.listCount);

    [Fact]
    public void ImmutableHashSet_Dedups()
    {
        Assert.Equal(2, Result.setCount);
        Assert.True(Result.setHasAda);
        Assert.True(Result.setHasGrace);
    }
}
