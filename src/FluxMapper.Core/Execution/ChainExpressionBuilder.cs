using System.Linq.Expressions;
using System.Reflection;
using FluxMapper.Core.Ir;

namespace FluxMapper.Core.Execution;

/// <summary>
/// Builds a null-safe member-access chain (C#'s <c>?.</c> semantics) for a resolved
/// <see cref="ResolvedSource.MemberChain"/> — an intermediate hop that's null short-circuits the whole
/// chain to <c>default</c> instead of throwing <see cref="NullReferenceException"/>. This is what makes
/// flattening safe to use against optional nested objects (e.g. a nullable
/// <c>Address</c>) without every caller having to null-check by hand.
/// Only the FINAL leaf value is subject to a member's configured <see cref="FluxMapper.Abstractions.NullPolicy"/>
/// — an intermediate null is a "there's nothing here to flatten,"
/// not a nullability contract violation of the leaf itself.
/// </summary>
internal static class ChainExpressionBuilder
{
    public static Expression BuildSafeAccess(Expression root, IReadOnlyList<MemberInfo> members)
    {
        var leafType = MemberValueTypeHelper.GetMemberType(members[^1]);
        return Build(root, members, 0, leafType);
    }

    private static Expression Build(Expression current, IReadOnlyList<MemberInfo> members, int index, Type leafType)
    {
        var member = members[index];
        Expression access = member switch
        {
            PropertyInfo p => Expression.Property(current, p),
            FieldInfo f => Expression.Field(current, f),
            _ => throw new NotSupportedException($"Unsupported chain member kind: {member.GetType()}"),
        };

        var isLast = index == members.Count - 1;
        if (isLast)
            return access;

        var canBeNull = !access.Type.IsValueType || Nullable.GetUnderlyingType(access.Type) is not null;
        if (!canBeNull)
            return Build(access, members, index + 1, leafType);

        var underlying = Nullable.GetUnderlyingType(access.Type);
        var temp = Expression.Variable(access.Type, "n" + index);
        Expression nextRoot = underlying is not null ? Expression.Property(temp, "Value") : temp;
        var continuation = Build(nextRoot, members, index + 1, leafType);
        var continuationConverted = continuation.Type == leafType ? continuation : Expression.Convert(continuation, leafType);

        return Expression.Block(
            leafType,
            [temp],
            Expression.Assign(temp, access),
            Expression.Condition(
                Expression.Equal(temp, Expression.Constant(null, access.Type)),
                Expression.Default(leafType),
                continuationConverted));
    }
}
