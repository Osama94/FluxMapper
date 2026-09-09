// Assembly-level converter registration must live outside any namespace, so this is at the very top of
// the file -- see MapFromConverterAttribute's doc comment. Registers BroadenedMoney -> decimal globally
// for every [MapFrom] target in this compilation.
[assembly: FluxMapper.Abstractions.MapFromConverter(typeof(FluxMapper.Tests.BroadenedMoney), typeof(decimal), typeof(FluxMapper.Tests.BroadenedMoneyToDecimalConverter))]

namespace FluxMapper.Tests;

using FluxMapper.Abstractions;

// Coverage for the second source-generator broadening round: plain (non-record) struct destinations,
// NamingConvention.SnakeCase on [MapFrom], one-level flattening, a registered [assembly: MapFromConverter],
// and wider collection/dictionary shapes (HashSet<T>, common read-oriented interfaces,
// Dictionary<TKey,TValue> and its interfaces). None of the MapFrom/MapFromCore bodies below exist in this
// file -- a build failure here means the generator didn't run, or generated something that doesn't
// compile.

// ---- Plain (non-record) struct destination ------------------------------------------------------

public class StructSource
{
    public int Id { get; set; }
    public decimal Total { get; set; }
}

[MapFrom(typeof(StructSource))]
public partial struct PlainStructDto
{
    public int Id { get; set; }
    public decimal Total { get; set; }
}

// ---- Naming convention (SnakeCase) ---------------------------------------------------------------

public class SnakeSource
{
    public string user_name { get; set; } = "";
    public int user_age { get; set; }
}

[MapFrom(typeof(SnakeSource), NamingConvention = MapFromNamingConvention.SnakeCase)]
public partial class SnakeDto
{
    public string UserName { get; set; } = "";
    public int UserAge { get; set; }
}

// Control: without the naming convention, "UserName"/"UserAge" have no exact-name match against
// "user_name"/"user_age" -- proving the preset above is actually doing something, not matching by
// coincidence.
[MapFrom(typeof(SnakeSource))]
public partial class SnakeDtoWithoutConvention
{
    public string UserName { get; set; } = "unset";
    public int UserAge { get; set; } = -1;
}

// ---- One-level flattening -------------------------------------------------------------------------

public class FlattenAddressSource
{
    public string City { get; set; } = "";
    public string Street { get; set; } = "";
}

public class FlattenSource
{
    public int Id { get; set; }
    public FlattenAddressSource? Address { get; set; }
}

[MapFrom(typeof(FlattenSource))]
public partial class FlattenDto
{
    public int Id { get; set; }
    public string AddressCity { get; set; } = "";
    public string AddressStreet { get; set; } = "";
}

// ---- Global converter (via [assembly: MapFromConverter]) ------------------------------------------

public struct BroadenedMoney
{
    public decimal Amount { get; set; }
}

public sealed class BroadenedMoneyToDecimalConverter : IValueConverter<BroadenedMoney, decimal>
{
    public static int InvocationCount;

    public decimal Convert(BroadenedMoney source, ResolutionContext context)
    {
        InvocationCount++;
        return source.Amount;
    }
}

public class ConverterSource
{
    public int Id { get; set; }
    public BroadenedMoney Total { get; set; }
}

[MapFrom(typeof(ConverterSource))]
public partial class ConverterDto
{
    public int Id { get; set; }
    public decimal Total { get; set; }
}

// ---- Sequence shape matrix: Indexed/CountedEnumerable sources x Array/List/HashSet destinations ---

public class SequenceShapesSource
{
    public List<int> FromList { get; set; } = [];
    public HashSet<int> FromSet { get; set; } = [];
    public HashSet<int> FromSet2 { get; set; } = [];
}

