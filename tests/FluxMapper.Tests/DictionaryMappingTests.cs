namespace FluxMapper.Tests;

using FluxMapper.Core.Configuration;
using FluxMapper.Core.Execution;
using FluxMapper.Tests.Model;

// Dictionary mapping -- both a directly-convertible-value dictionary
// (Dictionary<string,int>) and a complex-value dictionary (Dictionary<string,Tag>) on the same type.
public class DictionaryMappingTests
{
    private static readonly (int countsCount, int applesCount, int pearsCount, string featuredLabel) Result = Compute();

    private static (int, int, int, string) Compute()
    {
        var config = MapperConfiguration.Create(cfg =>
        {
            cfg.CreateMap<Tag, TagDto>();
            cfg.CreateMap<Catalog, CatalogDto>();
        });

        config.AssertConfigurationIsValid();
        var mapper = new Mapper(config);
        var catalog = new Catalog
        {
            Counts = new Dictionary<string, int> { ["apples"] = 3, ["pears"] = 5 },
            Tags = new Dictionary<string, Tag> { ["featured"] = new Tag { Label = "Featured" } },
        };

        var dto = mapper.Map<CatalogDto>(catalog);
        return (dto.Counts.Count, dto.Counts["apples"], dto.Counts["pears"], dto.Tags["featured"].Label);
    }

    [Fact]
    public void SimpleValueDictionary_CountPreserved() => Assert.Equal(2, Result.countsCount);

    [Fact]
    public void SimpleValueDictionary_ValuesCopied()
    {
        Assert.Equal(3, Result.applesCount);
        Assert.Equal(5, Result.pearsCount);
    }

    [Fact]
    public void ComplexValueDictionary_MapsNestedType() => Assert.Equal("Featured", Result.featuredLabel);
}
