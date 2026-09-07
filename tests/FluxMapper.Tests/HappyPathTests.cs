namespace FluxMapper.Tests;

using FluxMapper.Core.Configuration;
using FluxMapper.Core.Execution;
using FluxMapper.Tests.Model;

// 1) Happy path: flat + nested + collection construction mapping.
public class HappyPathTests
{
    private static readonly (bool validates, int id, string name, string city, int orderCount, int firstOrderId, decimal firstOrderTotal) Result = Compute();

    private static (bool, int, string, string, int, int, decimal) Compute()
    {
        var config = MapperConfiguration.Create(cfg =>
        {
            cfg.CreateMap<Address, AddressDto>();
            cfg.CreateMap<Order, OrderDto>();
            cfg.CreateMap<User, UserDto>();
        });

        Exception? validationError = null;
        try { config.AssertConfigurationIsValid(); } catch (Exception ex) { validationError = ex; }

        var mapper = new Mapper(config);
        var user = new User
        {
            Id = 42,
            Name = "Ada Lovelace",
            Address = new Address { City = "London", Street = "Baker St" },
            Orders = [new Order { Id = 1, Total = 9.99m }, new Order { Id = 2, Total = 19.99m }],
        };

        var dto = mapper.Map<UserDto>(user);
        return (validationError is null, dto.Id, dto.Name, dto.Address.City, dto.Orders.Count, dto.Orders[0].Id, dto.Orders[0].Total);
    }

    [Fact]
    public void AssertConfigurationIsValid_DoesNotThrow() => Assert.True(Result.validates);

    [Fact]
    public void FlatId_IsCopied() => Assert.Equal(42, Result.id);

    [Fact]
    public void FlatName_IsCopied() => Assert.Equal("Ada Lovelace", Result.name);

    [Fact]
    public void NestedAddressCity_IsCopied() => Assert.Equal("London", Result.city);

    [Fact]
    public void CollectionCount_IsPreserved() => Assert.Equal(2, Result.orderCount);

    [Fact]
    public void CollectionElement_IsMapped()
    {
        Assert.Equal(1, Result.firstOrderId);
        Assert.Equal(9.99m, Result.firstOrderTotal);
    }
}
