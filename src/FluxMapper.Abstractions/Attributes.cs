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

    /// <summary>
    /// Loosens FluxMapper.SourceGenerator's member-matching from an exact name match to also accept a
    /// case-insensitive, underscore-insensitive match (e.g. destination <c>UserName</c> against source
    /// <c>user_name</c>) -- the source-generator-side equivalent of the compiled-expression tier's
    /// <c>NamingConvention.SnakeCase()</c>/<c>LowerUnderscore()</c> presets. An exact match is always
    /// tried first regardless of this setting; when more than one source member would normalize to the
    /// same name, the generator treats the destination member as unmatched rather than guessing which
    /// one was meant.
    /// </summary>
    public MapFromNamingConvention NamingConvention { get; set; } = MapFromNamingConvention.Exact;
}

/// <summary>Naming-match strategy for <see cref="MapFromAttribute"/> -- see its <c>NamingConvention</c> property.</summary>
public enum MapFromNamingConvention
{
    /// <summary>Destination and source member names must match exactly (ordinal, case-sensitive) -- the original, default behavior.</summary>
    Exact = 0,

    /// <summary>Case-insensitive, underscore-insensitive match -- mirrors <c>NamingConvention.SnakeCase()</c>/<c>LowerUnderscore()</c>.</summary>
    SnakeCase = 1,
}

/// <summary>Explicitly ignore a destination member during automatic mapping.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class IgnoreMapAttribute : Attribute;

/// <summary>
/// Registers a global, source-generator-visible type-pair converter -- the <c>[MapFrom]</c>-generator
/// counterpart to <c>FluxMapper.Core.Configuration.MapperConfigurationExpression.RegisterConverter</c> on
/// the compiled-expression tier. Applied at the assembly level so it's visible to every
/// <c>[MapFrom]</c> target in the same compilation:
/// <c>[assembly: MapFromConverter(typeof(Money), typeof(decimal), typeof(MoneyToDecimalConverter))]</c>.
///
/// <paramref name="converterType"/> is constructed via <c>new TConverter()</c> at each generated call
/// site -- it must have an accessible public parameterless constructor, since the generator has no DI
/// container or service provider to consult at compile time (a converter that needs one isn't a fit for
/// this tier; register it on the compiled-expression tier via <c>RegisterConverter</c> instead, which
/// does have DI available). Only a converter registered in the SAME compilation is seen -- one
/// registered in a referenced assembly is not visible to a downstream consumer's own
/// <c>[MapFrom]</c> targets; this is a stated scope boundary, not an oversight.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class MapFromConverterAttribute(Type sourceType, Type destinationType, Type converterType) : Attribute
{
    public Type SourceType { get; } = sourceType;
    public Type DestinationType { get; } = destinationType;
    public Type ConverterType { get; } = converterType;
}
