namespace FluxMapper.Benchmarks;

// Deliberately separate from tests/FluxMapper.Tests/Model.cs (not shared via project reference to an Exe
// output) -- a small, self-contained shape representative of a typical DTO mapping: a flat scalar case
// (BenchOrder/BenchOrderDto) and a nested + collection case (BenchUser/BenchUserDto), matching the two
// shapes whose relative execution-tier cost matters most.

public class BenchOrder
{
    public int Id { get; set; }
    public decimal Total { get; set; }
    public DateTime CreatedAt { get; set; }
    public string Customer { get; set; } = "";
}

public class BenchOrderDto
{
    public int Id { get; set; }
    public decimal Total { get; set; }
    public DateTime CreatedAt { get; set; }
    public string Customer { get; set; } = "";
}

// [MapFrom(typeof(BenchOrder))] emits a static MapFrom(BenchOrder) factory at compile time -- this
// same flat type doubles as the Orders element type for BenchUserGeneratedDto below, once the source
// generator's nested/collection support needs a [MapFrom]-attributed element to compose against.
[FluxMapper.Abstractions.MapFrom(typeof(BenchOrder))]
public partial class BenchOrderGeneratedDto
{
    public int Id { get; set; }
    public decimal Total { get; set; }
    public DateTime CreatedAt { get; set; }
    public string Customer { get; set; } = "";
}

public class BenchAddress
{
    public string City { get; set; } = "";
    public string Street { get; set; } = "";
}

public class BenchAddressDto
{
    public string City { get; set; } = "";
    public string Street { get; set; } = "";
}

public class BenchUser
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public BenchAddress Address { get; set; } = new();
    public List<BenchOrder> Orders { get; set; } = [];
}

public class BenchUserDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public BenchAddressDto Address { get; set; } = new();
    public List<BenchOrderDto> Orders { get; set; } = [];
}

// The source generator now composes nested/List<T> members too (see MapFromGenerator's own doc comment
// for the exact scope: nested via a same-named member whose type is itself [MapFrom]-attributed,
// collections via an exact List<T>/array on both sides) -- BenchUserGeneratedDto exercises that directly,
// reusing BenchOrderGeneratedDto (already [MapFrom]-attributed above) as its Orders element type so the
// per-element mapping is itself generated code, not a fallback.
[FluxMapper.Abstractions.MapFrom(typeof(BenchAddress))]
public partial class BenchAddressGeneratedDto
{
    public string City { get; set; } = "";
    public string Street { get; set; } = "";
}

[FluxMapper.Abstractions.MapFrom(typeof(BenchUser))]
public partial class BenchUserGeneratedDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public BenchAddressGeneratedDto Address { get; set; } = new();
    public List<BenchOrderGeneratedDto> Orders { get; set; } = [];
}
