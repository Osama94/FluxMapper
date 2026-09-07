using System.Reflection;
using FluxMapper.Abstractions;

namespace FluxMapper.Core.Ir;

/// <summary>One resolved destination member.</summary>
public sealed record MemberPlan(
    MemberInfo DestinationMember,
    ResolvedSource Source,
    MemberStrategy Strategy,
    NullPolicy NullPolicy,
    IReadOnlyList<CandidateSource> Candidates,
    Func<object, bool>? Condition = null,
    object? NullSubstitute = null,
    MappingPlan? NestedPlan = null,
    CollectionPlan? CollectionPlan = null,
    DictionaryPlan? DictionaryPlan = null)
{
    /// <summary>
    /// Computed, never user-set: true only if this
    /// member's source is provably translatable inside an IQueryable projection. The
    /// projection compiler reads this. Dictionary mapping is
    /// deliberately excluded (Strategy != DictionaryMapping check below) -- <c>ToDictionary()</c> over an
    /// <c>IQueryable</c> is not translatable by EF Core's query provider as of this writing, so a
    /// dictionary-valued member is never projection-safe, matching the "prove it, don't assume it"
    /// posture of this flag.
    /// </summary>
    public bool IsProjectionSafe => Strategy != MemberStrategy.DictionaryMapping
        && Source is ResolvedSource.MemberChain or ResolvedSource.InlineExpression or ResolvedSource.ProjectionResolver
        && Condition is null
        && (NestedPlan is null || NestedPlan.IsProjectionSafe)
        && (CollectionPlan is null || CollectionPlan.ElementPlan is null || CollectionPlan.ElementPlan.IsProjectionSafe);
}
