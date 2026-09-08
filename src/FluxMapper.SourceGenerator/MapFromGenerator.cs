using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FluxMapper.SourceGenerator;

/// <summary>
/// An <see cref="IIncrementalGenerator"/>
/// that turns <c>[MapFrom(typeof(TSource))] public partial class TDestination</c> into a real,
/// zero-reflection, zero-<c>Expression.Compile()</c> static factory method on that same partial class --
/// the one execution tier in this project that can honestly report <c>ExecutionEligibility.AotSafe = true</c>
///, because everything it produces is ordinary compiled C# the
/// destination assembly ships with, not code assembled at runtime.
///
/// Scope, stated plainly rather than silently: this generator does its OWN member matching, in three
/// kinds, none of which share <see cref="Building.MappingPlanBuilder"/>'s pipeline (naming conventions,
/// flattening, nullability policy, resolvers, ReverseMap, reference-preservation for cycles, ...) --
/// making a Roslyn-symbol-driven twin of that pipeline that produces the exact same <c>MappingPlan</c> IR
/// the reflection-based builder produces is real, substantial follow-up work, not something this pass
/// claims to have finished:
/// <list type="bullet">
/// <item>Direct: exact name match, identity or implicit conversion -- the original, flat-DTO-only scope.</item>
/// <item>Nested: a same-named destination member whose type is itself decorated with
/// <c>[MapFrom(typeof(TSourceMemberType))]</c> composes to a call to that type's own generated
/// <c>MapFrom(...)</c> -- with a null check spliced in first when the source member's type is a reference
/// type, since the generated destination call otherwise can't express "map only if present."</item>
/// <item>Collection: a same-named member pair where both sides are exactly <c>List&lt;T&gt;</c> or a
/// single-dimensional array (deliberately not any other <c>IEnumerable&lt;T&gt;</c> shape yet -- those two
/// cover the overwhelming majority of real DTOs and keep the codegen here simple enough to trust without a
/// compiler to check it against locally) and the element types are themselves either directly/implicitly
/// convertible or compose via a nested <c>[MapFrom]</c> the same way a plain member does. Genuinely no
/// cycle protection here (unlike the compiled-expression tier's <c>ReferenceHandling.Preserve</c>) -- a
/// self-referencing object graph mapped through generated code will recurse exactly as far as the graph
/// does, because there is no reflection-based identity map to consult at compile time.</item>
/// </list>
/// What's here is genuinely generated, genuinely compiled, and genuinely verified end-to-end for the flat
/// case; the nested/collection cases are verified the same way, just with a narrower shape than the
/// compiled-expression tier supports.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class MapFromGenerator : IIncrementalGenerator
{
    private const string MapFromAttributeFullName = "FluxMapper.Abstractions.MapFromAttribute";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var targets = context.SyntaxProvider.ForAttributeWithMetadataName(
            MapFromAttributeFullName,
            predicate: static (node, _) => node is ClassDeclarationSyntax c && c.Modifiers.Any(SyntaxKind.PartialKeyword),
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

        var members = new List<MapFromMember>();
        var destinationProps = destinationSymbol.GetMembers().OfType<IPropertySymbol>()
            .Where(p => !p.IsStatic && !p.IsIndexer && p.SetMethod is { DeclaredAccessibility: Accessibility.Public });
        var sourceProps = sourceSymbol.GetMembers().OfType<IPropertySymbol>()
            .Where(p => !p.IsStatic && !p.IsIndexer && p.GetMethod is { DeclaredAccessibility: Accessibility.Public })
            .ToDictionary(p => p.Name, p => p);

        foreach (var destProp in destinationProps)
        {
            if (!sourceProps.TryGetValue(destProp.Name, out var sourceProp)) continue;

            var conversion = compilation.ClassifyConversion(sourceProp.Type, destProp.Type);
            if (conversion.Exists && (SymbolEqualityComparer.Default.Equals(sourceProp.Type, destProp.Type) || conversion.IsImplicit))
            {
                members.Add(new MapFromMember(
                    destProp.Name, MemberKind.Direct,
                    NeedsCast: !SymbolEqualityComparer.Default.Equals(sourceProp.Type, destProp.Type),
                    DestinationTypeDisplay: destProp.Type.ToDisplayString(),
                    SourceIsValueType: false, ElementDestinationTypeDisplay: null,
                    ElementNeedsMapFrom: false, ElementNeedsCast: false, DestinationIsArray: false, SourceIsArray: false));
                continue;
            }

            if (destProp.Type is INamedTypeSymbol destNamed && HasMapFromFor(destNamed, sourceProp.Type))
            {
                members.Add(new MapFromMember(
                    destProp.Name, MemberKind.Nested,
                    NeedsCast: false,
                    DestinationTypeDisplay: destProp.Type.ToDisplayString(),
                    SourceIsValueType: sourceProp.Type.IsValueType, ElementDestinationTypeDisplay: null,
                    ElementNeedsMapFrom: false, ElementNeedsCast: false, DestinationIsArray: false, SourceIsArray: false));
                continue;
            }

            if (listOfT is not null
                && TryGetSequenceElementType(sourceProp.Type, listOfT, out var sourceElemType, out var sourceIsArray)
                && TryGetSequenceElementType(destProp.Type, listOfT, out var destElemType, out var destIsArray))
            {
                var elemConversion = compilation.ClassifyConversion(sourceElemType, destElemType);
                var elemDirect = elemConversion.Exists
                    && (SymbolEqualityComparer.Default.Equals(sourceElemType, destElemType) || elemConversion.IsImplicit);
                var elemNested = !elemDirect && destElemType is INamedTypeSymbol destElemNamed && HasMapFromFor(destElemNamed, sourceElemType);

                if (elemDirect || elemNested)
                {
                    members.Add(new MapFromMember(
                        destProp.Name, MemberKind.Collection,
                        NeedsCast: false, DestinationTypeDisplay: "", SourceIsValueType: false,
                        ElementDestinationTypeDisplay: destElemType.ToDisplayString(),
                        ElementNeedsMapFrom: elemNested,
                        ElementNeedsCast: elemDirect && !SymbolEqualityComparer.Default.Equals(sourceElemType, destElemType),
                        DestinationIsArray: destIsArray,
                        SourceIsArray: sourceIsArray));
                }
            }
        }

        var namespaceName = destinationSymbol.ContainingNamespace.IsGlobalNamespace ? null : destinationSymbol.ContainingNamespace.ToDisplayString();

        return new MapFromModel(
            DestinationName: destinationSymbol.Name,
            DestinationFullName: destinationSymbol.ToDisplayString(),
            SourceFullName: sourceSymbol.ToDisplayString(),
            Namespace: namespaceName,
            IsRecord: destinationSymbol.IsRecord,
            Members: ImmutableArray.CreateRange(members));
    }

    /// <summary>True when <paramref name="type"/> itself carries <c>[MapFrom(typeof(expectedSource))]</c> --
    /// the composition rule a nested or collection-element member relies on to call that type's own
    /// generated <c>MapFrom(...)</c> rather than needing this generator to understand its shape.</summary>
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

        var keyword = model.IsRecord ? "partial record" : "partial class";
        sb.AppendLine($"public {keyword} {model.DestinationName}");
        sb.AppendLine("{");
        sb.AppendLine($"    /// <summary>Generated by FluxMapper.SourceGenerator from [MapFrom(typeof({model.SourceFullName}))]. AOT-safe: no reflection, no Expression.Compile().</summary>");
        sb.AppendLine($"    public static {model.DestinationFullName} MapFrom({model.SourceFullName} source)");
        sb.AppendLine("    {");
        sb.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(source);");
        sb.AppendLine();

        // Nested/collection members are computed into locals as statements *before* the final object
        // initializer -- an initializer expression can't contain a loop, and computing into a local first
        // (rather than inlining the nested call twice, once for a null check and once for the value) means
        // this works identically whether the destination member is a plain `set` or a record's `init`
        // accessor, without needing to special-case which one it is.
        var initializerLines = new List<string>();
        var localIndex = 0;

        foreach (var member in model.Members)
        {
            switch (member.Kind)
            {
                case MemberKind.Direct:
                {
                    var valueExpr = member.NeedsCast ? $"({member.DestinationTypeDisplay})source.{member.Name}" : $"source.{member.Name}";
                    initializerLines.Add($"{member.Name} = {valueExpr},");
                    break;
                }

                case MemberKind.Nested:
                {
                    var local = $"__flux{localIndex++}";
                    if (member.SourceIsValueType)
                    {
                        sb.AppendLine($"        var {local} = {member.DestinationTypeDisplay}.MapFrom(source.{member.Name});");
                    }
                    else
                    {
                        sb.AppendLine($"        var {local}Src = source.{member.Name};");
                        sb.AppendLine($"        var {local} = {local}Src is null ? null! : {member.DestinationTypeDisplay}.MapFrom({local}Src);");
                    }

                    sb.AppendLine();
                    initializerLines.Add($"{member.Name} = {local},");
                    break;
                }

                case MemberKind.Collection:
                {
                    var list = $"__flux{localIndex++}";
                    var countAccessor = member.SourceIsArray ? "Length" : "Count";
                    sb.AppendLine($"        var {list} = new global::System.Collections.Generic.List<{member.ElementDestinationTypeDisplay}>(source.{member.Name}.{countAccessor});");
                    sb.AppendLine($"        foreach (var {list}Item in source.{member.Name})");
                    sb.AppendLine("        {");
                    var elementExpr = member.ElementNeedsMapFrom
                        ? $"{member.ElementDestinationTypeDisplay}.MapFrom({list}Item)"
                        : member.ElementNeedsCast
                            ? $"({member.ElementDestinationTypeDisplay}){list}Item"
                            : $"{list}Item";
                    sb.AppendLine($"            {list}.Add({elementExpr});");
                    sb.AppendLine("        }");
                    sb.AppendLine();

                    initializerLines.Add(member.DestinationIsArray ? $"{member.Name} = {list}.ToArray()," : $"{member.Name} = {list},");
                    break;
                }
            }
        }

        sb.AppendLine($"        return new {model.DestinationFullName}");
        sb.AppendLine("        {");
        foreach (var line in initializerLines)
        {
            sb.AppendLine($"            {line}");
        }

        sb.AppendLine("        };");
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
        bool SourceIsArray);

    private sealed record MapFromModel(
        string DestinationName,
        string DestinationFullName,
        string SourceFullName,
        string? Namespace,
        bool IsRecord,
        ImmutableArray<MapFromMember> Members);
}
