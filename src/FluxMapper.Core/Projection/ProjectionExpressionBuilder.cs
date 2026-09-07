using System.Linq.Expressions;
using System.Reflection;
using FluxMapper.Core.Ir;

namespace FluxMapper.Core.Projection;

/// <summary>
/// Compiles a <see cref="MappingPlan"/> into a single
/// <see cref="LambdaExpression"/> a real <c>IQueryable</c> provider (EF Core included) can translate --
/// deliberately a *separate, simpler* builder from <see cref="Execution.CompiledMapperFactory"/> rather
/// than a shared code path, because the two have fundamentally different legal-construct sets: the
/// runtime tier can use <c>Expression.Block</c>, local variables, null-guard temporaries, dictionary
/// lookups (for <c>ReferenceHandling.Preserve</c>), and <c>Activator.CreateInstance</c>-backed resolver
/// calls freely, because it ends in <see cref="LambdaExpression.Compile()"/> and runs as ordinary .NET
/// code; none of those constructs are guaranteed translatable by a query provider, and some (a
/// <c>Block</c> with local variables, in particular) are reliably *not* supported by EF Core's query
/// translator at all. This builder only ever emits <c>MemberInit</c>/<c>New</c>/<c>Select</c>/direct
/// member access -- exactly the shape <c>ProjectTo&lt;T&gt;</c> implementations are expected to produce.
/// Call <see cref="ProjectionValidator.EnsureProjectable"/> first; this builder assumes the plan already
/// passed that check and does not re-validate.
/// </summary>
public static class ProjectionExpressionBuilder
{
    public static LambdaExpression BuildLambda(MappingPlan plan)
    {
        var sourceParam = Expression.Parameter(plan.SourceType, "s");
        var body = BuildConstruct(plan, sourceParam);
        return Expression.Lambda(body, sourceParam);
    }

    private static Expression BuildConstruct(MappingPlan plan, Expression sourceExpr)
    {
        if (plan.CollectionPlan is not null)
            return BuildCollection(plan.CollectionPlan, sourceExpr, plan.DestinationType);

        // DictionaryPlan/PolymorphismPlan roots are rejected by ProjectionValidator before this runs.

        var ctorPlan = plan.ConstructorPlan;
        NewExpression newExpr;

        if (ctorPlan.Constructor is not null && ctorPlan.ParameterBindings.Count > 0)
        {
            var args = ctorPlan.ParameterBindings.Select(binding =>
            {
                var matchingMember = plan.MemberPlans.FirstOrDefault(m =>
                    m.Strategy == MemberStrategy.ConstructorArgument &&
                    string.Equals(m.DestinationMember.Name, binding.Parameter.Name, StringComparison.OrdinalIgnoreCase));

                var valueExpr = matchingMember is not null
                    ? BuildMemberValue(matchingMember, sourceExpr)
                    : BuildRaw(binding.Source, sourceExpr);

                return (Expression)Expression.Convert(valueExpr, binding.Parameter.ParameterType);
            }).ToList();

            newExpr = Expression.New(ctorPlan.Constructor, args);
        }
        else
        {
            newExpr = Expression.New(plan.DestinationType);
        }

        var bindings = new List<MemberBinding>();
        foreach (var member in plan.MemberPlans)
        {
            if (member.Strategy is MemberStrategy.ConstructorArgument or MemberStrategy.Ignored) continue;
            if (member.Source is ResolvedSource.Unresolved) continue;

            var destinationMemberType = MemberValueTypeHelper.GetMemberType(member.DestinationMember);
            var valueExpr = Expression.Convert(BuildMemberValue(member, sourceExpr), destinationMemberType);
            bindings.Add(Expression.Bind(member.DestinationMember, valueExpr));
        }

        return bindings.Count > 0 ? Expression.MemberInit(newExpr, bindings) : newExpr;
    }

    private static Expression BuildMemberValue(MemberPlan member, Expression sourceExpr)
    {
        var destinationType = MemberValueTypeHelper.GetMemberType(member.DestinationMember);

        return member.Strategy switch
        {
            MemberStrategy.NestedMapping => Expression.Convert(BuildConstruct(member.NestedPlan!, BuildRaw(member.Source, sourceExpr)), destinationType),
            MemberStrategy.CollectionMapping => BuildCollection(member.CollectionPlan!, BuildRaw(member.Source, sourceExpr), destinationType),
            MemberStrategy.ProjectionResolver => BuildProjectionResolver(member.Source, sourceExpr, destinationType),
            _ => BuildScalar(member.Source, sourceExpr, destinationType),
        };
    }

