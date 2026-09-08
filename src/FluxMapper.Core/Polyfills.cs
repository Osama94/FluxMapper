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

#endif
