namespace FluxMapper.Abstractions;

/// <summary>
/// A runtime-only value resolver. Can capture DI services, do arbitrary work,
/// and read/write ambient state via <see cref="ResolutionContext"/>. Deliberately NOT usable inside
/// a projection: see <see cref="IProjectionValueResolver{TSource,TMember}"/> for why the distinction
/// is a separate interface rather than a runtime flag on this one.
/// </summary>
public interface IValueResolver<in TSource, in TDestination, TMember>
{
    TMember Resolve(TSource source, TDestination destination, TMember destinationMember, ResolutionContext context);
}

/// <summary>
/// A projection-safe value resolver: it must hand back an <see cref="System.Linq.Expressions.Expression"/>
/// that a LINQ provider can translate, never execute arbitrary code itself. Only resolvers implementing
/// this interface are considered when the plan builder computes projection eligibility.
/// </summary>
public interface IProjectionValueResolver<TSource, TMember>
{
    System.Linq.Expressions.Expression<Func<TSource, TMember>> GetExpression();
}

/// <summary>A type-pair value converter, e.g. DateTime -&gt; string.</summary>
public interface IValueConverter<in TSource, out TDestination>
{
    TDestination Convert(TSource source, ResolutionContext context);
}

/// <summary>
/// Ambient, per-call state threaded through a single <c>Map</c> invocation — never shared globally.
/// </summary>
public sealed class ResolutionContext
{
    private readonly Dictionary<string, object?> _items = new();
    private readonly Dictionary<object, object> _referenceMap = new(ReferenceEqualityComparer.Instance);

    public IServiceProvider? Services { get; init; }

    public int Depth { get; internal set; }

    public object? this[string key]
    {
        get => _items.TryGetValue(key, out var v) ? v : null;
        set => _items[key] = value;
    }

    /// <summary>Used by <c>ReferenceHandling.Preserve</c> to avoid infinite recursion on cyclic graphs.</summary>
    internal bool TryGetExistingDestination(object source, out object destination)
        => _referenceMap.TryGetValue(source, out destination!);

    internal void RegisterDestination(object source, object destination)
        => _referenceMap[source] = destination;
}
