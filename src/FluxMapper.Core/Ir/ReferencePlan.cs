using FluxMapper.Abstractions;

namespace FluxMapper.Core.Ir;

/// <summary>Default is None with a fixed MaxDepth guard against runaway recursion.</summary>
public sealed record ReferencePlan(ReferenceHandling Handling, int MaxDepth)
{
    public static readonly ReferencePlan Default = new(ReferenceHandling.None, MaxDepth: 64);
}

/// <summary>
/// Which of the four execution strategies are legal for a resolved plan. Today only
/// CompiledDelegateEligible/ReflectionOnly are populated meaningfully; SourceGenEligible and AotSafe are
/// wired up but conservatively false, because the compiled-expression tier goes through
/// System.Linq.Expressions.Compile(), which is JIT-dependent and therefore never AOT-safe.
/// </summary>
public sealed record ExecutionEligibility(
    bool SourceGenEligible,
    bool CompiledDelegateEligible,
    bool CompiledExpressionEligible,
    bool ReflectionOnly,
    bool AotSafe)
{
    public static ExecutionEligibility ForCompiledExpression(bool aotSafe = false) =>
        new(SourceGenEligible: false, CompiledDelegateEligible: true, CompiledExpressionEligible: true,
            ReflectionOnly: false, AotSafe: aotSafe);

    public static ExecutionEligibility ForReflectionOnly() =>
        new(SourceGenEligible: false, CompiledDelegateEligible: false, CompiledExpressionEligible: false,
            ReflectionOnly: true, AotSafe: false);
}
