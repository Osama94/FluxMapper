using System.Threading;

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
    private static readonly AsyncLocal<ResolutionContext?> AmbientCurrent = new();

    private readonly Dictionary<string, object?> _items = new();
    private readonly Dictionary<object, object> _referenceMap = new(ReferenceEqualityComparer.Instance);

    public IServiceProvider? Services { get; init; }

    public int Depth { get; internal set; }

    public object? this[string key]
    {
        get => _items.TryGetValue(key, out var v) ? v : null;
        set => _items[key] = value;
    }

    /// <summary>
    /// Arbitrary per-call state, mirroring AutoMapper's <c>ResolutionContext.Items</c> — set via
    /// <see cref="IMappingOperationOptions{TSource,TDestination}.Items"/> at the top of one <c>Map</c>
    /// call (e.g. <c>mapper.Map(source, opt =&gt; opt.Items["EditReasonId"] = editReasonId)</c>) and read
    /// back here inside a resolver or a contextual <c>.MapFrom((s, d, cur, ctx) =&gt; ...)</c> anywhere in
    /// that call's object graph, including nested members. The indexer above is an older, equivalent
    /// accessor kept for source compatibility; this property is the one that matches AutoMapper's shape
    /// (<c>context.Items.ContainsKey(...)</c>, <c>context.Items["key"]</c>).
    /// </summary>
    public IDictionary<string, object?> Items => _items;

    /// <summary>
    /// The <see cref="ResolutionContext"/> for the <c>Map</c> call currently executing on this logical
    /// call context, when one was created because the caller supplied per-call options with at least one
    /// <see cref="Items"/> entry (set by FluxMapper.Core's compiled-expression execution tier and its
    /// <c>Mapper</c> implementation of <see cref="IMapper"/>); <c>null</c> for a plain <c>Map</c> call with
    /// no options, in which case every
    /// resolver invoked during that call still gets its own fresh, empty context exactly as before this
    /// property existed. This ambient slot exists so a per-call <see cref="Items"/> bag set once at the
    /// top of a <c>Map</c> call reaches every resolver/contextual-<c>MapFrom</c> invocation anywhere in
    /// that call's object graph without threading an extra parameter through every compiled-expression
    /// builder method — nested mapping is already inlined into the same compiled tree as its root call,
    /// so a single ambient read at each resolver call site is sufficient. Backed by
    /// <see cref="AsyncLocal{T}"/>, not a plain static field, so concurrent or reentrant <c>Map</c> calls
    /// (including a resolver that itself calls <c>Map</c> again) never observe each other's <see cref="Items"/>.
    /// </summary>
    public static ResolutionContext? Current => AmbientCurrent.Value;

    /// <summary>
    /// Makes <paramref name="context"/> the value <see cref="Current"/> returns for the duration of the
    /// returned scope, restoring whatever <see cref="Current"/> was before on <see cref="IDisposable.Dispose"/>
    /// (correct for nested/reentrant pushes, not just the single-level case). Public so a caller
    /// orchestrating its own resolver pipeline outside <c>Mapper</c> can use the same mechanism; the
    /// common case is entirely internal to <c>Mapper</c>'s per-call-options <c>Map</c> overload.
    /// </summary>
    public static IDisposable Push(ResolutionContext context)
    {
        var previous = AmbientCurrent.Value;
        AmbientCurrent.Value = context;
        return new AmbientScope(previous);
    }

    private sealed class AmbientScope(ResolutionContext? previous) : IDisposable
    {
        public void Dispose() => AmbientCurrent.Value = previous;
    }

    /// <summary>Used by <c>ReferenceHandling.Preserve</c> to avoid infinite recursion on cyclic graphs.</summary>
    internal bool TryGetExistingDestination(object source, out object destination)
        => _referenceMap.TryGetValue(source, out destination!);

    internal void RegisterDestination(object source, object destination)
        => _referenceMap[source] = destination;
}
