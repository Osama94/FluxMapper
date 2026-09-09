# FluxMapper Documentation

This is the complete reference for FluxMapper: every configuration option, every runtime mapping mode,
every diagnostic code, and how the pieces fit together. For a fast overview and installation
instructions, see [`README.md`](README.md); this document goes deeper into *why* things work the way
they do and *how* to use every feature, including the ones that only come up in real-world usage.

## Table of contents

1. [Installation](#installation)
2. [Quick start](#quick-start)
3. [How FluxMapper thinks about a mapping](#how-fluxmapper-thinks-about-a-mapping)
4. [Configuring a map](#configuring-a-map)
   - [`CreateMap` and automatic convention matching](#createmap-and-automatic-convention-matching)
   - [`Map` — explicit member sources](#map--explicit-member-sources)
   - [`ForMember` and the member options builder](#formember-and-the-member-options-builder)
   - [`Ignore`](#ignore)
   - [`Condition`](#condition)
   - [Nullability: `NullPolicy` and `NullSubstitute`](#nullability-nullpolicy-and-nullsubstitute)
   - [`ConstructUsing`](#constructusing)
   - [`BeforeMap` / `AfterMap`](#beforemap--aftermap)
   - [`ResolveUsing` — runtime resolver classes](#resolveusing--runtime-resolver-classes)
   - [The four-argument contextual `MapFrom`](#the-four-argument-contextual-mapfrom)
   - [`ProjectUsing` — projection-safe resolvers](#projectusing--projection-safe-resolvers)
   - [`ForPath` — mapping into a nested destination path](#forpath--mapping-into-a-nested-destination-path)
   - [`ReverseMap`](#reversemap)
   - [`PreserveReferences` — cycles and shared references](#preservereferences--cycles-and-shared-references)
   - [`Profile`, `AddProfile`, `AddMaps`](#profile-addprofile-addmaps)
   - [Naming conventions](#naming-conventions)
   - [Global type-pair converters (`RegisterConverter`)](#global-type-pair-converters-registerconverter)
5. [Running a mapping](#running-a-mapping)
   - [`Map<TDestination>(source)` and `Map<TSource,TDestination>(source)`](#maptdestinationsource-and-maptsourcetdestinationsource)
   - [Update-in-place: `Map(source, destination)`](#update-in-place-mapsource-destination)
   - [Per-call options: one-off `Items` and `AfterMap`](#per-call-options-one-off-items-and-aftermap)
   - [`MapAsync`](#mapasync)
   - [`Explain`](#explain)
   - [`GetTypedMapper` — a zero-boxing fast path for hot loops](#gettypedmapper--a-zero-boxing-fast-path-for-hot-loops)
6. [Collections and dictionaries](#collections-and-dictionaries)
7. [Polymorphic dispatch](#polymorphic-dispatch)
8. [Projection: `ProjectTo<T>()` for EF Core and any `IQueryable`](#projection-projecttot-for-ef-core-and-any-iqueryable)
9. [The source generator: `[MapFrom]` and Native AOT](#the-source-generator-mapfrom-and-native-aot)
10. [Roslyn analyzers](#roslyn-analyzers)
11. [Dependency injection](#dependency-injection)
12. [Validation and the full diagnostic catalog](#validation-and-the-full-diagnostic-catalog)
13. [Execution tiers and AOT/trim safety](#execution-tiers-and-aottrim-safety)
14. [Migrating from AutoMapper](#migrating-from-automapper)
15. [Packages](#packages)

## Installation

```
dotnet add package FluxMapper
```

installs everything. To take only what you need:

```
dotnet add package FluxMapper.Core                          # fluent config + execution engine
dotnet add package FluxMapper.SourceGenerator                # [MapFrom] incremental generator (AOT-safe tier)
dotnet add package FluxMapper.Analyzers                      # edit-time diagnostics for [MapFrom]
dotnet add package FluxMapper.Extensions.DependencyInjection # AddFluxMapper for IServiceCollection
```

`FluxMapper.Abstractions` (contracts only — `IMapper`, `IValueResolver<>`, `[MapFrom]`, and so on) comes
in transitively wherever it's needed; you rarely reference it directly.

## Quick start

```csharp
using FluxMapper.Core.Configuration;

public class Address
{
    public string City { get; set; } = "";
    public string Street { get; set; } = "";
}

public class AddressDto
{
    public string City { get; set; } = "";
}

public class Order
{
    public int Id { get; set; }
    public decimal Total { get; set; }
}

public class OrderDto
{
    public int Id { get; set; }
    public decimal Total { get; set; }
}

public class User
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public Address Address { get; set; } = new();
    public List<Order> Orders { get; set; } = [];
}

public class UserDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public AddressDto Address { get; set; } = new();
    public List<OrderDto> Orders { get; set; } = [];
}

var config = MapperConfiguration.Create(cfg =>
{
    cfg.CreateMap<Address, AddressDto>();
    cfg.CreateMap<Order, OrderDto>();
    cfg.CreateMap<User, UserDto>();
});

config.AssertConfigurationIsValid(); // fail fast at startup, not on the first real request

var mapper = config.CreateMapper();
var dto = mapper.Map<UserDto>(user);
```

No `.ForMember()` calls were needed here: `Id`, `Name`, `Total`, and `City` all match by name, and
`Address`/`Orders` are nested/collection members whose element types (`Address`→`AddressDto`,
`Order`→`OrderDto`) both have their own registered maps, so FluxMapper recurses into them automatically.
This is the common case — most of this document is about the remaining 20%: what to do when a member
*doesn't* match by name, needs custom logic, needs to survive an object graph with cycles, or needs to be
translated into a database query instead of executed in memory.

## How FluxMapper thinks about a mapping

Every `CreateMap<TSource, TDestination>()` eventually produces one immutable `MappingPlan`: a description
of exactly how to go from a `TSource` to a `TDestination`; for every writable destination member, which
source expression feeds it, in what order constructor arguments are supplied, and any diagnostics found
along the way. Nothing downstream — the compiled-expression executor, `Explain()`,
`AssertConfigurationIsValid()`, the projection compiler — makes its own independent decisions; they all
read the same plan.

Building that plan runs through the same pipeline for every map, in this order:

1. **Constructor selection.** A public parameterless constructor always wins when one exists (the
   destination is then populated via settable members). Otherwise, the public constructor with the most
   parameters that can *all* be resolved from the source type wins. A destination with no parameterless
   constructor and no fully-resolvable parameterized one is a hard error (`MAPSG002`) unless you supply
   `.ConstructUsing(...)`.
2. **Candidate discovery.** For every destination member not already consumed by a constructor parameter,
   gather every plausible source: an exact name match, a naming-convention match (after configured prefix
   stripping/replacements), a zero-argument method call (`GetName()`/`IsActive()`-style), and *flattening*
   — decomposing the destination name into a chain of nested member names (`AddressCity` → `Address.City`)
   up to 4 levels deep. Every candidate is kept, not just the best one.
3. **Ambiguity resolution.** Each candidate has a confidence score (exact match beats naming-convention
   match beats flattening, and so on). The top-scoring candidate wins — *unless* two or more candidates
   tie for the top score, in which case this is a hard error (`MAP2007`) naming every tied candidate,
   never a silent, order-dependent pick.
4. **Nullability analysis.** A nullable source feeding a non-nullable destination member with no
   configured policy is a hard error (`MAP0001`) rather than a silent `null`/default assignment or a
   runtime `NullReferenceException` surprise. `.NullSubstitute()`/`.NullPolicy()` resolve it explicitly.
5. **Nested/collection/dictionary sub-planning.** A member whose resolved value needs its own recursive
   `MappingPlan` (a complex nested type, a collection of complex elements, a dictionary with complex
   values) gets one, built through this exact same pipeline — there's no separate, weaker code path for
   "the second level of nesting."
6. **Diagnostics.** Anything the earlier stages flagged is attached to the plan. A plan with any
   `Error`-severity diagnostic is not "buildable" and cannot be handed to the execution tier — see
   [Validation and the full diagnostic catalog](#validation-and-the-full-diagnostic-catalog).

Once a plan is built it's cached for the lifetime of the `MapperConfiguration` — building the same
(source, destination) pair's plan twice never happens.

## Configuring a map

Every option below is available two ways: as a flat method directly on the `IMappingExpression<TSource,
TDestination>` returned by `CreateMap` (`.Map(...)`, `.Ignore(...)`, `.Condition(...)`, ...), or bundled
per-member through `.ForMember(destinationMember, opt => ...)`. They are fully interchangeable — pick
whichever reads better, and mix both styles freely within the same `CreateMap` call.

### `CreateMap` and automatic convention matching

```csharp
cfg.CreateMap<Source, Destination>();
```

Calling `CreateMap` for the same `(TSource, TDestination)` pair twice returns the *same* configuration
object — later calls add to it rather than replacing it, so you can split configuration for one pair
across multiple `.CreateMap<S,D>()...` chains if that reads better in your codebase.

By default, a destination member is matched against the source by, in order of preference: an exact
name match, a naming-convention match (see [Naming conventions](#naming-conventions)), a matching
zero-argument method (`string GetName()` satisfies a destination member named `Name`), or a flattened
chain of nested member names (`OrderTotal` on the destination can resolve to `source.Order.Total`, up to
4 levels deep). If nothing matches and nothing was explicitly configured, the member is left unresolved
and configuration validation fails with `MAP2001`.

### `Map` — explicit member sources

```csharp
cfg.CreateMap<Order, OrderDto>()
    .Map(d => d.ProductCode, s => s.Sku);
```

The flat equivalent of AutoMapper's `.ForMember(d => d.ProductCode, opt => opt.MapFrom(s => s.Sku))`.
Use this when the destination and source member names genuinely don't correspond and you just need a
different source expression — it participates in nullability/nested-mapping analysis exactly like an
auto-discovered member, because the resulting `ResolvedSource` is exactly the same one convention
discovery would have produced.

### `ForMember` and the member options builder

```csharp
cfg.CreateMap<Order, OrderDto>()
    .ForMember(d => d.ProductCode, opt => opt.MapFrom(s => s.Sku))
    .ForMember(d => d.InternalNotes, opt => opt.Ignore())
    .ForMember(d => d.DiscountedTotal, opt => opt.Condition(s => s.HasDiscount));
```

`ForMember`'s options builder (`IMemberConfigurationExpression<TSource,TDestination,TMember>`) exposes
`MapFrom` (both the expression-based and four-argument contextual overloads), `Ignore`, `Condition`,
`NullSubstitute`, `NullPolicy`, `ResolveUsing<TResolver>()`, and `ProjectUsing<TResolver>()` — every one
of them forwards to the identically-named flat method on `IMappingExpression<TSource,TDestination>`.
There is no behavioral difference between the two styles; `ForMember` exists purely because it groups
every option for one member under one call, which some codebases prefer for readability.

### `Ignore`

```csharp
cfg.CreateMap<Order, OrderDto>().Ignore(d => d.InternalNotes);
```

Excludes a destination member from automatic discovery and validation entirely — it is never assigned by
FluxMapper (it keeps whatever value the constructor/object initializer gave it) and never reported as
unresolved. Use this for a destination member that's populated some other way (a default value, code
running after the `Map` call, and so on) rather than leaving it to fail with `MAP2001`.

### `Condition`

```csharp
cfg.CreateMap<Patch, Entity>()
    .Condition(d => d.Name, s => s.AllowNameChange);
```

Guards a member assignment on a predicate evaluated against the *source* object. On construction
(`Map<TDestination>(source)`), a false condition yields `default(TMember)` for that member — there's no
existing value to fall back to. On update-in-place (`Map(source, destination)`), a false condition is
where `Condition` earns its keep: the destination member is left completely untouched, preserving
whatever value it already had:

```csharp
var entity = new Entity { Name = "Original", Value = 1 };
mapper.Map(new Patch { Name = "Changed", AllowNameChange = false, Value = 2 }, entity);
// entity.Name is still "Original" -- the condition was false, so update-in-place left it alone.
// entity.Value is now 2 -- Value has no condition, so it always updates.
```

### Nullability: `NullPolicy` and `NullSubstitute`

A nullable source member feeding a non-nullable destination member is, by default, a configuration error
(`MAP0001`) — FluxMapper refuses to guess whether you'd rather throw, substitute a default, or leave the
destination member alone. Resolve it explicitly with one of:

```csharp
cfg.CreateMap<Person, PersonDto>()
    .NullSubstitute(d => d.Nickname, "N/A");              // null source -> "N/A"; non-null passes through

cfg.CreateMap<Person, PersonDto>()
    .NullPolicy(d => d.Nickname, NullPolicy.Default);      // null source -> default(TMember)
```

The five `NullPolicy` values:

| Policy | Behavior on a `null` source value |
|---|---|
| `Throw` (default, if nothing else is configured) | Throws at map time. |
| `Ignore` | Leaves the destination member at its default/unset value. |
| `Substitute` | Uses the value passed to `.NullSubstitute(...)`. |
| `Default` | Uses `default(TMember)`. |
| `Map` | Passes the `null` straight through — only legal when the destination member is itself nullable. |

`.NullSubstitute(destinationMember, value)` is really `.NullPolicy(destinationMember, NullPolicy.Substitute)`
plus recording `value`; you rarely need to call `.NullPolicy()` directly except to select `Ignore`,
`Default`, or `Map` explicitly.

### `ConstructUsing`

```csharp
cfg.CreateMap<Order, OrderDto>()
    .ConstructUsing(s => new OrderDto(s.Id));
```

Replaces automatic constructor selection entirely for this map. Every other configured/writable member
is still discovered and assigned afterward exactly as normal — this only takes over the `new
OrderDto(...)` step itself, so you don't lose convention-based mapping for the rest of the type just
because the constructor needs something special.

### `BeforeMap` / `AfterMap`

```csharp
cfg.CreateMap<Order, OrderDto>()
    .BeforeMap((src, dest) => dest.ProcessedAtUtc = DateTime.UtcNow)
    .AfterMap((src, dest) => dest.Total = Math.Round(dest.Total, 2));
```

Configured once, on the map itself, these run for *every* mapping of this `(TSource, TDestination)`
pair — including every time it's reached as a nested member inside a larger object graph, not just
top-level `Map()` calls. `BeforeMap` runs immediately after construction and before any member is
assigned; `AfterMap` runs immediately after every configured member has been assigned. For a one-off
hook that should apply to a single call only, see [per-call options](#per-call-options-one-off-items-and-aftermap)
instead — there is deliberately no per-call `BeforeMap` (see that section for why).

Both take a plain `Action<TSource,TDestination>`, matching AutoMapper's shape — which means an `async`
lambda compiles but is never awaited. If you need the hook to `await` something, use
[`MapAsync`](#mapasync) instead.

### `ResolveUsing` — runtime resolver classes

```csharp
public sealed class GreetingResolver(IGreetingService greetingService)
    : IValueResolver<Contact, GreetingDto, string>
{
    public string Resolve(Contact source, GreetingDto destination, string destinationMember, ResolutionContext context)
        => greetingService.Greet(source.First);
}

cfg.CreateMap<Contact, GreetingDto>()
    .ResolveUsing<string, GreetingResolver>(d => d.Greeting);
```

For a member whose value needs arbitrary code — calling a service, doing multi-step logic — rather than
a pure expression. `TResolver` is deliberately *not* constrained to `new()`: it may declare constructor
parameters, resolved from the `IServiceProvider` passed to `AddFluxMapper`/`MapperConfiguration
.CreateMapper(services)` (see [Dependency injection](#dependency-injection)). Without DI, a resolver with
no dependencies just needs a public parameterless constructor and is instantiated via
`Activator.CreateInstance`.

`IValueResolver<>` is **never** usable inside a `ProjectTo<T>()` projection — it executes arbitrary code,
which no `IQueryable` provider can translate to SQL. For a resolver that *is* projection-safe, see
[`ProjectUsing`](#projectusing--projection-safe-resolvers).

### The four-argument contextual `MapFrom`

Sometimes a member's value depends on something known only at the call site — not the source object, and
not something worth a permanent `.Condition()`/`.Map()` on the type pair itself. This mirrors
AutoMapper's `opt.Items["key"] = value` plus a four-argument `.MapFrom((src, dest, current, context) =>
...)` that reads it back:

```csharp
cfg.CreateMap<Article, ArticleDto>()
    .ForMember(d => d.TextArWithParams, opt => opt.MapFrom((src, dest, current, context) =>
        context.Items.TryGetValue("EditReasonId", out var v) && v is int id && id == EditReasonType.DecisionByBoard.Id
            ? src.TextWithParams
            : (src.Article is null ? string.Empty : src.TextWithParams)));

// ... at the call site:
var dto = mapper.Map<Article, ArticleDto>(article, opt => opt.Items["EditReasonId"] = editReasonId);
```

A flat, non-`ForMember` equivalent exists too, for the same reason every other option has a flat form:

```csharp
cfg.CreateMap<Article, ArticleDto>()
    .ResolveUsing<string>(d => d.TextArWithParams, (src, dest, current, context) =>
        context.Items.TryGetValue("EditReasonId", out var v) /* ... */);
```

`context` is a `ResolutionContext` — the same type your `IValueResolver<>`/`IValueConverter<>`
implementations receive. `context.Items` is empty and the whole `ResolutionContext` is ambient-free for a
plain `mapper.Map(source)` call with no options: a `ResolutionContext` is only constructed and threaded
through the mapping when the caller actually populates `Items` via
`opt.Items["key"] = value`, so a contextual `MapFrom` that never sees populated `Items` costs nothing
extra over a plain expression-based `Map`. `Items` set for one call never leaks into a later, unrelated
call — each populated `ResolutionContext` is scoped to exactly the one `Map()` invocation that created it,
even under concurrent/reentrant calls (it's backed by `AsyncLocal<T>`, not a shared static).

The "current value" parameter (third argument) is always a default value in this version of FluxMapper —
there is not yet a way to read the in-progress destination's already-assigned value from inside a
resolver. This matches a documented, existing limitation of `IValueResolver<>` too; both are tracked
together as a possible future enhancement.

### `ProjectUsing` — projection-safe resolvers

```csharp
public sealed class FullNameProjectionResolver : IProjectionValueResolver<Contact, string>
{
    public Expression<Func<Contact, string>> GetExpression() => c => c.First + " " + c.Last;
}

cfg.CreateMap<Contact, ContactDto>()
    .ProjectUsing<string, FullNameProjectionResolver>(d => d.FullName);
```

Unlike `IValueResolver<>`, an `IProjectionValueResolver<TSource,TMember>` hands back an
`Expression<Func<TSource,TMember>>` rather than executing code — that expression is spliced directly into
both the compiled-expression execution tier's tree *and* into a `ProjectTo<T>()` query, so it stays
translatable by a real `IQueryable` provider (EF Core included). `TResolver` here *is* constrained to
`new()`, since it's never meant to carry dependencies — a projection body has to survive being handed to
a database provider, which a captured DI-resolved instance would not.

### `ForPath` — mapping into a nested destination path

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

Things worth knowing before you reach for `ForPath`:

- Every intermediate segment in the path (`SaudiAddress` above) is **always freshly constructed** — it
  needs a public parameterless constructor, or configuration validation fails with `MAP0004` — and is
  never merged into an existing instance, even when mapping into an existing destination
  (`Map(source, destination)`).
- Multiple `ForPath` calls that share a common prefix (`d.SaudiAddress.City` and
  `d.SaudiAddress.PostalCode` above) merge into the *same* constructed subtree rather than each building
  their own `SaudiAddress` — you never pay for redundant construction just because you configured two
  leaves under the same branch.
- Any other member of a `ForPath`-touched type left uncovered by a `ForPath` registration keeps its own
  default value; it is not separately validated or convention-matched, the same way `.Ignore()` already
  exempts a member.
- A single-hop selector (`d => d.City`) doesn't need `ForPath` at all and is rejected with an
  `ArgumentException` at configuration time — use `.Map()`/`.ForMember()` for that.
- The `IPathConfigurationExpression<TSource,TMember>` passed to a `ForPath` callback only exposes
  `MapFrom(Expression<Func<TSource,TMember>>)` — no `Ignore`/`Condition`/etc. A path leaf is always
  freshly constructed alongside the rest of its tree, so "ignore this leaf" doesn't have the same meaning
  it does for an ordinary top-level member.

### `ReverseMap`

```csharp
cfg.CreateMap<Order, OrderDto>().ReverseMap();
```

Registers the reverse `(TDestination, TSource)` map automatically. Reversibility is *checked*, not
assumed: if the forward map uses flattening, a custom resolver, or a custom converter on any member
(information that can't be mechanically un-collapsed), the reverse map must carry at least one explicit
`.Map()` override of its own — evidence you're aware of and have addressed the irreversible member(s) —
or configuration validation fails with `MAP0003`, naming every member that made the map irreversible:

```csharp
cfg.CreateMap<FlattenSource, FlattenDto>()   // FlattenDto.NestedValue <- flattened from source.Nested.Value
    .ReverseMap()                             // fails MAP0003 without the next line
    .Map(d => d.Nested, s => new Nested { Value = s.NestedValue });
```

### `PreserveReferences` — cycles and shared references

```csharp
cfg.CreateMap<Employee, EmployeeDto>().PreserveReferences();
```

Opts a map into reference-cycle-safe, identity-preserving execution: an object reachable more than once
from the same root `Map()` call — including a genuine cycle (`manager.Reports[0].Manager == manager`) —
is mapped exactly once, and the *same* destination instance is reused for every later occurrence, instead
of infinite recursion or duplicate destination objects. This works for destination types constructible
via a parameterless constructor plus member assignment; it's opt-in per map (an unconfigured map
mapping the same shared source object twice still produces two independent destination instances, which
is usually what you want and never a surprise).

### `Profile`, `AddProfile`, `AddMaps`

```csharp
public class OrderProfile : Profile
{
    public OrderProfile()
    {
        CreateMap<Order, OrderDto>()
            .ForMember(d => d.ProductCode, opt => opt.MapFrom(s => s.Sku));
    }
}

// Register one profile instance or type:
var config = MapperConfiguration.Create(cfg => cfg.AddProfile<OrderProfile>());
// or: cfg.AddProfile(new OrderProfile());

// Or scan a whole assembly for every public Profile with a parameterless constructor,
// mirroring AutoMapper's services.AddAutoMapper(Assembly.GetExecutingAssembly()):
var config2 = MapperConfiguration.Create(cfg => cfg.AddMaps(typeof(OrderProfile).Assembly));
```

A `Profile` groups related `CreateMap` calls into one reusable, discoverable unit. It needs a public
parameterless constructor to be picked up by `AddMaps`' assembly scan. A map registered directly on the
top-level configuration for the same `(TSource, TDestination)` pair a profile also registers is
overwritten by the profile's version when both are present.

### Naming conventions

```csharp
var config = MapperConfiguration.Create(cfg =>
{
    cfg.UseNamingConvention(new NamingConvention()
        .RecognizePrefix("m_")
        .Replace("_", ""));
    cfg.CreateMap<Source, Destination>();
});
```

`RecognizePrefix` strips a recognized prefix before comparing names (the first matching prefix wins, and
only one is stripped); `Replace` applies a substring replacement afterward. Both apply to every map
registered *after* the naming convention is configured — set it before your `CreateMap` calls. Naming
conventions affect exact-vs-convention candidate scoring (an exact match still outranks a
naming-convention match) and are the same normalization used by flattening's member-name decomposition,
so both agree on "what a name means."

Build on `new NamingConvention()`, never `NamingConvention.Default` — `RecognizePrefix`/`Replace` mutate
the instance they're called on and return it (not copy-on-write), so chaining calls onto the shared
`Default` singleton mutates it for every other map in the process that also falls back to `Default`, not
just the one you're configuring.

**Ready-made presets** for the common "our DTOs are PascalCase, the wire format/legacy schema is
snake_case" case:

```csharp
cfg.UseNamingConvention(NamingConvention.SnakeCase()); // user_name <-> UserName
cfg.UseNamingConvention(NamingConvention.LowerUnderscore()); // alias for SnakeCase(), matching AutoMapper's name
```

Both return a fresh instance on every call, so it's safe to chain further customization onto what they
return (e.g. `NamingConvention.SnakeCase().RecognizePrefix("legacy_")`) without touching a shared
singleton. There's no `KebabCase` preset: a naming convention compares real CLR member names
(`MemberInfo.Name`), and a C# (or VB/F#) property or field name can never contain a hyphen in the first
place, so a kebab-case *member-name* convention could never match anything real.

### Global type-pair converters (`RegisterConverter`)

```csharp
cfg.RegisterConverter(new MoneyToDecimalConverter()); // instance-based, shared across every call
cfg.RegisterConverter<LegacyStatus, string, LegacyStatusConverter>(); // type-based, DI-resolved per plan build

public class MoneyToDecimalConverter : IValueConverter<Money, decimal>
{
    public decimal Convert(Money source, ResolutionContext context) => source.Amount;
}
```

AutoMapper's `CreateMap<TSource,TDestination>().ConvertUsing(...)` equivalent, applied globally: once
registered, `converter` (or `TConverter`, for the type-based overload) is used for *every* member, across
*every* map, whose resolved source value type is exactly `TSource` and destination value type is exactly
`TDestination` — no repeating `ResolveUsing` on every member that happens to touch a recurring conversion
(a custom `Money` type, a string-backed ID, a legacy enum shape). An explicit per-member override
(`ResolveUsing`/`ProjectUsing`/an explicit `.Map(...)` expression) always takes precedence over this
default for that one member. The instance-based overload shares one converter instance across every call
site it applies to; the type-based overload resolves a fresh instance per plan build through the same
DI-first/Activator-fallback rule every other resolver already uses — prefer it when the converter has
DI-injected dependencies rather than being safe to share as a single instance forever. Register global
converters on the top-level `MapperConfigurationExpression`, not inside a `Profile` — `AddProfile` doesn't
merge a profile's global converters today. Like a per-member converter, a member resolved this way is
never projection-safe (see [Projection](#projection-projecttot-for-ef-core-and-any-iqueryable)) — a global
converter runs arbitrary code, which a real `IQueryable` provider cannot translate.

## Running a mapping

`config.CreateMapper()` (or DI-resolved `IMapper`) gives you an `IMapper` with these members:

### `Map<TDestination>(source)` and `Map<TSource,TDestination>(source)`

```csharp
var dto = mapper.Map<UserDto>(user);                 // TSource inferred from the runtime type of `user`
var dto2 = mapper.Map<User, UserDto>(user);           // both types pinned explicitly at the call site
```

Constructs a brand-new `TDestination` from `source`. Prefer the two-type-parameter overload when you
already know both types statically — it avoids a runtime type lookup and reads slightly more explicitly
at the call site.

### Update-in-place: `Map(source, destination)`

```csharp
mapper.Map(patch, existingEntity);
```

Maps `source` onto the already-constructed `destination` instead of creating a new instance — the
"patch" pattern. This is where `.Condition()` is most useful: a false condition leaves the existing
destination value untouched rather than overwriting it with a mapped-but-discarded value (see
[`Condition`](#condition) above).

### Per-call options: one-off `Items` and `AfterMap`

```csharp
var dto = mapper.Map<Order, OrderDto>(order,
    opt => opt.AfterMap((src, dest) => dest.RequestId = requestId));

var dto2 = mapper.Map<Article, ArticleDto>(article,
    opt => opt.Items["EditReasonId"] = editReasonId);
```

`IMappingOperationOptions<TSource,TDestination>` exposes `AfterMap` (a hook for this single call only —
it does not affect any other mapping of the pair) and `Items` (per-call ambient state, read back via
`ResolutionContext.Items` inside a resolver or contextual `MapFrom` anywhere in this call's object graph
— see [The four-argument contextual `MapFrom`](#the-four-argument-contextual-mapfrom)). There is
deliberately no per-call `BeforeMap`: by the time you have a constructed `TDestination` to act on,
mapping has already finished, so a per-call "before" hook has nothing distinct to run against — configure
`.BeforeMap()` on the map itself instead when you need to see the destination before its members are
populated.

### `MapAsync`

```csharp
var dto = await mapper.MapAsync<Order, OrderDto>(order, async (src, dest) =>
{
    dest.Status = await statusService.GetStatusAsync(src.Id);
});
```

Maps synchronously, then awaits `afterMapAsync` against the finished `TDestination` before returning it.
This exists specifically because `AfterMap`'s delegate type is `Action<TSource,TDestination>` (matching
AutoMapper) — passing an `async` lambda there compiles, but the resulting `Task` is never awaited, so any
exception or ordering guarantee you expected from that "await" silently disappears. `MapAsync` gives that
same "do something async with the mapped result" need a signature that's actually awaited end to end.

### `Explain`

```csharp
Console.WriteLine(mapper.Explain<User, UserDto>());
```

Produces a human-readable description of exactly how `TSource` maps to `TDestination`: every member,
which strategy resolves it (`DirectAssignment`, `Flattening`, `NestedMapping`, `CollectionMapping`,
`DictionaryMapping`, `CustomResolver`, `ConstructorArgument`, `PathMapping`, ...), its `NullPolicy` when
it isn't the default, whether a `Condition` is configured, and any diagnostics attached to the plan.
`Explain` is a pure formatter over the already-built `MappingPlan` — it never re-derives mapping
decisions, only renders the ones the plan builder already made, so what you see is exactly what will run.

### `GetTypedMapper` — a zero-boxing fast path for hot loops

```csharp
var fast = mapper.GetTypedMapper<User, UserDto>(); // build/cache once, outside the loop

foreach (var user in users)
    results.Add(fast(user)); // no object boxing, no cache lookup, from here on
```

`Map<TDestination>(object source)` and `Map<TSource,TDestination>(source)` both go through an
`object`-boxed entry point by design: `IMapper` is a single, non-generic-source call site that also has to
support runtime-polymorphic dispatch (a `TSource` that's a base type, with the actual instance a
registered subtype — see [Polymorphic dispatch](#polymorphic-dispatch)), which needs the source's *runtime*
type, not just its compile-time one. That costs a small, constant amount per call: a cast at the `object`
boundary and a `source.GetType()` read.

`GetTypedMapper<TSource,TDestination>()` is the opt-in escape hatch for a caller who already knows the
exact static type pair and is calling it enough times that the constant cost adds up — a bulk import/export
job, a hot request path. It compiles a delegate parameterized directly on `TSource`/`TDestination` (no
`object` anywhere in its signature) and caches it per type pair on the `Mapper` instance, the same way
`Map`'s own delegate cache is scoped — never a process-wide static, so two `Mapper` instances built from
two different `MapperConfiguration`s never share a cached delegate. Polymorphic subtype dispatch configured
on the plan, if any, still fires correctly through the returned delegate; only the *entry* boundary changes
from `object` to `TSource`. Store the returned `Func<TSource,TDestination>` somewhere that outlives the
loop (a field, a local above the loop) — calling `GetTypedMapper` itself isn't free the first time for a
given pair (it builds and compiles the delegate), only cheap on every call after that.

## Collections and dictionaries

Every collection/array/set destination shape FluxMapper recognizes is populated automatically once the
element type itself is mappable (either directly convertible, or has its own registered `CreateMap`):

```csharp
public class Roster
{
    public List<string> Names { get; set; } = [];
    public List<Order> Orders { get; set; } = [];
}

public class RosterDto
{
    public ImmutableArray<string> Names { get; set; }      // System.Collections.Immutable
    public List<OrderDto> Orders { get; set; } = [];
}

cfg.CreateMap<Order, OrderDto>();
cfg.CreateMap<Roster, RosterDto>();
```

Supported destination collection shapes: arrays, `List<T>`, `HashSet<T>` (deduplicating), any
`IEnumerable<T>`-compatible interface, and the `System.Collections.Immutable` family —
`ImmutableArray<T>`, `ImmutableList<T>`/`IImmutableList<T>`, `ImmutableHashSet<T>`/`IImmutableSet<T>`.
When the source exposes `Count`/`Length`, the destination collection's capacity is preallocated from it
rather than growing incrementally. An element type that converts directly (matching primitive/simple
types) skips building a whole recursive `MappingPlan` for the element — that would be pure overhead for,
say, `List<int> -> List<int>`.

Dictionaries are a distinct, keyed shape (`IDictionary<TKey,TValue>`/`IReadOnlyDictionary<TKey,TValue>`,
`Dictionary<,>`, and `ImmutableDictionary<,>`/`IImmutableDictionary<,>`), detected *before* the plain
collection check even though a dictionary is technically also an `IEnumerable<KeyValuePair<,>>` — treating
it as a plain sequence would silently produce a collection of key/value pairs instead of a keyed
dictionary:

```csharp
public class Catalog
{
    public Dictionary<string, int> Counts { get; set; } = [];
    public Dictionary<string, Tag> Tags { get; set; } = [];
}

cfg.CreateMap<Tag, TagDto>();
cfg.CreateMap<Catalog, CatalogDto>();
```

Dictionary values recurse the same way collection elements do (a complex value type gets its own
`MappingPlan`; a directly-convertible one doesn't). Keys are intentionally restricted to direct
conversion only — real-world dictionary keys are overwhelmingly simple types (`string`/`int`/enum/`Guid`),
and a full recursive key-mapping-with-identity story (what does it mean for two *mapped* keys to
collide?) is out of scope by design.

## Polymorphic dispatch

If a base-typed member's runtime type has its *own* registered `CreateMap`, that map is used instead of
the base map — automatically, with no extra configuration beyond registering the subtype maps:

```csharp
public class Animal { public string Name { get; set; } = ""; }
public class Dog : Animal { public string Breed { get; set; } = ""; }
public class Cat : Animal { public bool Indoor { get; set; } }

public class Shelter { public Animal Pet { get; set; } = null!; }

cfg.CreateMap<Animal, AnimalDto>();
cfg.CreateMap<Dog, DogDto>();     // DogDto : AnimalDto
cfg.CreateMap<Cat, CatDto>();     // CatDto : AnimalDto
cfg.CreateMap<Shelter, ShelterDto>();

var dto = mapper.Map<ShelterDto>(new Shelter { Pet = new Dog { Name = "Rex", Breed = "Husky" } });
// dto.Pet is actually a DogDto, with Breed populated -- not just an AnimalDto with Breed dropped.
```

An instance that really is the exact base type (not a registered subtype) still falls back to the base
plan, as you'd expect. Dispatch compiles to a runtime `GetType()` check chain resolved purely from
registered configuration at plan-build time — never runtime reflection-based guessing — and cases are
mutually exclusive on exact runtime type, so there's never an ambiguous partial match.

## Projection: `ProjectTo<T>()` for EF Core and any `IQueryable`

```csharp
using FluxMapper.Core.Projection;

var dtos = await dbContext.Orders.ProjectTo<OrderDto>(config).ToListAsync();
```

`ProjectTo<TDestination>()` builds a `Select()` lambda and hands it to `IQueryable.Provider.CreateQuery`,
so it goes through exactly the same path a real EF Core `DbSet<T>`/`IQueryable<T>` would take, translated
into a single SQL query by whatever provider backs the source query — `FluxMapper.Core` doesn't reference
EF Core at all; it works against `IQueryable` itself, so it works unmodified against EF Core, Dapper's
query providers, or any other real `IQueryable` implementation. `SelectProject` is an identical alias, for
a codebase migrating from AutoMapper that wants to avoid the name colliding textually with AutoMapper's
identically-named but differently-typed extension method during a side-by-side migration.

Projection is a **separate, deliberately narrower** expression builder from the compiled-expression
runtime tier — not every plan that runs fine at runtime is translatable to SQL. Before applying a
`Select()`, FluxMapper validates the resolved plan and throws `ProjectionTranslationException` (naming the
offending member) if it contains anything a query provider can't translate:

- A `IValueResolver<>`-based member (`.ResolveUsing<TMember,TResolver>()`) — arbitrary code, not an
  expression. Use `IProjectionValueResolver<>` (`.ProjectUsing<TMember,TResolver>()`) instead, which hands
  back a translatable expression and *is* projection-safe.
- A dictionary-valued member, or a dictionary as the projection root — `ToDictionary()` does not
  translate over `IQueryable` as of this writing.
- A `ForPath`-touched member — it constructs and mutates real objects, which isn't an expression a LINQ
  provider could translate.
- A member with a `BeforeMap`/`AfterMap` hook configured on its nested plan, or any unresolved member.

`config.GetProjectionExpression<TSource,TDestination>()` returns the raw `Expression<Func<TSource,
TDestination>>` FluxMapper would use, without applying it to a query — useful for composing it manually
against a provider-specific API, or asserting on its shape in a test.

## The source generator: `[MapFrom]` and Native AOT

```csharp
using FluxMapper.Abstractions;

[MapFrom(typeof(Order))]
public partial class OrderDto
{
    public int Id { get; set; }
    public decimal Total { get; set; }
}

var dto = OrderDto.MapFrom(order); // generated at compile time -- IMapper is never involved
```

`FluxMapper.SourceGenerator` is a Roslyn incremental generator that turns `[MapFrom(typeof(TSource))]`
on a `partial class`/`partial record` into a real, generated `static TDestination MapFrom(TSource
source)` method on that same type — genuinely zero reflection and zero `Expression.Compile()` at
runtime, because everything it produces is ordinary compiled C# your assembly ships with, not code
assembled at runtime. This is the **only** execution path in FluxMapper that can honestly report
AOT-safety, and it's why Native AOT-published applications should reach for `[MapFrom]` on their DTOs
wherever the mapping is flat enough to qualify (see the scope note below).

Member matching here is deliberately simple and self-contained: an exact-name, direct-or-implicit-
conversion match between a public settable destination property and a public readable source property of
the same name. It does **not** share `MappingPlanBuilder`'s richer pipeline — no naming conventions, no
flattening, no nested/collection mapping, no resolvers, no `ReverseMap`. If a destination property has no
matching source property, it's simply left off the generated initializer (default-initialized) rather
than causing a generator error; see [Roslyn analyzers](#roslyn-analyzers) for how you find out about that
at edit time instead of by inspecting generated code.

Every other execution path in FluxMapper (everything reached through `IMapper`) is annotated
`[RequiresDynamicCode]`/`[RequiresUnreferencedCode]` — accurately, not defensively — because it always
compiles a `System.Linq.Expressions` tree and discovers members via reflection. A trimmed or Native AOT
published application calling through `IMapper` gets a build-time warning pointing at this, rather than a
confusing runtime failure. `samples/FluxMapper.AotSmokeTest` in the repository is a real, working Native
AOT-published console app exercising the `[MapFrom]` path end to end.

## Roslyn analyzers

`FluxMapper.Analyzers` runs independently of the source generator, so a project using `[MapFrom]` gets
IDE-time feedback even without invoking `FluxMapper.SourceGenerator` directly:

- **`FLUX0001`** (error): the `[MapFrom]`-attributed class isn't declared `partial`. Without this
  diagnostic, the failure you'd actually see is a confusing "type already defines a member called
  `MapFrom`" or simply "nothing got generated" — this points straight at the real cause.
- **`FLUX0002`** (warning): the attributed class has zero destination members the generator could match
  against the declared source type (the same exact-name, direct/implicit-conversion rule the generator
  itself uses) — almost certainly a naming mismatch or the wrong source type, not an intentional
  "generate an empty mapper."

## Dependency injection

```csharp
using FluxMapper.Extensions.DependencyInjection;

services.AddFluxMapper(cfg =>
{
    cfg.CreateMap<Contact, GreetingDto>()
        .ResolveUsing<string, GreetingResolver>(d => d.Greeting); // GreetingResolver takes a DI dependency
});

// later, anywhere in the container:
public class SomeService(IMapper mapper) { /* ... */ }
```

`AddFluxMapper` registers a `MapperConfiguration` (always a singleton — it's immutable once built, so
there's never a reason to rebuild it per request) and an `IMapper` resolved from it via
`MapperConfiguration.CreateMapper(services)`, passing the container's `IServiceProvider` through so any
`IValueResolver<>`/`IValueConverter<>` your configuration references can itself take constructor-injected
dependencies instead of always requiring a parameterless constructor.

Overloads:

```csharp
services.AddFluxMapper(cfg => { /* ... */ });                                  // build from a callback, Singleton IMapper
services.AddFluxMapper(cfg => { /* ... */ }, ServiceLifetime.Scoped);          // build from a callback, Scoped IMapper
services.AddFluxMapper(existingConfiguration);                                 // reuse a MapperConfiguration built elsewhere
services.AddFluxMapper(Assembly.GetExecutingAssembly());                       // AddMaps assembly scan, Singleton
services.AddFluxMapper(ServiceLifetime.Scoped, Assembly.GetExecutingAssembly()); // AddMaps assembly scan, Scoped
```

`mapperLifetime` defaults to `Singleton` — the common case, where no resolver has a scoped dependency.
Pick `Scoped` instead when at least one registered `IValueResolver<>`/`IValueConverter<>` takes a scoped
dependency (a scoped `DbContext`, for instance) in its constructor: a resolver's dependencies are
resolved once, at the moment the compiled delegate for its `(source, destination)` pair is first built by
a given `IMapper` instance — a Singleton-lifetime `IMapper` would otherwise capture the *first* request's
scoped instance and keep reusing it for every later request, which is exactly the bug `Scoped` avoids.

## Validation and the full diagnostic catalog

```csharp
config.AssertConfigurationIsValid();
```

Walks every registered map's plan — recursively through nested/collection sub-plans — and throws a
`ConfigurationValidationException` aggregating every `Error`-severity diagnostic found anywhere in the
graph. Call this once, at startup (or in a test), rather than discovering a bad mapping the first time
a request happens to exercise it. `ex.Diagnostics` gives you the full `IReadOnlyList<PlanDiagnostic>` if
you want to inspect codes programmatically instead of parsing the exception message.

Every diagnostic FluxMapper can produce:

| Code | Severity | Meaning | How to fix |
|---|---|---|---|
| `MAP0001` | Error | A nullable source member feeds a non-nullable destination member with no configured policy. | `.NullSubstitute(d => d.X, value)` or `.NullPolicy(d => d.X, NullPolicy.Ignore/.Default/.Map)`. |
| `MAP0002` | Error | The resolved source and destination member types can't convert directly (not identical, not assignable, not both numeric/enum, no `implicit`/`explicit` operator) and no converter/resolver is configured. | Register a converter, or `.ResolveUsing<>()`/`.Map()` with an expression that performs the conversion. |
| `MAP0003` | Error | `.ReverseMap()` was requested, but the forward map uses flattening/a custom resolver/a custom converter on a member, and the reverse map has no explicit override compensating for it. | Add `.Map(d => d.OriginalMember, s => /* explicit reverse path */)` on the reverse map. |
| `MAP0004` | Error | A `.ForPath(...)` target segment's type has no public parameterless constructor. | Give the type a parameterless constructor, or map that member without `ForPath`. |
| `MAP0099` | Warning | Circular or excessively deep type graph detected while building a plan; the plan builder stopped recursing and this branch maps no further members. | If this is reached at runtime for an object not already seen earlier in the same `Map()` call, add `.PreserveReferences()` so the object graph resolves via runtime identity instead of relying on unrolled plan depth. |
| `MAP2001` | Error | No source member, method, or flattened path could be resolved for a destination member. | `.Map(d => d.X, s => /* explicit source expression */)`, or `.Ignore(d => d.X)` if it's intentionally unmapped. |
| `MAP2007` | Error | Two or more candidate sources tied for the same top confidence score for one destination member — never silently resolved. | `.Map(d => d.X, s => s.Whichever)` to pick one explicitly; the diagnostic lists every tied candidate. |
| `MAP4001` | Error | (Projection only, thrown by `ProjectTo<T>()`/`GetProjectionExpression`, not by `AssertConfigurationIsValid`) The plan isn't safe to project over `IQueryable` — a runtime-only resolver/converter, a dictionary-valued member, polymorphic dispatch, a `.Condition()`, or a `ForPath`-touched member somewhere in the shape. | Remove the offending member from the projected shape, map it after materializing (`.ToList()` then a normal in-memory `Map()`), or replace a runtime `IValueResolver<>` with `IProjectionValueResolver<>` + `.ProjectUsing<>()`. |
| `MAPSG002` | Error | No usable public constructor for the destination: no parameterless constructor exists, and no parameterized constructor's parameters could all be resolved from the source type. | `.ConstructUsing((src) => new Destination(/* ... */))`. |

`FLUX0001`/`FLUX0002` (the `[MapFrom]` analyzer's diagnostics) are edit-time-only and not part of this
runtime catalog — see [Roslyn analyzers](#roslyn-analyzers).

## Execution tiers and AOT/trim safety

FluxMapper picks the execution strategy per mapping *shape*, not globally, so you don't have to choose
one trade-off for your whole codebase:

- **`[MapFrom]` + the source generator** compiles to real C# at build time — zero reflection, zero
  `Expression.Compile()`. This is the only tier that's genuinely Native AOT-safe. See
  [The source generator](#the-source-generator-mapfrom-and-native-aot).
- **Everything reached through `IMapper`** (nested objects, collections, dictionaries, polymorphism,
  cycles, custom resolvers, `ForPath`, ...) runs through a cached compiled-expression tier: a
  `MappingPlan` compiled once into a `System.Linq.Expressions`-built delegate and cached for the lifetime
  of the `IMapper`. This is fast (the expression tree is compiled once, not interpreted per call) but
  honestly annotated `[RequiresDynamicCode]`/`[RequiresUnreferencedCode]` rather than silently breaking a
  trimmed or Native AOT-published app — you get a build-time warning, not a runtime surprise.
- **`ProjectTo<T>()`** uses a third, deliberately simpler expression builder whose only job is producing
  something a real `IQueryable` provider can translate — see
  [Projection](#projection-projecttot-for-ef-core-and-any-iqueryable) for exactly what that does and
  doesn't include.

If your application publishes with `PublishAot=true` (or trims aggressively), audit which of your
mappings can move to `[MapFrom]` — flat DTOs are usually the majority of a typical mapping surface, and
every one you move off `IMapper` is one less `[RequiresDynamicCode]` warning to reason about.

## Migrating from AutoMapper

FluxMapper's fluent surface is deliberately AutoMapper-shaped where the concepts genuinely match, so most
of a migration is a namespace change and picking the FluxMapper name for the handful of things that
differ:

| AutoMapper | FluxMapper | Notes |
|---|---|---|
| `IMapper.Map<TDestination>(source)` | Same | Identical shape. |
| `Profile` + `CreateMap` | Same | Identical shape; register with `AddProfile`/`AddMaps`. |
| `.ForMember(d => d.X, opt => opt.MapFrom(s => s.Y))` | Same | Identical shape. |
| `.ForMember(d => d.X, opt => opt.Ignore())` / `.Ignore(d => d.X)` | Same | Both flat and `ForMember` forms exist. |
| `.ForMember(d => d.X, opt => opt.Condition(s => ...))` | Same | Same false-condition-on-update-in-place semantics. |
| `.NullSubstitute(value)` | `.NullSubstitute(d => d.X, value)` (flat) or via `ForMember` | Same concept; FluxMapper additionally exposes the full `NullPolicy` enum directly. |
| `.ConstructUsing(...)` | Same | Identical shape. |
| `.BeforeMap(...)` / `.AfterMap(...)` | Same | Identical shape, plus per-call `AfterMap` via mapping options. |
| `opt.Items["key"] = value` + four-argument `MapFrom` | Same | Identical shape — see [contextual `MapFrom`](#the-four-argument-contextual-mapfrom). |
| `.ForPath(d => d.A.B.C, opt => opt.MapFrom(s => ...))` | Same | See the scoping differences noted under [`ForPath`](#forpath--mapping-into-a-nested-destination-path) (always-fresh construction, no per-leaf `Ignore`/`Condition`). |
| `.ReverseMap()` | Same, but *validated* | AutoMapper accepts an irreversible reverse map silently; FluxMapper fails fast with `MAP0003` unless you add a compensating override. |
| `IValueResolver<TSource,TDestination,TMember>` | Same interface shape | `.ResolveUsing<TMember,TResolver>(d => d.X)`. |
| `ProjectTo<TDestination>()` | Same name, `SelectProject` alias available | Backed by a dedicated, narrower expression builder with pre-flight validation (`MAP4001`) instead of best-effort translation. |
| `Action<TSource,TDestination> AfterMap` with an `async` lambda | **Don't** — see `MapAsync` | AutoMapper silently drops the `Task` from an `async` `AfterMap` lambda; FluxMapper's `MapAsync` is the intentional, awaited replacement. |
| (no equivalent) | `[MapFrom]` + `FluxMapper.SourceGenerator` | Genuinely AOT-safe generated mapping for flat DTOs — AutoMapper has no compile-time-generated tier. |
| (no equivalent) | `Explain<TSource,TDestination>()` | Renders exactly what the resolved plan will do, member by member. |
| (no equivalent) | `AssertConfigurationIsValid()`'s ambiguity/nullability checks | Stricter than AutoMapper's own validation — a tied naming match or an unhandled nullable-to-non-nullable member is a hard error here, not a runtime surprise. |

## Packages

| Package | Depends on | Notes |
|---|---|---|
| `FluxMapper` | Everything below | Umbrella package, no code of its own — installs every FluxMapper feature in one step. |
| `FluxMapper.Abstractions` | BCL only | Contracts (`IMapper`, `IValueResolver<>`, `IProjectionValueResolver<>`, `[MapFrom]`). Fully AOT/trim compatible. |
| `FluxMapper.Core` | `FluxMapper.Abstractions` | Fluent configuration, compiled-expression execution tier, projection engine. |
| `FluxMapper.SourceGenerator` | Roslyn (build-time only) | `[MapFrom]` incremental generator. |
| `FluxMapper.Analyzers` | Roslyn (build-time only) | `[MapFrom]` diagnostics (`FLUX0001`/`FLUX0002`). |
| `FluxMapper.Extensions.DependencyInjection` | `FluxMapper.Core` | `AddFluxMapper` for `IServiceCollection`. |

`FluxMapper`, `FluxMapper.Abstractions`, `FluxMapper.Core`, and `FluxMapper.Extensions.DependencyInjection`
each multi-target `netstandard2.0` and `net10.0`, so referencing them does not require being on .NET 10 --
.NET Framework 4.6.1+, .NET Core 2.0+, and every currently supported .NET version can all consume the
netstandard2.0 build. `FluxMapper.SourceGenerator` and `FluxMapper.Analyzers` target `netstandard2.0` only,
which is normal for Roslyn components: they run inside whatever compiler/IDE host builds your project,
not inside your app's own runtime, so your app's target framework doesn't constrain them.

A handful of newer BCL types (`DateOnly`/`TimeOnly`) aren't classified as scalar-like by
`TypeClassification.IsSimple` when FluxMapper.Core itself is consumed via its netstandard2.0 build,
since those types don't exist in netstandard2.0's reference assemblies -- they fall through to ordinary
object mapping there instead. Consuming the net10.0 build (i.e. your own app also targets net10.0)
classifies them normally.

See `benchmarks/FluxMapper.Benchmarks` for a runnable comparison of both FluxMapper execution tiers
against hand-written mapping, AutoMapper, and Mapster (`dotnet run -c Release --project
benchmarks/FluxMapper.Benchmarks`). One measured run: on a flat DTO, FluxMapper's source-generated tier
(28.5 ns/op) is the outright winner, edging out even hand-written code (29.8 ns/op) and beating both
AutoMapper 14.0.0 (230.2 ns/op) and Mapster's default runtime mode (89.9 ns/op). On a
nested-object-plus-collection shape, `[MapFrom]`'s source generator now composes nested members and
`List<T>`/array collections too, and that tier is again the best FluxMapper result (240.1 ns/op) --
beating AutoMapper (280.6 ns/op) outright, though still behind Mapster's default mode (175.8 ns/op) by
around 27% on this specific shape. The compiled-expression tier, used when `[MapFrom]` doesn't apply, was
rewritten to build a direct loop instead of a LINQ pipeline and improved roughly 6.5x on this shape (from
an original 1954.4 ns/op down to 299.3 ns/op), now ahead of hand-written LINQ (334.1 ns/op). See
[`COMPETITIVE_GAP_ANALYSIS.md`](COMPETITIVE_GAP_ANALYSIS.md) for the full numbers and the one remaining,
honestly-tracked gap (Mapster's default mode on nested+collection shapes).

---

Found a gap in this document, or a real-world pattern it doesn't cover? Open an issue on
[GitHub](https://github.com/Osama94/FluxMapper) — this file is meant to genuinely describe everything
FluxMapper does, not just the highlights.
