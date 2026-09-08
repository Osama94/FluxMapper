# FluxMapper

A next-generation object-mapping framework for .NET: an AutoMapper-shaped fluent API, backed by an
adaptive execution engine that picks the fastest safe strategy available — a compile-time source
generator when it can, a cached compiled-expression tier when it can't, and real `IQueryable` projection
(`ProjectTo<T>`) for EF Core and friends — instead of committing to reflection or expression trees alone.

> **Status: v1.2.0.** The API surface and execution engine are implemented and
> covered by an xunit test suite (93 tests), including a real `Microsoft.EntityFrameworkCore.InMemory`
> projection test and a real Native AOT publish smoke test. See "What's implemented" below.

**[Read the full documentation](DOCUMENTATION.md)** for every configuration option, every runtime mapping mode, the complete diagnostic catalog, and an AutoMapper migration cheat sheet. This README stays a quick-start; DOCUMENTATION.md is the reference.

## Why another mapper

AutoMapper and Mapster both made real, different trade-offs: reflection-friendly configuration with a
runtime cost, or source-generation speed with a narrower feature set. FluxMapper's premise is that you
shouldn't have to choose up front — a single fluent configuration surface should let the *engine* pick the
cheapest execution strategy that's actually safe for a given mapping:

- **Flat, generator-friendly mappings** compile to real C# at build time (`[MapFrom]` +
  `FluxMapper.SourceGenerator`) — zero reflection, zero `Expression.Compile()`, genuinely Native-AOT-safe.
- **Everything else** (nested objects, collections, dictionaries, polymorphism, cycles, custom resolvers)
  runs through a cached compiled-expression tier — still fast, but honestly annotated
  (`[RequiresDynamicCode]`/`[RequiresUnreferencedCode]`) as not AOT-safe, rather than silently breaking a
  trimmed or Native AOT app.
- **Query translation** (`ProjectTo<TDestination>()`) uses a separate, deliberately simpler expression
  builder so the result is safe to hand to a real `IQueryable` provider like EF Core, instead of the
  richer (but provider-opaque) shapes the runtime tier is free to use internally.

## Quick start

```csharp
using FluxMapper.Core.Configuration;

var config = MapperConfiguration.Create(cfg =>
{
    cfg.CreateMap<Order, OrderDto>();
    cfg.CreateMap<User, UserDto>();
});

config.AssertConfigurationIsValid(); // fail at startup, not in production

var mapper = config.CreateMapper();
var dto = mapper.Map<UserDto>(user);
```

**Dependency injection** (`FluxMapper.Extensions.DependencyInjection`):

```csharp
services.AddFluxMapper(cfg => cfg.CreateMap<Order, OrderDto>());
// later: constructor-inject IMapper
```

**EF Core / IQueryable projection** — no EF-specific package needed, `ProjectTo` works against any
`IQueryable`:

```csharp
var dtos = await dbContext.Orders.ProjectTo<OrderDto>(config).ToListAsync();
```

**AOT-safe generated mapping** for a flat DTO:

```csharp
[MapFrom(typeof(Order))]
public partial class OrderDto { public int Id { get; set; } public decimal Total { get; set; } }

var dto = OrderDto.MapFrom(order); // emitted at compile time, no IMapper involved
```

## Member customization & lifecycle hooks

Convention-based, name-matching maps cover the common case, but real codebases eventually need to
override *how* a specific member is populated, *how* the destination is constructed, or run logic before
or after a mapping completes — the same needs AutoMapper's `ForMember`/`ConstructUsing`/`AfterMap`/
`Profile` cover. FluxMapper has the same surface, compiled into the same execution tier as everything
else (no separate slow path):

```csharp
public class OrderProfile : Profile
{
    public OrderProfile()
    {
        CreateMap<Order, OrderDto>()
            // AutoMapper-shaped per-member options -- equivalent to .Map()/.Ignore()/.Condition() below,
            // pick whichever reads better.
            .ForMember(d => d.ProductCode, opt => opt.MapFrom(s => s.Sku))
            .ForMember(d => d.InternalNotes, opt => opt.Ignore())
            // Replaces automatic constructor selection; every other configured/writable member is still
            // mapped afterward as normal.
            .ConstructUsing(s => new OrderDto(s.Id))
            // Run once per mapping of this pair (including nested occurrences), before/after members
            // are assigned.
            .BeforeMap((src, dest) => dest.ProcessedAtUtc = DateTime.UtcNow)
            .AfterMap((src, dest) => dest.Total = Math.Round(dest.Total, 2));
    }
}

// Discover every Profile in an assembly, mirroring services.AddAutoMapper(...):
services.AddFluxMapper(ServiceLifetime.Singleton, Assembly.GetExecutingAssembly());
// or, without DI:
var config = MapperConfiguration.Create(cfg => cfg.AddMaps(Assembly.GetExecutingAssembly()));
```

