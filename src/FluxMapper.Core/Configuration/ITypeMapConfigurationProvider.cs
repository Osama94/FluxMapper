namespace FluxMapper.Core.Configuration;

/// <summary>
/// Lets the plan builder look up explicit configuration for a type pair it's recursing into (a nested
/// or collection-element mapping might itself have its own explicit <c>CreateMap</c> registration).
/// Implemented by <see cref="MapperConfigurationExpression"/>.
/// </summary>
public interface ITypeMapConfigurationProvider
{
    TypeMapConfiguration? Get(Type sourceType, Type destinationType);

    /// <summary>
    /// Every explicitly registered (source, destination) pair, so the plan builder
    /// can discover subtype registrations for polymorphic dispatch (a base-type plan needs to know about
    /// every subtype pair someone registered, not just look one up by exact type).
    /// </summary>
    IReadOnlyCollection<TypeMapConfiguration> AllRegisteredMaps { get; }

    /// <summary>
    /// Looks up a converter registered globally for the exact (<paramref name="sourceValueType"/>,
    /// <paramref name="destinationValueType"/>) pair -- see <c>MapperConfigurationExpression.RegisterConverter</c>.
    /// Consulted by <c>Building.MappingPlanBuilder</c> for any member whose resolved value types exactly
    /// match, so a recurring conversion (a custom Money type, a string-backed ID, a legacy enum shape)
    /// doesn't need repeating on every member that touches it. An explicit per-member override always
    /// takes precedence over this default.
    /// </summary>
    bool TryGetGlobalConverter(Type sourceValueType, Type destinationValueType, out (Type ConverterType, object? Instance) converter);
}
