// Hand-rolled Stopwatch-based benchmark harness -- see FluxMapper.Benchmarks.csproj for why
// this isn't BenchmarkDotNet. Real wall-clock measurements of real
// compiled code: each scenario is warmed up (JIT + FluxMapper's own delegate-compile-on-first-call cache)
// before being timed, and GC is forced between warmup and measurement to reduce first-measured-iteration
// skew from a warmup-phase collection. Run with `dotnet run -c Release` -- a Debug build measures the
// debugger-friendly (un-optimized) JIT output, which is not representative of anything.

using System.Diagnostics;
using System.Reflection;
using FluxMapper.Benchmarks;
using FluxMapper.Core.Configuration;
using Mapster;
using AutoMapperConfiguration = AutoMapper.MapperConfiguration;

#if DEBUG
Console.WriteLine("*** DEBUG build -- these numbers are not meaningful. Re-run with `dotnet run -c Release`. ***");
Console.WriteLine();
#endif

Console.WriteLine("FluxMapper benchmark harness (Stopwatch-based substitute for BenchmarkDotNet)");
Console.WriteLine(new string('=', 100));

const int Warmup = 20_000;
const int Iterations = 500_000;

static double Bench(string name, int warmup, int iterations, Action action)
{
    for (var i = 0; i < warmup; i++) action();

    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();

    var sw = Stopwatch.StartNew();
    for (var i = 0; i < iterations; i++) action();
    sw.Stop();

    var nsPerOp = sw.Elapsed.TotalNanoseconds / iterations;
    Console.WriteLine($"{name,-42} {nsPerOp,12:F1} ns/op   {iterations / sw.Elapsed.TotalSeconds,15:N0} ops/sec");
    return nsPerOp;
}

// A deliberately naive, fully-generic reflection mapper -- the "worst realistic case" comparison point
// every other tier is measured against. Recurses into complex members/collections rather than just
// copying flat scalars, so it is a fair (if slow) baseline for the nested scenario too, not a straw man
// rigged to fail on anything but the flattest possible shape.
static object? ReflectionMapValue(object? source, Type destType)
{
    if (source is null) return null;
    if (destType.IsPrimitive || destType.IsEnum || destType == typeof(string) || destType == typeof(decimal) || destType == typeof(DateTime))
        return source;

    if (typeof(System.Collections.IEnumerable).IsAssignableFrom(destType) && destType != typeof(string))
    {
        var destElementType = destType.GetGenericArguments()[0];
        var list = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(destElementType))!;
        foreach (var item in (System.Collections.IEnumerable)source)
            list.Add(ReflectionMapValue(item, destElementType));
        return list;
    }

    var dest = Activator.CreateInstance(destType)!;
    foreach (var destProp in destType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
    {
        if (!destProp.CanWrite) continue;
        var srcProp = source.GetType().GetProperty(destProp.Name, BindingFlags.Public | BindingFlags.Instance);
        if (srcProp is null) continue;

        var value = srcProp.GetValue(source);
        destProp.SetValue(dest, destProp.PropertyType.IsInstanceOfType(value) ? value : ReflectionMapValue(value, destProp.PropertyType));
    }

    return dest;
}

static TDest ReflectionMap<TDest>(object source) => (TDest)ReflectionMapValue(source, typeof(TDest))!;

// ---------------------------------------------------------------------------
// scenario 1: flat mapping (4 scalar members) -- manual vs. reflection vs. FluxMapper's compiled-
// expression tier vs. FluxMapper's source-generated tier.
// ---------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("Scenario 1: flat mapping (BenchOrder -> BenchOrderDto, 4 scalar members)");
Console.WriteLine(new string('-', 100));
{
    var order = new BenchOrder { Id = 42, Total = 199.99m, CreatedAt = DateTime.UtcNow, Customer = "Ada Lovelace" };

    var config = MapperConfiguration.Create(cfg => cfg.CreateMap<BenchOrder, BenchOrderDto>());
    var mapper = config.CreateMapper();
    mapper.Map<BenchOrderDto>(order); // force delegate compilation before timing starts

    var autoMapperConfig = new AutoMapperConfiguration(cfg => cfg.CreateMap<BenchOrder, BenchOrderDto>());
    var autoMapper = autoMapperConfig.CreateMapper();
    autoMapper.Map<BenchOrderDto>(order);

    order.Adapt<BenchOrderDto>(); // Mapster warm-up: its first Adapt<> call for a type pair also compiles and caches a delegate

    BenchOrderDto ManualMap(BenchOrder o) => new() { Id = o.Id, Total = o.Total, CreatedAt = o.CreatedAt, Customer = o.Customer };

    Bench("Manual hand-written mapping", Warmup, Iterations, () => { _ = ManualMap(order); });
    Bench("FluxMapper: compiled-expression tier", Warmup, Iterations, () => { _ = mapper.Map<BenchOrderDto>(order); });
    Bench("FluxMapper: source-generated tier (AOT-safe)", Warmup, Iterations, () => { _ = BenchOrderGeneratedDto.MapFrom(order); });
    Bench("AutoMapper 14.0.0 (last MIT version)", Warmup, Iterations, () => { _ = autoMapper.Map<BenchOrderDto>(order); });
    Bench("Mapster (default runtime mode)", Warmup, Iterations, () => { _ = order.Adapt<BenchOrderDto>(); });
    Bench("Naive reflection (worst-case baseline)", Warmup / 10, Iterations / 10, () => { _ = ReflectionMap<BenchOrderDto>(order); });
}

