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
}

#endif
