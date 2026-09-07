using System.Reflection;

namespace FluxMapper.Core.Conventions;

/// <summary>Can a value of type <c>from</c> reach a destination of type <c>to</c> without a custom converter/resolver?</summary>
public static class TypeConversion
{
    public static bool CanConvertDirectly(Type from, Type to)
    {
        var f = Nullable.GetUnderlyingType(from) ?? from;
        var t = Nullable.GetUnderlyingType(to) ?? to;

        if (f == t) return true;
        if (t.IsAssignableFrom(f)) return true;
        if (IsNumeric(f) && IsNumeric(t)) return true;
        if (f.IsEnum && IsNumeric(t)) return true;
        if (IsNumeric(f) && t.IsEnum) return true;
        if (f.IsEnum && t.IsEnum) return true;

        return HasConversionOperator(f, t) || HasConversionOperator(t, f);
    }

    private static bool HasConversionOperator(Type declaring, Type other)
        => declaring.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Any(m => (m.Name is "op_Implicit" or "op_Explicit")
                      && ((m.ReturnType == other && m.GetParameters().Length == 1)
                          || (m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == other)));

    private static bool IsNumeric(Type t) =>
        (t.IsPrimitive && t != typeof(bool) && t != typeof(char) && t != typeof(IntPtr) && t != typeof(UIntPtr))
        || t == typeof(decimal);
}
