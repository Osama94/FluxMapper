using System.Linq.Expressions;
using System.Reflection;
using FluxMapper.Core.Ir;

namespace FluxMapper.Core.Configuration;

internal static class MemberExpressionHelpers
{
    /// <summary>Extracts the destination member name from <c>d =&gt; d.Foo</c>.</summary>
    public static string GetMemberName<TDestination, TMember>(Expression<Func<TDestination, TMember>> selector)
    {
        var body = StripConvert(selector.Body);
        if (body is MemberExpression member)
            return member.Member.Name;

        throw new ArgumentException(
            $"Expected a simple member access such as 'd => d.PropertyName', got: {selector}", nameof(selector));
    }

    /// <summary>
    /// Extracts the full destination member-access path from <c>d =&gt; d.A.B.C</c>, for <c>ForPath</c>
    /// -- unlike <see cref="GetMemberName{TDestination,TMember}"/>, this accepts (and requires) more than
    /// one hop.
    /// </summary>
    public static IReadOnlyList<MemberInfo> GetMemberPath<TDestination, TMember>(Expression<Func<TDestination, TMember>> selector)
    {
        var body = StripConvert(selector.Body);
        var chain = new List<MemberInfo>();

        while (body is MemberExpression member)
        {
            chain.Insert(0, member.Member);
            body = member.Expression is null ? null : StripConvert(member.Expression);
        }

        if (chain.Count >= 2 && body is ParameterExpression)
            return chain;

        // A single-hop "path" is just an ordinary member -- .Map()/.ForMember() already cover it (and do
        // so without ForPath's "always freshly construct this subtree" semantics), so this is rejected
        // here rather than silently accepted as a degenerate one-level ForPath.
        throw new ArgumentException(
            $"Expected a multi-hop member-access path such as 'd => d.A.B.C', got: {selector}. " +
            "A single-member path doesn't need ForPath -- use .Map() or .ForMember() instead.", nameof(selector));
    }

    /// <summary>
    /// Converts a <c>s =&gt; ...</c> source expression into a <see cref="ResolvedSource"/>: a plain member
    /// chain (<c>s.Address.City</c>) becomes <see cref="ResolvedSource.MemberChain"/> so it participates
    /// in nested/collection/nullability analysis exactly like a convention-discovered candidate; anything
    /// else becomes <see cref="ResolvedSource.InlineExpression"/>.
    /// </summary>
    public static ResolvedSource ToResolvedSource<TSource, TMember>(Expression<Func<TSource, TMember>> selector)
    {
        var body = StripConvert(selector.Body);
        var chain = new List<MemberInfo>();

        while (body is MemberExpression member)
        {
            chain.Insert(0, member.Member);
            body = member.Expression is null ? null : StripConvert(member.Expression);
        }

        if (chain.Count > 0 && body is ParameterExpression)
            return new ResolvedSource.MemberChain(chain);

        return new ResolvedSource.InlineExpression(selector);
    }

    private static Expression? StripConvert(Expression? expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
            expression = unary.Operand;
        return expression;
    }
}
