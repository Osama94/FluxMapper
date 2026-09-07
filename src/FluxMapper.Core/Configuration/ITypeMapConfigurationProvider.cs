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
}
