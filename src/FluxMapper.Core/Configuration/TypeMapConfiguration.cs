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
}