A one-off hook for a single call, rather than every mapping of the pair, uses per-call options instead of
`CreateMap`:

```csharp
var dto = mapper.Map<Order, OrderDto>(order, opt => opt.AfterMap((src, dest) => dest.RequestId = requestId));
```

`AfterMap`/per-call `AfterMap` take a plain `Action<TSource,TDestination>`, matching AutoMapper — which
means an `async` lambda passed there compiles but is never awaited (a real, observed bug pattern: `opt
.AfterMap(async (s, d) => d.Status = await GetStatusAsync(s))` silently drops that `Task`). Where the hook
needs to `await` something, use `MapAsync` instead, which *is* awaited end to end:

```csharp
var dto = await mapper.MapAsync<Order, OrderDto>(order, async (src, dest) =>
{
    dest.Status = await statusService.GetStatusAsync(src.Id);
});
```

## Per-call state and a contextual `MapFrom`

Sometimes a member's value depends on something known only at the call site — not the source object,
and not something worth a permanent `.Condition()`/`.Map()` on the type pair itself. AutoMapper covers
this with `opt.Items["key"] = value` plus a four-argument `.MapFrom((src, dest, current, context) => ...)`
that can read it back; FluxMapper has the same two pieces:

```csharp
cfg.CreateMap<Article, ArticleDto>()
    .ForMember(d => d.TextArWithParams, opt => opt.MapFrom((src, dest, current, context) =>
        context.Items.TryGetValue("EditReasonId", out var v) && v is int id && id == EditReasonType.DecisionByBoard.Id
            ? src.TextWithParams
            : (src.Article is null ? string.Empty : src.TextWithParams)));

// ...

var dto = mapper.Map<Article, ArticleDto>(article, opt => opt.Items["EditReasonId"] = editReasonId);
```

A flat, non-`ForMember` equivalent exists too (`.ResolveUsing<TMember>(destinationMember, resolver)`),
matching the same pairing the rest of the fluent surface follows. `ResolutionContext.Items` is empty and
ambient-free for a plain `mapper.Map(source)` call with no options — a `ResolutionContext` is only built
and threaded through the mapping when the caller actually populates `Items`, so this costs nothing on the
hot path when it isn't used.

## Mapping into a nested destination path (`ForPath`)

Sometimes the destination has a nested shape the source doesn't mirror at all — not just a
differently-named member, but a whole intermediate object with no source-side counterpart, such as a
real-world `dest.Company.SaudiAddress` populated from `src.Company.NationalAddress`. Ordinary nested
mapping has nothing to discover there (there's no `NationalAddress` on the destination side, and no
`SaudiAddress` on the source side), so `.Map()`/`.ForMember()` can't express it either — `ForPath` maps
directly into the destination path instead:

```csharp
cfg.CreateMap<CompanySource, CompanyDestination>()
    .ForPath(d => d.SaudiAddress.City, opt => opt.MapFrom(s => s.NationalAddress.CityName))
    .ForPath(d => d.SaudiAddress.PostalCode, opt => opt.MapFrom(s => s.NationalAddress.Zip));
```

Every intermediate segment in the path (`SaudiAddress` above) is always freshly constructed — it needs a
public parameterless constructor, or configuration validation fails with `MAP0004` — never merged into an
existing instance, even when mapping into an existing destination (`Map(source, destination)`). Multiple
`ForPath` calls that share a common prefix (`d.SaudiAddress.City` and `d.SaudiAddress.PostalCode` above)
merge into the *same* constructed subtree rather than each building their own `SaudiAddress`. Any other
member of a `ForPath`-touched type left uncovered by a `ForPath` registration keeps its own default value
-- it isn't separately validated or convention-matched, the same way `.Ignore()` already exempts a member.
A single-hop selector (`d => d.City`) doesn't need `ForPath` at all — use `.Map()`/`.ForMember()` for that.

