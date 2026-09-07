using System.Linq.Expressions;

namespace FluxMapper.Core.Configuration;

/// <summary>
/// The options surface passed to a <see cref="IMappingExpression{TSource,TDestination}.ForPath{TMember}"/>
/// callback. Deliberately narrower than <see cref="IMemberConfigurationExpression{TSource,TDestination,TMember}"/>
/// (no <c>Ignore</c>/<c>Condition</c>/etc.) -- a path leaf is always freshly constructed alongside the rest
/// of its <c>ForPath</c> tree (see <see cref="Ir.PathPlan"/>), so "ignore this leaf" or "conditionally skip
/// it" don't have the same meaning they do for an ordinary top-level member; that scoping can grow later
/// if a real need for it shows up.
/// </summary>
public interface IPathConfigurationExpression<TSource, TMember>
{
    /// <summary>The only supported leaf source today: a plain expression over <typeparamref name="TSource"/>, matching every real <c>ForPath</c> usage this was built from.</summary>
    void MapFrom(Expression<Func<TSource, TMember>> sourceExpression);
}
