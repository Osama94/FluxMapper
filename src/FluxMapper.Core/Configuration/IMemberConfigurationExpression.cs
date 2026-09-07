using System.Linq.Expressions;
using FluxMapper.Abstractions;

namespace FluxMapper.Core.Configuration;

/// <summary>
/// The per-member options surface passed to a <see cref="IMappingExpression{TSource,TDestination}.ForMember{TMember}"/>
/// callback. Every method here is a thin wrapper over the equivalent flat method on
/// <see cref="IMappingExpression{TSource,TDestination}"/> (<c>Map</c>, <c>Ignore</c>, <c>Condition</c>, and so
/// on) -- <c>ForMember</c> exists purely as an ergonomic, AutoMapper-shaped alternative for configuring many
/// members in one options-builder call per member, not as a separate code path.
/// </summary>
public interface IMemberConfigurationExpression<TSource, TDestination, TMember>
{
    /// <summary>Equivalent to <c>cfg.Map(destinationMember, sourceExpression)</c>.</summary>
    void MapFrom(Expression<Func<TSource, TMember>> sourceExpression);

    /// <summary>
    /// AutoMapper's four-argument <c>MapFrom</c>: an inline resolver function instead of an expression,
    /// given the source, the destination, the member's current value (a default value in FluxMapper today
    /// -- see <see cref="Execution.CompiledMapperFactory"/>'s resolver-call builder), and the per-call
    /// <see cref="ResolutionContext"/>. Equivalent to
    /// <c>cfg.ResolveUsing(destinationMember, resolver)</c>. Use this over the expression-based overload
    /// when the value depends on ambient per-call state (<see cref="ResolutionContext.Items"/>, set via
    /// <see cref="IMappingOperationOptions{TSource,TDestination}.Items"/>) rather than only the source
    /// object -- something a compile-time <see cref="Expression"/> cannot express.
    /// </summary>
    void MapFrom(Func<TSource, TDestination, TMember, ResolutionContext, TMember> resolver);

    /// <summary>Equivalent to <c>cfg.Ignore(destinationMember)</c>.</summary>
    void Ignore();

    /// <summary>Equivalent to <c>cfg.Condition(destinationMember, condition)</c>.</summary>
    void Condition(Func<TSource, bool> condition);

    /// <summary>Equivalent to <c>cfg.NullSubstitute(destinationMember, value)</c>.</summary>
    void NullSubstitute(TMember value);

    /// <summary>Equivalent to <c>cfg.NullPolicy(destinationMember, policy)</c>.</summary>
    void NullPolicy(NullPolicy policy);

    /// <summary>Equivalent to <c>cfg.ResolveUsing&lt;TMember,TResolver&gt;(destinationMember)</c>.</summary>
    void ResolveUsing<TResolver>() where TResolver : IValueResolver<TSource, TDestination, TMember>;

    /// <summary>Equivalent to <c>cfg.ProjectUsing&lt;TMember,TResolver&gt;(destinationMember)</c>.</summary>
    void ProjectUsing<TResolver>() where TResolver : IProjectionValueResolver<TSource, TMember>, new();
}
