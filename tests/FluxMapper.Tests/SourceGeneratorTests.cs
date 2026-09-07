namespace FluxMapper.Tests;

using FluxMapper.Tests.Model;

// Source generator --
// GeneratedOrderDto.MapFrom(Order) is emitted at compile time by FluxMapper.SourceGenerator from
// the [MapFrom(typeof(Order))] attribute; calling it involves zero reflection and zero
// Expression.Compile() -- the one path in this project that can honestly claim AOT-safety.
public class SourceGeneratorTests
{
    private static readonly GeneratedOrderDto Generated = GeneratedOrderDto.MapFrom(new Order { Id = 7, Total = 42.5m });

    [Fact]
    public void GeneratedMapFrom_CopiesId() => Assert.Equal(7, Generated.Id);

    [Fact]
    public void GeneratedMapFrom_CopiesTotal() => Assert.Equal(42.5m, Generated.Total);
}
