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

    // GeneratedUserDto.MapFrom(User) -- verifies the generator's nested (Address) and List<T> collection
    // (Orders) composition, not just the flat case above. Like GeneratedOrderDto, none of this method
    // body exists in this file; if the generator's matching logic silently failed to recognize either
    // member, these members would sit at their default (an empty GeneratedAddressDto / an empty list)
    // instead of raising a compile error, so these assertions -- not just a successful build -- are what
    // actually catches that failure mode.
    private static readonly GeneratedUserDto GeneratedUser = GeneratedUserDto.MapFrom(new User
    {
        Id = 3,
        Name = "Grace",
        Address = new Address { City = "Boston", Street = "Main St" },
        Orders = [new Order { Id = 1, Total = 10m }, new Order { Id = 2, Total = 20m }],
    });

    [Fact]
    public void GeneratedMapFrom_ComposesNestedMember()
    {
        Assert.Equal("Boston", GeneratedUser.Address.City);
        Assert.Equal("Main St", GeneratedUser.Address.Street);
    }

    [Fact]
    public void GeneratedMapFrom_ComposesCollectionMember()
    {
        Assert.Equal(2, GeneratedUser.Orders.Count);
        Assert.Equal(1, GeneratedUser.Orders[0].Id);
        Assert.Equal(10m, GeneratedUser.Orders[0].Total);
        Assert.Equal(2, GeneratedUser.Orders[1].Id);
        Assert.Equal(20m, GeneratedUser.Orders[1].Total);
    }

    [Fact]
    public void GeneratedMapFrom_NestedMember_NullSourceMapsToNullNotAnEmptyInstance()
    {
        var withNullAddress = GeneratedUserDto.MapFrom(new User { Id = 4, Name = "NoAddress", Address = null!, Orders = [] });
        Assert.Null(withNullAddress.Address);
    }

    [Fact]
    public void GeneratedMapFrom_CollectionMember_EmptySourceMapsToEmptyList()
    {
        var withNoOrders = GeneratedUserDto.MapFrom(new User { Id = 5, Name = "NoOrders", Address = new Address(), Orders = [] });
        Assert.Empty(withNoOrders.Orders);
    }
}
