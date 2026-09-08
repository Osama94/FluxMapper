using System.Reflection;
using FluxMapper.Core.Conventions;
using FluxMapper.Core.Internal;

namespace FluxMapper.Core.Configuration;

/// <summary>
/// The <c>cfg</c> parameter of <c>MapperConfiguration.Create(cfg => ...)</c>. Also the
/// <see cref="ITypeMapConfigurationProvider"/> the plan builder queries while recursing into
/// nested/collection type pairs.
/// </summary>
public sealed class MapperConfigurationExpression : ITypeMapConfigurationProvider
{
    private readonly Dictionary<(Type Source, Type Destination), TypeMapConfiguration> _configs = [];

    /// <summary><c>cfg.Naming.RecognizePrefix(...)</c> — applies to every map registered after it's configured.</summary>
    public NamingConvention Naming { get; private set; } = NamingConvention.Default;

    public MapperConfigurationExpression UseNamingConvention(NamingConvention naming)
    {
        Naming = naming;
        return this;
    }

    public IMappingExpression<TSource, TDestination> CreateMap<TSource, TDestination>()
    {
        var key = (typeof(TSource), typeof(TDestination));
        if (!_configs.TryGetValue(key, out var config))
        {
            config = new TypeMapConfiguration(typeof(TSource), typeof(TDestination)) { Naming = Naming };
            _configs[key] = config;
        }
        return new MappingExpression<TSource, TDestination>(config, this);
    }

    public TypeMapConfiguration? Get(Type sourceType, Type destinationType)
        => _configs.GetValueOrDefault((sourceType, destinationType));

    public IReadOnlyCollection<TypeMapConfiguration> AllRegisteredMaps => _configs.Values;

    internal IReadOnlyCollection<TypeMapConfiguration> RegisteredMaps => _configs.Values;

    /// <summary>
    /// Merges every <c>CreateMap</c> registered by <paramref name="profile"/> into this configuration, mirroring
    /// AutoMapper's <c>cfg.AddProfile(new SomeProfile())</c>. A map already registered directly on this
    /// configuration for the same source/destination pair is overwritten by the profile's version.
    /// </summary>
    public MapperConfigurationExpression AddProfile(Profile profile)
    {
        ArgumentGuard.ThrowIfNull(profile, nameof(profile));
        foreach (var config in profile.RegisteredMaps)
        {
            _configs[(config.SourceType, config.DestinationType)] = config;
        }
        return this;
    }

    /// <summary>Instantiates <typeparamref name="TProfile"/> with its parameterless constructor and merges it in.</summary>
    public MapperConfigurationExpression AddProfile<TProfile>() where TProfile : Profile, new()
        => AddProfile(new TProfile());

    /// <summary>
    /// Scans <paramref name="assembly"/> for every non-abstract <see cref="Profile"/> with a parameterless
    /// constructor and merges each one in, mirroring AutoMapper's
    /// <c>services.AddAutoMapper(Assembly.GetExecutingAssembly())</c> convention.
    /// </summary>
    public MapperConfigurationExpression AddMaps(Assembly assembly) => AddMaps([assembly]);

    /// <summary>Scans each of <paramref name="assemblies"/> for <see cref="Profile"/> types and merges them in.</summary>
    public MapperConfigurationExpression AddMaps(params Assembly[] assemblies)
    {
        ArgumentGuard.ThrowIfNull(assemblies, nameof(assemblies));
        foreach (var assembly in assemblies)
        {
            foreach (var type in assembly.GetTypes())
            {
                if (type.IsAbstract || type.IsInterface || !typeof(Profile).IsAssignableFrom(type))
                {
                    continue;
                }

                if (type.GetConstructor(Type.EmptyTypes) is null)
                {
                    continue;
                }

                AddProfile((Profile)Activator.CreateInstance(type)!);
            }
        }
        return this;
    }
}
