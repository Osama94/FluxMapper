using System.Reflection;

namespace FluxMapper.Core.Nullability;

/// <summary>
/// Distinguishing <c>string</c> from <c>string?</c> and <c>int</c> from <c>int?</c>.
/// Wraps <see cref="NullabilityInfoContext"/> (reference-type annotations) and <see cref="Nullable{T}"/>
/// (value types) behind one question so the rest of the pipeline never has to know which rule applied.
/// </summary>
public static class NullabilityAnalysis
{
    public static bool IsNullable(MemberInfo member)
    {
        var context = new NullabilityInfoContext();
        return member switch
        {
            PropertyInfo p => IsNullable(p.PropertyType, () => context.Create(p).ReadState),
            FieldInfo f => IsNullable(f.FieldType, () => context.Create(f).ReadState),
            MethodInfo m => IsNullable(m.ReturnType, () => context.Create(m.ReturnParameter).ReadState),
            _ => false,
        };
    }

    public static bool IsNullable(ParameterInfo parameter)
    {
        var context = new NullabilityInfoContext();
        return IsNullable(parameter.ParameterType, () => context.Create(parameter).ReadState);
    }

    private static bool IsNullable(Type type, Func<NullabilityState> referenceStateProvider)
    {
        if (type.IsValueType)
            return Nullable.GetUnderlyingType(type) is not null;

        return referenceStateProvider() == NullabilityState.Nullable;
    }
}
