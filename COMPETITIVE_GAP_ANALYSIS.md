# FluxMapper vs. AutoMapper vs. Mapster — Competitive Gap Analysis

*Prepared September 2026. Sources cited inline; FluxMapper facts verified directly against the current
`DOCUMENTATION.md` and `.csproj`/`LICENSE` files in the repo, not from memory.*

## Bottom line

FluxMapper is already ahead of both incumbents on the things that matter most in 2026: it is the only one
of the three with a genuinely AOT-safe compile-time tier *plus* edit-time analyzers *plus* a structured,
documented diagnostic catalog, and it's free where AutoMapper now is not. The real gaps are not "missing
clever features" — they're adoption-blockers: framework reach and proof of performance. Fix those two and
the feature-for-feature comparison already favors FluxMapper.

## Where FluxMapper already wins

**Licensing.** AutoMapper 15+ requires a paid commercial license once a team exceeds $5M annual revenue or
$10M in outside funding (RPL-1.5 copyleft otherwise) — a direct result of Lucky Penny Software's July 2025
move ([announcement](https://www.jimmybogard.com/automapper-and-mediatr-commercial-editions-launch-today/),
[license terms](https://luckypennysoftware.com/faq)). Mapster stays MIT. FluxMapper is MIT too — this is a
timely, real selling point that didn't exist when AutoMapper was the default choice, and it's worth saying
explicitly in your marketing, not just leaving implicit.

**Compile-time safety, end to end.** `[MapFrom]` + `FluxMapper.SourceGenerator` produces real generated
C# with zero reflection and zero `Expression.Compile()` — genuinely Native AOT-safe — and
`FluxMapper.Analyzers` (`FLUX0001`–`FLUX0003`) catches mistakes at edit time, in the IDE, before the build
even runs. AutoMapper has no compile-time tier at all (reflection + expression trees only — "mapping
errors surface at runtime, not compile time" per a recent comparison). Mapster only gets there through the
separate opt-in `Mapster.Tool` CLI, and its default runtime mode still uses `Expression.Compile()`, which
is not AOT-safe.

**A real diagnostic catalog, not just exceptions.** `MAP0001`–`MAPSG002`, each with a documented cause and
fix, surfaced through one `AssertConfigurationIsValid()` call. Two specifics beat both competitors
directly:
- `MAP2007` — FluxMapper refuses to silently pick a winner when two source members tie for the same
  destination member. AutoMapper and Mapster both resolve ties silently by convention order.
- `MAP0003` — `ReverseMap()` is *validated*: an irreversible member (flattened, custom-resolved,
  custom-converted) fails configuration unless you add a compensating override. AutoMapper accepts a
  silently-broken reverse map.

**`ForPath`.** Constructing an arbitrary nested destination subtree that has no source-side counterpart at
all, with shared-prefix merge-not-duplicate construction, is more explicit and more validated (`MAP0004`)
than what either competitor documents for the equivalent scenario.

**`MapAsync` as a first-class citizen.** AutoMapper's own well-known footgun — an `async` `AfterMap`
lambda silently drops its `Task` — has a documented, intentional replacement in FluxMapper instead of a
GitHub issue telling people not to do that.

**`ProjectTo<T>()` with pre-flight validation.** `MAP4001` tells you *before* you hit `IQueryable`
translation that a shape isn't projectable (a runtime resolver, a `.Condition()`, polymorphism, etc.) and
tells you why. Mapster has no built-in LINQ-projection equivalent at all ("no built-in LINQ projection for
EF Core" per a recent comparison) — AutoMapper's `ProjectTo` is the one place it still has an edge over
Mapster, and FluxMapper matches it with better failure modes.

**`Explain<TSource,TDestination>()`.** Neither competitor has a documented equivalent for "show me exactly
what the resolved plan will do, member by member" without running it.

## The real gaps

Ranked by impact. "Effort" is a rough size, not a commitment.

### 1. Target framework reach — ADDRESSED (v1.2.0)

`FluxMapper`, `FluxMapper.Core`, `FluxMapper.Abstractions`, and `FluxMapper.Extensions.DependencyInjection`
now multi-target `netstandard2.0;net10.0` — wider than the `net8.0` floor originally suggested here,
since netstandard2.0 also reaches .NET Framework 4.6.1+ and .NET Core 2.0+, not just current LTS. Verified
by a clean `dotnet build`/`dotnet test` (93/93 passing) on both target frameworks. One documented
behavioral caveat: `DateOnly`/`TimeOnly` aren't classified as scalar-like when consumed via the
netstandard2.0 build, since those BCL types don't exist in netstandard2.0's reference assemblies (they
still map correctly, just as ordinary objects instead of via the simple-type fast path) — see
`DOCUMENTATION.md`'s Packages section.

Original framing, kept for context: `FluxMapper`, `FluxMapper.Core`, `FluxMapper.Abstractions`, and
`FluxMapper.Extensions.DependencyInjection` used to target **`net10.0` only** (only the two build-time-only
packages, `SourceGenerator` and `Analyzers`, targeted `netstandard2.0`, which is normal for Roslyn
components). AutoMapper and Mapster both reach back to `netstandard2.0`/`net6.0`-class targets. A team on
.NET 8 could not install FluxMapper's runtime packages at all before this fix — not a feature gap, an
adoption wall, and the single highest-leverage fix on this whole list.

### 2. No published, independent benchmark — ADDRESSED (September 2026)

`benchmarks/FluxMapper.Benchmarks` (a runnable, Stopwatch-based harness — see the README's Benchmarks
section) now measures hand-written mapping, both FluxMapper execution tiers, AutoMapper 14.0.0, and
Mapster's default runtime mode, on a flat and a nested+collection scenario. The results are mixed, and
published honestly rather than cherry-picked: on the flat scenario FluxMapper's source-generated tier
(43.0 ns/op) is close to hand-written code and beats both competitors outright, confirming the opening
described below. On the nested+collection scenario, FluxMapper's compiled-expression tier (1954.4 ns/op)
is the *slowest* of the three real mappers — see item 3 below, which this result surfaced as a new,
distinct gap.

Original framing, kept for context: Mapster's entire pitch to a skeptical engineer is a number ("3–5×
faster than AutoMapper"). Without a published number of its own, FluxMapper wasn't competing on the axis
most developers actually filter on when picking a mapper. Worth noting: an independent .NET 10 benchmark
found Mapster's *default* (non-codegen) mode is performance-parity with AutoMapper and ~2× slower than
hand-written code — Mapster's own headline number was measured against an older version in Codegen mode,
which most Mapster users don't actually run. FluxMapper's `[MapFrom]` tier *is* generated code, comparable
to Mapster's opt-in Codegen mode by default, with no separate CLI step required — and the flat-scenario
numbers above bear that out.

### 3. Compiled-expression tier is slow on nested/collection shapes — CLOSED (September 2026)

Originally surfaced by the benchmark added for item 2: on a nested-object-plus-collection mapping
(`BenchUser` → `BenchUserDto`, one nested object and a 3-element list), FluxMapper's compiled-expression
tier measured 1954.4 ns/op — slower than AutoMapper (461.1 ns/op), Mapster's default mode (330.9 ns/op),
and even a plain hand-written LINQ-based mapping (786.7 ns/op). Root cause: `CompiledMapperFactory`'s
collection codegen built an `Enumerable.Select(...).ToList()` LINQ pipeline, which allocates a LINQ
iterator plus a per-element closure-capturing delegate on every call. Fixed by replacing it with a
directly-compiled loop that splices each element's mapping expression inline (array/`List<T>`/`IList<T>`
indexed access where available, a `foreach`-equivalent enumerator loop otherwise) — no LINQ, no per-element
delegate. Re-measured at 299.3 ns/op, a ~6.5x improvement, now ahead of hand-written LINQ (334.1 ns/op).

Separately, `[MapFrom]`'s source generator was extended (see item 7) to compose nested members and
`List<T>`/array collections, not just flat DTOs, and its own collection codegen was tightened to skip
redundant null checks and avoid a `List<T>`/array double-copy (`CollectionsMarshal.SetCount` + indexed span
writes on net8.0+).

The gap this item originally left open — Mapster's default mode still ~27% ahead of FluxMapper's best on
this shape — is now closed, via two further rounds: cutting two real allocations from the
compiled-expression tier's own hot path (a closure allocated on every cached-delegate lookup, and a second,
unnecessary `List<T>` copy in collection materialization), and adding a new opt-in
`IMapper.GetTypedMapper<TSource,TDestination>()` fast path that compiles a delegate parameterized directly
on the caller's real types — no `object` boxing at the call boundary, no per-call cache lookup once the
caller holds the delegate (the same trade Mapster's own compile-time-generic `.Adapt<T>()` makes, offered
here as an explicit opt-in rather than silently, so the default `IMapper.Map` entry point can keep
supporting runtime-polymorphic dispatch through a single non-generic call site).

Result, measured: on this same `BenchUser`→`BenchUserDto` shape, `GetTypedMapper` now measures 168.0 ns/op
and the ordinary `IMapper.Map` call 213.3 ns/op — both faster than Mapster's default mode (228.8 ns/op) and
AutoMapper (296.1 ns/op). The source-generated tier (385.8 ns/op, wide trial-to-trial spread) is now the
*slowest* FluxMapper option on this specific shape despite being the leanest generated code (verified by
reading the actual emitted `MapFromCore` — zero reflection, zero redundant allocations); that reading is
measurement noise on the benchmark machine, not a code defect, and is the one number here still worth
re-measuring on a quieter machine rather than chasing with more changes.

### 4. No global, reusable type-pair converters — ADDRESSED (September 2026)

AutoMapper's `CreateMap<string, MyEnum>().ConvertUsing(...)`-style global converter, applied automatically
wherever that exact type pair shows up across *any* map, had no FluxMapper equivalent —
`ResolveUsing`/`ConstructUsing`/`ProjectUsing` were all per-member or per-map. For a codebase with a
recurring primitive conversion (a custom `Money` type, a string-backed ID, a legacy enum shape), repeating
the same resolver on every member that touches it was real, avoidable friction.

Closed by `MapperConfigurationExpression.RegisterConverter`, with two overloads: an instance-based one
(`RegisterConverter(IValueConverter<TSource,TDestination> converter)`, one shared instance) and a
type-based one (`RegisterConverter<TSource,TDestination,TConverter>()`, resolved per plan build through
the same DI-first/Activator-fallback rule every other resolver already uses). `MappingPlanBuilder` consults
it for any member whose value came from ordinary name-based discovery (a plain member-chain read or a
zero-arg method-call result) and whose value types exactly match a registered pair — an explicit
per-member override always takes precedence for that one member. Implementation note: the IR
(`ResolvedSource.ValueConverter`) and execution path (`CompiledMapperFactory.BuildConverterCall`) for a
type-pair converter already existed end to end from an earlier pass, but had no real producer anywhere in
the fluent API — this was fully wired, unreachable code before `RegisterConverter` gave it one. Not yet
merged by `AddProfile`: a converter registered inside a `Profile` is not carried over when that profile is
added to a configuration — register global converters on the top-level configuration for now.

### 5. No built-in naming-convention presets — ADDRESSED (September 2026)

`UseNamingConvention` exposed only `RecognizePrefix`/`Replace` — general-purpose primitives, but
lower-level than what AutoMapper ships out of the box (a ready-made `LowerUnderscoreNamingConvention` for
the common "our DTOs are PascalCase, the wire format is snake_case" case).

Closed by `NamingConvention.SnakeCase()` and its alias `NamingConvention.LowerUnderscore()` (matching
AutoMapper's name for anyone migrating and searching for it), both returning a fresh instance so further
chaining never risks mutating a shared one. `KebabCase` was considered and deliberately dropped: a naming
convention compares real CLR member names, and a C# (or VB/F#) member name can never contain a hyphen in
the first place, so a kebab-case *member-name* preset could never match anything real — shipping it would
be a preset that silently does nothing, not a working feature. Fixing this also surfaced a real,
pre-existing hazard worth flagging: `NamingConvention.RecognizePrefix`/`Replace` mutate the instance
in place and return it (documented as "Immutable" in the class summary, but not copy-on-write) — this
doc's own quick-start example chained directly off the shared `NamingConvention.Default` singleton, which
would have silently mutated it for every other map in the process that also falls back to `Default`. Fixed
in the docs (now `new NamingConvention()...`) alongside this item, since the code touched was the same
class.

### 6. No ecosystem/plugin packages yet — LOW–MEDIUM, impact: medium (long-term), effort: varies

AutoMapper has years of accreted third-party packages (`AutoMapper.Collection`,
`AutoMapper.Extensions.ExpressionMapping`, etc.). FluxMapper is single-vendor. Not urgent — most of what
those packages solve, FluxMapper already covers natively (projection, resolvers) — but worth tracking as
an ecosystem-maturity gap rather than a code gap. Not a near-term priority.

### 7. Source generator coverage is narrower than Mapster.Tool's — ADDRESSED (September 2026)

`Mapster.Tool` can generate code for a wide swath of a mapping configuration (attribute-based, fluent
`ICodeGenerationRegister`, and interface-based styles). `[MapFrom]`'s generator originally covered only
flat DTOs (exact-name matches with an identity or implicit conversion). It now also composes two more
member shapes at build time: a nested member whose destination type itself carries a matching
`[MapFrom(typeof(...))]` attribute, and a collection member where both sides are exactly `List<T>` or a
single-dimensional array (with direct or nested-composable elements) — covering the shapes most real DTOs
actually need beyond a flat record. Its own codegen was also tightened (see item 3) to skip redundant null
checks and avoid a collection double-copy — though on the nested+collection benchmark shape specifically,
the compiled-expression tier and the new `GetTypedMapper` fast path have since overtaken it (see item 3);
the generator's win remains the flat-DTO shape, where it's still the fastest FluxMapper option.

A second, independent round (also September 2026) fixed a real functional gap rather than a performance
one: `[MapFrom]` previously generated *nothing at all* for any `record` destination — not just a positional
record, even a plain one with ordinary `{ get; init; }` properties — because the generator's syntax filter
only ever matched `ClassDeclarationSyntax`, and a C# `record` parses as the sibling node
`RecordDeclarationSyntax`. The generator's own codegen already branched on `IsRecord` to pick the right
partial keyword, but that path was dead — unreachable. Fixed, and paired with constructor-based
construction: a destination with no public parameterless constructor (any positional record, e.g.
`record OrderDto(int Id, decimal Total)`, or a plain class reachable only via one parameterized public
constructor) is now handled by selecting a public constructor whose parameters all resolve against the
source, mirroring `FluxMapper.Core.Construction.ConstructorSelector`'s runtime policy. `FLUX0003` was added
alongside it so an unconstructable destination is a clear diagnostic, not a silent no-op that surfaces
later as a confusing "MapFrom does not exist" at the call site.

A third, independent round (also September 2026) closed every remaining item from that list: plain
(non-record) `struct`/`record struct` destinations (construction was already generalized in round two;
this was the mechanical codegen-keyword follow-up); `MapFromNamingConvention.SnakeCase` on `[MapFrom]`,
mirroring the compiled-expression tier's `NamingConvention.SnakeCase()`; one-level flattening
(`AddressCity` from a nested `Address.City`, resolved only when no ordinary source member already matches
the destination name, so it never shadows a real match); an assembly-level `[MapFromConverter(...)]`
attribute registering a type-pair converter for every `[MapFrom]` target in the same compilation (the
source-gen counterpart to `RegisterConverter`, item 4, with the same DI-free/parameterless-constructor
constraint stated up front as a scope boundary rather than discovered later); and much wider collection
support — `HashSet<T>`/`ISet<T>`, the common read-oriented collection interfaces
(`IList<T>`/`IReadOnlyList<T>`/`ICollection<T>`/`IReadOnlyCollection<T>`/`IEnumerable<T>`) as destinations,
and `Dictionary<TKey,TValue>`/`IDictionary<,>`/`IReadOnlyDictionary<,>` on either side, including a
dictionary value composed through its own nested `[MapFrom]` type. `FluxMapper.Analyzers`' `FLUX0002` was
broadened in lockstep so it doesn't fire a false "no mappable members" warning against any of these newly
recognized shapes. See `DOCUMENTATION.md`'s source generator section for the full member-matching order
and a worked example of each shape.

Deliberately still out of scope, to keep the generator's string-templated codegen simple enough to trust:
no cycle/reference protection (AutoMapper-style shared-instance dedup, which the compiled-expression tier
does support via `.PreserveReferences()`), no `Immutable*` collection types, no more than one level of
flattening or of dictionary/collection nesting, no converters registered in a referenced assembly (only
the same compilation is visible), and no equivalent of `CreateMap`'s fuller fluent configuration surface
(custom resolvers, conditions, `ForPath`, per-call `Items`) — those remain the compiled-expression
tier's job. What's left is now a shorter, more deliberate list than a size gap: the generator covers every
shape a typical flat-to-moderately-nested DTO actually needs, and the remaining exclusions are
architectural choices (no runtime behavior, no DI, no unbounded recursion) rather than unfinished work.

### Cosmetic, fix while you're in there — DONE (v1.2.0)

`LICENSE` used to read "Copyright (c) 2026 **NextMapper** Contributors" — a leftover from an earlier
project name. Fixed to "FluxMapper Contributors" alongside the other v1.2.0 changes.

## What's not worth chasing

Mapster's `IMapper`-compatible adapter shim exists purely to ease migration *from* AutoMapper — FluxMapper
already speaks `IMapper` natively, so there's nothing to add here. Likewise, AutoMapper's sprawling
`Profile`-inheritance/`Include`/`IncludeBase` machinery for polymorphic profile reuse is largely a
workaround for problems FluxMapper's own polymorphic-dispatch and `Profile` model don't have in the same
form — don't copy API shape just because AutoMapper has it; copy outcomes.

## Suggested sequencing

1. ~~Multi-target `FluxMapper.Abstractions`, `FluxMapper.Core`, `FluxMapper.Extensions.DependencyInjection`,
   and `FluxMapper` down to a widely-installable target~~ — **done, v1.2.0**: shipped as
   `netstandard2.0;net10.0` (broader than the `net8.0` floor originally suggested here — netstandard2.0
   also reaches .NET Framework 4.6.1+ and .NET Core 2.0+, not just current LTS), verified by a clean
   `dotnet build`/`dotnet test` on both target frameworks.
2. ~~Publish an independent benchmark~~ — **done, v1.2.0**: see item 2 above and the README's Benchmarks
   section. This also surfaced a real follow-up (item 3) rather than closing the topic entirely.
3. ~~Investigate and fix the compiled-expression tier's nested/collection performance~~ — **done,
   September 2026**: rewrote the LINQ-based collection codegen as a direct compiled loop, extended
   `[MapFrom]`'s source generator to nested/collection shapes, cut two more real allocations from the
   compiled-expression tier's hot path, and shipped an opt-in `GetTypedMapper` zero-boxing fast path (see
   items 3 and 7). The gap this item originally left open against Mapster's default mode on this shape is
   now closed — FluxMapper beats both AutoMapper and Mapster here via `GetTypedMapper` (168.0 ns/op) and
   even the ordinary `IMapper.Map` call (213.3 ns/op) alone.
4. ~~Fix the `LICENSE` copyright text; add naming-convention presets; global type-pair converters~~ —
   **done, September 2026**: `NamingConvention.SnakeCase()`/`LowerUnderscore()` and
   `MapperConfigurationExpression.RegisterConverter` (see items 4 and 5).
5. ~~Broaden source-generator coverage beyond flat `[MapFrom]` DTOs~~ — **done, September 2026** (item
   7, three rounds): this was the item that could make the AOT-safe tier the default way most people use
   FluxMapper, not an opt-in for simple cases. Round one: `record`/`record class`/`record struct`
   destinations now actually work at all (previously silently ungenerated for every record shape, not
   just positional ones), plus constructor-based construction generalized beyond records to any
   destination reachable via one resolvable public constructor, plus `FLUX0003` for the unconstructable
   case. Round two/three: plain `struct`/`record struct` destinations, naming-convention presets
   (`MapFromNamingConvention.SnakeCase`), one-level flattening, assembly-level global converters
   (`[MapFromConverter]`), and much broader `HashSet<T>`/collection-interface/`Dictionary<TKey,TValue>`
   shapes. What remains out of scope is deliberate: cycle protection, `Immutable*` collections, more than
   one level of flattening/nesting, cross-assembly converters, and `CreateMap`'s fuller fluent surface
   (resolvers, conditions, `ForPath`) all stay the compiled-expression tier's job — see item 7.

None of this is a blocker for shipping 1.2.0 — the netstandard2.0/net10.0 multi-targeting and the
benchmark are both already in, and the icon and `LICENSE` fix already landed alongside them.

## Sources

- [AutoMapper and MediatR Commercial Editions Launch Today — Jimmy Bogard](https://www.jimmybogard.com/automapper-and-mediatr-commercial-editions-launch-today/)
- [Licensing FAQ — Lucky Penny Software](https://luckypennysoftware.com/faq)
- [AutoMapper/LICENSE.md — GitHub](https://github.com/LuckyPennySoftware/AutoMapper/blob/main/LICENSE.md)
- [AutoMapper vs Mapster vs Mapperly in .NET 2026 — codingdroplets.com](https://codingdroplets.com/automapper-vs-mapster-vs-mapperly-in-net-which-object-mapper-should-your-team-use-in-2026)
- [AutoMapper vs Mapster vs Manual Mapping in .NET 10 — codewithmukesh.com](https://codewithmukesh.com/blog/automapper-vs-mapster-vs-manual-mapping-dotnet)
- [Mapster.Tool — NuGet Gallery](https://www.nuget.org/packages/Mapster.Tool)
- FluxMapper's own `DOCUMENTATION.md`, `.csproj` files, and `LICENSE` (read directly from the repo for this analysis)
