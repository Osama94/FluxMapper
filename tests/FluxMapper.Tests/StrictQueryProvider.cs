using System.Linq.Expressions;

namespace FluxMapper.Tests;

/// <summary>
/// A hand-rolled
/// <see cref="IQueryable{T}"/>/<see cref="IQueryProvider"/> pair that is deliberately stricter than
/// <c>List&lt;T&gt;.AsQueryable()</c> -- the LINQ-to-Objects provider the projection tests would otherwise
/// exercise. It walks every expression tree handed to it and throws
/// <see cref="NotSupportedException"/> if that tree contains a node shape a real server-side LINQ
/// provider (EF Core's query translator chief among them) could never translate to SQL: a
/// <see cref="BlockExpression"/> (local variables/statements -- exactly what
/// <c>FluxMapper.Core.Execution.CompiledMapperFactory</c>'s runtime tier freely uses and
/// <c>FluxMapper.Core.Projection.ProjectionExpressionBuilder</c> exists specifically to avoid), an
/// <see cref="InvocationExpression"/> (a captured delegate invoked inline -- also something the runtime
/// tier uses for <c>.Condition()</c> but the projection tier must not), or a
/// <see cref="MethodCallExpression"/> whose method is not on this file's small allowlist of well-known,
/// genuinely translatable methods.
///
/// This is a structural check that complements, rather than replaces, <c>EfCoreRealTests.cs</c>'s
/// genuine <c>Microsoft.EntityFrameworkCore.InMemory</c> <c>DbContext</c>/<c>DbSet&lt;T&gt;</c> coverage:
/// it proves FluxMapper's projection tier produces a tree shape a real translator could plausibly accept,
/// independently of any one provider's actual translation behavior.
/// </summary>
internal static class StrictTranslationQueryable
{
    public static IQueryable<T> Wrap<T>(IEnumerable<T> source) => new StrictQueryable<T>(source.AsQueryable());
}

internal sealed class StrictQueryable<T>(IQueryable<T> inner) : IQueryable<T>
{
    public Type ElementType => inner.ElementType;
    public Expression Expression => inner.Expression;
    public IQueryProvider Provider { get; } = new StrictQueryProvider(inner.Provider);

    public IEnumerator<T> GetEnumerator() => inner.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

internal sealed class StrictQueryProvider(IQueryProvider inner) : IQueryProvider
{
    /// <summary>
    /// Every method name FluxMapper.Core.Projection.ProjectionExpressionBuilder is known to ever emit a
    /// call to (see CompiledMapperFactory's collection/dictionary materializers, which the projection
    /// tier shares the same target-kind switch with) plus the handful of BCL member-getters (compiled as
    /// method calls, e.g. <c>KeyValuePair&lt;,&gt;.Key</c>'s getter) that appear in a dictionary
    /// projection. Anything outside this allowlist is presumed untranslatable and rejected -- a
    /// deliberately closed, not open, list.
    /// </summary>
    private static readonly HashSet<string> AllowedMethodNames =
    [
        nameof(Enumerable.Select), nameof(Enumerable.ToList), nameof(Enumerable.ToArray),
        nameof(Enumerable.ToDictionary), nameof(Enumerable.ToHashSet),
        "ToImmutableArray", "ToImmutableList", "ToImmutableHashSet", "ToImmutableDictionary",
        "get_Key", "get_Value",
    ];

    public IQueryable CreateQuery(Expression expression)
    {
        Validate(expression);
        return inner.CreateQuery(expression);
    }

    public IQueryable<TElement> CreateQuery<TElement>(Expression expression)
    {
        Validate(expression);
        return inner.CreateQuery<TElement>(expression);
    }

    public object? Execute(Expression expression)
    {
        Validate(expression);
        return inner.Execute(expression);
    }

    public TResult Execute<TResult>(Expression expression)
    {
        Validate(expression);
        return inner.Execute<TResult>(expression);
    }

    private static void Validate(Expression expression) => new TranslatabilityVisitor().Visit(expression);

    private sealed class TranslatabilityVisitor : ExpressionVisitor
    {
        protected override Expression VisitBlock(BlockExpression node)
            => throw new NotSupportedException(
                $"Block expression ('{node}') is not translatable by a real IQueryable provider such as EF Core.");

        protected override Expression VisitInvocation(InvocationExpression node)
            => throw new NotSupportedException(
                $"Invoke expression ('{node}') -- a captured delegate invoked inline -- is not translatable by a real IQueryable provider such as EF Core.");

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (!AllowedMethodNames.Contains(node.Method.Name))
            {
                throw new NotSupportedException(
                    $"Call to {node.Method.DeclaringType?.Name}.{node.Method.Name} is not on the allowlist of well-known translatable methods.");
            }

            return base.VisitMethodCall(node);
        }
    }
}
