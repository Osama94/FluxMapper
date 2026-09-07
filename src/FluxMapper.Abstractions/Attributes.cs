namespace FluxMapper.Abstractions;

/// <summary>
/// Marks a partial class/struct as a source-generation target for Mode B mapping.
/// Consumed by FluxMapper.SourceGenerator — declared here in Abstractions so a project can
/// reference the attribute without depending on the generator package itself, and so that
/// FluxMapper.Core's reflection-based plan builder can also recognize [MapFrom] types for consistent
/// behavior between Mode A and Mode B.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class MapFromAttribute(Type sourceType) : Attribute
{
    public Type SourceType { get; } = sourceType;
}

/// <summary>Explicitly ignore a destination member during automatic mapping.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class IgnoreMapAttribute : Attribute;
