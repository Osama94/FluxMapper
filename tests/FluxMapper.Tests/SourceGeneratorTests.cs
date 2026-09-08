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

    [Fact]
    public void GeneratedMapFrom_CollectionMember_HandlesMoreThanTwoElements()
    {
        // The 2-element case above shares a code path with any other count, but this exercises the
        // indexed loop (and, on net10.0, the CollectionsMarshal.SetCount/AsSpan fast path) across enough
        // elements that an off-by-one in the index bound or the span length would show up.
        var user = new User
        {
            Id = 6,
            Name = "ManyOrders",
            Address = new Address { City = "Riyadh", Street = "King Fahd Rd" },
            Orders = [.. Enumerable.Range(1, 5).Select(i => new Order { Id = i, Total = i * 10m })],
        };

        var dto = GeneratedUserDto.MapFrom(user);

        Assert.Equal(5, dto.Orders.Count);
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(i + 1, dto.Orders[i].Id);
            Assert.Equal((i + 1) * 10m, dto.Orders[i].Total);
        }
    }

    [Fact]
    public void MapFrom_NullSource_StillThrowsArgumentNullException()
    {
        // MapFrom (the public entry point) keeps its ArgumentNullException.ThrowIfNull guard even
        // though the actual mapping body moved to MapFromCore -- this is the regression test for that
        // split not accidentally dropping the guard on the public method.
        Assert.Throws<ArgumentNullException>(() => GeneratedOrderDto.MapFrom(null!));
        Assert.Throws<ArgumentNullException>(() => GeneratedUserDto.MapFrom(null!));
    }

    [Fact]
    public void GeneratedMapFrom_CollectionMember_NullElementThrowsNullReferenceException()
    {
        // Documents a deliberate, accepted perf/correctness tradeoff: composing a collection element
        // calls the target type's MapFromCore directly (skipping its own null-argument guard) to avoid a
        // redundant check on the hot path -- see MapFromGenerator's type doc comment. A null element in
        // the source list is the one case where that shows up: it surfaces as a NullReferenceException
        // from inside MapFromCore rather than the clean ArgumentNullException MapFrom would have given.
        var userWithNullOrder = new User
        {
            Id = 7,
            Name = "HasNullOrder",
            Address = new Address(),
            Orders = [new Order { Id = 1, Total = 1m }, null!],
        };

        Assert.Throws<NullReferenceException>(() => GeneratedUserDto.MapFrom(userWithNullOrder));
    }
}
