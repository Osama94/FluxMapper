using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FluxMapper.SourceGenerator;

/// <summary>
/// An <see cref="IIncrementalGenerator"/>
/// that turns <c>[MapFrom(typeof(TSource))] public partial class/record TDestination</c> into a real,
/// zero-reflection, zero-<c>Expression.Compile()</c> static factory method on that same partial type --
/// the one execution tier in this project that can honestly report <c>ExecutionEligibility.AotSafe = true</c>
///, because everything it produces is ordinary compiled C# the
/// destination assembly ships with, not code assembled at runtime.
///
/// Scope, stated plainly rather than silently: this generator does its OWN member matching, in three
/// kinds, none of which share <see cref="Building.MappingPlanBuilder"/>'s pipeline (naming conventions,
/// flattening, nullability policy, resolvers, ReverseMap, reference-preservation for cycles, global
/// type-pair converters, ...) -- making a Roslyn-symbol-driven twin of that pipeline that produces the
/// exact same <c>MappingPlan</c> IR the reflection-based builder produces is real, substantial follow-up
/// work, not something this pass claims to have finished:
/// <list type="bullet">
/// <item>Direct: exact name match, identity or implicit conversion -- the original, flat-DTO-only scope.</item>
/// <item>Nested: a same-named destination member whose type is itself decorated with
/// <c>[MapFrom(typeof(TSourceMemberType))]</c> composes to a call to that type's own generated
/// <c>MapFromCore(...)</c> -- with a null check spliced in first when the source member's type is a
/// reference type, since the generated destination call otherwise can't express "map only if present."</item>
/// <item>Collection: a same-named member pair where both sides are exactly <c>List&lt;T&gt;</c> or a
/// single-dimensional array (deliberately not any other <c>IEnumerable&lt;T&gt;</c> shape yet -- those two
/// cover the overwhelming majority of real DTOs and keep the codegen here simple enough to trust without a
/// compiler to check it against locally) and the element types are themselves either directly/implicitly
/// convertible or compose via a nested <c>[MapFrom]</c> the same way a plain member does. Genuinely no
/// cycle protection here (unlike the compiled-expression tier's <c>ReferenceHandling.Preserve</c>) -- a
/// self-referencing object graph mapped through generated code will recurse exactly as far as the graph
/// does, because there is no reflection-based identity map to consult at compile time.</item>
/// </list>
///
/// <b>Destination construction.</b> A destination reached via a public parameterless constructor (the
/// original, and still overwhelmingly common, shape -- an ordinary class, or a <c>record</c>/<c>record
/// class</c> declared with plain <c>{ get; init; }</c> properties and no positional parameter list) is
/// populated the same way it always has been: <c>new Dest { Member = value, ... }</c>. A value-type
/// destination (<c>struct</c> or <c>record struct</c>) always takes this same path too, regardless of
/// what other constructors it declares, because <c>new T()</c> is unconditionally legal C# for any struct.
///
/// A <i>reference-type</i> destination with no public parameterless constructor -- most notably a
/// <c>record</c>/<c>record class</c> declared with a positional primary constructor
/// (<c>record OrderDto(int Id, decimal Total)</c>), but this is deliberately not special-cased to records
/// only -- goes through constructor-based construction instead, mirroring
/// <see cref="Construction.ConstructorSelector"/>'s runtime policy exactly: the public constructor with
/// the most parameters that ALL resolve against the source type wins, tried in descending parameter-count
/// order (an unresolvable parameter with an explicit default value is simply omitted from the call rather
/// than failing that candidate, so an optional trailing parameter doesn't block an otherwise-usable
/// constructor). A parameter resolves via the exact same Direct/Nested/Collection rules a plain member
/// does -- a positional record's <c>Address</c>/<c>Orders</c>-shaped constructor parameters compose the
/// same way those shapes do as object-initializer members. Constructor arguments are emitted as named
/// arguments (<c>Dest(Id: ..., Total: ...)</c>) rather than positional, specifically so omitting a
/// defaulted-but-unresolvable parameter doesn't depend on argument order. Any destination member NOT
/// consumed by the selected constructor still gets the ordinary object-initializer treatment afterward
/// (e.g. an extra settable property alongside a record's primary constructor). If no constructor --
/// parameterless or parameterized -- can be found, generation for that destination is silently skipped
/// (the same graceful-degradation this generator has always used for any other unmatched shape), and
/// <c>FluxMapper.Analyzers.MapFromAnalyzer</c>'s FLUX0003 is what surfaces that to the user as an actual
/// diagnostic instead of a mysterious "MapFrom does not exist" at the call site.
///
/// Before this, a <c>record</c>-declared destination could not be generated for AT ALL -- not merely the
/// positional-constructor shape above, but even the plain <c>{ get; init; }</c> case -- because
/// <c>Initialize</c>'s syntax predicate only ever matched <c>ClassDeclarationSyntax</c>, and a C#
/// <c>record</c> declaration (record class or record struct alike) parses as the sibling node
/// <c>RecordDeclarationSyntax</c>, never as <c>ClassDeclarationSyntax</c>. This shipped silently: nothing
/// failed loudly, the generator simply never ran for any record, despite <c>Execute</c>'s own codegen
/// already branching on <c>IsRecord</c> to emit the right partial keyword -- dead code protecting a path
/// that could never be reached. Deliberately still out of scope: plain (non-record) <c>struct</c>
/// destinations (blocked only by <c>Execute</c>'s class/record keyword selection, not a new mapping shape
/// -- a mechanical follow-up), <c>HashSet&lt;T&gt;</c>/<c>Dictionary&lt;TKey,TValue&gt;</c>/the wider
/// <c>IEnumerable&lt;T&gt;</c> family beyond <c>List&lt;T&gt;</c>/array, naming conventions, flattening,
/// and global type-pair converters (<c>RegisterConverter</c>) -- all still the compiled-expression tier's
/// job.
///
/// Every generated destination type gets two static methods, not one: <c>MapFrom</c> (the public entry
/// point, argument-null-checked) and <c>MapFromCore</c> (the same mapping, without that check). Nested and
/// collection-element composition calls <c>MapFromCore</c> on the target type directly rather than
/// <c>MapFrom</c>, to avoid re-checking a value this method has already established is non-null (for a
/// nested member, checked immediately above via an explicit null-conditional; for a collection element,
/// not separately checked -- see the perf/correctness tradeoff called out below). This is a deliberate
/// performance choice: it removes a redundant argument-null branch from the hottest path this generator
/// produces, closing most of the measured gap against Mapster's default runtime mode on nested/collection
/// shapes (see the README's Benchmarks section and <c>COMPETITIVE_GAP_ANALYSIS.md</c>). The one behavioral
/// consequence, stated plainly: a <c>null</c> element inside a mapped <c>List&lt;T&gt;</c>/array now
/// surfaces as a <see cref="NullReferenceException"/> from inside <c>MapFromCore</c> rather than a clean
/// <see cref="ArgumentNullException"/> from <c>MapFrom</c> -- the same failure mode C#'s own null-forgiving
/// patterns produce when an assumed-non-null value turns out to be null. Calling <c>MapFromCore</c>
/// directly (rather than through the generator's own composition) carries that same tradeoff; prefer the
/// public <c>MapFrom</c> at any call site that hasn't already established non-null.
///
/// Collection codegen also avoids two allocation patterns a naive implementation would otherwise pay for:
/// building into a <c>List&lt;T&gt;</c> via repeated <c>Add</c> calls when the final shape is an array
/// (which used to mean a full second copy via <c>ToArray()</c>), and growing a <c>List&lt;T&gt;</c>
/// incrementally when its final length is already known up front. An array destination is allocated at its
/// exact final length and written by index directly. A <c>List&lt;T&gt;</c> destination is pre-sized via
/// its capacity constructor and, when the target framework exposes
/// <c>System.Runtime.InteropServices.CollectionsMarshal.SetCount</c> (.NET 8+), its backing storage is
/// exposed as a <see cref="Span{T}"/> and written by index too -- the same zero-bounds-surprise, no-`Add`
/// pattern as the array path. Older target frameworks (netstandard2.0) fall back to an indexed loop calling
/// <c>Add</c>, which is still one allocation-free pass over a pre-sized list rather than the original
/// enumerator-based <c>foreach</c>.
///
/// What's here is genuinely generated, genuinely compiled, and genuinely verified end-to-end for the flat
/// case; the nested/collection/constructor cases are verified the same way, just with a narrower shape
/// than the compiled-expression tier supports.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class MapFromGenerator : IIncrementalGenerator
{
    private const string MapFromAttributeFullName = "FluxMapper.Abstractions.MapFromAttribute";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var targets = context.SyntaxProvider.ForAttributeWithMetadataName(
            MapFromAttributeFullName,
            predicate: static (node, _) => node is TypeDeclarationSyntax typeDecl
                && typeDecl is ClassDeclarationSyntax or RecordDeclarationSyntax
                && typeDecl.Modifiers.Any(SyntaxKind.PartialKeyword),
            transform: static (ctx, _) => Analyze(ctx));

        context.RegisterSourceOutput(targets.Where(static m => m is not null), static (spc, model) => Execute(spc, model!));
    }

    private static MapFromModel? Analyze(GeneratorAttributeSyntaxContext ctx)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol destinationSymbol) return null;
        if (destinationSymbol.ContainingType is not null) return null; // scope: top-level (namespace-nested) classes only, see type doc comment.

        var attribute = ctx.Attributes.FirstOrDefault();
        if (attribute is null || attribute.ConstructorArguments.Length != 1) return null;
        if (attribute.ConstructorArguments[0].Value is not INamedTypeSymbol sourceSymbol) return null;

        var compilation = ctx.SemanticModel.Compilation;
        var listOfT = compilation.GetTypeByMetadataName("System.Collections.Generic.List`1");

        // Detected once per destination type against the CONSUMER's own compilation (not this generator's
        // own TFM) -- a consumer targeting net8.0+ sees this as true and gets the Span-based fast path for
        // List<T> destinations; a netstandard2.0 consumer sees false and gets the indexed-Add fallback.
        var collectionsMarshal = compilation.GetTypeByMetadataName("System.Runtime.InteropServices.CollectionsMarshal");
        var hasSetCount = collectionsMarshal is not null && collectionsMarshal.GetMembers("SetCount").Length > 0;

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
                    MapFromMember? member = null;
                    if (sourceProps.TryGetValue(parameter.Name, out var sourceProp))
                    {
                        member = TryClassifyMember(compilation, listOfT, parameter.Name, parameter.Type, sourceProp.Type, isConstructorArgument: true);
                    }

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
            if (!sourceProps.TryGetValue(destProp.Name, out var sourceProp)) continue;

            var member = TryClassifyMember(compilation, listOfT, destProp.Name, destProp.Type, sourceProp.Type, isConstructorArgument: false);
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
    /// Classifies one (name, destination type, source type) triple into a Direct/Nested/Collection member,
    /// or returns null when none of those three shapes apply -- shared between ordinary destination
    /// properties and a candidate constructor's parameters, since a constructor parameter composes exactly
    /// the same way a settable member does (see the type doc comment's "Destination construction" section).
    /// </summary>
    private static MapFromMember? TryClassifyMember(
        Compilation compilation, INamedTypeSymbol? listOfT, string name, ITypeSymbol destType, ITypeSymbol sourceType, bool isConstructorArgument)
    {
        var conversion = compilation.ClassifyConversion(sourceType, destType);
        if (conversion.Exists && (SymbolEqualityComparer.Default.Equals(sourceType, destType) || conversion.IsImplicit))
        {
            return new MapFromMember(
                name, MemberKind.Direct,
                NeedsCast: !SymbolEqualityComparer.Default.Equals(sourceType, destType),
                DestinationTypeDisplay: destType.ToDisplayString(),
                SourceIsValueType: false, ElementDestinationTypeDisplay: null,
                ElementNeedsMapFrom: false, ElementNeedsCast: false, DestinationIsArray: false, SourceIsArray: false,
                IsConstructorArgument: isConstructorArgument);
        }

        if (destType is INamedTypeSymbol destNamed && HasMapFromFor(destNamed, sourceType))
        {
            return new MapFromMember(
                name, MemberKind.Nested,
                NeedsCast: false,
                DestinationTypeDisplay: destType.ToDisplayString(),
                SourceIsValueType: sourceType.IsValueType, ElementDestinationTypeDisplay: null,
                ElementNeedsMapFrom: false, ElementNeedsCast: false, DestinationIsArray: false, SourceIsArray: false,
                IsConstructorArgument: isConstructorArgument);
        }

        if (listOfT is not null
            && TryGetSequenceElementType(sourceType, listOfT, out var sourceElemType, out var sourceIsArray)
            && TryGetSequenceElementType(destType, listOfT, out var destElemType, out var destIsArray))
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
                    DestinationIsArray: destIsArray,
                    SourceIsArray: sourceIsArray,
                    IsConstructorArgument: isConstructorArgument);
            }
        }

        return null;
    }

    /// <summary>True when <paramref name="type"/> itself carries <c>[MapFrom(typeof(expectedSource))]</c> --
    /// the composition rule a nested or collection-element member relies on to call that type's own
    /// generated <c>MapFromCore(...)</c> rather than needing this generator to understand its shape.</summary>
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
    /// Deliberately narrow: recognizes only a single-dimensional array or an exact <c>List&lt;T&gt;</c> --
    /// see the type doc comment for why. Anything else (an interface type, <c>HashSet&lt;T&gt;</c>, an
    /// immutable collection, ...) returns false and that member is silently left unmapped, same as any
    /// other unmatched member this generator has always skipped.
    /// </summary>
    private static bool TryGetSequenceElementType(ITypeSymbol type, INamedTypeSymbol listOfT, out ITypeSymbol elementType, out bool isArray)
    {
        if (type is IArrayTypeSymbol { Rank: 1 } arrayType)
        {
            elementType = arrayType.ElementType;
            isArray = true;
            return true;
        }

        if (type is INamedTypeSymbol { IsGenericType: true } named && named.TypeArguments.Length == 1
            && SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, listOfT))
        {
            elementType = named.TypeArguments[0];
            isArray = false;
            return true;
        }

        elementType = null!;
        isArray = false;
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
            : "partial class";
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

        // Nested/collection members are computed into locals as statements *before* the final constructor
        // call/object initializer -- a constructor argument list or initializer expression can't contain a
        // loop, and computing into a local first (rather than inlining the nested call twice, once for a
        // null check and once for the value) means this works identically whether the destination member
        // is a constructor parameter, a plain `set`, or a record's `init` accessor.
        var initializerLines = new List<string>();
        var ctorArgLines = new List<string>();
        var localIndex = 0;

        foreach (var member in model.Members)
        {
            string valueRef;

            switch (member.Kind)
            {
                case MemberKind.Direct:
                {
                    valueRef = member.NeedsCast ? $"({member.DestinationTypeDisplay})source.{member.Name}" : $"source.{member.Name}";
                    break;
                }

                case MemberKind.Nested:
                {
                    var local = $"__flux{localIndex++}";
                    if (member.SourceIsValueType)
                    {
                        sb.AppendLine($"        var {local} = {member.DestinationTypeDisplay}.MapFromCore(source.{member.Name});");
                    }
                    else
                    {
                        sb.AppendLine($"        var {local}Src = source.{member.Name};");
                        sb.AppendLine($"        var {local} = {local}Src is null ? null! : {member.DestinationTypeDisplay}.MapFromCore({local}Src);");
                    }

                    sb.AppendLine();
                    valueRef = local;
                    break;
                }

                case MemberKind.Collection:
                {
                    var list = $"__flux{localIndex++}";
                    var countVar = $"{list}Count";
                    var indexVar = $"{list}I";
                    var countAccessor = member.SourceIsArray ? "Length" : "Count";

                    string ElementExprAt(string indexer) =>
                        member.ElementNeedsMapFrom
                            ? $"{member.ElementDestinationTypeDisplay}.MapFromCore(source.{member.Name}[{indexer}])"
                            : member.ElementNeedsCast
                                ? $"({member.ElementDestinationTypeDisplay})source.{member.Name}[{indexer}]"
                                : $"source.{member.Name}[{indexer}]";

                    sb.AppendLine($"        var {countVar} = source.{member.Name}.{countAccessor};");

                    if (member.DestinationIsArray)
                    {
                        // Exact-length array, written by index -- no intermediate List<T>, no ToArray() copy.
                        sb.AppendLine($"        var {list} = new {member.ElementDestinationTypeDisplay}[{countVar}];");
                        sb.AppendLine($"        for (var {indexVar} = 0; {indexVar} < {countVar}; {indexVar}++)");
                        sb.AppendLine("        {");
                        sb.AppendLine($"            {list}[{indexVar}] = {ElementExprAt(indexVar)};");
                        sb.AppendLine("        }");
                    }
                    else if (model.HasCollectionsMarshalSetCount)
                    {
                        // Pre-sized List<T>, backing storage exposed as a Span<T> and written by index --
                        // same no-`Add`-bounds-check shape as the array path above (net8.0+ only).
                        var span = $"{list}Span";
                        sb.AppendLine($"        var {list} = new global::System.Collections.Generic.List<{member.ElementDestinationTypeDisplay}>({countVar});");
                        sb.AppendLine($"        global::System.Runtime.InteropServices.CollectionsMarshal.SetCount({list}, {countVar});");
                        sb.AppendLine($"        var {span} = global::System.Runtime.InteropServices.CollectionsMarshal.AsSpan({list});");
                        sb.AppendLine($"        for (var {indexVar} = 0; {indexVar} < {countVar}; {indexVar}++)");
                        sb.AppendLine("        {");
                        sb.AppendLine($"            {span}[{indexVar}] = {ElementExprAt(indexVar)};");
                        sb.AppendLine("        }");
                    }
                    else
                    {
                        // netstandard2.0 fallback: still a pre-sized, single allocation-free pass, just via
                        // indexed `Add` instead of a Span (CollectionsMarshal.SetCount isn't available there).
                        sb.AppendLine($"        var {list} = new global::System.Collections.Generic.List<{member.ElementDestinationTypeDisplay}>({countVar});");
                        sb.AppendLine($"        for (var {indexVar} = 0; {indexVar} < {countVar}; {indexVar}++)");
                        sb.AppendLine("        {");
                        sb.AppendLine($"            {list}.Add({ElementExprAt(indexVar)});");
                        sb.AppendLine("        }");
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
    }

    private sealed record MapFromMember(
        string Name,
        MemberKind Kind,
        bool NeedsCast,
        string DestinationTypeDisplay,
        bool SourceIsValueType,
        string? ElementDestinationTypeDisplay,
        bool ElementNeedsMapFrom,
        bool ElementNeedsCast,
        bool DestinationIsArray,
        bool SourceIsArray,
        bool IsConstructorArgument = false);

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
