using FluxMapper.Core.Conventions;

namespace FluxMapper.Core.Configuration;

/// <summary>
/// Base class for grouping related <c>CreateMap</c> calls into one reusable, discoverable unit, mirroring
/// AutoMapper's <c>Profile</c>. Derive from this class, call <see cref="CreateMap{TSource,TDestination}"/>
/// from the constructor (or an instance initializer) for each pair you want to map, and register the
/// profile with <see cref="MapperConfigurationExpression.AddProfile(Profile)"/>,
/// <see cref="MapperConfigurationExpression.AddProfile{TProfile}"/>, or the assembly-scanning
/// <see cref="MapperConfigurationExpression.AddMaps(System.Reflection.Assembly)"/> overloads. A profile
/// needs a public parameterless constructor to be picked up by assembly scanning.
/// </summary>
public abstract class Profile
{
    private readonly MapperConfigurationExpression _expression = new();

    /// <summary>Sets the naming convention used by maps registered through this profile from this point on.</summary>
    protected Profile UseNamingConvention(NamingConvention naming)
    {
        _expression.UseNamingConvention(naming);
        return this;
    }

    /// <summary>Registers a source/destination pair, exactly as <see cref="MapperConfigurationExpression.CreateMap{TSource,TDestination}"/> does.</summary>
    protected IMappingExpression<TSource, TDestination> CreateMap<TSource, TDestination>()
        => _expression.CreateMap<TSource, TDestination>();

    /// <summary>The maps this profile has accumulated, read by <see cref="MapperConfigurationExpression.AddProfile(Profile)"/>.</summary>
    internal IReadOnlyCollection<TypeMapConfiguration> RegisteredMaps => _expression.AllRegisteredMaps;
}
