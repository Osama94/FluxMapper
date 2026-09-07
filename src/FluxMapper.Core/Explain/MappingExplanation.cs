using System.Text;
using FluxMapper.Core.Ir;

namespace FluxMapper.Core.Explain;

/// <summary>
/// <c>mapper.Explain&lt;TSource,TDestination&gt;()</c>. A pure formatter over an
/// already-built <see cref="MappingPlan"/> — it never re-derives mapping decisions, only
/// renders the ones the plan builder already made, matching the brief's own example output shape.
/// </summary>
public static class MappingExplanation
{
    public static string Format(MappingPlan plan)
    {
        var sb = new StringBuilder();
        sb.Append("Mapping ").Append(plan.SourceType.Name).Append(" -> ").Append(plan.DestinationType.Name).AppendLine();
        sb.AppendLine();

        if (plan.CollectionPlan is not null)
        {
            sb.Append("Collection mapping: ").Append(plan.CollectionPlan.SourceElementType.Name)
              .Append(" -> ").Append(plan.CollectionPlan.ElementType.Name)
              .Append(" (").Append(plan.CollectionPlan.TargetKind).AppendLine(")");
            if (plan.CollectionPlan.HasCapacityHint)
                sb.AppendLine("    Capacity preallocated from source Count/Length.");
            return sb.ToString();
        }

        foreach (var member in plan.MemberPlans)
        {
            sb.Append(member.DestinationMember.Name).AppendLine(":");
            sb.Append("    ").Append(DescribeSource(member)).AppendLine();
            sb.Append("    Strategy: ").Append(member.Strategy).AppendLine();
            if (member.NullPolicy != FluxMapper.Abstractions.NullPolicy.Map)
                sb.Append("    NullPolicy: ").Append(member.NullPolicy).AppendLine();
            if (member.Condition is not null)
                sb.AppendLine("    Condition: configured");

            if (member.NestedPlan is not null)
            {
                foreach (var line in Format(member.NestedPlan).Split('\n'))
                    if (line.Trim().Length > 0) sb.Append("        ").AppendLine(line.TrimEnd('\r'));
            }
        }

        if (plan.Diagnostics.Count > 0)
        {
            sb.AppendLine().AppendLine("Diagnostics:");
            foreach (var d in plan.Diagnostics)
                sb.Append("  ").AppendLine(d.ToString().Replace("\n", "\n  "));
        }

        return sb.ToString();
    }

    private static string DescribeSource(MemberPlan member) => member.Source switch
    {
        ResolvedSource.MemberChain mc => $"{member.DestinationMember.DeclaringType?.Name}.{mc.PathText}",
        ResolvedSource.MethodCall m => $"{m.Method.DeclaringType?.Name}.{m.Method.Name}()",
        ResolvedSource.ValueResolver r => $"resolver: {r.ResolverType.Name}",
        ResolvedSource.ValueConverter c => $"converter: {c.ConverterType.Name}",
        ResolvedSource.InlineExpression => "inline expression",
        ResolvedSource.ConstantOrDefault c => $"constant: {c.Value ?? "null"}",
        ResolvedSource.Unresolved => "UNRESOLVED",
        _ => "?",
    };
}
