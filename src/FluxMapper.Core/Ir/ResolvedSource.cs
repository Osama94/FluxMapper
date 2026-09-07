using System.Reflection;

namespace FluxMapper.Core.Ir;

/// <summary>
/// Discriminated union of "where a destination member's value comes from".
/// Implements the reflection-input
/// (System.Reflection.MemberInfo) shape only; a future compile-time input (ISymbol-based) would
/// produce the same union through the same MappingPlanBuilder pipeline — ResolvedSource
/// itself never special-cases which input produced it.
/// </summary>
public abstract record ResolvedSource
{
    /// <summary>A chain of member accesses, e.g. User -&gt; Address -&gt; City -&gt; Name.</summary>
    public sealed record MemberChain(IReadOnlyList<MemberInfo> Members) : ResolvedSource
    {
        public Type ValueType => MemberValueTypeHelper.GetMemberType(Members[^1]);

        public string PathText => string.Join('.', Members.Select(m => m.Name));
    }

    /// <summary>A zero-argument method call, e.g. <c>GetName()</c>.</summary>
    public sealed record MethodCall(MethodInfo Method) : ResolvedSource;

    /// <summary>A runtime-only resolver instance/type. Never projection-safe.</summary>
    public sealed record ValueResolver(Type ResolverType) : ResolvedSource;

    /// <summary>
    /// A registered <see cref="Abstractions.IProjectionValueResolver{TSource,TMember}"/>
    /// -- unlike <see cref="ValueResolver"/>, this
    /// hands back a translatable <see cref="System.Linq.Expressions.LambdaExpression"/> instead of
    /// executing arbitrary code, so both the compiled-expression execution tier and the projection
    /// compiler splice its body directly into their own expression tree (never call it as an opaque
    /// delegate) -- this is what makes it safe to use inside a real IQueryable projection.
    /// </summary>
    public sealed record ProjectionResolver(Type ResolverType, Type MemberType) : ResolvedSource;

    /// <summary>A registered type-pair converter.</summary>
    public sealed record ValueConverter(Type ConverterType, Type SourceType, Type DestinationType) : ResolvedSource;

    /// <summary>
    /// An explicit <c>.Map(dest, src =&gt; expr)</c> whose expression body is not a
    /// plain member-access chain — e.g. <c>s =&gt; s.First + " " + s.Last</c>. Kept as a genuine
    /// <see cref="System.Linq.Expressions.LambdaExpression"/> rather than downgraded to an opaque
    /// delegate, so it can still be spliced directly into a larger compiled expression (execution tier)
    /// or, in a future phase, checked for provider-translatability instead of being unconditionally
    /// treated as runtime-only the way a true <see cref="ValueResolver"/> must be.
    /// </summary>
    public sealed record InlineExpression(System.Linq.Expressions.LambdaExpression Expression) : ResolvedSource;

    /// <summary>A literal/default value, used for null substitution and defaulted members.</summary>
    public sealed record ConstantOrDefault(object? Value) : ResolvedSource;

    /// <summary>
    /// Transient-only marker: a member the candidate-discovery stage could not resolve.
    /// A MappingPlan containing any Unresolved member after validation is not considered
    /// buildable/executable — see MappingPlanBuilder.
    /// </summary>
    public sealed record Unresolved : ResolvedSource;
}

public static class MemberValueTypeHelper
{
    public static Type GetMemberType(MemberInfo member) => member switch
    {
        PropertyInfo p => p.PropertyType,
        FieldInfo f => f.FieldType,
        MethodInfo m => m.ReturnType,
        _ => throw new NotSupportedException($"Unsupported member kind: {member.GetType()}"),
    };
}
