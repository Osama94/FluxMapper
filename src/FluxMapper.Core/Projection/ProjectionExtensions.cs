using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using FluxMapper.Core.Configuration;
using FluxMapper.Core.Internal;

namespace FluxMapper.Core.Projection;

/// <summary>
/// The public projection surface: projects an
/// <c>IQueryable</c> directly into destination shapes, translated by whatever
/// provider backs the source query (EF Core's <c>DbSet&lt;T&gt;</c>/<c>IQueryable&lt;T&gt;</c> included --
/// this project depends on nothing but <see cref="IQueryable"/> itself, so it works unmodified against
/// EF Core without FluxMapper.Core ever referencing an EF Core assembly). <c>SelectProject</c> is offered
/// as an alias for teams that want to avoid the name colliding textually with AutoMapper's identically-
/// named but differently-typed extension method during a side-by-side migration.
/// </summary>
public static class ProjectionExtensions
{
    /// <summary>Mode C: builds and applies a <c>Select()</c> translated by the underlying <c>IQueryable</c> provider. Throws <see cref="Abstractions.ProjectionTranslationException"/> (MAP4001) if the resolved plan isn't projection-safe -- see <see cref="ProjectionValidator"/>.</summary>
    [RequiresDynamicCode("Builds a System.Linq.Expressions projection lambda and MakeGenericMethod'd Queryable.Select call at runtime, which requires a JIT and is not supported when publishing Native AOT.")]
    [RequiresUnreferencedCode("Builds the projection expression using reflection (MemberInfo/MethodInfo) over the mapped types, which trimming can remove.")]
    public static IQueryable<TDestination> ProjectTo<TDestination>(this IQueryable source, MapperConfiguration configuration)
    {
        ArgumentGuard.ThrowIfNull(source, nameof(source));
        ArgumentGuard.ThrowIfNull(configuration, nameof(configuration));

        var plan = configuration.GetPlan(source.ElementType, typeof(TDestination));
        ProjectionValidator.EnsureProjectable(plan);

        var lambda = ProjectionExpressionBuilder.BuildLambda(plan);

        var selectCall = Expression.Call(
            typeof(Queryable),
            nameof(Queryable.Select),
            [source.ElementType, typeof(TDestination)],
            source.Expression,
            lambda);

        return source.Provider.CreateQuery<TDestination>(selectCall);
    }

    /// <summary>Alias for <see cref="ProjectTo{TDestination}(IQueryable, MapperConfiguration)"/> (see type-level remarks).</summary>
    [RequiresDynamicCode("See ProjectTo<TDestination>(IQueryable, MapperConfiguration).")]
    [RequiresUnreferencedCode("See ProjectTo<TDestination>(IQueryable, MapperConfiguration).")]
    public static IQueryable<TDestination> SelectProject<TDestination>(this IQueryable source, MapperConfiguration configuration)
        => source.ProjectTo<TDestination>(configuration);

    /// <summary>
    /// Returns the translatable <see cref="LambdaExpression"/> FluxMapper would use for <paramref name="configuration"/>'s
    /// (<typeparamref name="TSource"/>, <typeparamref name="TDestination"/>) plan without applying it to any
    /// query -- useful for composing it manually (e.g. passing it to a provider-specific API) or for
    /// asserting on the shape of the generated projection in a test.
    /// </summary>
    [RequiresDynamicCode("Builds a System.Linq.Expressions projection lambda at runtime, which requires a JIT and is not supported when publishing Native AOT.")]
    [RequiresUnreferencedCode("Builds the projection expression using reflection (MemberInfo/MethodInfo) over the mapped types, which trimming can remove.")]
    public static Expression<Func<TSource, TDestination>> GetProjectionExpression<TSource, TDestination>(this MapperConfiguration configuration)
    {
        ArgumentGuard.ThrowIfNull(configuration, nameof(configuration));
        var plan = configuration.GetPlan(typeof(TSource), typeof(TDestination));
        ProjectionValidator.EnsureProjectable(plan);
        return (Expression<Func<TSource, TDestination>>)ProjectionExpressionBuilder.BuildLambda(plan);
    }
}
