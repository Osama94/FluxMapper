namespace FluxMapper.Abstractions;

/// <summary>
/// Per-call options for a single <see cref="IMapper.Map{TSource,TDestination}(TSource,System.Action{IMappingOperationOptions{TSource,TDestination}})"/>
/// invocation, mirroring AutoMapper's <c>mapper.Map(source, opt => opt.AfterMap(...))</c> pattern for a
/// one-off hook that shouldn't apply to every mapping of this (source, destination) pair the way
/// <c>IMappingExpression{TSource,TDestination}.AfterMap</c> (configured once, at <c>CreateMap</c> time)
/// does. There is deliberately no per-call <c>BeforeMap</c>: by the time a caller has a constructed
/// <typeparamref name="TDestination"/> to act on, mapping has already finished, so a per-call "before"
/// hook has nothing distinct to run against — configure <c>.BeforeMap()</c> on the map itself instead when
/// the hook needs to see the destination before its members are populated.
/// </summary>
public interface IMappingOperationOptions<TSource, TDestination>
{
    /// <summary>Runs after the mapping completes (after any plan-level <c>AfterMap</c> configured on the map itself), against the finished <typeparamref name="TDestination"/>.</summary>
    void AfterMap(Action<TSource, TDestination> afterMap);

    /// <summary>
    /// Arbitrary per-call state, mirroring AutoMapper's <c>opt.Items["key"] = value</c> pattern. Populate
    /// this before returning from the configuration callback; every resolver and contextual
    /// <c>.MapFrom((src, dest, current, context) =&gt; ...)</c> reached anywhere in this one call's object
    /// graph (including nested members) can then read it back via <see cref="ResolutionContext.Items"/> on
    /// the <see cref="ResolutionContext"/> it's given. Empty by default -- populating it is what makes
    /// FluxMapper actually create and thread a <see cref="ResolutionContext"/> for this call; leaving it
    /// empty costs nothing extra over a plain <c>Map</c> call.
    /// </summary>
    IDictionary<string, object?> Items { get; }
}
