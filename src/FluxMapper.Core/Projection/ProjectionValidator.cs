using FluxMapper.Abstractions;
using FluxMapper.Core.Ir;

namespace FluxMapper.Core.Projection;

/// <summary>
/// Checks a <see cref="MappingPlan"/>'s
/// <see cref="MappingPlan.IsProjectionSafe"/> (and every reachable nested/collection-element plan's)
/// *before* <see cref="ProjectionExpressionBuilder"/> ever runs, so an untranslatable member produces a
/// specific, actionable diagnostic naming the offending member -- never a confusing provider-level
/// exception surfacing from deep inside EF Core's (or any other <c>IQueryable</c> provider's) query
/// translator, and never a silently-wrong query that happens to compile.
/// </summary>
public static class ProjectionValidator
{
    /// <summary>Throws <see cref="ProjectionTranslationException"/> naming every non-projection-safe member if <paramref name="plan"/> (or anything it recursively depends on) is not projection-safe.</summary>
    public static void EnsureProjectable(MappingPlan plan)
    {
        var offenders = new List<string>();
        CollectOffenders(plan, plan.SourceType.Name, offenders, new HashSet<MappingPlan>(ReferenceEqualityComparer.Instance));

        if (offenders.Count == 0) return;

        var diagnostic = PlanDiagnostic.Create(
            code: "MAP4001",
            severity: DiagnosticSeverity.Error,
            sourcePath: plan.SourceType.Name,
            destinationPath: plan.DestinationType.Name,
            reason: $"{plan.SourceType.Name} -> {plan.DestinationType.Name} is not safe to project over IQueryable " +
                    " -- one or more members use a runtime-only construct that cannot be translated " +
                    "by a query provider.",
            candidates: offenders,
            suggestedFixes:
            [
                "Remove the offending member from the projected shape, or map it after materializing " +
                "(e.g. .ToList() first, then a normal in-memory Map()/mapper.Map()).",
                "Replace a runtime IValueResolver<> with an IProjectionValueResolver<> and .ProjectUsing<>() " +
                "if the value can be expressed as a translatable expression.",
            ]);

        throw new ProjectionTranslationException(diagnostic);
    }

    private static void CollectOffenders(MappingPlan plan, string path, List<string> offenders, HashSet<MappingPlan> visited)
    {
        if (!visited.Add(plan)) return;

        if (plan.DictionaryPlan is not null)
            offenders.Add($"{path}: dictionary-valued (ToDictionary() does not translate over IQueryable)");

        if (plan.PolymorphismPlan is not null && plan.PolymorphismPlan.Cases.Count > 0)
            offenders.Add($"{path}: polymorphic dispatch (GetType() runtime-type checks do not translate over IQueryable)");

        foreach (var member in plan.MemberPlans)
        {
            if (member.Strategy is MemberStrategy.Ignored or MemberStrategy.ConstructorArgument) continue;

            if (!member.IsProjectionSafe)
            {
                offenders.Add($"{path}.{member.DestinationMember.Name}: {DescribeWhyUnsafe(member)}");
                continue;
            }

            if (member.NestedPlan is not null) CollectOffenders(member.NestedPlan, $"{path}.{member.DestinationMember.Name}", offenders, visited);
            if (member.CollectionPlan?.ElementPlan is not null) CollectOffenders(member.CollectionPlan.ElementPlan, $"{path}.{member.DestinationMember.Name}[]", offenders, visited);
        }
    }

    private static string DescribeWhyUnsafe(Ir.MemberPlan member) => member.Source switch
    {
        ResolvedSource.ValueResolver => "uses a runtime-only IValueResolver<> (use IProjectionValueResolver<> + .ProjectUsing<>() instead)",
        ResolvedSource.ValueConverter => "uses a runtime-only IValueConverter<>",
        ResolvedSource.ConstantOrDefault => "resolves to a constant/default computed outside the query",
        ResolvedSource.Unresolved => "has no resolved source",
        _ when member.Condition is not null => ".Condition() cannot be translated (it depends on custom per-instance C# logic)",
        _ when member.Strategy == MemberStrategy.DictionaryMapping => "dictionary-valued (ToDictionary() does not translate over IQueryable)",
        _ => "not translatable",
    };
}
