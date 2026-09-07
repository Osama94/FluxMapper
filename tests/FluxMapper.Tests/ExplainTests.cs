namespace FluxMapper.Tests;

using FluxMapper.Core.Configuration;
using FluxMapper.Core.Execution;
using FluxMapper.Tests.Model;

// 6) Explain().
public class ExplainTests
{
    private static readonly string Explanation = Compute();

    private static string Compute()
    {
        var config = MapperConfiguration.Create(cfg =>
        {
            cfg.CreateMap<Address, AddressDto>();
            cfg.CreateMap<Order, OrderDto>();
            cfg.CreateMap<User, UserDto>();
        });
        var mapper = new Mapper(config);
        return mapper.Explain<User, UserDto>();
    }

    [Fact]
    public void MentionsTheTypePair() => Assert.Contains("User -> UserDto", Explanation);

    [Fact]
    public void MentionsANestedStrategy() => Assert.Contains("NestedMapping", Explanation);

    [Fact]
    public void MentionsACollectionStrategy() => Assert.Contains("CollectionMapping", Explanation);
}
