using System.Diagnostics.CodeAnalysis;

namespace FluxMapper.Abstractions;

/// <summary>
/// Resolves and executes mappings between types. Obtained from a built <c>MapperConfiguration</c>
/// (see FluxMapper.Core) — never a global static.
///
/// Every <c>Map</c> overload here is annotated <see cref="RequiresDynamicCodeAttribute"/>
/// and <see cref="RequiresUnreferencedCodeAttribute"/> — accurately, not defensively — because
/// <c>FluxMapper.Core.Execution.Mapper</c> (the only implementation of this interface) always goes
/// through the compiled-expression tier (<c>System.Linq.Expressions.Expression.Compile()</c>, JIT-
/// dependent, and reflection over the mapped types' members, trimming-sensitive). A trimmed or Native
/// AOT published application calling through <see cref="IMapper"/> will get a build-time warning here
/// rather than a confusing runtime failure. This is exactly why the source generator exists as a
/// separate calling convention (a generated <c>static TDestination MapFrom(TSource source)</c> method,
/// never routed through <see cref="IMapper"/> at all) — that path carries neither attribute, because it
/// genuinely needs neither.
/// </summary>
public interface IMapper
{
    /// <summary>Mode A: create a new <typeparamref name="TDestination"/> from <paramref name="source"/>.</summary>
    [RequiresDynamicCode("Mode A/B mapping compiles System.Linq.Expressions trees via Expression.Compile(), which requires a JIT and is not supported when publishing Native AOT. Use the FluxMapper.SourceGenerator [MapFrom] path for an AOT-safe alternative.")]
    [RequiresUnreferencedCode("Mode A/B mapping discovers mapped members via reflection over the source/destination types, which trimming can remove. Use the FluxMapper.SourceGenerator [MapFrom] path for a trim-safe alternative.")]
    TDestination Map<TDestination>(object source);

    /// <summary>Same as <see cref="Map{TDestination}"/> but with both types pinned explicitly at the call site.</summary>
    [RequiresDynamicCode("Mode A/B mapping compiles System.Linq.Expressions trees via Expression.Compile(), which requires a JIT and is not supported when publishing Native AOT. Use the FluxMapper.SourceGenerator [MapFrom] path for an AOT-safe alternative.")]
    [RequiresUnreferencedCode("Mode A/B mapping discovers mapped members via reflection over the source/destination types, which trimming can remove. Use the FluxMapper.SourceGenerator [MapFrom] path for a trim-safe alternative.")]
    TDestination Map<TSource, TDestination>(TSource source);

    /// <summary>
    /// Update-in-place / patch mapping: maps <paramref name="source"/> onto the
    /// already-constructed <paramref name="destination"/> instead of creating a new instance.
    /// </summary>
    [RequiresDynamicCode("Update-in-place mapping compiles System.Linq.Expressions trees via Expression.Compile(), which requires a JIT and is not supported when publishing Native AOT.")]
    [RequiresUnreferencedCode("Update-in-place mapping discovers mapped members via reflection over the source/destination types, which trimming can remove.")]
    TDestination Map<TSource, TDestination>(TSource source, TDestination destination);

    /// <summary>
    /// Produces a human-readable explanation of how <typeparamref name="TSource"/> maps to
    /// <typeparamref name="TDestination"/> — a formatter over the underlying MappingPlan.
    /// </summary>
    string Explain<TSource, TDestination>();
}
