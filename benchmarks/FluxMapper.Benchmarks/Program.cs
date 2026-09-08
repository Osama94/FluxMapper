// Hand-rolled Stopwatch-based benchmark harness -- see FluxMapper.Benchmarks.csproj for why
// this isn't BenchmarkDotNet. Real wall-clock measurements of real
// compiled code: each scenario is warmed up once (JIT + FluxMapper's own delegate-compile-on-first-call
// cache) before being timed, then measured across several independent trials rather than a single
// Stopwatch sample. A single sample is not trustworthy on a shared/laptop machine: background load,
// thermal throttling, or a badly-timed GC can and does swing one Stopwatch run by 2x or more with zero
// code change -- observed directly during this project's own benchmarking (see COMPETITIVE_GAP_ANALYSIS.md).
// Reporting the MEDIAN across trials (not the mean, which one bad trial can drag arbitrarily far) and
// printing the min/max alongside it turns that noise from an invisible trap into a visible number, so a
// reader can see for themselves how much to trust a given result instead of taking a single run on faith.
// Run with `dotnet run -c Release` -- a Debug build measures the debugger-friendly (un-optimized) JIT
// output, which is not representative of anything.

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
const int Iterations = 50_000;
const int Trials = 15;

// Runs `action` `warmup` times once (JIT tiering + FluxMapper's own delegate-compile-on-first-call cache
// both stabilize after the first handful of calls, so re-warming per trial would only cost time, not
// change what's measured), then times it across `trials` independent trials of `iterations` calls each,
// forcing a GC between every trial so one trial's garbage can't skew the next. Reports the MEDIAN trial
// (robust to a single noisy trial in a way a mean isn't) as the headline ns/op, with the min and max
// trial printed alongside it so a wide spread -- a real signal of system noise, not a FluxMapper problem
// -- is visible in the output rather than silently averaged away.
static double Bench(string name, int warmup, int iterations, Action action)
{
    for (var i = 0; i < warmup; i++) action();

    var samples = new double[Trials];
    for (var t = 0; t < Trials; t++)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++) action();
        sw.Stop();

        samples[t] = sw.Elapsed.TotalNanoseconds / iterations;
    }

    Array.Sort(samples);
    var median = Trials % 2 == 1
        ? samples[Trials / 2]
        : (samples[Trials / 2 - 1] + samples[Trials / 2]) / 2.0;
    var min = samples[0];
    var max = samples[^1];
    var opsPerSec = 1_000_000_000.0 / median;

    // A wide min-max spread relative to the median means this particular result was measured on a noisy
    // machine right now, not that FluxMapper's timing is actually that variable call-to-call -- flagged
    // inline rather than left for someone to notice only when two runs disagree.
    var noiseFlag = max > median * 1.5 ? "  (high spread -- see min/max; treat this result cautiously)" : "";
    Console.WriteLine($"{name,-42} {median,10:F1} ns/op  [min {min,8:F1}, max {max,8:F1}]  {opsPerSec,15:N0} ops/sec{noiseFlag}");
    return median;
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
    var fastOrderMap = mapper.GetTypedMapper<BenchOrder, BenchOrderDto>(); // opt-in typed fast path, grabbed once outside the timed loop -- see IMapper.GetTypedMapper

    var autoMapperConfig = new AutoMapperConfiguration(cfg => cfg.CreateMap<BenchOrder, BenchOrderDto>());
    var autoMapper = autoMapperConfig.CreateMapper();
    autoMapper.Map<BenchOrderDto>(order);

    order.Adapt<BenchOrderDto>(); // Mapster warm-up: its first Adapt<> call for a type pair also compiles and caches a delegate

    BenchOrderDto ManualMap(BenchOrder o) => new() { Id = o.Id, Total = o.Total, CreatedAt = o.CreatedAt, Customer = o.Customer };

    Bench("Manual hand-written mapping", Warmup, Iterations, () => { _ = ManualMap(order); });
    Bench("FluxMapper: compiled-expression tier", Warmup, Iterations, () => { _ = mapper.Map<BenchOrderDto>(order); });
    Bench("FluxMapper: typed fast-path (GetTypedMapper)", Warmup, Iterations, () => { _ = fastOrderMap(order); });
    Bench("FluxMapper: source-generated tier (AOT-safe)", Warmup, Iterations, () => { _ = BenchOrderGeneratedDto.MapFrom(order); });
    Bench("AutoMapper 14.0.0 (last MIT version)", Warmup, Iterations, () => { _ = autoMapper.Map<BenchOrderDto>(order); });
    Bench("Mapster (default runtime mode)", Warmup, Iterations, () => { _ = order.Adapt<BenchOrderDto>(); });
    Bench("Naive reflection (worst-case baseline)", Warmup / 10, Iterations / 10, () => { _ = ReflectionMap<BenchOrderDto>(order); });
}

// ---------------------------------------------------------------------------
// scenario 2: nested + collection mapping -- the case the compiled-expression tier's caching and
// null-safe chain navigation exist for. The source generator now reaches this shape too (nested members
// compose via a same-named [MapFrom]-attributed member type; List<T>/array collections compose the same
// way per element) via BenchUserGeneratedDto -- see Model.cs.
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
    var fastUserMap = mapper.GetTypedMapper<BenchUser, BenchUserDto>(); // opt-in typed fast path, grabbed once outside the timed loop -- see IMapper.GetTypedMapper

    var autoMapperConfig = new AutoMapperConfiguration(cfg =>
    {
        cfg.CreateMap<BenchAddress, BenchAddressDto>();
        cfg.CreateMap<BenchOrder, BenchOrderDto>();
        cfg.CreateMap<BenchUser, BenchUserDto>();
    });
    var autoMapper = autoMapperConfig.CreateMapper();
    autoMapper.Map<BenchUserDto>(user);

    user.Adapt<BenchUserDto>(); // Mapster warm-up, same reason as scenario 1

    BenchUserGeneratedDto.MapFrom(user); // no delegate to warm up (it's generated static code, not compiled at runtime) -- just confirms it runs before timing starts

    BenchUserDto ManualMap(BenchUser u) => new()
    {
        Id = u.Id,
        Name = u.Name,
        Address = new BenchAddressDto { City = u.Address.City, Street = u.Address.Street },
        Orders = u.Orders.Select(o => new BenchOrderDto { Id = o.Id, Total = o.Total, CreatedAt = o.CreatedAt, Customer = o.Customer }).ToList(),
    };

    Bench("Manual hand-written mapping", Warmup, Iterations, () => { _ = ManualMap(user); });
    Bench("FluxMapper: compiled-expression tier", Warmup, Iterations, () => { _ = mapper.Map<BenchUserDto>(user); });
    Bench("FluxMapper: typed fast-path (GetTypedMapper)", Warmup, Iterations, () => { _ = fastUserMap(user); });
    Bench("FluxMapper: source-generated tier (AOT-safe)", Warmup, Iterations, () => { _ = BenchUserGeneratedDto.MapFrom(user); });
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
Console.WriteLine();
Console.WriteLine($"Each row is the MEDIAN of {Trials} independent trials of {Iterations:N0} calls each (reflection: {Iterations / 10:N0});");
Console.WriteLine("min/max are printed alongside so you can see this run's own noise instead of trusting one number.");
Console.WriteLine("Run this more than once, especially on a laptop or a shared machine -- a \"(high spread)\" flag");
Console.WriteLine("on a row means that particular result was measured under noise and shouldn't be quoted alone.");
