using System.Linq.Expressions;
using FluxMapper.Abstractions;

namespace FluxMapper.Core.Configuration;

internal sealed class MemberConfigurationExpression<TSource, TDestination, TMember>(
    IMappingExpression<TSource, TDestination> owner, Expression<Func<TDestination, TMember>> destinationMember)
    : IMemberConfigurationExpression<TSource, TDestination, TMember>
{
    public void MapFrom(Expression<Func<TSource, TMember>> sourceExpression) => owner.Map(destinationMember, sourceExpression);

    public void MapFrom(Func<TSource, TDestination, TMember, ResolutionContext, TMember> resolver)
        => owner.ResolveUsing(destinationMember, resolver);

    public void Ignore() => owner.Ignore(destinationMember);

    public void Condition(Func<TSource, bool> condition) => owner.Condition(destinationMember, condition);

    public void NullSubstitute(TMember value) => owner.NullSubstitute(destinationMember, value);

    public void NullPolicy(NullPolicy policy) => owner.NullPolicy(destinationMember, policy);

    public void ResolveUsing<TResolver>() where TResolver : IValueResolver<TSource, TDestination, TMember>
        => owner.ResolveUsing<TMember, TResolver>(destinationMember);

    public void ProjectUsing<TResolver>() where TResolver : IProjectionValueResolver<TSource, TMember>, new()
        => owner.ProjectUsing<TMember, TResolver>(destinationMember);
}