[MapFrom(typeof(SequenceShapesSource))]
public partial class SequenceShapesDto
{
    public HashSet<int> FromList { get; set; } = [];  // Indexed (List<T>) source -> HashSet destination
    public List<int> FromSet { get; set; } = [];       // CountedEnumerable (HashSet<T>) source -> List destination
    public HashSet<int> FromSet2 { get; set; } = [];   // CountedEnumerable (HashSet<T>) source -> HashSet destination
}

[MapFrom(typeof(SequenceShapesSource))]
public partial class SequenceShapesArrayDto
{
    public int[] FromSet { get; set; } = [];            // CountedEnumerable (HashSet<T>) source -> Array destination
}

// ---- Common read-oriented interfaces on both sides -------------------------------------------------

public class InterfaceShapesSource
{
    public List<int> Numbers { get; set; } = [];
    public ICollection<string> Tags { get; set; } = [];
}

[MapFrom(typeof(InterfaceShapesSource))]
public partial class InterfaceShapesDto
{
    public IReadOnlyList<int> Numbers { get; set; } = [];
    public IReadOnlyCollection<string> Tags { get; set; } = [];
}

// ---- Dictionary<TKey,TValue> and its common interfaces ---------------------------------------------

public class DictScalarSource
{
    public Dictionary<string, int> Counts { get; set; } = new();
}

[MapFrom(typeof(DictScalarSource))]
public partial class DictScalarDto
{
    public IReadOnlyDictionary<string, int> Counts { get; set; } = new Dictionary<string, int>();
}

public class DictValueSource
{
    public string Label { get; set; } = "";
}

[MapFrom(typeof(DictValueSource))]
public partial class DictValueDto
{
    public string Label { get; set; } = "";
}

public class DictOwnerSource
{
    public Dictionary<string, DictValueSource> Items { get; set; } = new();
}

[MapFrom(typeof(DictOwnerSource))]
public partial class DictOwnerDto
{
    public Dictionary<string, DictValueDto> Items { get; set; } = new();
}

public class SourceGeneratorBroadenedTests
{
    [Fact]
    public void PlainStruct_Destination_IsGenerated()
    {
        var dto = PlainStructDto.MapFrom(new StructSource { Id = 1, Total = 9.99m });
        Assert.Equal(1, dto.Id);
        Assert.Equal(9.99m, dto.Total);
    }

