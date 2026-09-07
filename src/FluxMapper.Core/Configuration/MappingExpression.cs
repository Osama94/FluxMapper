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

    public IMappingExpression<TSource, TDestination> ResolveUsing<TMember>(
        Expression<Func<TDestination, TMember>> destinationMember,
        Func<TSource, TDestination, TMember, Abstractions.ResolutionContext, TMember> resolver)
    {
        config.MapExplicit(
            MemberExpressionHelpers.GetMemberName(destinationMember),
            new FluxMapper.Core.Ir.ResolvedSource.ContextualResolver((src, dest, current, ctx) => resolver(
                (TSource)src!,
                // dest/current are placeholder defaults today (see CompiledMapperFactory.BuildContextualResolverCall) --
                // pattern-matched rather than blindly cast, so a value-type TDestination/TMember never throws
                // unboxing a boxed null.
                dest is TDestination typedDest ? typedDest : default!,
                current is TMember typedCurrent ? typedCurrent : default!,
                ctx)));
        return this;
    }

    public IMappingExpression<TSource, TDestination> ForMember<TMember>(
        Expression<Func<TDestination, TMember>> destinationMember,
        Action<IMemberConfigurationExpression<TSource, TDestination, TMember>> memberOptions)
    {
        memberOptions(new MemberConfigurationExpression<TSource, TDestination, TMember>(this, destinationMember));
        return this;
    }

    public IMappingExpression<TDestination, TSource> ReverseMap()
    {
        config.ReverseMapRequested = true;
        return owner.CreateMap<TDestination, TSource>();
    }

    public IMappingExpression<TSource, TDestination> ConstructUsing(Expression<Func<TSource, TDestination>> constructor)
    {
        config.CustomConstructor = constructor;
        return this;
    }

    public IMappingExpression<TSource, TDestination> BeforeMap(Action<TSource, TDestination> beforeMap)
    {
        config.BeforeMap = (src, dest) => beforeMap((TSource)src, (TDestination)dest);
        return this;
    }

    public IMappingExpression<TSource, TDestination> AfterMap(Action<TSource, TDestination> afterMap)
    {
        config.AfterMap = (src, dest) => afterMap((TSource)src, (TDestination)dest);
        return this;
    }

    public IMappingExpression<TSource, TDestination> ForPath<TMember>(
        Expression<Func<TDestination, TMember>> destinationPath,
        Action<IPathConfigurationExpression<TSource, TMember>> pathOptions)
    {
        var path = MemberExpressionHelpers.GetMemberPath(destinationPath);
        var expr = new PathConfigurationExpression<TSource, TMember>();
        pathOptions(expr);

        if (expr.Source is not null)
        {
            config.MapPath(path, expr.Source);
        }

        return this;
    }
}
