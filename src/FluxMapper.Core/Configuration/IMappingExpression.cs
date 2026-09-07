using System.Linq.Expressions;
using FluxMapper.Abstractions;

namespace FluxMapper.Core.Configuration;

/// <summary>
/// The per-type-pair fluent surface. Every automatic
/// behavior has an explicit override here.
/// </summary>
public interface IMappingExpression<TSource, TDestination>
{
    IMappingExpression<TSource, TDestination> Map<TMember>(
        Expression<Func<TDestination, TMember>> destinationMember, Expression<Func<TSource, TMember>> sourceExpression);

    IMappingExpression<TSource, TDestination> Ignore<TMember>(Expression<Func<TDestination, TMember>> destinationMember);

    IMappingExpression<TSource, TDestination> Condition<TMember>(
        Expression<Func<TDestination, TMember>> destinationMember, Func<TSource, bool> condition);

    IMappingExpression<TSource, TDestination> NullSubstitute<TMember>(
        Expression<Func<TDestination, TMember>> destinationMember, TMember value);

    IMappingExpression<TSource, TDestination> NullPolicy<TMember>(
        Expression<Func<TDestination, TMember>> destinationMember, FluxMapper.Abstractions.NullPolicy policy);

    /// <summary>
    /// Opts this map into reference-cycle-safe, identity-preserving execution
    /// -- an object reachable more than once from the same root
    /// <c>Map()</c> call is mapped once and the same destination instance is reused for every occurrence,
    /// including genuine cycles (A -&gt; B -&gt; A) for destination types constructible via a parameterless
    /// constructor + member assignment. See <see cref="Execution.CompiledMapperFactory"/> for the documented
    /// limitation on constructor-bound destinations.
    /// </summary>
    IMappingExpression<TSource, TDestination> PreserveReferences();

    /// <summary>
    /// Resolves this member using
    /// a projection-safe resolver -- its <see cref="Abstractions.IProjectionValueResolver{TSource,TMember}.GetExpression"/>
    /// body is spliced directly into both the compiled-expression execution tier's tree and any
    /// <c>ProjectTo</c> projection, so (unlike <see cref="Abstractions.IValueResolver{TSource,TDestination,TMember}"/>)
    /// it remains translatable by a real <c>IQueryable</c> provider such as EF Core.
    /// </summary>
    IMappingExpression<TSource, TDestination> ProjectUsing<TMember, TResolver>(Expression<Func<TDestination, TMember>> destinationMember)
        where TResolver : FluxMapper.Abstractions.IProjectionValueResolver<TSource, TMember>, new();

    /// <summary>
    /// Resolves this member with a runtime-only <see cref="FluxMapper.Abstractions.IValueResolver{TSource,TDestination,TMember}"/> --
    /// can capture DI services and do arbitrary work, but (unlike <see cref="ProjectUsing{TMember,TResolver}"/>)
    /// is never usable inside a <c>ProjectTo</c> projection.
    ///
    /// Deliberately NOT constrained to <c>new()</c> (unlike <see cref="ProjectUsing{TMember,TResolver}"/>
    /// which stays <c>new()</c>-constrained): <typeparamref name="TResolver"/> may declare constructor
    /// parameters resolved via the <see cref="IServiceProvider"/> passed to <c>AddFluxMapper</c> /
    /// <see cref="MapperConfiguration.CreateMapper"/> (see <see cref="Execution.CompiledMapperFactory"/>).
    /// A resolver with no such dependencies still just needs a public parameterless constructor -- calling
    /// this without ever touching DI behaves the same either way, since a missing
    /// <see cref="IServiceProvider"/> falls back to <see cref="Activator.CreateInstance(Type)"/> at the
    /// point the resolver is actually instantiated, not here at fluent-configuration time.
    /// </summary>
    IMappingExpression<TSource, TDestination> ResolveUsing<TMember, TResolver>(Expression<Func<TDestination, TMember>> destinationMember)
        where TResolver : FluxMapper.Abstractions.IValueResolver<TSource, TDestination, TMember>;

    /// <summary>
    /// Registers the reverse map. Reversibility is checked when
    /// the configuration is built (<see cref="MapperConfiguration.Create"/>/<c>AssertConfigurationIsValid</c>),
    /// not here, because whether a member is reversible depends on the fully resolved forward
    /// <c>MappingPlan</c>, which does not exist yet at fluent-configuration time.
    /// </summary>
    IMappingExpression<TDestination, TSource> ReverseMap();
}
