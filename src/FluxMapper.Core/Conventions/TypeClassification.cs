using System.Collections;
using System.Reflection;

namespace FluxMapper.Core.Conventions;

/// <summary>Shared "what kind of type is this" helpers used by candidate discovery, construction, and collection planning.</summary>
public static class TypeClassification
{
    private static readonly HashSet<Type> SimpleTypes =
    [
        typeof(string), typeof(decimal), typeof(DateTime), typeof(DateTimeOffset),
        typeof(TimeSpan), typeof(Guid), typeof(Uri),
#if NET6_0_OR_GREATER
        // DateOnly/TimeOnly don't exist in netstandard2.0's reference assemblies -- this assembly's
        // netstandard2.0 build simply doesn't special-case them as scalar-like (they fall through to
        // ordinary object mapping instead), while the net10.0 build classifies them same as any other
        // BCL value type here.
        typeof(DateOnly), typeof(TimeOnly),
#endif
    ];

    public static bool IsSimple(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        return t.IsPrimitive || t.IsEnum || SimpleTypes.Contains(t);
    }

    /// <summary>
    /// Detects <c>IDictionary&lt;TKey,TValue&gt;</c>/<c>IReadOnlyDictionary&lt;TKey,TValue&gt;</c>
    /// (including concrete <c>Dictionary&lt;,&gt;</c> and immutable dictionary types, which implement one of
    /// these). Must be checked *before* <see cref="IsCollection"/> everywhere it matters -- a dictionary is
    /// also an <c>IEnumerable&lt;KeyValuePair&lt;TKey,TValue&gt;&gt;</c>, and treating it as a plain sequence
    /// would silently produce a collection of key/value pairs instead of a keyed dictionary.
    /// </summary>
    public static bool IsDictionary(Type type, out Type keyType, out Type valueType)
    {
        var dictInterface = (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IDictionary<,>)) ||
                             (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>))
            ? type
            : type.GetInterfaces().FirstOrDefault(i => i.IsGenericType &&
                (i.GetGenericTypeDefinition() == typeof(IDictionary<,>) || i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)));

        if (dictInterface is not null)
        {
            var args = dictInterface.GetGenericArguments();
            keyType = args[0];
            valueType = args[1];
            return true;
        }

        keyType = typeof(object);
        valueType = typeof(object);
        return false;
    }

    public static bool IsCollection(Type type, out Type elementType)
    {
        if (type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(type))
        {
            var enumerableInterface = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>)
                ? type
                : type.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));

            if (enumerableInterface is not null)
            {
                elementType = enumerableInterface.GetGenericArguments()[0];
                return true;
            }

            elementType = typeof(object);
            return true;
        }

        elementType = type;
        return false;
    }

    public static bool IsComplex(Type type) => !IsSimple(type) && !IsDictionary(type, out _, out _) && !IsCollection(type, out _);

    public static IEnumerable<MemberInfo> GetReadableMembers(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .Cast<MemberInfo>()
        .Concat(type.GetFields(BindingFlags.Public | BindingFlags.Instance));

    public static IEnumerable<MemberInfo> GetWritableMembers(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && p.GetIndexParameters().Length == 0)
            .Cast<MemberInfo>()
        .Concat(type.GetFields(BindingFlags.Public | BindingFlags.Instance).Where(f => !f.IsInitOnly));
}