## What's implemented

| Area | Status |
|---|---|
| Core engine: fluent config, conventions, ambiguity/nullability diagnostics, compiled-expression tier, `Explain()` | Done, tested |
| Dictionaries, immutable collections, polymorphic dispatch, `ReferenceHandling.Preserve` (cycles + shared refs) | Done, tested |
| Projection (`ProjectTo<T>`) with a dedicated provider-translatable expression builder and pre-flight validation | Done, tested |
| Roslyn incremental source generator (`[MapFrom]`) — the AOT-safe tier | Done, tested (real generated code inspected) |
| Roslyn analyzers (`FLUX0001`/`FLUX0002`) for `[MapFrom]` misuse | Done, tested |
| `AddFluxMapper` DI integration, real constructor-injected resolver support | Done, tested |
| `ForMember`, `ConstructUsing`, `BeforeMap`/`AfterMap` (configured-once and per-call), `Profile` + `AddProfile`/`AddMaps` assembly scanning, `MapAsync`, `ForPath` | Done, tested |
| EF Core interop | Done — verified against a real `Microsoft.EntityFrameworkCore.InMemory` `DbContext`, plus a stricter structural check against a hand-rolled query provider |
| Native AOT publish | Done — a dedicated sample (`samples/FluxMapper.AotSmokeTest`) publishes with `PublishAot=true` and runs as a native executable |
| Performance benchmarks | Done (Stopwatch-based harness) |
| AOT/trim hardening | Done — public reflection/JIT-dependent surface annotated `[RequiresDynamicCode]`/`[RequiresUnreferencedCode]`; the generator/analyzer packages target `netstandard2.0` |

## Packages

| Package | Depends on | Notes |
|---|---|---|
| `FluxMapper` | Everything below | Umbrella package, no code of its own — installs every FluxMapper feature in one step. |
| `FluxMapper.Abstractions` | BCL only | Contracts (`IMapper`, `IValueResolver<>`, `IProjectionValueResolver<>`, `[MapFrom]`). Fully AOT/trim compatible. |
| `FluxMapper.Core` | `FluxMapper.Abstractions` | Fluent configuration, compiled-expression execution tier, projection engine. |
| `FluxMapper.SourceGenerator` | Roslyn (build-time only) | `[MapFrom]` incremental generator. |
| `FluxMapper.Analyzers` | Roslyn (build-time only) | `[MapFrom]` diagnostics. |
| `FluxMapper.Extensions.DependencyInjection` | `FluxMapper.Core` | `AddFluxMapper` for `IServiceCollection`. |

`FluxMapper`, `FluxMapper.Abstractions`, `FluxMapper.Core`, and `FluxMapper.Extensions.DependencyInjection`
all multi-target `netstandard2.0` and `net10.0` — .NET Framework 4.6.1+, .NET Core 2.0+, Mono, Xamarin, and
every actively supported .NET version can all reference them, not just net10.0. `FluxMapper.SourceGenerator`
and `FluxMapper.Analyzers` are Roslyn components and always target `netstandard2.0` regardless of your
app's own target framework, since they run inside the compiler/IDE host rather than your app.

## Benchmarks

`benchmarks/FluxMapper.Benchmarks` is a runnable, Stopwatch-based comparison of hand-written mapping,
both of FluxMapper's execution tiers, AutoMapper (pinned to 14.0.0, its last MIT-licensed release), and
Mapster (default runtime mode), on a flat and a nested+collection scenario:

```
dotnet run -c Release --project benchmarks/FluxMapper.Benchmarks
```

Run it yourself rather than taking any mapper's marketing numbers, FluxMapper's own included, at face
value — results depend on your hardware, .NET version, and shape of data. See
[`COMPETITIVE_GAP_ANALYSIS.md`](COMPETITIVE_GAP_ANALYSIS.md) for the fuller competitive positioning this
benchmark is part of.

## Installation

Want everything with one package:

```
dotnet add package FluxMapper
```

Or pick only what you need:

```
dotnet add package FluxMapper.Core
dotnet add package FluxMapper.SourceGenerator
```

Add `FluxMapper.Extensions.DependencyInjection` if you're using `IServiceCollection`, and
`FluxMapper.Analyzers` for edit-time diagnostics on `[MapFrom]`.

## License

MIT — see [`LICENSE`](LICENSE).
