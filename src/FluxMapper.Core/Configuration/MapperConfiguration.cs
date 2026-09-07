using System.Collections.Concurrent;
using FluxMapper.Abstractions;
using FluxMapper.Core.Building;
using FluxMapper.Core.Ir;

namespace FluxMapper.Core.Configuration;

/// <summary>
/// The built, queryable configuration. Immutable after
/// construction — <see cref="Create"/> is the only place a
/// <see cref="MapperConfigurationExpression"/> is mutated; every plan is built (and cached) from that
/// frozen snapshot.
/// </summary>
public sealed class MapperConfiguration
{
    private readonly MapperConfigurationExpression _expression;
    private readonly IMappingPlanBuilder _planBuilder;
    private readonly ConcurrentDictionary<(Type Source, Type Destination), MappingPlan> _planCache = new();

    private MapperConfiguration(MapperConfigurationExpression expression)
    {
        _expression = expression;
        _planBuilder = new MappingPlanBuilder(expression);
        ValidateReverseMaps();
    }

    public static MapperConfiguration Create(Action<MapperConfigurationExpression> configure)
    {
        var expression = new MapperConfigurationExpression();
        configure(expression);
        return new MapperConfiguration(expression);
    }

    public MappingPlan GetPlan(Type sourceType, Type destinationType)
        => _planCache.GetOrAdd((sourceType, destinationType), key => _planBuilder.Build(key.Source, key.Destination));

    public MappingPlan GetPlan<TSource, TDestination>() => GetPlan(typeof(TSource), typeof(TDestination));

    /// <summary>
    /// Builds an <see cref="IMapper"/> bound to this configuration,
    /// optionally DI-aware. This is what a manual (non-DI) caller uses; FluxMapper.Extensions.DependencyInjection's
    /// <c>AddFluxMapper</c> calls this same method internally, passing the container's <see cref="IServiceProvider"/>,
    /// so both paths share identical behavior.
    /// </summary>
    public IMapper CreateMapper(IServiceProvider? services = null) => new Execution.Mapper(this, services);

    /// <summary>
    /// Walks every registered map's plan (recursively through nested/collection
    /// sub-plans) and throws an aggregate <see cref="ConfigurationValidationException"/> if any
    /// error-severity diagnostic exists anywhere in the graph. Meant to be called at startup/in tests.
    /// </summary>
    public void AssertConfigurationIsValid()
    {
        var allDiagnostics = new List<PlanDiagnostic>();
        var visited = new HashSet<MappingPlan>(ReferenceEqualityComparer.Instance);

        foreach (var typeMap in _expression.RegisteredMaps)
        {
            var plan = GetPlan(typeMap.SourceType, typeMap.DestinationType);
            CollectDiagnostics(plan, visited, allDiagnostics);
        }

        var errors = allDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (errors.Count > 0)
            throw new ConfigurationValidationException(errors);
    }

    private static void CollectDiagnostics(MappingPlan plan, HashSet<MappingPlan> visited, List<PlanDiagnostic> into)
    {
        if (!visited.Add(plan)) return;

        into.AddRange(plan.Diagnostics);

        foreach (var member in plan.MemberPlans)
        {
            if (member.NestedPlan is not null) CollectDiagnostics(member.NestedPlan, visited, into);
            if (member.CollectionPlan?.ElementPlan is not null) CollectDiagnostics(member.CollectionPlan.ElementPlan, visited, into);
        }

        if (plan.CollectionPlan?.ElementPlan is not null) CollectDiagnostics(plan.CollectionPlan.ElementPlan, visited, into);
    }

    /// <summary>
    /// ReverseMap() is a validated convenience, not a blind one. This is a
    /// deliberately conservative version of the check rather than full per-member reconstructability
    /// analysis (a real but larger undertaking): if the forward map has
    /// any Flattening/CustomResolver/CustomConverter member, the reverse map must carry at least one
    /// explicit <c>.Map()</c> override of its own -- evidence the author is aware of and has addressed
    /// the irreversible member(s) -- or configuration validation fails with MAP0003, naming every
    /// member that made the map irreversible.
    /// </summary>
    private void ValidateReverseMaps()
    {
        foreach (var config in _expression.RegisteredMaps.ToList())
        {
            if (!config.ReverseMapRequested) continue;

            var reverseConfig = _expression.Get(config.DestinationType, config.SourceType);
            if (reverseConfig is null) continue;

            var forwardPlan = GetPlan(config.SourceType, config.DestinationType);
            var irreversible = forwardPlan.MemberPlans
                .Where(m => m.Strategy is MemberStrategy.Flattening or MemberStrategy.CustomResolver or MemberStrategy.CustomConverter)
                .ToList();

            if (irreversible.Count == 0 || reverseConfig.ExplicitSources.Count > 0)
                continue;

            var diagnostic = PlanDiagnostic.Create(
                code: "MAP0003",
                severity: DiagnosticSeverity.Error,
                sourcePath: config.SourceType.Name,
                destinationPath: config.DestinationType.Name,
                reason: "ReverseMap() was requested, but the forward map uses flattening, a custom resolver, " +
                        "or a custom converter on member(s) listed below, and the reverse map has no explicit " +
                        "override compensating for them -- reconstruction is not well-defined.",
                candidates: irreversible.Select(m => $"{config.SourceType.Name} -> {config.DestinationType.Name}.{m.DestinationMember.Name} ({m.Strategy})").ToList(),
                suggestedFixes:
                [
                    $"cfg.CreateMap<{config.DestinationType.Name}, {config.SourceType.Name}>()" +
                    $".Map(d => d./* original member */, s => s./* explicit reverse path */)",
                ]);

            var reversePlan = GetPlan(config.DestinationType, config.SourceType);
            _planCache[(config.DestinationType, config.SourceType)] = reversePlan with { Diagnostics = [.. reversePlan.Diagnostics, diagnostic] };
        }
    }
}
