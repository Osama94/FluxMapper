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
/// against the declared source type -- almost certainly a naming mismatch or the wrong source type, not
/// an intentional "generate an empty mapper."
///
/// FLUX0003 (error): the attributed type has no public parameterless constructor AND no public
/// parameterized constructor whose parameters all resolve against the source type -- the generator
/// cannot construct it at all and will silently emit nothing. Never fires for a value-type destination
/// (struct/record struct), since <c>new T()</c> is always legal C# for those regardless of what other
/// constructors are declared.
///
/// Both FLUX0002 and FLUX0003's resolvability check (<see cref="IsResolvable"/>) mirrors
/// <see cref="SourceGenerator.MapFromGenerator"/>'s own Converter/Direct/Nested/Dictionary/Collection/
/// Flattened rules closely enough to predict whether the generator will actually succeed, including a
/// registered <c>[assembly: MapFromConverter(...)]</c> and the <c>NamingConvention</c> relaxation on
/// <c>[MapFrom]</c> itself -- without sharing code across the two separate compiler-extension projects
/// (an analyzer and a source generator ship as different NuGet assets and can't reference each other's
/// internals). This mirror is deliberately biased toward NOT firing when uncertain: a false "looks fine"
/// is a missed diagnostic, but a false "this is broken" would contradict what the generator just
/// successfully built, which is a worse experience than saying nothing.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MapFromAnalyzer : DiagnosticAnalyzer
{
    private const string MapFromAttributeFullName = "FluxMapper.Abstractions.MapFromAttribute";
    private const string MapFromConverterAttributeFullName = "FluxMapper.Abstractions.MapFromConverterAttribute";

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
        messageFormat: "'{0}' is decorated with [MapFrom(typeof({1}))] but has no public parameterless constructor, and no public parameterized constructor whose parameters all resolve against '{1}' -- FluxMapper.SourceGenerator cannot construct it and will not generate a MapFrom(...) method",
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
        // `partial record struct`/`partial struct` destination -- a `record`/`struct` declaration parses
        // as the sibling nodes RecordDeclarationSyntax/StructDeclarationSyntax, never as
        // ClassDeclarationSyntax (see MapFromGenerator's type doc comment for the matching fix on the
        // generator side). Checked across every declaring syntax reference, not just the first, since a
        // partial type's modifier can legally live on any one of its parts.
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

        var namingArg = mapFromAttribute.NamedArguments.FirstOrDefault(kv => kv.Key == "NamingConvention");
        var useSnakeCase = namingArg.Value.Value is int namingValue && namingValue == 1;

        var converters = CollectConverters(compilation);

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
                        || IsMemberResolvable(compilation, sourceProps, converters, p.Name, p.Type, useSnakeCase)));

                if (!hasUsableParameterizedCtor)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        NoUsableConstructorRule, type.Locations.FirstOrDefault() ?? Location.None, type.Name, sourceType.Name));
                    return; // FLUX0002 would be redundant/misleading noise on a type that can't be constructed at all.
                }
            }
        }

        var anyMatch = destinationProps.Any(destProp => IsMemberResolvable(compilation, sourceProps, converters, destProp.Name, destProp.Type, useSnakeCase));

        if (!anyMatch)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                NoMappableMembersRule, type.Locations.FirstOrDefault() ?? Location.None, type.Name, sourceType.Name));
        }
    }

    /// <summary>
    /// True when <paramref name="name"/> (a destination property or constructor parameter name) would
    /// resolve against <paramref name="sourceProps"/> under <see cref="SourceGenerator.MapFromGenerator"/>'s
    /// rules -- an ordinary (possibly naming-convention-relaxed) match first, one-level flattening as a
    /// fallback when no ordinary source member exists under that name at all.
    /// </summary>
    private static bool IsMemberResolvable(
        Compilation compilation,
        Dictionary<string, IPropertySymbol> sourceProps,
        ImmutableArray<(ITypeSymbol Source, ITypeSymbol Destination, INamedTypeSymbol Converter)> converters,
        string name, ITypeSymbol destType, bool useSnakeCase)
    {
        if (TryResolveSourceMember(sourceProps, name, useSnakeCase, out var sourceProp)
            && IsResolvable(compilation, converters, destType, sourceProp.Type))
        {
            return true;
        }

        return IsFlattenable(compilation, sourceProps, name, destType, useSnakeCase);
    }

    /// <summary>
    /// True when a member/parameter typed <paramref name="destType"/> can be populated from a source
    /// member typed <paramref name="sourceType"/> that has already been matched by name -- Converter,
    /// Direct, Nested, Dictionary, or Collection, mirroring
    /// <see cref="SourceGenerator.MapFromGenerator"/>'s <c>TryClassifyMember</c>.
    /// </summary>
    private static bool IsResolvable(
        Compilation compilation,
        ImmutableArray<(ITypeSymbol Source, ITypeSymbol Destination, INamedTypeSymbol Converter)> converters,
        ITypeSymbol destType, ITypeSymbol sourceType)
    {
        if (converters.Any(c => SymbolEqualityComparer.Default.Equals(c.Source, sourceType)
                && SymbolEqualityComparer.Default.Equals(c.Destination, destType)
                && c.Converter.InstanceConstructors.Any(ctor => ctor.Parameters.Length == 0 && ctor.DeclaredAccessibility == Accessibility.Public)))
        {
            return true;
        }

        var conversion = compilation.ClassifyConversion(sourceType, destType);
        if (conversion.Exists && (SymbolEqualityComparer.Default.Equals(sourceType, destType) || conversion.IsImplicit))
        {
            return true;
        }

        if (destType is INamedTypeSymbol destNamed && HasMapFromFor(destNamed, sourceType))
        {
            return true;
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
                if (valueDirect) return true;
                if (destValueType is INamedTypeSymbol destValueNamed && HasMapFromFor(destValueNamed, sourceValueType)) return true;
            }
        }

        if (TryGetSequenceElementType(sourceType, out var sourceElemType)
            && TryGetSequenceElementType(destType, out var destElemType))
        {
            var elemConversion = compilation.ClassifyConversion(sourceElemType, destElemType);
            var elemDirect = elemConversion.Exists
                && (SymbolEqualityComparer.Default.Equals(sourceElemType, destElemType) || elemConversion.IsImplicit);
            if (elemDirect) return true;
            if (destElemType is INamedTypeSymbol destElemNamed && HasMapFromFor(destElemNamed, sourceElemType)) return true;
        }

        return false;
    }

    /// <summary>One-level flattening -- see <see cref="SourceGenerator.MapFromGenerator"/>'s
    /// <c>TryClassifyFlattenedMember</c> for the exact rule this mirrors.</summary>
    private static bool IsFlattenable(
        Compilation compilation, Dictionary<string, IPropertySymbol> sourceProps, string destName, ITypeSymbol destType, bool useSnakeCase)
    {
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
            if (conversion.Exists && (SymbolEqualityComparer.Default.Equals(leafProp.Type, destType) || conversion.IsImplicit))
            {
                return true;
            }
        }

        return false;
    }

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
            if (match is not null) { sourceProp = null!; return false; }
            match = candidate;
        }

        sourceProp = match!;
        return match is not null;
    }

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

    /// <summary>
    /// Yes/no version of the generator's source/destination sequence-shape checks: does not need to
    /// distinguish indexed-vs-foreach reads or array-vs-list-vs-hashset materialization the way the
    /// generator's codegen does, only whether SOME recognized sequence shape exists on both sides.
    /// </summary>
    private static bool TryGetSequenceElementType(ITypeSymbol type, out ITypeSymbol elementType)
    {
        if (type is IArrayTypeSymbol { Rank: 1 } arrayType)
        {
            elementType = arrayType.ElementType;
            return true;
        }

        if (type is INamedTypeSymbol { IsGenericType: true } named && named.TypeArguments.Length == 1
            && named.OriginalDefinition.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic"
            && named.OriginalDefinition.Name is "List" or "IList" or "IReadOnlyList" or "ICollection" or "IReadOnlyCollection" or "IEnumerable" or "HashSet" or "ISet")
        {
            elementType = named.TypeArguments[0];
            return true;
        }

        elementType = null!;
        return false;
    }
}
