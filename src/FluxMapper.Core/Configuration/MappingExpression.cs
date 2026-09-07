using System.Linq.Expressions;

namespace FluxMapper.Core.Configuration;

internal sealed class MappingExpression<TSource, TDestination>(TypeMapConfiguration config, MapperConfigurationExpression owner)
    : IMappingExpression<TSource, TDestination>
{
    public IMappingExpression<TSource, TDestination> Map<TMember>(
        Expression<Func<TDestination, TMember>> destinationMember, Expression<Func<TSource, TMember>> sourceExpression)
    {
        var name = MemberExpressionHelpers.GetMemberName(destinationMember);
        config.MapExplicit(name, MemberExpressionHelpers.ToResolvedSource(sourceExpression));
        return this;
    }

    public IMappingExpression<TSource, TDestination> Ignore<TMember>(Expression<Func<TDestination, TMember>> destinationMember)
    {
        config.Ignore(MemberExpressionHelpers.GetMemberName(destinationMember));
        return this;
    }

    public IMappingExpression<TSource, TDestination> Condition<TMember>(
        Expression<Func<TDestination, TMember>> destinationMember, Func<TSource, bool> condition)
    {
        config.SetCondition(MemberExpressionHelpers.GetMemberName(destinationMember), src => condition((TSource)src));
        return this;
    }

    public IMappingExpression<TSource, TDestination> NullSubstitute<TMember>(
        Expression<Func<TDestination, TMember>> destinationMember, TMember value)
    {
        config.SetNullSubstitute(MemberExpressionHelpers.GetMemberName(destinationMember), value);
        return this;
    }

    public IMappingExpression<TSource, TDestination> NullPolicy<TMember>(
        Expression<Func<TDestination, TMember>> destinationMember, Abstractions.NullPolicy policy)
    {
        config.SetNullPolicy(MemberExpressionHelpers.GetMemberName(destinationMember), policy);
        return this;
    }

    public IMappingExpression<TSource, TDestination> PreserveReferences()
    {
        config.ReferenceHandling = Abstractions.ReferenceHandling.Preserve;
        return this;
    }

    public IMappingExpression<TSource, TDestination> ProjectUsing<TMember, TResolver>(Expression<Func<TDestination, TMember>> destinationMember)
        where TResolver : Abstractions.IProjectionValueResolver<TSource, TMember>, new()
    {
        config.MapExplicit(
            MemberExpressionHelpers.GetMemberName(destinationMember),
            new FluxMapper.Core.Ir.ResolvedSource.ProjectionResolver(typeof(TResolver), typeof(TMember)));
        return this;
    }

    public IMappingExpression<TSource, TDestination> ResolveUsing<TMember, TResolver>(Expression<Func<TDestination, TMember>> destinationMember)
        where TResolver : Abstractions.IValueResolver<TSource, TDestination, TMember>
    {
        config.MapExplicit(
            MemberExpressionHelpers.GetMemberName(destinationMember),
            new FluxMapper.Core.Ir.ResolvedSource.ValueResolver(typeof(TResolver)));
        return this;
    }

    public IMappingExpression<TDestination, TSource> ReverseMap()
    {
        config.ReverseMapRequested = true;
        return owner.CreateMap<TDestination, TSource>();
    }
}
