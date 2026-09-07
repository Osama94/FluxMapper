namespace FluxMapper.Core.Ir;

public enum CollectionTargetKind
{
    Array,
    List,
    HashSet,
    Enumerable,
    /// <summary><see cref="System.Collections.Immutable.ImmutableArray{T}"/>.</summary>
    ImmutableArray,
    /// <summary><see cref="System.Collections.Immutable.ImmutableList{T}"/> / <c>IImmutableList&lt;T&gt;</c>.</summary>
    ImmutableList,
    /// <summary><see cref="System.Collections.Immutable.ImmutableHashSet{T}"/> / <c>IImmutableSet&lt;T&gt;</c>.</summary>
    ImmutableHashSet,
    // Dictionaries are a distinct shape (keyed, not just an element sequence) and are modeled by
    // dictionaryPlan instead of as a CollectionTargetKind.
}

/// <summary>
/// The collection engine.
/// <paramref name="ElementPlan"/> is null when elements convert directly (simple/primitive element
/// types) — building a full recursive MappingPlan for e.g. <c>int -&gt; int</c> would be pure overhead;
/// it is populated only when elements are complex types that themselves need object-to-object mapping.
/// </summary>
public sealed record CollectionPlan(
    MappingPlan? ElementPlan,
    CollectionTargetKind TargetKind,
    Type SourceElementType,
    Type ElementType,
    bool HasCapacityHint);
