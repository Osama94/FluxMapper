namespace FluxMapper.Tests;

using FluxMapper.Abstractions;

// Coverage for the source-generator broadening beyond flat, parameterless-constructor DTOs: [MapFrom] now
// supports record/record class/record struct destinations at all -- previously the generator's own syntax
// filter silently excluded every `record` declaration, regardless of shape, despite its codegen already
// branching on IsRecord (see MapFromGenerator's type doc comment) -- including a positional record's
// primary constructor, and more generally any destination reachable through exactly one resolvable public
// constructor when no public parameterless constructor exists. None of the `MapFrom`/`MapFromCore` method
// bodies below exist in this file; a build failure here means the generator didn't run, or generated
// something that doesn't compile -- the same "compile error beats a runtime assertion" property the
// existing SourceGeneratorTests.cs relies on.

public class SimpleOrderSource
{
    public int Id { get; set; }
    public decimal Total { get; set; }
}

public class AddressSource
{
    public string City { get; set; } = "";
    public string Street { get; set; } = "";
}

public class CompositeSource
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public AddressSource Address { get; set; } = new();
    public List<SimpleOrderSource> Orders { get; set; } = [];
}

// ---- Plain record, init-only properties, no primary constructor -- the shape that was silently
// ungenerated for every record before this fix, not just the positional one below. ----
[MapFrom(typeof(SimpleOrderSource))]
public partial record PlainRecordDto
{
    public int Id { get; init; }
    public decimal Total { get; init; }
}

// ---- Positional record -- every member is a primary-constructor parameter; no parameterless constructor
// exists at all, so this only works via the new constructor-call codegen path. ----
[MapFrom(typeof(SimpleOrderSource))]
public partial record PositionalOrderDto(int Id, decimal Total);

// ---- Positional record composing Nested + Collection constructor arguments -- proves the same
// Direct/Nested/Collection classification a plain member gets also applies to a constructor parameter. ----
[MapFrom(typeof(AddressSource))]
public partial record PositionalAddressDto(string City, string Street);

[MapFrom(typeof(SimpleOrderSource))]
public partial record PositionalNestedOrderDto(int Id, decimal Total);

[MapFrom(typeof(CompositeSource))]
public partial record PositionalCompositeDto(int Id, string Name, PositionalAddressDto Address, List<PositionalNestedOrderDto> Orders);

// ---- Positional record with an extra settable property alongside the primary constructor -- proves the
// constructor call and a trailing object initializer can coexist in one generated method. ----
[MapFrom(typeof(SimpleOrderSource))]
public partial record PositionalOrderWithExtraDto(int Id, decimal Total)
{
    public string Note { get; set; } = "unset";
}

// ---- record struct, positional -- new T() is unconditionally legal for any struct, so this always goes
// through the ordinary object-initializer path; this exercises the "partial record struct" keyword choice
// rather than constructor-call codegen. ----
[MapFrom(typeof(SimpleOrderSource))]
public partial record struct PositionalOrderStructDto(int Id, decimal Total);

// ---- Plain (non-record) class reachable only via a single parameterized public constructor -- proves the
// capability is general ("any destination reachable through exactly one resolvable public constructor"),
// not a record-specific special case. ----
[MapFrom(typeof(SimpleOrderSource))]
public partial class ManualCtorDto
{
    public int Id { get; }
    public decimal Total { get; }

    public ManualCtorDto(int Id, decimal Total)
    {
        this.Id = Id;
        this.Total = Total;
    }
}

public class SourceGeneratorRecordTests
{
    [Fact]
    public void PlainRecord_WithInitOnlyProperties_IsGenerated()
    {
        var dto = PlainRecordDto.MapFrom(new SimpleOrderSource { Id = 1, Total = 9.99m });
        Assert.Equal(1, dto.Id);
        Assert.Equal(9.99m, dto.Total);
    }

    [Fact]
    public void PositionalRecord_FlatMembers_AreConstructorArguments()
    {
        var dto = PositionalOrderDto.MapFrom(new SimpleOrderSource { Id = 2, Total = 19.99m });
        Assert.Equal(2, dto.Id);
        Assert.Equal(19.99m, dto.Total);
    }

    [Fact]
    public void PositionalRecord_ComposesNestedAndCollectionConstructorArguments()
    {
        var source = new CompositeSource
        {
            Id = 3,
            Name = "Grace",
            Address = new AddressSource { City = "Boston", Street = "Main St" },
            Orders = [new SimpleOrderSource { Id = 1, Total = 10m }, new SimpleOrderSource { Id = 2, Total = 20m }],
        };

        var dto = PositionalCompositeDto.MapFrom(source);

        Assert.Equal(3, dto.Id);
        Assert.Equal("Grace", dto.Name);
        Assert.Equal("Boston", dto.Address.City);
        Assert.Equal("Main St", dto.Address.Street);
        Assert.Equal(2, dto.Orders.Count);
        Assert.Equal(1, dto.Orders[0].Id);
        Assert.Equal(10m, dto.Orders[0].Total);
        Assert.Equal(2, dto.Orders[1].Id);
        Assert.Equal(20m, dto.Orders[1].Total);
    }

    [Fact]
    public void PositionalRecord_NestedConstructorArgument_NullSourceMapsToNullNotAnEmptyInstance()
    {
        var source = new CompositeSource { Id = 4, Name = "NoAddress", Address = null!, Orders = [] };
        var dto = PositionalCompositeDto.MapFrom(source);
        Assert.Null(dto.Address);
    }

    [Fact]
    public void PositionalRecord_WithExtraProperty_CombinesConstructorArgsAndObjectInitializer()
    {
        // Note has no matching source member, so it's left at its own default -- proving the trailing
        // initializer block still runs (with zero lines from source, in this case) alongside the
        // constructor call rather than one silently replacing the other.
        var dto = PositionalOrderWithExtraDto.MapFrom(new SimpleOrderSource { Id = 5, Total = 5m });
        Assert.Equal(5, dto.Id);
        Assert.Equal(5m, dto.Total);
        Assert.Equal("unset", dto.Note);
    }

    [Fact]
    public void PositionalRecordStruct_UsesTheOrdinaryObjectInitializerPath()
    {
        var dto = PositionalOrderStructDto.MapFrom(new SimpleOrderSource { Id = 6, Total = 6m });
        Assert.Equal(6, dto.Id);
        Assert.Equal(6m, dto.Total);
    }

    [Fact]
    public void PlainClass_WithOnlyAParameterizedConstructor_IsGenerated()
    {
        var dto = ManualCtorDto.MapFrom(new SimpleOrderSource { Id = 7, Total = 7m });
        Assert.Equal(7, dto.Id);
        Assert.Equal(7m, dto.Total);
    }

    [Fact]
    public void MapFrom_NullSource_StillThrowsArgumentNullException_ThroughTheConstructorCallPath()
    {
        Assert.Throws<ArgumentNullException>(() => PositionalOrderDto.MapFrom(null!));
        Assert.Throws<ArgumentNullException>(() => PositionalCompositeDto.MapFrom(null!));
        Assert.Throws<ArgumentNullException>(() => ManualCtorDto.MapFrom(null!));
    }
}