    /// <summary>
    /// Direct member-chain access, deliberately with *no* null-guard: an optional one-to-one navigation
    /// property (e.g. a nullable FK) is exactly how EF Core (and any comparable provider) already
    /// represents "no related row" at the SQL level (a LEFT JOIN producing NULL columns) -- adding an
    /// in-tree null check here would be redundant for a provider and actively wrong for plain
    /// LINQ-to-Objects use (<c>.AsQueryable()</c> against real in-memory objects), where a null in the
    /// middle of the chain legitimately throws <see cref="NullReferenceException"/> exactly as ordinary
    /// c# member access would. This is a deliberate, documented difference from
    /// <see cref="Execution.ChainExpressionBuilder"/>'s null-safe chain used by the runtime tier, not an
    /// oversight.
    /// </summary>
    private static Expression BuildRaw(ResolvedSource source, Expression sourceExpr) => source switch
    {
        ResolvedSource.MemberChain mc => BuildDirectChain(sourceExpr, mc.Members),
        ResolvedSource.InlineExpression ie => ReplaceParameter(ie.Expression, sourceExpr),
        _ => throw new NotSupportedException(
            "Projection requires a member-chain or inline-expression source; ProjectionValidator should have rejected anything else before this point."),
    };

    private static Expression BuildDirectChain(Expression root, IReadOnlyList<MemberInfo> members)
    {
        Expression current = root;
        foreach (var member in members)
        {
            current = member switch
            {
                PropertyInfo p => Expression.Property(current, p),
                FieldInfo f => Expression.Field(current, f),
                _ => throw new NotSupportedException($"Unsupported member kind in projection chain: {member.GetType()}"),
            };
        }
        return current;
    }

    private static Expression BuildScalar(ResolvedSource source, Expression sourceExpr, Type targetType)
    {
        var raw = BuildRaw(source, sourceExpr);
        return raw.Type == targetType ? raw : Expression.Convert(raw, targetType);
    }

    private static Expression BuildProjectionResolver(ResolvedSource source, Expression sourceExpr, Type targetType)
    {
        var resolver = (ResolvedSource.ProjectionResolver)source;
        var lambda = ProjectionResolverHelper.GetResolverExpression(resolver.ResolverType);
        var inlined = ReplaceParameter(lambda, sourceExpr);
        return inlined.Type == targetType ? inlined : Expression.Convert(inlined, targetType);
    }

    private static Expression BuildCollection(CollectionPlan collectionPlan, Expression sourceCollectionExpr, Type destinationType)
    {
        var sourceElementType = collectionPlan.SourceElementType;
        var destinationElementType = collectionPlan.ElementType;

        var elementParam = Expression.Parameter(sourceElementType, "e");
        var elementBody = collectionPlan.ElementPlan is not null
            ? Expression.Convert(BuildConstruct(collectionPlan.ElementPlan, elementParam), destinationElementType)
            : Expression.Convert(elementParam, destinationElementType);
        var elementLambda = Expression.Lambda(elementBody, elementParam);

        var selectMethod = typeof(Enumerable).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .First(m => m.Name == nameof(Enumerable.Select) && m.GetParameters().Length == 2)
            .MakeGenericMethod(sourceElementType, destinationElementType);
        Expression selected = Expression.Call(selectMethod, sourceCollectionExpr, elementLambda);

        // Materializing with ToList() here is the one pattern every major IQueryable provider (EF Core
        // included) is documented and known to translate for a nested one-to-many navigation inside a
        // top-level Select(); Array/HashSet/Immutable-shaped nested collections are intentionally NOT
        // attempted in projection (unlike the runtime tier) because provider translation support for
        // those materializations is inconsistent -- a destination that needs one of those shapes for a
        // *nested* collection should materialize the query first (ToList()) and finish mapping in memory.
        var toListMethod = typeof(Enumerable).GetMethod(nameof(Enumerable.ToList))!.MakeGenericMethod(destinationElementType);
        Expression materialized = Expression.Call(toListMethod, selected);

        return destinationType.IsAssignableFrom(materialized.Type)
            ? materialized
            : Expression.Convert(materialized, destinationType);
    }

    private static Expression ReplaceParameter(LambdaExpression lambda, Expression replacement)
        => new ParameterReplacer(lambda.Parameters[0], replacement).Visit(lambda.Body)!;

    private sealed class ParameterReplacer(ParameterExpression from, Expression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
    }
}