// ---------------------------------------------------------------------------
// scenario 2: nested + collection mapping -- the case the compiled-expression tier's caching and
// null-safe chain navigation exist for; the source generator doesn't reach this shape (flat-only scope
// today), so it's omitted here.
// ---------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("Scenario 2: nested + collection mapping (BenchUser -> BenchUserDto, 1 nested + 3-element list)");
Console.WriteLine(new string('-', 100));
{
    var user = new BenchUser
    {
        Id = 1,
        Name = "Grace Hopper",
        Address = new BenchAddress { City = "NYC", Street = "Wall St" },
        Orders =
        [
            new BenchOrder { Id = 1, Total = 10m, CreatedAt = DateTime.UtcNow, Customer = "A" },
            new BenchOrder { Id = 2, Total = 20m, CreatedAt = DateTime.UtcNow, Customer = "B" },
            new BenchOrder { Id = 3, Total = 30m, CreatedAt = DateTime.UtcNow, Customer = "C" },
        ],
    };

    var config = MapperConfiguration.Create(cfg =>
    {
        cfg.CreateMap<BenchAddress, BenchAddressDto>();
        cfg.CreateMap<BenchOrder, BenchOrderDto>();
        cfg.CreateMap<BenchUser, BenchUserDto>();
    });
    var mapper = config.CreateMapper();
    mapper.Map<BenchUserDto>(user);

    var autoMapperConfig = new AutoMapperConfiguration(cfg =>
    {
        cfg.CreateMap<BenchAddress, BenchAddressDto>();
        cfg.CreateMap<BenchOrder, BenchOrderDto>();
        cfg.CreateMap<BenchUser, BenchUserDto>();
    });
    var autoMapper = autoMapperConfig.CreateMapper();
    autoMapper.Map<BenchUserDto>(user);

    user.Adapt<BenchUserDto>(); // Mapster warm-up, same reason as scenario 1

    BenchUserDto ManualMap(BenchUser u) => new()
    {
        Id = u.Id,
        Name = u.Name,
        Address = new BenchAddressDto { City = u.Address.City, Street = u.Address.Street },
        Orders = u.Orders.Select(o => new BenchOrderDto { Id = o.Id, Total = o.Total, CreatedAt = o.CreatedAt, Customer = o.Customer }).ToList(),
    };

    Bench("Manual hand-written mapping", Warmup, Iterations, () => { _ = ManualMap(user); });
    Bench("FluxMapper: compiled-expression tier", Warmup, Iterations, () => { _ = mapper.Map<BenchUserDto>(user); });
    Bench("AutoMapper 14.0.0 (last MIT version)", Warmup, Iterations, () => { _ = autoMapper.Map<BenchUserDto>(user); });
    Bench("Mapster (default runtime mode)", Warmup, Iterations, () => { _ = user.Adapt<BenchUserDto>(); });
    Bench("Naive reflection (worst-case baseline)", Warmup / 10, Iterations / 10, () => { _ = ReflectionMap<BenchUserDto>(user); });
}

Console.WriteLine();
Console.WriteLine("Interpretation: the compiled-expression tier pays a one-time Expression.Compile() cost per");
Console.WriteLine("(source,destination) type pair (paid once above, before timing starts, mirroring real usage --");
Console.WriteLine("a long-lived Mapper/IMapper compiles each pair at most once) and is then a plain delegate call,");
Console.WriteLine("so its steady-state cost should sit close to hand-written code and far below per-call reflection.");
Console.WriteLine();
Console.WriteLine("AutoMapper (14.0.0, its last MIT-licensed release) and Mapster (default runtime mode, no");
Console.WriteLine("Mapster.Tool codegen) are included as the two most commonly reached-for alternatives -- see");
Console.WriteLine("COMPETITIVE_GAP_ANALYSIS.md for why an independent, runnable comparison mattered enough to add.");
