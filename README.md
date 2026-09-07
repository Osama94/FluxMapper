# FluxMapper

A next-generation object-mapping framework for .NET: an AutoMapper-shaped fluent API, backed by an
adaptive execution engine that picks the fastest safe strategy available — a compile-time source
generator when it can, a cached compiled-expression tier when it can't, and real `IQueryable` projection
(`ProjectTo<T>`) for EF Core and friends — instead of committing to reflection or expression trees alone.

> **Status: v1.0.0.** The API surface and execution engine are implemented and
> covered by an xunit test suite (69 tests), including a real `Microsoft.EntityFrameworkCore.InMemory`
> projection test and a real Native AOT publish smoke test. See "What's implemented" below.

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

## What's implemented

| Area | Status |
|---|---|
| Core engine: fluent config, conventions, ambiguity/nullability diagnostics, compiled-expression tier, `Explain()` | Done, tested |
| Dictionaries, immutable collections, polymorphic dispatch, `ReferenceHandling.Preserve` (cycles + shared refs) | Done, tested |
| Projection (`ProjectTo<T>`) with a dedicated provider-translatable expression builder and pre-flight validation | Done, tested |
| Roslyn incremental source generator (`[MapFrom]`) — the AOT-safe tier | Done, tested (real generated code inspected) |
| Roslyn analyzers (`FLUX0001`/`FLUX0002`) for `[MapFrom]` misuse | Done, tested |
| `AddFluxMapper` DI integration, real constructor-injected resolver support | Done, tested |
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
