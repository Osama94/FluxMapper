namespace FluxMapper.Abstractions;

/// <summary>How to handle a nullable source feeding a member with a nullability contract.</summary>
public enum NullPolicy
{
    /// <summary>Default for a nullable source into a non-nullable destination with no other configuration: throw at map time.</summary>
    Throw,

    /// <summary>Leave the destination member at its default/unset value.</summary>
    Ignore,

    /// <summary>Use a configured substitute value when the source is null.</summary>
    Substitute,

    /// <summary>Use <c>default(TMember)</c> when the source is null.</summary>
    Default,

    /// <summary>Map the null through as-is (only legal when the destination member is itself nullable).</summary>
    Map,
}

/// <summary>Circular reference handling policy.</summary>
public enum ReferenceHandling
{
    None,
    Preserve,
    Ignore,
    Throw,
}

/// <summary>How <c>Map(source, existingDestination)</c> treats members already set on the destination.</summary>
public enum UpdatePolicy
{
    Overwrite,
    IgnoreNull,
    IgnoreDefault,
    MapOnlyChanged,
    ExplicitMembers,
}
