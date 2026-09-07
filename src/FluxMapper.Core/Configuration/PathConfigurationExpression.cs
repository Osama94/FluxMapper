using System.Linq.Expressions;

namespace FluxMapper.Core.Configuration;

internal sealed class PathConfigurationExpression<TSource, TMember> : IPathConfigurationExpression<TSource, TMember>
{
    public Ir.ResolvedSource? Source { get; private set; }

    public void MapFrom(Expression<Func<TSource, TMember>> sourceExpression)
        => Source = MemberExpressionHelpers.ToResolvedSource(sourceExpression);
}