    [Fact]
    public void PlainStruct_NullSource_StillThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => PlainStructDto.MapFrom(null!));
    }

    [Fact]
    public void SnakeCase_MatchesUnderscoredSourceToPascalCaseDestination()
    {
        var dto = SnakeDto.MapFrom(new SnakeSource { user_name = "Ada", user_age = 36 });
        Assert.Equal("Ada", dto.UserName);
        Assert.Equal(36, dto.UserAge);
    }

    [Fact]
    public void WithoutTheConvention_UnderscoredNamesDoNotMatch()
    {
        var dto = SnakeDtoWithoutConvention.MapFrom(new SnakeSource { user_name = "Ada", user_age = 36 });
        Assert.Equal("unset", dto.UserName);
        Assert.Equal(-1, dto.UserAge);
    }

    [Fact]
    public void Flattening_ComposesOneLevelFromANestedSourceMember()
    {
        var dto = FlattenDto.MapFrom(new FlattenSource { Id = 1, Address = new FlattenAddressSource { City = "Boston", Street = "Main St" } });
        Assert.Equal(1, dto.Id);
        Assert.Equal("Boston", dto.AddressCity);
        Assert.Equal("Main St", dto.AddressStreet);
    }

    [Fact]
    public void Flattening_NullPrefixMapsToNullNotTheDeclaredDefault()
    {
        // Documents the same accepted tradeoff as a null Nested composition: the null-forgiven read
        // still assigns a real null through the object initializer, overriding AddressCity's own
        // `= ""` field default.
        var dto = FlattenDto.MapFrom(new FlattenSource { Id = 2, Address = null });
        Assert.Null(dto.AddressCity);
        Assert.Null(dto.AddressStreet);
    }

    [Fact]
    public void GlobalConverter_AppliesViaAssemblyLevelRegistration()
    {
        var dto = ConverterDto.MapFrom(new ConverterSource { Id = 1, Total = new BroadenedMoney { Amount = 42.5m } });
        Assert.Equal(1, dto.Id);
        Assert.Equal(42.5m, dto.Total);
    }

    [Fact]
    public void GlobalConverter_IsActuallyInvoked()
    {
        var before = BroadenedMoneyToDecimalConverter.InvocationCount;
        ConverterDto.MapFrom(new ConverterSource { Id = 2, Total = new BroadenedMoney { Amount = 3m } });
        Assert.Equal(before + 1, BroadenedMoneyToDecimalConverter.InvocationCount);
    }

    [Fact]
    public void SequenceShapes_IndexedSourceMaterializesAsHashSetDestination()
    {
        var dto = SequenceShapesDto.MapFrom(new SequenceShapesSource { FromList = [1, 2, 2, 3] });
        Assert.Equal(3, dto.FromList.Count);
        Assert.Contains(1, dto.FromList);
        Assert.Contains(2, dto.FromList);
        Assert.Contains(3, dto.FromList);
    }

    [Fact]
    public void SequenceShapes_CountedEnumerableSourceMaterializesAsListDestination()
    {
        var dto = SequenceShapesDto.MapFrom(new SequenceShapesSource { FromSet = [5, 6, 7] });
        Assert.Equal(3, dto.FromSet.Count);
        Assert.Contains(5, dto.FromSet);
        Assert.Contains(6, dto.FromSet);
        Assert.Contains(7, dto.FromSet);
    }

    [Fact]
    public void SequenceShapes_CountedEnumerableSourceMaterializesAsHashSetDestination()
    {
        var dto = SequenceShapesDto.MapFrom(new SequenceShapesSource { FromSet2 = [8, 9] });
        Assert.Equal(2, dto.FromSet2.Count);
        Assert.Contains(8, dto.FromSet2);
        Assert.Contains(9, dto.FromSet2);
    }

    [Fact]
    public void SequenceShapes_CountedEnumerableSourceMaterializesAsArrayDestination()
    {
        var dto = SequenceShapesArrayDto.MapFrom(new SequenceShapesSource { FromSet = [10, 11, 12] });
        Assert.Equal(3, dto.FromSet.Length);
        Assert.Contains(10, dto.FromSet);
        Assert.Contains(11, dto.FromSet);
        Assert.Contains(12, dto.FromSet);
    }

    [Fact]
    public void InterfaceShapes_ReadOrientedInterfacesOnBothSidesAreRecognized()
    {
        var dto = InterfaceShapesDto.MapFrom(new InterfaceShapesSource { Numbers = [1, 2, 3], Tags = new List<string> { "a", "b" } });
        Assert.Equal([1, 2, 3], dto.Numbers);
        Assert.Equal(2, dto.Tags.Count);
        Assert.Contains("a", dto.Tags);
        Assert.Contains("b", dto.Tags);
    }

    [Fact]
    public void Dictionary_ScalarValue_MapsThroughAnInterfaceDestination()
    {
        var dto = DictScalarDto.MapFrom(new DictScalarSource { Counts = new Dictionary<string, int> { ["x"] = 1, ["y"] = 2 } });
        Assert.Equal(2, dto.Counts.Count);
        Assert.Equal(1, dto.Counts["x"]);
        Assert.Equal(2, dto.Counts["y"]);
    }

    [Fact]
    public void Dictionary_NestedMapFromValue_ComposesPerEntry()
    {
        var dto = DictOwnerDto.MapFrom(new DictOwnerSource
        {
            Items = new Dictionary<string, DictValueSource> { ["a"] = new DictValueSource { Label = "hi" } },
        });

        Assert.Single(dto.Items);
        Assert.Equal("hi", dto.Items["a"].Label);
    }
}
