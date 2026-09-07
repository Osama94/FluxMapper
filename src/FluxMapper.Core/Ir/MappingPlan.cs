using FluxMapper.Abstractions;

namespace FluxMapper.Core.Ir;

/// <summary>
/// The central artifact of the whole architecture.
/// Every consumer — the compiled-expression executor, AssertConfigurationIsValid, Explain() — reads this
/// same immutable record; none of them derive mapping decisions independently.
/// </summary>
public sealed record MappingPlan(
    Type SourceType,
    Type DestinationType,
    MappingKind Kind,
    ConstructorPlan ConstructorPlan,
    IReadOnlyList<MemberPlan> MemberPlans,
    ReferencePlan ReferencePlan,
    ExecutionEligibility ExecutionEligibility,
    IReadOnlyList<PlanDiagnostic> Diagnostics,
    CollectionPlan? CollectionPlan = null,
    DictionaryPlan? DictionaryPlan = null,
    PolymorphismPlan? PolymorphismPlan = null,
    Action<object, object>? BeforeMap = null,
    Action<object, object>? AfterMap = null)
{
    // ProjectionPlan is implemented
    // separately in FluxMapper.Core/Projection rather than as a field on this record -- projection is
    // a distinct compiler stage over the same mappingPlan, not extra state carried on the plan itself.
    // DictionaryPlan and
    // polymorphismPlan are populated below.

    /// <summary>
    /// True only if the plan (and, recursively, every nested/collection-element plan) contains no
    /// unresolved member and no Error-severity diagnostic. Only a buildable plan may be handed to an
    /// execution tier — see MappingPlanBuilder and CompiledMapperFactory.
    /// </summary>
    public bool IsBuildable =>
        Diagnostics.All(d => d.Severity != DiagnosticSeverity.Error)
        && MemberPlans.Where(m => m.Strategy is not (MemberStrategy.Ignored or MemberStrategy.PathMapping))
                      .All(m => m.Source is not ResolvedSource.Unresolved);

    /// <summary>
    /// Every member must be projection-safe. A
    /// root-level dictionary-to-dictionary map (<see cref="DictionaryPlan"/> populated, no
    /// <see cref="MemberPlans"/> to check) is never projection-safe for the same reason a dictionary-typed
    /// *member* isn't (see <see cref="MemberPlan.IsProjectionSafe"/>) -- <c>ToDictionary()</c> does not
    /// translate over <c>IQueryable</c>. <see cref="BeforeMap"/>/<see cref="AfterMap"/> are plain delegates
    /// invoked against a materialized instance -- there is nothing to splice into an <c>IQueryable</c>
    /// expression tree, so their presence disqualifies the plan from projection the same way an unresolved
    /// member does.
    /// </summary>
    public bool IsProjectionSafe =>
        DictionaryPlan is null && BeforeMap is null && AfterMap is null && MemberPlans.All(m => m.IsProjectionSafe);
}
