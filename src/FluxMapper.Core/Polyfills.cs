// See FluxMapper.Abstractions/Polyfills.cs for the full rationale. FluxMapper.Core also defines its own
// records/init-accessor IR types (MappingPlan, MemberPlan, ResolvedSource and friends), and needs its own
// IsExternalInit for netstandard2.0 -- FluxMapper.Abstractions' copy is `internal` to that assembly and
// deliberately not relied upon here. RequiresDynamicCodeAttribute/RequiresUnreferencedCodeAttribute are
// NOT redeclared here: FluxMapper.Core references FluxMapper.Abstractions, whose polyfills for those two
// are `public`, so this project resolves them from there instead of needing a second copy.
#if NETSTANDARD2_0

namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}

namespace System.Collections.Generic
{
    /// <summary>
    /// Polyfill for the real netstandard2.1+/.NET Core 2.1+ instance <c>KeyValuePair&lt;TKey,TValue&gt;.
    /// Deconstruct</c> method, needed by Mapper.cs's <c>foreach (var (key, value) in options.Items)</c>
    /// over a <c>Dictionary&lt;string, object?&gt;</c>. An extension method here is picked up by ordinary
    /// deconstruction-pattern lookup precisely because this namespace is already implicitly <c>using</c>'d
    /// (ImplicitUsings) everywhere a <c>Dictionary&lt;,&gt;</c> would be in scope to begin with.
    /// </summary>
    internal static class KeyValuePairPolyfillExtensions
    {
        public static void Deconstruct<TKey, TValue>(this KeyValuePair<TKey, TValue> pair, out TKey key, out TValue value)
        {
            key = pair.Key;
            value = pair.Value;
        }
    }

    /// <summary>
    /// Polyfill for the real netstandard2.1+/.NET Core 2.1+ <c>CollectionExtensions.GetValueOrDefault</c>
    /// methods, used throughout FluxMapper.Core's config/plan lookups (e.g.
    /// <c>MapperConfigurationExpression.Get</c>, <c>MappingPlanBuilder</c>'s self-reference-depth stack).
    /// </summary>
    internal static class DictionaryPolyfillExtensions
    {
        public static TValue? GetValueOrDefault<TKey, TValue>(this IReadOnlyDictionary<TKey, TValue> dictionary, TKey key)
            => dictionary.TryGetValue(key, out var value) ? value : default;

        public static TValue GetValueOrDefault<TKey, TValue>(this IReadOnlyDictionary<TKey, TValue> dictionary, TKey key, TValue defaultValue)
            => dictionary.TryGetValue(key, out var value) ? value : defaultValue;
    }
}

namespace System
{
    /// <summary>
    /// Polyfill for the real netstandard2.1+/.NET Core 2.0+ 3-argument <c>string.Replace(string, string,
    /// StringComparison)</c> overload, used by <c>NamingConvention.Normalize</c>'s case-insensitive
    /// substring replacement. An extension method with this exact name/signature is picked up by ordinary
    /// overload resolution wherever the real instance method doesn't exist -- the call site
    /// (<c>name.Replace(from, to, StringComparison.OrdinalIgnoreCase)</c>) needs no change at all on
    /// either target framework.
    /// </summary>
    internal static class StringPolyfillExtensions
    {
        public static string Replace(this string str, string oldValue, string? newValue, StringComparison comparisonType)
        {
            newValue ??= string.Empty;
            if (oldValue.Length == 0) return str;

            var result = new System.Text.StringBuilder();
            var index = 0;
            while (true)
            {
                var found = str.IndexOf(oldValue, index, comparisonType);
                if (found < 0)
                {
                    result.Append(str, index, str.Length - index);
                    break;
                }

                result.Append(str, index, found - index);
                result.Append(newValue);
                index = found + oldValue.Length;
            }

            return result.ToString();
        }
    }
}

#endif
