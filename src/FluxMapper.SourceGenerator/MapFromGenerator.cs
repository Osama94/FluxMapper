using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FluxMapper.SourceGenerator;

/// <summary>
/// An <see cref="IIncrementalGenerator"/>
/// that turns <c>[MapFrom(typeof(TSource))] public partial class/record/struct TDestination</c> into a
/// real, zero-reflection, zero-<c>Expression.Compile()</c> static factory method on that same partial
/// type -- the one execution tier in this project that can honestly report
/// <c>ExecutionEligibility.AotSafe = true</c>, because everything it produces is ordinary compiled C# the
/// destination assembly ships with, not code assembled at runtime.
///
/// Scope, stated plainly rather than silently: this generator does its OWN member matching, which still
/// does not share <see cref="Building.MappingPlanBuilder"/>'s pipeline (nullability policy, `ForPath`,
/// per-member resolvers/conditions, `ReverseMap`, reference-preservation for cycles, ...). It now covers
/// seven member shapes:
/// <list type="bullet">
/// <item>Converter: a registered <c>[assembly: MapFromConverter(typeof(TSource), typeof(TDest),
/// typeof(TConverter))]</c> pair takes precedence over any other rule below, mirroring how a registered
/// global converter always wins on the compiled-expression tier too. <c>TConverter</c> is constructed via
/// <c>new TConverter()</c> at the call site (it must have an accessible public parameterless
/// constructor -- there is no DI container at compile time) and only a converter registered in the SAME
/// compilation is visible.</item>
/// <item>Direct: exact name match (or a naming-convention-relaxed match, see below), identity or implicit
/// conversion -- the original, flat-DTO-only scope.</item>
/// <item>Nested: a same-named destination member whose type is itself decorated with
/// <c>[MapFrom(typeof(TSourceMemberType))]</c> composes to a call to that type's own generated
/// <c>MapFromCore(...)</c> -- with a null check spliced in first when the source member's type is a
/// reference type, since the generated destination call otherwise can't express "map only if present."</item>
/// <item>Dictionary: both sides resolve to <c>Dictionary&lt;TKey,TValue&gt;</c> or a same-shaped
/// <c>IDictionary&lt;,&gt;</c>/<c>IReadOnlyDictionary&lt;,&gt;</c>. The key must be directly/implicitly
/// convertible (no nested/collection key composition); the value may be Direct or Nested (not itself a
/// further collection or dictionary -- kept to one level to bound the recursion this round adds).</item>
/// <item>Collection: covers `T[]`, `List&lt;T&gt;`, `HashSet&lt;T&gt;`, and the common same-shaped
/// interfaces on both sides (`IList&lt;T&gt;`/`IReadOnlyList&lt;T&gt;` behave like `List&lt;T&gt;` for
/// reading; `IEnumerable&lt;T&gt;`/`ICollection&lt;T&gt;`/`IReadOnlyCollection&lt;T&gt;`/`IList&lt;T&gt;`/
/// `IReadOnlyList&lt;T&gt;` as a destination all materialize a `List&lt;T&gt;`; `ISet&lt;T&gt;` as a
/// destination materializes a `HashSet&lt;T&gt;`). A source with `Count` but no indexer
/// (`HashSet&lt;T&gt;`/`ICollection&lt;T&gt;`/`IReadOnlyCollection&lt;T&gt;`) is read via `foreach`
/// instead of an indexed loop; a plain `IEnumerable&lt;T&gt;` source (no guaranteed `Count`) is
/// deliberately still out of scope, since pre-sizing the destination is central to how this generator
/// avoids the allocation patterns described below. Elements compose the same way a plain member does
/// (Direct or Nested -- not a converter or a further nested collection).</item>
/// <item>Flattened: one level only. A destination member with no ordinary source match at all (not even
/// under a relaxed naming convention) is decomposed into [source member name][remaining name] --
/// e.g. destination <c>AddressCity</c> against a source with an <c>Address</c> property whose own type has
/// a <c>City</c> property -- resolved as a Direct leaf against that nested type's own member (itself
/// naming-convention-aware). The source-side prefix segment itself is still matched by an exact,
/// case-sensitive prefix of the destination name (not naming-convention-relaxed) to keep the search space
/// bounded; a two-level flatten (`Customer.Address.City` into `CustomerAddressCity`) is out of scope. An
/// ambiguous split -- more than one source member works as a prefix -- is treated as no match, never a
/// guess.</item>
/// </list>
///
/// <b>Naming conventions.</b> <c>[MapFrom(typeof(Source), NamingConvention =
/// MapFromNamingConvention.SnakeCase)]</c> loosens every ordinary (non-flattened) member match from an
/// exact name to also accept a case-insensitive, underscore-insensitive one -- the source-generator
/// counterpart to the compiled-expression tier's <c>NamingConvention.SnakeCase()</c>/
/// <c>LowerUnderscore()</c> presets. An exact match always wins first; a relaxed match that would tie
/// between two or more source members is treated as unmatched rather than guessed.
///
/// <b>Destination construction.</b> A destination reached via a public parameterless constructor (an
/// ordinary class, a value-type destination -- <c>struct</c>, <c>record struct</c> -- which always gets
/// one from the C# language itself regardless of what else is declared, or a <c>record</c>/<c>record
/// class</c> declared with plain <c>{ get; init; }</c> properties and no positional parameter list) is
/// populated with <c>new Dest { Member = value, ... }</c>, same as always.
///
/// A <i>reference-type</i> destination with no public parameterless constructor -- most notably a
/// positional <c>record</c>/<c>record class</c> (<c>record OrderDto(int Id, decimal Total)</c>), but this
/// is deliberately not special-cased to records only -- goes through constructor-based construction
/// instead, mirroring <see cref="Construction.ConstructorSelector"/>'s runtime policy exactly: the public
/// constructor with the most parameters that ALL resolve against the source wins, tried in descending
/// parameter-count order (an unresolvable parameter with an explicit default value is simply omitted from
/// the call, so an optional trailing parameter doesn't block an otherwise-usable constructor). A parameter
/// resolves via the exact same seven-shape rule a plain member does. Constructor arguments are emitted as
/// named arguments (<c>Dest(Id: ..., Total: ...)</c>) rather than positional, specifically so omitting a
/// defaulted-but-unresolvable parameter doesn't depend on argument order. Any destination member NOT
/// consumed by the selected constructor still gets the ordinary object-initializer treatment afterward. If
/// no constructor can be found, generation for that destination is silently skipped (the same
/// graceful-degradation this generator has always used for any other unmatched shape), and
/// <c>FluxMapper.Analyzers.MapFromAnalyzer</c>'s FLUX0003 is what surfaces that as an actual diagnostic
/// instead of a mysterious "MapFrom does not exist" at the call site.
///
/// Before the record support above, a <c>record</c>-declared destination could not be generated for AT
/// ALL, because <c>Initialize</c>'s syntax predicate only ever matched <c>ClassDeclarationSyntax</c>, and
/// a C# <c>record</c>/<c>struct</c> declaration parses as the sibling nodes <c>RecordDeclarationSyntax</c>/
/// <c>StructDeclarationSyntax</c>, never as <c>ClassDeclarationSyntax</c>.
///
/// Deliberately still out of scope, to keep this generator's string-templated codegen simple enough to
/// trust: no cycle/reference protection (AutoMapper-style shared-instance dedup, which the
/// compiled-expression tier does support), <c>Immutable*</c> collection targets, a plain non-countable
/// <c>IEnumerable&lt;T&gt;</c> source, more than one level of flattening or dictionary/collection nesting,
/// a converter registered in a referenced assembly rather than the current compilation, and no equivalent
/// of `CreateMap`'s fuller fluent configuration surface (conditions, `ForPath`, per-member resolvers,
/// etc.) -- those remain the compiled-expression tier's job.
///
/// Every generated destination type gets two static methods, not one: <c>MapFrom</c> (the public entry
/// point, argument-null-checked) and <c>MapFromCore</c> (the same mapping, without that check). Nested and
/// collection-element composition calls <c>MapFromCore</c> on the target type directly rather than
/// <c>MapFrom</c>, to avoid re-checking a value this method has already established is non-null (for a
/// nested member, checked immediately above via an explicit null-conditional; for a collection element,
/// not separately checked -- see the perf/correctness tradeoff called out below). This is a deliberate
/// performance choice: it removes a redundant argument-null branch from the hottest path this generator
/// produces. The one behavioral consequence, stated plainly: a <c>null</c> element inside a mapped
/// <c>List&lt;T&gt;</c>/array/<c>HashSet&lt;T&gt;</c> now surfaces as a
/// <see cref="NullReferenceException"/> from inside <c>MapFromCore</c> rather than a clean
/// <see cref="ArgumentNullException"/> from <c>MapFrom</c> -- the same failure mode C#'s own
/// null-forgiving patterns produce when an assumed-non-null value turns out to be null. Calling
/// <c>MapFromCore</c> directly (rather than through the generator's own composition) carries that same
/// tradeoff; prefer the public <c>MapFrom</c> at any call site that hasn't already established non-null.
///
/// Collection codegen also avoids two allocation patterns a naive implementation would otherwise pay for:
/// building into a <c>List&lt;T&gt;</c> via repeated <c>Add</c> calls when the final shape is an array
/// (which used to mean a full second copy via <c>ToArray()</c>), and growing a <c>List&lt;T&gt;</c>
/// incrementally when its final length is already known up front. An array destination is allocated at its
/// exact final length and written by index directly. A <c>List&lt;T&gt;</c> destination is pre-sized via
/// its capacity constructor and, when the target framework exposes
/// <c>System.Runtime.InteropServices.CollectionsMarshal.SetCount</c> (.NET 8+), its backing storage is
/// exposed as a <see cref="Span{T}"/> and written by index too. Older target frameworks (netstandard2.0)
/// fall back to an indexed/`Add` loop, which is still one allocation-free pass over a pre-sized list.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class MapFromGenerator : IIncrementalGenerator
{
    private const string MapFromAttributeFullName = "FluxMapper.Abstractions.MapFromAttribute";
    private const string MapFromConverterAttributeFullName = "FluxMapper.Abstractions.MapFromConverterAttribute";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var targets = context.SyntaxProvider.ForAttributeWithMetadataName(
            MapFromAttributeFullName,
            predicate: static (node, _) => node is TypeDeclarationSyntax typeDecl
                && typeDecl is ClassDeclarationSyntax or RecordDeclarationSyntax or StructDeclarationSyntax
                && typeDecl.Modifiers.Any(SyntaxKind.PartialKeyword),
            transform: static (ctx, _) => Analyze(ctx));

        context.RegisterSourceOutput(targets.Where(static m => m is not null), static (spc, model) => Execute(spc, model!));
    }

    private static MapFromModel? Analyze(GeneratorAttributeSyntaxContext ctx)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol destinationSymbol) return null;
        if (destinationSymbol.ContainingType is not null) return null; // scope: top-level (namespace-nested) types only, see type doc comment.

        var attribute = ctx.Attributes.FirstOrDefault();
        if (attribute is null || attribute.ConstructorArguments.Length != 1) return null;
        if (attribute.ConstructorArguments[0].Value is not INamedTypeSymbol sourceSymbol) return null;

        var compilation = ctx.SemanticModel.Compilation;

        // Detected once per destination type against the CONSUMER's own compilation (not this generator's
        // own TFM) -- a consumer targeting net8.0+ sees this as true and gets the Span-based fast path for
        // List<T> destinations; a netstandard2.0 consumer sees false and gets the indexed-Add fallback.
        var collectionsMarshal = compilation.GetTypeByMetadataName("System.Runtime.InteropServices.CollectionsMarshal");
        var hasSetCount = collectionsMarshal is not null && collectionsMarshal.GetMembers("SetCount").Length > 0;

        // MapFromNamingConvention.SnakeCase = 1 (see FluxMapper.Abstractions.Attributes.cs) -- read as a
        // boxed int rather than referencing the actual enum type, consistent with how this generator
        // already avoids taking a compile-time dependency on Abstractions' types (MapFromAttributeFullName
        // is matched by metadata name string, not typeof(...), so it works against whatever Abstractions
        // build the CONSUMER references).
        var namingArg = attribute.NamedArguments.FirstOrDefault(kv => kv.Key == "NamingConvention");
        var useSnakeCase = namingArg.Value.Value is int namingValue && namingValue == 1;

        var converters = CollectConverters(compilation);

        var destinationProps = destinationSymbol.GetMembers().OfType<IPropertySymbol>()
            .Where(p => !p.IsStatic && !p.IsIndexer && p.SetMethod is { DeclaredAccessibility: Accessibility.Public })
            .ToList();
        var sourceProps = sourceSymbol.GetMembers().OfType<IPropertySymbol>()
            .Where(p => !p.IsStatic && !p.IsIndexer && p.GetMethod is { DeclaredAccessibility: Accessibility.Public })
            .ToDictionary(p => p.Name, p => p);

        var members = new List<MapFromMember>();
        var consumedNames = new HashSet<string>();

        // Construction strategy -- see the type doc comment. A value-type destination always gets an
        // implicit parameterless constructor from the C# language itself (new T() is legal for any
        // struct regardless of what else is declared), so only a reference-type destination with no
        // public parameterless constructor of its own needs the constructor-selection path below.
        var publicCtors = destinationSymbol.Constructors.Where(c => c.DeclaredAccessibility == Accessibility.Public).ToImmutableArray();
        var hasPublicParameterless = publicCtors.Any(c => c.Parameters.Length == 0);

        if (!destinationSymbol.IsValueType && !hasPublicParameterless)
        {
            var candidateCtors = publicCtors
                .Where(c => c.Parameters.Length > 0)
                // A record's compiler-synthesized copy constructor Foo(Foo other) is never public (it's
                // protected, or private on a sealed record), so this exclusion is defense-in-depth rather
                // than load-bearing -- kept so a hand-written public "copy-shaped" constructor is never
                // mistaken for the one that should be fed source-type values.
                .Where(c => !(c.Parameters.Length == 1 && SymbolEqualityComparer.Default.Equals(c.Parameters[0].Type, destinationSymbol)))
                .OrderByDescending(c => c.Parameters.Length);

            List<MapFromMember>? selectedBindings = null;

            foreach (var ctor in candidateCtors)
            {
                var bindings = new List<MapFromMember>();
                var resolvedAll = true;

                foreach (var parameter in ctor.Parameters)
                {
                    var member = ResolveMember(compilation, sourceProps, converters, parameter.Name, parameter.Type, useSnakeCase, isConstructorArgument: true);

                    if (member is not null)
                    {
                        bindings.Add(member);
                        continue;
                    }

                    if (parameter.HasExplicitDefaultValue) continue; // omitted from the call -- the constructor's own default applies.

                    resolvedAll = false;
                    break;
                }

                if (resolvedAll)
                {
                    selectedBindings = bindings;
                    break;
                }
            }

            if (selectedBindings is null) return null; // no usable public constructor -- see FLUX0003.

            members.AddRange(selectedBindings);
            foreach (var binding in selectedBindings) consumedNames.Add(binding.Name);
        }

        foreach (var destProp in destinationProps)
        {
            if (consumedNames.Contains(destProp.Name)) continue;

            var member = ResolveMember(compilation, sourceProps, converters, destProp.Name, destProp.Type, useSnakeCase, isConstructorArgument: false);
            if (member is not null) members.Add(member);
        }

        var namespaceName = destinationSymbol.ContainingNamespace.IsGlobalNamespace ? null : destinationSymbol.ContainingNamespace.ToDisplayString();

        return new MapFromModel(
            DestinationName: destinationSymbol.Name,
            DestinationFullName: destinationSymbol.ToDisplayString(),
            SourceFullName: sourceSymbol.ToDisplayString(),
            Namespace: namespaceName,
            IsRecord: destinationSymbol.IsRecord,
            IsValueType: destinationSymbol.IsValueType,
            HasCollectionsMarshalSetCount: hasSetCount,
            Members: ImmutableArray.CreateRange(members));
    }

    /// <summary>
    /// One name resolves to at most one member: try an ordinary (possibly naming-convention-relaxed)
    /// source member first via <see cref="TryClassifyMember"/>'s Converter/Direct/Nested/Dictionary/
    /// Collection rules, then fall back to one-level flattening (see
    /// <see cref="TryClassifyFlattenedMember"/>) only when no ordinary source member exists under that
    /// name at all. Shared between destination properties and constructor parameters, since a constructor
    /// parameter composes exactly the same way a settable member does.
    /// </summary>
    private static MapFromMember? ResolveMember(
        Compilation compilation,
        Dictionary<string, IPropertySymbol> sourceProps,
        ImmutableArray<(ITypeSymbol Source, ITypeSymbol Destination, INamedTypeSymbol Converter)> converters,
        string name, ITypeSymbol destType, bool useSnakeCase, bool isConstructorArgument)
    {
        if (TryResolveSourceMember(sourceProps, name, useSnakeCase, out var sourceProp))
        {
            var member = TryClassifyMember(compilation, converters, name, destType, sourceProp.Type, sourceProp.Name, isConstructorArgument);
            if (member is not null) return member;
        }

        return TryClassifyFlattenedMember(compilation, sourceProps, name, destType, useSnakeCase, isConstructorArgument);
    }

    /// <summary>
    /// Classifies one (name, destination type, source type) triple, given the ACTUAL source member name
    /// (<paramref name="sourceName"/>) it resolved against -- which can differ from <paramref name="name"/>
    /// under a naming convention (destination <c>UserName</c> against source <c>user_name</c>), so the
    /// generated code must read <c>source.{sourceName}</c>, never <c>source.{name}</c> (the previous,
    /// naming-convention-less version of this generator could assume the two were always identical; that
    /// assumption no longer holds).
    /// </summary>
    private static MapFromMember? TryClassifyMember(
        Compilation compilation,
        ImmutableArray<(ITypeSymbol Source, ITypeSymbol Destination, INamedTypeSymbol Converter)> converters,
        string name, ITypeSymbol destType, ITypeSymbol sourceType, string sourceName, bool isConstructorArgument)
    {
        // A registered global converter takes precedence over a coincidental identity/implicit
        // conversion between the same two types -- mirrors MappingPlanBuilder's runtime behavior, which
        // substitutes a registered converter unconditionally once an ordinary member source resolves to
        // that exact type pair, not only when no built-in conversion would otherwise apply.
        if (TryFindConverter(converters, sourceType, destType, out var converterType))
        {
            return new MapFromMember(
                name, MemberKind.Converter,
                NeedsCast: false, DestinationTypeDisplay: destType.ToDisplayString(),
                SourceIsValueType: false, ElementDestinationTypeDisplay: null,
                ElementNeedsMapFrom: false, ElementNeedsCast: false, SourceIsArray: false,
                IsConstructorArgument: isConstructorArgument, SourceName: sourceName,
                ConverterTypeDisplay: converterType.ToDisplayString());
        }

        var conversion = compilation.ClassifyConversion(sourceType, destType);
        if (conversion.Exists && (SymbolEqualityComparer.Default.Equals(sourceType, destType) || conversion.IsImplicit))
        {
            return new MapFromMember(
                name, MemberKind.Direct,
                NeedsCast: !SymbolEqualityComparer.Default.Equals(sourceType, destType),
                DestinationTypeDisplay: destType.ToDisplayString(),
                SourceIsValueType: false, ElementDestinationTypeDisplay: null,
                ElementNeedsMapFrom: false, ElementNeedsCast: false, SourceIsArray: false,
                IsConstructorArgument: isConstructorArgument, SourceName: sourceName);
        }

        if (destType is INamedTypeSymbol destNamed && HasMapFromFor(destNamed, sourceType))
        {
            return new MapFromMember(
                name, MemberKind.Nested,
                NeedsCast: false,
                DestinationTypeDisplay: destType.ToDisplayString(),
                SourceIsValueType: sourceType.IsValueType, ElementDestinationTypeDisplay: null,
                ElementNeedsMapFrom: false, ElementNeedsCast: false, SourceIsArray: false,
                IsConstructorArgument: isConstructorArgument, SourceName: sourceName);
        }

        if (TryGetDictionaryTypes(sourceType, out var sourceKeyType, out var sourceValueType)
            && TryGetDictionaryTypes(destType, out var destKeyType, out var destValueType))
        {
            var keyConversion = compilation.ClassifyConversion(sourceKeyType, destKeyType);
            var keyDirect = keyConversion.Exists && (SymbolEqualityComparer.Default.Equals(sourceKeyType, destKeyType) || keyConversion.IsImplicit);

            if (keyDirect)
            {
                var valueConversion = compilation.ClassifyConversion(sourceValueType, destValueType);
                var valueDirect = valueConversion.Exists
                    && (SymbolEqualityComparer.Default.Equals(sourceValueType, destValueType) || valueConversion.IsImplicit);
                var valueNested = !valueDirect && destValueType is INamedTypeSymbol destValueNamed && HasMapFromFor(destValueNamed, sourceValueType);

                if (valueDirect || valueNested)
                {
                    return new MapFromMember(
                        name, MemberKind.Dictionary,
                        NeedsCast: false, DestinationTypeDisplay: destType.ToDisplayString(),
                        SourceIsValueType: false, ElementDestinationTypeDisplay: null,
                        ElementNeedsMapFrom: false, ElementNeedsCast: false, SourceIsArray: false,
                        IsConstructorArgument: isConstructorArgument, SourceName: sourceName,
                        DictionaryKeyTypeDisplay: destKeyType.ToDisplayString(),
                        DictionaryValueTypeDisplay: destValueType.ToDisplayString(),
                        DictionaryKeyNeedsCast: !SymbolEqualityComparer.Default.Equals(sourceKeyType, destKeyType),
                        DictionaryValueNeedsCast: valueDirect && !SymbolEqualityComparer.Default.Equals(sourceValueType, destValueType),
                        DictionaryValueNeedsMapFrom: valueNested,
                        DictionaryValueSourceIsValueType: sourceValueType.IsValueType);
                }
            }
        }

        var sourceShape = ClassifySequenceSource(sourceType, out var sourceElemType, out var sourceIsArray);
        var destContainer = ClassifySequenceDestination(destType, out var destElemType);

        if (sourceShape is not null && destContainer is not null)
        {
            var elemConversion = compilation.ClassifyConversion(sourceElemType, destElemType);
            var elemDirect = elemConversion.Exists
                && (SymbolEqualityComparer.Default.Equals(sourceElemType, destElemType) || elemConversion.IsImplicit);
            var elemNested = !elemDirect && destElemType is INamedTypeSymbol destElemNamed && HasMapFromFor(destElemNamed, sourceElemType);

            if (elemDirect || elemNested)
            {
                return new MapFromMember(
                    name, MemberKind.Collection,
                    NeedsCast: false, DestinationTypeDisplay: "", SourceIsValueType: false,
                    ElementDestinationTypeDisplay: destElemType.ToDisplayString(),
                    ElementNeedsMapFrom: elemNested,
                    ElementNeedsCast: elemDirect && !SymbolEqualityComparer.Default.Equals(sourceElemType, destElemType),
                    SourceIsArray: sourceIsArray,
                    IsConstructorArgument: isConstructorArgument, SourceName: sourceName,
                    SourceShape: sourceShape.Value,
                    ContainerKind: destContainer.Value);
            }
        }

        return null;
    }

    /// <summary>
    /// Looks up <paramref name="name"/> in <paramref name="sourceProps"/> by exact match first; when that
    /// fails and <paramref name="useSnakeCase"/> is set, falls back to a case-insensitive,
    /// underscore-insensitive match -- but only when exactly one source member normalizes to the same
    /// name (an ambiguous normalized match is treated as no match, never a guess).
    /// </summary>
    private static bool TryResolveSourceMember(
        Dictionary<string, IPropertySymbol> sourceProps, string name, bool useSnakeCase, out IPropertySymbol sourceProp)
    {
        if (sourceProps.TryGetValue(name, out sourceProp!)) return true;
        if (!useSnakeCase) { sourceProp = null!; return false; }

        var normalized = name.Replace("_", "");
        IPropertySymbol? match = null;

        foreach (var candidate in sourceProps.Values)
        {
            if (!string.Equals(candidate.Name.Replace("_", ""), normalized, StringComparison.OrdinalIgnoreCase)) continue;
            if (match is not null) { sourceProp = null!; return false; } // ambiguous -- never guess.
            match = candidate;
        }

        sourceProp = match!;
        return match is not null;
    }

    /// <summary>
    /// One-level flattening -- see the type doc comment's "Flattened" bullet. Tries every source property
    /// as a candidate prefix of <paramref name="destName"/> (an exact, case-sensitive prefix match on the
    /// source's own member name); when the remainder resolves to a property on the prefix's type (itself
    /// naming-convention-aware) that is Direct-convertible to <paramref name="destType"/>, that's a match.
    /// More than one working split is treated as ambiguous -- no match, never a guess.
    /// </summary>
    private static MapFromMember? TryClassifyFlattenedMember(
        Compilation compilation, Dictionary<string, IPropertySymbol> sourceProps, string destName, ITypeSymbol destType, bool useSnakeCase, bool isConstructorArgument)
    {
        MapFromMember? found = null;

        foreach (var prefixProp in sourceProps.Values)
        {
            if (destName.Length <= prefixProp.Name.Length) continue;
            if (!destName.StartsWith(prefixProp.Name, StringComparison.Ordinal)) continue;

            var remainderName = destName.Substring(prefixProp.Name.Length);
            var prefixMembers = prefixProp.Type.GetMembers().OfType<IPropertySymbol>()
                .Where(p => !p.IsStatic && !p.IsIndexer && p.GetMethod is { DeclaredAccessibility: Accessibility.Public })
                .ToDictionary(p => p.Name, p => p);

            if (!TryResolveSourceMember(prefixMembers, remainderName, useSnakeCase, out var leafProp)) continue;

            var conversion = compilation.ClassifyConversion(leafProp.Type, destType);
            var directMatch = conversion.Exists && (SymbolEqualityComparer.Default.Equals(leafProp.Type, destType) || conversion.IsImplicit);
            if (!directMatch) continue;

            if (found is not null) return null; // ambiguous split -- never guess.

            found = new MapFromMember(
                destName, MemberKind.Flattened,
                NeedsCast: !SymbolEqualityComparer.Default.Equals(leafProp.Type, destType),
                DestinationTypeDisplay: destType.ToDisplayString(),
                SourceIsValueType: prefixProp.Type.IsValueType,
                ElementDestinationTypeDisplay: null, ElementNeedsMapFrom: false, ElementNeedsCast: false,
                SourceIsArray: false,
                IsConstructorArgument: isConstructorArgument,
                FlattenedPrefixName: prefixProp.Name, FlattenedLeafName: leafProp.Name);
        }

        return found;
    }

    /// <summary>
    /// Collects every <c>[assembly: MapFromConverter(typeof(TSource), typeof(TDest),
    /// typeof(TConverter))]</c> in the CURRENT compilation only -- one declared in a referenced assembly
    /// is not visible here (see <c>MapFromConverterAttribute</c>'s doc comment).
    /// </summary>
    private static ImmutableArray<(ITypeSymbol Source, ITypeSymbol Destination, INamedTypeSymbol Converter)> CollectConverters(Compilation compilation)
    {
        var builder = ImmutableArray.CreateBuilder<(ITypeSymbol, ITypeSymbol, INamedTypeSymbol)>();

        foreach (var attr in compilation.Assembly.GetAttributes())
        {
            if (attr.AttributeClass?.ToDisplayString() != MapFromConverterAttributeFullName) continue;
            if (attr.ConstructorArguments.Length != 3) continue;
            if (attr.ConstructorArguments[0].Value is not ITypeSymbol sourceType) continue;
            if (attr.ConstructorArguments[1].Value is not ITypeSymbol destType) continue;
            if (attr.ConstructorArguments[2].Value is not INamedTypeSymbol converterType) continue;

            builder.Add((sourceType, destType, converterType));
        }

        return builder.ToImmutable();
    }

    private static bool TryFindConverter(
        ImmutableArray<(ITypeSymbol Source, ITypeSymbol Destination, INamedTypeSymbol Converter)> converters,
        ITypeSymbol sourceType, ITypeSymbol destType, out INamedTypeSymbol converterType)
    {
        foreach (var candidate in converters)
        {
            if (!SymbolEqualityComparer.Default.Equals(candidate.Source, sourceType)) continue;
            if (!SymbolEqualityComparer.Default.Equals(candidate.Destination, destType)) continue;

            // Requires an accessible public parameterless constructor -- the generator has no DI
            // container to consult at compile time (see MapFromConverterAttribute's doc comment).
            if (candidate.Converter.InstanceConstructors.Any(ctor => ctor.Parameters.Length == 0 && ctor.DeclaredAccessibility == Accessibility.Public))
            {
                converterType = candidate.Converter;
                return true;
            }
        }

        converterType = null!;
        return false;
    }

    /// <summary>True when <paramref name="type"/> itself carries <c>[MapFrom(typeof(expectedSource))]</c> --
    /// the composition rule a nested, collection-element, or dictionary-value member relies on to call
    /// that type's own generated <c>MapFromCore(...)</c> rather than needing this generator to understand
    /// its shape.</summary>
    private static bool HasMapFromFor(INamedTypeSymbol type, ITypeSymbol expectedSource)
    {
        foreach (var attr in type.GetAttributes())
        {
            if (attr.AttributeClass?.ToDisplayString() != MapFromAttributeFullName) continue;
            if (attr.ConstructorArguments.Length == 1
                && attr.ConstructorArguments[0].Value is INamedTypeSymbol attrSource
                && SymbolEqualityComparer.Default.Equals(attrSource, expectedSource))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A source sequence that exposes both a count and an integer indexer (array/`List&lt;T&gt;`/
    /// `IList&lt;T&gt;`/`IReadOnlyList&lt;T&gt;`) is read with the original indexed loop; one that only
    /// guarantees a count (`HashSet&lt;T&gt;`/`ICollection&lt;T&gt;`/`IReadOnlyCollection&lt;T&gt;`) is
    /// read via `foreach` instead. A plain `IEnumerable&lt;T&gt;` (no guaranteed count at all) is
    /// deliberately unrecognized -- see the type doc comment.
    /// </summary>
    private static SourceSequenceShape? ClassifySequenceSource(ITypeSymbol type, out ITypeSymbol elementType, out bool isArray)
    {
        if (type is IArrayTypeSymbol { Rank: 1 } arrayType)
        {
            elementType = arrayType.ElementType;
            isArray = true;
            return SourceSequenceShape.Indexed;
        }

        isArray = false;

        if (type is INamedTypeSymbol { IsGenericType: true } named && named.TypeArguments.Length == 1
            && named.OriginalDefinition.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic")
        {
            elementType = named.TypeArguments[0];
            return named.OriginalDefinition.Name switch
            {
                "List" or "IList" or "IReadOnlyList" => SourceSequenceShape.Indexed,
                "HashSet" or "ICollection" or "IReadOnlyCollection" => SourceSequenceShape.CountedEnumerable,
                _ => (SourceSequenceShape?)null,
            };
        }

        elementType = null!;
        return null;
    }

    /// <summary>
    /// What concrete container a destination sequence type should be materialized as -- `List&lt;T&gt;`
    /// for `List&lt;T&gt;` itself and every common read-oriented interface over it, `HashSet&lt;T&gt;` for
    /// `HashSet&lt;T&gt;`/`ISet&lt;T&gt;`, or an exact-length array.
    /// </summary>
    private static DestinationContainerKind? ClassifySequenceDestination(ITypeSymbol type, out ITypeSymbol elementType)
    {
        if (type is IArrayTypeSymbol { Rank: 1 } arrayType)
        {
            elementType = arrayType.ElementType;
            return DestinationContainerKind.Array;
        }

        if (type is INamedTypeSymbol { IsGenericType: true } named && named.TypeArguments.Length == 1
            && named.OriginalDefinition.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic")
        {
            elementType = named.TypeArguments[0];
            return named.OriginalDefinition.Name switch
            {
                "List" or "IList" or "IReadOnlyList" or "ICollection" or "IReadOnlyCollection" or "IEnumerable" => DestinationContainerKind.List,
                "HashSet" or "ISet" => DestinationContainerKind.HashSet,
                _ => (DestinationContainerKind?)null,
            };
        }

        elementType = null!;
        return null;
    }

    private static bool TryGetDictionaryTypes(ITypeSymbol type, out ITypeSymbol keyType, out ITypeSymbol valueType)
    {
        if (type is INamedTypeSymbol { IsGenericType: true } named && named.TypeArguments.Length == 2
            && named.OriginalDefinition.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic"
            && named.OriginalDefinition.Name is "Dictionary" or "IDictionary" or "IReadOnlyDictionary")
        {
            keyType = named.TypeArguments[0];
            valueType = named.TypeArguments[1];
            return true;
        }

        keyType = null!;
        valueType = null!;
        return false;
    }

    private static void Execute(SourceProductionContext spc, MapFromModel model)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("// Generated by FluxMapper.SourceGenerator from [MapFrom] -- zero reflection, zero Expression.Compile().");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();

        if (model.Namespace is not null)
        {
            sb.AppendLine($"namespace {model.Namespace};");
            sb.AppendLine();
        }

        var keyword = model.IsRecord
            ? (model.IsValueType ? "partial record struct" : "partial record")
            : (model.IsValueType ? "partial struct" : "partial class");
        sb.AppendLine($"public {keyword} {model.DestinationName}");
        sb.AppendLine("{");
        sb.AppendLine($"    /// <summary>Generated by FluxMapper.SourceGenerator from [MapFrom(typeof({model.SourceFullName}))]. AOT-safe: no reflection, no Expression.Compile().</summary>");
        sb.AppendLine($"    public static {model.DestinationFullName} MapFrom({model.SourceFullName} source)");
        sb.AppendLine("    {");
        sb.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(source);");
        sb.AppendLine("        return MapFromCore(source);");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>Same mapping as <c>MapFrom</c>, without its null-argument guard -- used internally when");
        sb.AppendLine("    /// composing from an already-known-non-null nested member or collection element. Calling this");
        sb.AppendLine("    /// directly with a null <c>source</c> throws <c>NullReferenceException</c> instead of the clean");
        sb.AppendLine("    /// <c>ArgumentNullException</c> that <c>MapFrom</c> gives; prefer <c>MapFrom</c> at any call site that");
        sb.AppendLine("    /// hasn't already established non-null.</summary>");
        sb.AppendLine($"    public static {model.DestinationFullName} MapFromCore({model.SourceFullName} source)");
        sb.AppendLine("    {");

        // Nested/collection/dictionary members are computed into locals as statements *before* the final
        // constructor call/object initializer -- a constructor argument list or initializer expression
        // can't contain a loop, and computing into a local first means this works identically whether the
        // destination member is a constructor parameter, a plain `set`, or a record's `init` accessor.
        var initializerLines = new List<string>();
        var ctorArgLines = new List<string>();
        var localIndex = 0;

        foreach (var member in model.Members)
        {
            string valueRef;

            switch (member.Kind)
            {
                case MemberKind.Converter:
                {
                    valueRef = $"new {member.ConverterTypeDisplay}().Convert(source.{member.SourceName}, new global::FluxMapper.Abstractions.ResolutionContext())";
                    break;
                }

                case MemberKind.Direct:
                {
                    valueRef = member.NeedsCast ? $"({member.DestinationTypeDisplay})source.{member.SourceName}" : $"source.{member.SourceName}";
                    break;
                }

                case MemberKind.Flattened:
                {
                    if (member.SourceIsValueType)
                    {
                        var expr = $"source.{member.FlattenedPrefixName}.{member.FlattenedLeafName}";
                        valueRef = member.NeedsCast ? $"({member.DestinationTypeDisplay}){expr}" : expr;
                    }
                    else
                    {
                        // A null-conditional read can't itself become a non-nullable destination type;
                        // forgiven the same way a null Nested composition already is below -- if the
                        // intermediate is actually null at runtime, this assigns whatever null/default
                        // that produces, not a generator-time failure.
                        var expr = $"source.{member.FlattenedPrefixName}?.{member.FlattenedLeafName}";
                        valueRef = member.NeedsCast ? $"({member.DestinationTypeDisplay})({expr})!" : $"{expr}!";
                    }

                    break;
                }

                case MemberKind.Nested:
                {
                    var local = $"__flux{localIndex++}";
                    if (member.SourceIsValueType)
                    {
                        sb.AppendLine($"        var {local} = {member.DestinationTypeDisplay}.MapFromCore(source.{member.SourceName});");
                    }
                    else
                    {
                        sb.AppendLine($"        var {local}Src = source.{member.SourceName};");
                        sb.AppendLine($"        var {local} = {local}Src is null ? null! : {member.DestinationTypeDisplay}.MapFromCore({local}Src);");
                    }

                    sb.AppendLine();
                    valueRef = local;
                    break;
                }

                case MemberKind.Dictionary:
                {
                    var dict = $"__flux{localIndex++}";
                    var countVar = $"{dict}Count";
                    var kvpVar = $"{dict}Kvp";
                    var valVar = $"{dict}Val";

                    var keyExpr = member.DictionaryKeyNeedsCast ? $"({member.DictionaryKeyTypeDisplay}){kvpVar}.Key" : $"{kvpVar}.Key";

                    sb.AppendLine($"        var {countVar} = source.{member.SourceName}.Count;");
                    sb.AppendLine($"        var {dict} = new global::System.Collections.Generic.Dictionary<{member.DictionaryKeyTypeDisplay}, {member.DictionaryValueTypeDisplay}>({countVar});");
                    sb.AppendLine($"        foreach (var {kvpVar} in source.{member.SourceName})");
                    sb.AppendLine("        {");

                    if (member.DictionaryValueNeedsMapFrom)
                    {
                        sb.AppendLine(member.DictionaryValueSourceIsValueType
                            ? $"            var {valVar} = {member.DictionaryValueTypeDisplay}.MapFromCore({kvpVar}.Value);"
                            : $"            var {valVar} = {kvpVar}.Value is null ? null! : {member.DictionaryValueTypeDisplay}.MapFromCore({kvpVar}.Value);");
                    }
                    else if (member.DictionaryValueNeedsCast)
                    {
                        sb.AppendLine($"            var {valVar} = ({member.DictionaryValueTypeDisplay}){kvpVar}.Value;");
                    }
                    else
                    {
                        sb.AppendLine($"            var {valVar} = {kvpVar}.Value;");
                    }

                    sb.AppendLine($"            {dict}[{keyExpr}] = {valVar};");
                    sb.AppendLine("        }");

                    sb.AppendLine();
                    valueRef = dict;
                    break;
                }

                case MemberKind.Collection:
                {
                    var list = $"__flux{localIndex++}";
                    var countVar = $"{list}Count";
                    var indexVar = $"{list}I";
                    var countAccessor = member.SourceIsArray ? "Length" : "Count";

                    string ElementExprFor(string elemExpr) =>
                        member.ElementNeedsMapFrom
                            ? $"{member.ElementDestinationTypeDisplay}.MapFromCore({elemExpr})"
                            : member.ElementNeedsCast
                                ? $"({member.ElementDestinationTypeDisplay}){elemExpr}"
                                : elemExpr;

                    sb.AppendLine($"        var {countVar} = source.{member.SourceName}.{countAccessor};");

                    if (member.SourceShape == SourceSequenceShape.Indexed)
                    {
                        var sourceElemExpr = $"source.{member.SourceName}[{indexVar}]";

                        if (member.ContainerKind == DestinationContainerKind.Array)
                        {
                            sb.AppendLine($"        var {list} = new {member.ElementDestinationTypeDisplay}[{countVar}];");
                            sb.AppendLine($"        for (var {indexVar} = 0; {indexVar} < {countVar}; {indexVar}++)");
                            sb.AppendLine("        {");
                            sb.AppendLine($"            {list}[{indexVar}] = {ElementExprFor(sourceElemExpr)};");
                            sb.AppendLine("        }");
                        }
                        else if (member.ContainerKind == DestinationContainerKind.HashSet)
                        {
                            sb.AppendLine($"        var {list} = new global::System.Collections.Generic.HashSet<{member.ElementDestinationTypeDisplay}>({countVar});");
                            sb.AppendLine($"        for (var {indexVar} = 0; {indexVar} < {countVar}; {indexVar}++)");
                            sb.AppendLine("        {");
                            sb.AppendLine($"            {list}.Add({ElementExprFor(sourceElemExpr)});");
                            sb.AppendLine("        }");
                        }
                        else if (model.HasCollectionsMarshalSetCount)
                        {
                            var span = $"{list}Span";
                            sb.AppendLine($"        var {list} = new global::System.Collections.Generic.List<{member.ElementDestinationTypeDisplay}>({countVar});");
                            sb.AppendLine($"        global::System.Runtime.InteropServices.CollectionsMarshal.SetCount({list}, {countVar});");
                            sb.AppendLine($"        var {span} = global::System.Runtime.InteropServices.CollectionsMarshal.AsSpan({list});");
                            sb.AppendLine($"        for (var {indexVar} = 0; {indexVar} < {countVar}; {indexVar}++)");
                            sb.AppendLine("        {");
                            sb.AppendLine($"            {span}[{indexVar}] = {ElementExprFor(sourceElemExpr)};");
                            sb.AppendLine("        }");
                        }
                        else
                        {
                            sb.AppendLine($"        var {list} = new global::System.Collections.Generic.List<{member.ElementDestinationTypeDisplay}>({countVar});");
                            sb.AppendLine($"        for (var {indexVar} = 0; {indexVar} < {countVar}; {indexVar}++)");
                            sb.AppendLine("        {");
                            sb.AppendLine($"            {list}.Add({ElementExprFor(sourceElemExpr)});");
                            sb.AppendLine("        }");
                        }
                    }
                    else
                    {
                        // CountedEnumerable -- no indexer on the source; read via foreach instead.
                        var enumVar = $"{list}Item";

                        if (member.ContainerKind == DestinationContainerKind.Array)
                        {
                            sb.AppendLine($"        var {list} = new {member.ElementDestinationTypeDisplay}[{countVar}];");
                            sb.AppendLine($"        var {indexVar} = 0;");
                            sb.AppendLine($"        foreach (var {enumVar} in source.{member.SourceName})");
                            sb.AppendLine("        {");
                            sb.AppendLine($"            {list}[{indexVar}] = {ElementExprFor(enumVar)};");
                            sb.AppendLine($"            {indexVar}++;");
                            sb.AppendLine("        }");
                        }
                        else if (member.ContainerKind == DestinationContainerKind.HashSet)
                        {
                            sb.AppendLine($"        var {list} = new global::System.Collections.Generic.HashSet<{member.ElementDestinationTypeDisplay}>({countVar});");
                            sb.AppendLine($"        foreach (var {enumVar} in source.{member.SourceName})");
                            sb.AppendLine("        {");
                            sb.AppendLine($"            {list}.Add({ElementExprFor(enumVar)});");
                            sb.AppendLine("        }");
                        }
                        else if (model.HasCollectionsMarshalSetCount)
                        {
                            var span = $"{list}Span";
                            sb.AppendLine($"        var {list} = new global::System.Collections.Generic.List<{member.ElementDestinationTypeDisplay}>({countVar});");
                            sb.AppendLine($"        global::System.Runtime.InteropServices.CollectionsMarshal.SetCount({list}, {countVar});");
                            sb.AppendLine($"        var {span} = global::System.Runtime.InteropServices.CollectionsMarshal.AsSpan({list});");
                            sb.AppendLine($"        var {indexVar} = 0;");
                            sb.AppendLine($"        foreach (var {enumVar} in source.{member.SourceName})");
                            sb.AppendLine("        {");
                            sb.AppendLine($"            {span}[{indexVar}] = {ElementExprFor(enumVar)};");
                            sb.AppendLine($"            {indexVar}++;");
                            sb.AppendLine("        }");
                        }
                        else
                        {
                            sb.AppendLine($"        var {list} = new global::System.Collections.Generic.List<{member.ElementDestinationTypeDisplay}>({countVar});");
                            sb.AppendLine($"        foreach (var {enumVar} in source.{member.SourceName})");
                            sb.AppendLine("        {");
                            sb.AppendLine($"            {list}.Add({ElementExprFor(enumVar)});");
                            sb.AppendLine("        }");
                        }
                    }

                    sb.AppendLine();
                    valueRef = list;
                    break;
                }

                default:
                    throw new global::System.InvalidOperationException($"Unreachable MemberKind: {member.Kind}");
            }

            if (member.IsConstructorArgument)
            {
                // Named, not positional -- a candidate constructor may have omitted a defaulted,
                // unresolvable parameter (see Analyze), so argument position alone can't be trusted to
                // line up with the constructor's declared parameter order.
                ctorArgLines.Add($"{member.Name}: {valueRef}");
            }
            else
            {
                initializerLines.Add($"{member.Name} = {valueRef},");
            }
        }

        if (ctorArgLines.Count > 0)
        {
            var ctorCall = $"new {model.DestinationFullName}({string.Join(", ", ctorArgLines)})";
            if (initializerLines.Count > 0)
            {
                sb.AppendLine($"        return {ctorCall}");
                sb.AppendLine("        {");
                foreach (var line in initializerLines)
                {
                    sb.AppendLine($"            {line}");
                }

                sb.AppendLine("        };");
            }
            else
            {
                sb.AppendLine($"        return {ctorCall};");
            }
        }
        else
        {
            sb.AppendLine($"        return new {model.DestinationFullName}");
            sb.AppendLine("        {");
            foreach (var line in initializerLines)
            {
                sb.AppendLine($"            {line}");
            }

            sb.AppendLine("        };");
        }

        sb.AppendLine("    }");
        sb.AppendLine("}");

        spc.AddSource($"{model.DestinationName}.MapFrom.g.cs", sb.ToString());
    }

    private enum MemberKind
    {
        Direct,
        Nested,
        Collection,
        Dictionary,
        Converter,
        Flattened,
    }

    private enum SourceSequenceShape
    {
        /// <summary>Has both a count and an integer indexer -- array, `List&lt;T&gt;`, `IList&lt;T&gt;`, `IReadOnlyList&lt;T&gt;`.</summary>
        Indexed,

        /// <summary>Has a count but no indexer -- `HashSet&lt;T&gt;`, `ICollection&lt;T&gt;`, `IReadOnlyCollection&lt;T&gt;`. Read via `foreach`.</summary>
        CountedEnumerable,
    }

    private enum DestinationContainerKind
    {
        Array,
        List,
        HashSet,
    }

    /// <param name="Name">The destination-side label: the property name being initialized, or the constructor parameter name.</param>
    /// <param name="SourceName">
    /// The actual source member being read. Usually equal to <paramref name="Name"/>, but can differ under
    /// a naming convention (destination <c>UserName</c> resolved against source <c>user_name</c>) -- always
    /// null for <see cref="MemberKind.Flattened"/>, which reads via <c>FlattenedPrefixName</c>/
    /// <c>FlattenedLeafName</c> instead.
    /// </param>
    private sealed record MapFromMember(
        string Name,
        MemberKind Kind,
        bool NeedsCast,
        string DestinationTypeDisplay,
        bool SourceIsValueType,
        string? ElementDestinationTypeDisplay,
        bool ElementNeedsMapFrom,
        bool ElementNeedsCast,
        bool SourceIsArray,
        bool IsConstructorArgument = false,
        string? SourceName = null,
        SourceSequenceShape SourceShape = SourceSequenceShape.Indexed,
        DestinationContainerKind ContainerKind = DestinationContainerKind.List,
        string? DictionaryKeyTypeDisplay = null,
        string? DictionaryValueTypeDisplay = null,
        bool DictionaryKeyNeedsCast = false,
        bool DictionaryValueNeedsCast = false,
        bool DictionaryValueNeedsMapFrom = false,
        bool DictionaryValueSourceIsValueType = false,
        string? ConverterTypeDisplay = null,
        string? FlattenedPrefixName = null,
        string? FlattenedLeafName = null);

    private sealed record MapFromModel(
        string DestinationName,
        string DestinationFullName,
        string SourceFullName,
        string? Namespace,
        bool IsRecord,
        bool IsValueType,
        bool HasCollectionsMarshalSetCount,
        ImmutableArray<MapFromMember> Members);
}
