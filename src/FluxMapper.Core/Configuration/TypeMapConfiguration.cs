using System.Linq.Expressions;
using System.Reflection;
using FluxMapper.Abstractions;
using FluxMapper.Core.Conventions;
using FluxMapper.Core.Ir;

namespace FluxMapper.Core.Configuration;

/// <summary>
/// The mutable, per-(source,destination) configuration record that <see cref="MapperConfigurationExpression"/>'s
/// fluent methods write into and that <see cref="Building.MappingPlanBuilder"/> reads from. Kept separate
/// from the fluent API surface so the plan builder depends on a plain
/// data shape, not on the fluent method-chaining types themselves.
/// </summary>
public sealed class TypeMapConfiguration(Type sourceType, Type destinationType)
{
    private readonly HashSet<string> _ignored = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ResolvedSource> _explicitSources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<object, bool>> _conditions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object?> _nullSubstitutes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NullPolicy> _explicitNullPolicies = new(StringComparer.Ordinal);
    private readonly List<(IReadOnlyList<MemberInfo> Path, ResolvedSource Source)> _pathMaps = [];

    public Type SourceType { get; } = sourceType;
    public Type DestinationType { get; } = destinationType;
    public NamingConvention Naming { get; set; } = NamingConvention.Default;
    public bool ReverseMapRequested { get; internal set; }

    /// <summary>Default <see cref="ReferenceHandling.None"/>.</summary>
    public ReferenceHandling ReferenceHandling { get; internal set; } = ReferenceHandling.None;

    public void Ignore(string memberName) => _ignored.Add(memberName);
    public bool IsIgnored(string memberName) => _ignored.Contains(memberName);

    public void MapExplicit(string memberName, ResolvedSource source) => _explicitSources[memberName] = source;
    public bool TryGetExplicitSource(string memberName, out ResolvedSource source) => _explicitSources.TryGetValue(memberName, out source!);
    public IReadOnlyDictionary<string, ResolvedSource> ExplicitSources => _explicitSources;

    public void SetCondition(string memberName, Func<object, bool> condition) => _conditions[memberName] = condition;
    public Func<object, bool>? GetCondition(string memberName) => _conditions.GetValueOrDefault(memberName);

    public void SetNullSubstitute(string memberName, object? value)
    {
        _nullSubstitutes[memberName] = value;
        _explicitNullPolicies[memberName] = NullPolicy.Substitute;
    }

    public bool TryGetNullSubstitute(string memberName, out object? value) => _nullSubstitutes.TryGetValue(memberName, out value);

    public void SetNullPolicy(string memberName, NullPolicy policy) => _explicitNullPolicies[memberName] = policy;
    public NullPolicy? GetExplicitNullPolicy(string memberName) => _explicitNullPolicies.TryGetValue(memberName, out var p) ? p : null;

    /// <summary>Set by <c>ConstructUsing</c>. A <c>LambdaExpression</c> (erased from <c>Expression&lt;Func&lt;TSource,TDestination&gt;&gt;</c>
    /// since this type is not itself generic over source/destination) spliced directly into the compiled
    /// construction expression in place of the normal <c>new TDestination(...)</c> call.</summary>
    public LambdaExpression? CustomConstructor { get; internal set; }

    /// <summary>Set by <c>BeforeMap</c>/<c>AfterMap</c>. Boxed as <c>Action&lt;object,object&gt;</c> (erased
    /// from <c>Action&lt;TSource,TDestination&gt;</c>) so this non-generic type can hold it; the compiled
    /// construction expression invokes it via <c>Expression.Invoke(Expression.Constant(...))</c>, the same
    /// pattern already used for per-member <c>Condition</c> delegates.</summary>
    public Action<object, object>? BeforeMap { get; internal set; }

    /// <summary>See <see cref="BeforeMap"/>.</summary>
    public Action<object, object>? AfterMap { get; internal set; }

    /// <summary>
    /// Set by <c>ForPath</c>: <paramref name="path"/> is the full destination member chain
    /// (<c>d =&gt; d.A.B.C</c> -&gt; <c>[A, B, C]</c>) and <paramref name="source"/> is that leaf's
    /// resolved value source. Multiple registrations may share a common prefix (<c>A.B.C1</c> and
    /// <c>A.B.C2</c>); <see cref="Building.MappingPlanBuilder"/> groups them into one <see cref="PathPlan"/>
    /// tree per distinct top-level destination member.
    /// </summary>
    public void MapPath(IReadOnlyList<MemberInfo> path, ResolvedSource source) => _pathMaps.Add((path, source));

    public IReadOnlyList<(IReadOnlyList<MemberInfo> Path, ResolvedSource Source)> PathMaps => _pathMaps;
}
