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

// The source generator is flat-only, so its comparison point is this same
// flat shape -- [MapFrom(typeof(BenchOrder))] emits a static MapFrom(BenchOrder) factory at compile time.
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
