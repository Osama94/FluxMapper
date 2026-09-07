namespace FluxMapper.Core.Ir;

/// <summary>What concrete dictionary shape the destination materializes to.</summary>
public enum DictionaryTargetKind
{
    /// <summary><c>Dictionary&lt;TKey,TValue&gt;</c> or any interface it satisfies (<c>IDictionary&lt;,&gt;</c>, <c>IReadOnlyDictionary&lt;,&gt;</c> when no more specific kind matches).</summary>
    Dictionary,

    /// <summary><c>System.Collections.Immutable.ImmutableDictionary&lt;TKey,TValue&gt;</c> / <c>IImmutableDictionary&lt;,&gt;</c>.</summary>
    ImmutableDictionary,
}

/// <summary>
/// Dictionary-to-dictionary mapping, the sibling of <see cref="CollectionPlan"/>
/// for keyed collections. Keys are intentionally restricted to direct-conversion (no
/// <see cref="ValuePlan"/>-equivalent for keys) -- real-world dictionary keys are overwhelmingly simple
/// types (string/int/enum/Guid), and a full recursive key-mapping-with-identity-semantics story is a much
/// larger feature (what does it mean for two mapped keys to collide?) that this deliberately does not
/// take on.
/// <paramref name="ValuePlan"/> is null when values convert directly, populated when values are complex
/// types needing their own recursive <see cref="MappingPlan"/> (mirrors <see cref="CollectionPlan.ElementPlan"/>).
/// </summary>
public sealed record DictionaryPlan(
    MappingPlan? ValuePlan,
    DictionaryTargetKind TargetKind,
    Type SourceKeyType,
    Type SourceValueType,
    Type KeyType,
    Type ValueType);
