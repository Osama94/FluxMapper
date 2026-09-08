using System.Reflection;

namespace FluxMapper.Core.Nullability;

/// <summary>
/// Distinguishing <c>string</c> from <c>string?</c> and <c>int</c> from <c>int?</c>.
/// On net10.0, wraps <c>NullabilityInfoContext</c> (reference-type annotations) and
/// <see cref="Nullable{T}"/> (value types) behind one question so the rest of the pipeline never has to
/// know which rule applied. netstandard2.0 doesn't have <c>NullabilityInfoContext</c> (added in .NET 6),
/// so that build answers the reference-type half of the question via
/// <c>NetStandardNullabilityHelper</c> instead -- see that type for the underlying mechanism and its
/// documented narrower scope.
/// </summary>
public static class NullabilityAnalysis
{
    public static bool IsNullable(MemberInfo member)
    {
        return member switch
        {
            PropertyInfo p => IsNullable(p.PropertyType, () => IsReferenceNullable(p)),
            FieldInfo f => IsNullable(f.FieldType, () => IsReferenceNullable(f)),
            MethodInfo m => IsNullable(m.ReturnType, () => IsReferenceNullable(m.ReturnParameter)),
            _ => false,
        };
    }

    public static bool IsNullable(ParameterInfo parameter)
    {
        return IsNullable(parameter.ParameterType, () => IsReferenceNullable(parameter));
    }

    private static bool IsNullable(Type type, Func<bool> referenceStateProvider)
    {
        if (type.IsValueType)
            return Nullable.GetUnderlyingType(type) is not null;

        return referenceStateProvider();
    }

#if NETSTANDARD2_0
    private static bool IsReferenceNullable(MemberInfo member) => NetStandardNullabilityHelper.IsReferenceTypeNullable(member);

    private static bool IsReferenceNullable(ParameterInfo parameter) => NetStandardNullabilityHelper.IsReferenceTypeNullable(parameter);
#else
    private static bool IsReferenceNullable(MemberInfo member)
    {
        var context = new NullabilityInfoContext();
        return member switch
        {
            PropertyInfo p => context.Create(p).ReadState == NullabilityState.Nullable,
            FieldInfo f => context.Create(f).ReadState == NullabilityState.Nullable,
            MethodInfo m => context.Create(m.ReturnParameter).ReadState == NullabilityState.Nullable,
            _ => false,
        };
    }

    private static bool IsReferenceNullable(ParameterInfo parameter)
    {
        var context = new NullabilityInfoContext();
        return context.Create(parameter).ReadState == NullabilityState.Nullable;
    }
#endif
}
