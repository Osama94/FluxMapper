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

    /// <summary>
    /// AutoMapper-shaped alternative to <see cref="Map{TMember}"/>/<see cref="Ignore{TMember}"/>/
    /// <see cref="Condition{TMember}"/>/<see cref="NullSubstitute{TMember}"/>/<see cref="ResolveUsing{TMember,TResolver}"/>/
    /// <see cref="ProjectUsing{TMember,TResolver}"/>: configures one member per call via an options builder
    /// (<see cref="IMemberConfigurationExpression{TSource,TDestination,TMember}"/>) instead of a flat method
    /// per concern. Every option on that builder just forwards to the equivalent method here -- pick whichever
    /// style reads better; they are fully interchangeable and can be mixed within the same <c>CreateMap</c>.
    /// </summary>
    IMappingExpression<TSource, TDestination> ForMember<TMember>(
        Expression<Func<TDestination, TMember>> destinationMember,
        Action<IMemberConfigurationExpression<TSource, TDestination, TMember>> memberOptions);

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
    /// Flat equivalent of <c>.ForMember(destinationMember, opt =&gt; opt.MapFrom(resolver))</c> with the
    /// four-argument (source, destination, current value, <see cref="ResolutionContext"/>) resolver
    /// function -- an inline counterpart to <see cref="ResolveUsing{TMember,TResolver}"/> for when a whole
    /// resolver class is overkill, and useful over the plain expression-based <see cref="Map{TMember}"/>
    /// specifically when the value needs ambient per-call state (<c>ResolutionContext.Items</c>) rather
    /// than only the source object.
    /// </summary>
    IMappingExpression<TSource, TDestination> ResolveUsing<TMember>(
        Expression<Func<TDestination, TMember>> destinationMember,
        Func<TSource, TDestination, TMember, FluxMapper.Abstractions.ResolutionContext, TMember> resolver);

    /// <summary>
    /// Maps into a nested destination path (<c>d =&gt; d.Address.City</c>) rather than a single top-level
    /// member -- for a destination subtree that doesn't have one matching nested source object of its own
    /// (see <see cref="Ir.PathPlan"/> for why <c>ForPath</c> is a genuinely different mechanism from
    /// <see cref="Map{TMember}"/> plus automatic nested mapping, not just sugar over it). Every
    /// intermediate segment in the path (<c>Address</c> above) is freshly constructed -- it must have a
    /// public parameterless constructor -- and any of its other members left uncovered by a <c>ForPath</c>
    /// registration keep their default value; they are not separately validated or convention-matched.
    /// Multiple <c>ForPath</c> calls sharing a common prefix (<c>d.Address.City</c> and
    /// <c>d.Address.PostalCode</c>) merge into the same constructed subtree rather than each constructing
    /// their own <c>Address</c>.
    /// </summary>
    IMappingExpression<TSource, TDestination> ForPath<TMember>(
        Expression<Func<TDestination, TMember>> destinationPath,
        Action<IPathConfigurationExpression<TSource, TMember>> pathOptions);

    /// <summary>
    /// Registers the reverse map. Reversibility is checked when
    /// the configuration is built (<see cref="MapperConfiguration.Create"/>/<c>AssertConfigurationIsValid</c>),
    /// not here, because whether a member is reversible depends on the fully resolved forward
    /// <c>MappingPlan</c>, which does not exist yet at fluent-configuration time.
    /// </summary>
    IMappingExpression<TDestination, TSource> ReverseMap();

    /// <summary>
    /// Overrides how the destination instance is constructed for this map, replacing automatic
    /// constructor selection entirely. Member assignment for any other configured/writable member still
    /// runs afterward as normal -- this only takes over the <c>new TDestination(...)</c> step itself.
    /// </summary>
    IMappingExpression<TSource, TDestination> ConstructUsing(Expression<Func<TSource, TDestination>> constructor);

    /// <summary>
    /// Runs once, immediately after the destination instance is constructed and before any member is
    /// assigned. Fires for every mapping of this (source, destination) pair, including occurrences nested
    /// inside a larger object graph -- not just top-level <c>Map()</c> calls. For a one-off hook on a
    /// single call instead, see the <c>IMapper.Map</c> overload that accepts mapping options.
    /// </summary>
    IMappingExpression<TSource, TDestination> BeforeMap(Action<TSource, TDestination> beforeMap);

    /// <summary>
    /// Runs once, immediately after every configured member has been assigned. Fires for every mapping of
    /// this (source, destination) pair, including occurrences nested inside a larger object graph -- not
    /// just top-level <c>Map()</c> calls. For a one-off hook on a single call instead, see the
    /// <c>IMapper.Map</c> overload that accepts mapping options.
    /// </summary>
    IMappingExpression<TSource, TDestination> AfterMap(Action<TSource, TDestination> afterMap);
}
