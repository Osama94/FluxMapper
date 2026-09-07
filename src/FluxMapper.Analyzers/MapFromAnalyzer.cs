using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace FluxMapper.Analyzers;

/// <summary>
/// Analyzers run independently of
/// the source generator, so a project using <c>[MapFrom]</c> gets IDE-time feedback even before/without
/// invoking FluxMapper.SourceGenerator. Two diagnostics, both genuinely reachable from real user
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

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(NotPartialRule, NoMappableMembersRule);

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

        var declaringSyntax = type.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() as ClassDeclarationSyntax;
        var isPartial = declaringSyntax?.Modifiers.Any(SyntaxKind.PartialKeyword) ?? type.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax()).OfType<ClassDeclarationSyntax>().Any(c => c.Modifiers.Any(SyntaxKind.PartialKeyword));

        if (!isPartial)
        {
            context.ReportDiagnostic(Diagnostic.Create(NotPartialRule, type.Locations.FirstOrDefault() ?? Location.None, type.Name));
            return; // FLUX0002 would be noise on a type the generator can't touch at all.
        }

        if (mapFromAttribute.ConstructorArguments.Length != 1 || mapFromAttribute.ConstructorArguments[0].Value is not INamedTypeSymbol sourceType)
            return;

        var destinationProps = type.GetMembers().OfType<IPropertySymbol>()
            .Where(p => !p.IsStatic && !p.IsIndexer && p.SetMethod is { DeclaredAccessibility: Accessibility.Public });
        var sourceProps = sourceType.GetMembers().OfType<IPropertySymbol>()
            .Where(p => !p.IsStatic && !p.IsIndexer && p.GetMethod is { DeclaredAccessibility: Accessibility.Public })
            .ToDictionary(p => p.Name, p => p);

        var compilation = context.Compilation;
        var anyMatch = destinationProps.Any(destProp =>
            sourceProps.TryGetValue(destProp.Name, out var sourceProp) &&
            compilation.ClassifyConversion(sourceProp.Type, destProp.Type) is { Exists: true } conv &&
            (SymbolEqualityComparer.Default.Equals(sourceProp.Type, destProp.Type) || conv.IsImplicit));

        if (!anyMatch)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                NoMappableMembersRule, type.Locations.FirstOrDefault() ?? Location.None, type.Name, sourceType.Name));
        }
    }
}
