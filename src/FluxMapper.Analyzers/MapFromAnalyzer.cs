using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace FluxMapper.Analyzers;

/// <summary>
/// Analyzers run independently of
/// the source generator, so a project using <c>[MapFrom]</c> gets IDE-time feedback even before/without
/// invoking FluxMapper.SourceGenerator. Three diagnostics, all genuinely reachable from real user
/// mistakes with <c>[MapFrom]</c>:
///
/// FLUX0001 (error): the attributed class isn't declared <c>partial</c> -- the generator cannot add
/// members to it at all, so without this diagnostic the failure a user would see is a confusing "type
/// already defines a member called 'MapFrom'" or simply "nothing got generated," not a message pointing
/// at the actual cause.
///
/// FLUX0002 (warning): the attributed class has zero destination members the generator could match
/// against the declared source type (by the same exact-name, direct/implicit-conversion rule
/// <see cref="SourceGenerator.MapFromGenerator"/> itself uses) -- almost certainly a naming mismatch or
/// the wrong source type, not an intentional "generate an empty mapper."
///
/// FLUX0003 (error): the attributed type has no public parameterless constructor AND no public
/// parameterized constructor whose parameters all resolve against the source type -- the generator
/// cannot construct it at all (see <see cref="SourceGenerator.MapFromGenerator"/>'s "Destination
/// construction" scope) and will silently emit nothing, which without this diagnostic looks identical to
/// FLUX0001's symptom ("MapFrom does not exist") but has a completely different fix. Never fires for a
/// value-type destination (struct/record struct), since <c>new T()</c> is always legal C# for those
/// regardless of what other constructors are declared.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MapFromAnalyzer : DiagnosticAnalyzer
{
    private const string MapFromAttributeFullName = "FluxMapper.Abstractions.MapFromAttribute";

    public static readonly DiagnosticDescriptor NotPartialRule = new(
        id: "FLUX0001",
        title: "[MapFrom] target must be declared partial",
        messageFormat: "'{0}' is decorated with [MapFrom] but is not declared 'partial' -- FluxMapper.SourceGenerator cannot add the generated MapFrom(...) method to it",
        category: "FluxMapper.SourceGenerator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Add the 'partial' modifier to the class so the source generator can contribute the generated static factory method.");

    public static readonly DiagnosticDescriptor NoMappableMembersRule = new(
        id: "FLUX0002",
        title: "[MapFrom] found no mappable members",
        messageFormat: "'{0}' is decorated with [MapFrom(typeof({1}))] but no destination member has a same-named, type-compatible source member -- the generated MapFrom(...) would construct an all-defaults instance",
        category: "FluxMapper.SourceGenerator",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Check for a naming mismatch between source and destination members, or confirm the source type argument is correct.");

    public static readonly DiagnosticDescriptor NoUsableConstructorRule = new(
        id: "FLUX0003",
        title: "[MapFrom] target has no usable constructor",
        messageFormat: "'{0}' is decorated with [MapFrom(typeof({1}))] but has no public parameterless constructor, and no public parameterized constructor whose parameters all resolve (by exact name and identity/implicit conversion, a same-named nested [MapFrom] member, or a List<T>/array collection member) against '{1}' -- FluxMapper.SourceGenerator cannot construct it and will not generate a MapFrom(...) method",
        category: "FluxMapper.SourceGenerator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Add a public parameterless constructor, rename/retype constructor parameters to match the source, or give an unresolvable parameter a default value so it can be omitted.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(NotPartialRule, NoMappableMembersRule, NoUsableConstructorRule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(AnalyzeNamedType, SymbolKind.NamedType);
    }

    private static void AnalyzeNamedType(SymbolAnalysisContext context)
    {
        if (context.Symbol is not INamedTypeSymbol type) return;

        var mapFromAttribute = type.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == MapFromAttributeFullName);
        if (mapFromAttribute is null) return;

        // TypeDeclarationSyntax (not ClassDeclarationSyntax) so this also recognizes a `partial record`/
        // `partial record struct` destination -- a plain `record` declaration parses as the sibling node
        // RecordDeclarationSyntax, never as ClassDeclarationSyntax (see MapFromGenerator's type doc
        // comment for the matching fix on the generator side). Checked across every declaring syntax
        // reference, not just the first, since a partial type's modifier can legally live on any one of
        // its parts.
        var isPartial = type.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax())
            .OfType<TypeDeclarationSyntax>()
            .Any(t => t.Modifiers.Any(SyntaxKind.PartialKeyword));

        if (!isPartial)
        {
            context.ReportDiagnostic(Diagnostic.Create(NotPartialRule, type.Locations.FirstOrDefault() ?? Location.None, type.Name));
            return; // FLUX0002/FLUX0003 would be noise on a type the generator can't touch at all.
        }

        if (mapFromAttribute.ConstructorArguments.Length != 1 || mapFromAttribute.ConstructorArguments[0].Value is not INamedTypeSymbol sourceType)
            return;

        var compilation = context.Compilation;
        var destinationProps = type.GetMembers().OfType<IPropertySymbol>()
            .Where(p => !p.IsStatic && !p.IsIndexer && p.SetMethod is { DeclaredAccessibility: Accessibility.Public });
        var sourceProps = sourceType.GetMembers().OfType<IPropertySymbol>()
            .Where(p => !p.IsStatic && !p.IsIndexer && p.GetMethod is { DeclaredAccessibility: Accessibility.Public })
            .ToDictionary(p => p.Name, p => p);

        if (!type.IsValueType)
        {
            var publicCtors = type.Constructors.Where(c => c.DeclaredAccessibility == Accessibility.Public).ToImmutableArray();
            var hasParameterless = publicCtors.Any(c => c.Parameters.Length == 0);

            if (!hasParameterless)
            {
                var hasUsableParameterizedCtor = publicCtors
                    .Where(c => c.Parameters.Length > 0)
                    .Where(c => !(c.Parameters.Length == 1 && SymbolEqualityComparer.Default.Equals(c.Parameters[0].Type, type)))
                    .Any(ctor => ctor.Parameters.All(p =>
                        p.HasExplicitDefaultValue
                        || (sourceProps.TryGetValue(p.Name, out var sourceProp) && IsResolvable(compilation, p.Type, sourceProp.Type))));

                if (!hasUsableParameterizedCtor)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        NoUsableConstructorRule, type.Locations.FirstOrDefault() ?? Location.None, type.Name, sourceType.Name));
                    return; // FLUX0002 would be redundant/misleading noise on a type that can't be constructed at all.
                }
            }
        }

        var anyMatch = destinationProps.Any(destProp =>
            sourceProps.TryGetValue(destProp.Name, out var sourceProp) && IsResolvable(compilation, destProp.Type, sourceProp.Type));

        if (!anyMatch)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                NoMappableMembersRule, type.Locations.FirstOrDefault() ?? Location.None, type.Name, sourceType.Name));
        }
    }

    /// <summary>
    /// True when a member/parameter typed <paramref name="destType"/> can be populated from a same-named
    /// source member typed <paramref name="sourceType"/> -- mirrors
    /// <see cref="SourceGenerator.MapFromGenerator"/>'s own Direct/Nested/Collection resolution rules
    /// closely enough to predict whether the generator will actually succeed, without needing to share
    /// code across the two separate compiler-extension projects (an analyzer and a source generator ship
    /// as different NuGet assets and can't reference each other's internals).
    /// </summary>
    private static bool IsResolvable(Compilation compilation, ITypeSymbol destType, ITypeSymbol sourceType)
    {
        var conversion = compilation.ClassifyConversion(sourceType, destType);
        if (conversion.Exists && (SymbolEqualityComparer.Default.Equals(sourceType, destType) || conversion.IsImplicit))
        {
            return true;
        }

        if (destType is INamedTypeSymbol destNamed && HasMapFromFor(destNamed, sourceType))
        {
            return true;
        }

        var listOfT = compilation.GetTypeByMetadataName("System.Collections.Generic.List`1");
        if (listOfT is not null
            && TryGetSequenceElementType(sourceType, listOfT, out var sourceElemType, out _)
            && TryGetSequenceElementType(destType, listOfT, out var destElemType, out _))
        {
            var elemConversion = compilation.ClassifyConversion(sourceElemType, destElemType);
            var elemDirect = elemConversion.Exists
                && (SymbolEqualityComparer.Default.Equals(sourceElemType, destElemType) || elemConversion.IsImplicit);
            if (elemDirect) return true;
            if (destElemType is INamedTypeSymbol destElemNamed && HasMapFromFor(destElemNamed, sourceElemType)) return true;
        }

        return false;
    }

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
}
