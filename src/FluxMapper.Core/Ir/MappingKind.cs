namespace FluxMapper.Core.Ir;

/// <summary>The high-level shape a mapping plan takes.</summary>
public enum MappingKind
{
    Flat,
    Nested,
    Collection,
    Dictionary,
    Polymorphic,
}

/// <summary>How a single destination member obtains its value.</summary>
public enum MemberStrategy
{
    DirectAssignment,
    Flattening,
    NestedMapping,
    CollectionMapping,
    DictionaryMapping,
    ProjectionResolver,
    CustomResolver,
    CustomConverter,
    ConstructorArgument,
    Ignored,

    /// <summary>Populated via one or more <c>ForPath</c> registrations -- see <see cref="PathPlan"/>.</summary>
    PathMapping,
}
