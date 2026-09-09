using System.Reflection;
using FluxMapper.Abstractions;
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
    private readonly Dictionary<(Type Source, Type Destination), (Type ConverterType, object? Instance)> _globalConverters = [];

    /// <summary><c>cfg.Naming.RecognizePrefix(...)</c> — applies to every map registered after it's configured.</summary>
    public NamingConvention Naming { get; private set; } = NamingConvention.Default;

    public MapperConfigurationExpression UseNamingConvention(NamingConvention naming)
    {
        Naming = naming;
        return this;
    }

    /// <summary>
    /// Registers <paramref name="converter"/> for every member, across every map, whose resolved source
    /// value type is exactly <typeparamref name="TSource"/> and destination value type is exactly
    /// <typeparamref name="TDestination"/> -- AutoMapper's
    /// <c>CreateMap&lt;TSource,TDestination&gt;().ConvertUsing(...)</c> equivalent, applied globally
    /// rather than repeating a per-member resolver on every member that happens to touch this pair (a
    /// custom <c>Money</c> type, a string-backed ID, a legacy enum shape). An explicit per-member override
    /// (<c>ResolveUsing</c>/<c>ProjectUsing</c>/an explicit <c>.Map(...)</c> expression) always takes
    /// precedence over this default for that one member -- see <c>Building.MappingPlanBuilder</c>. Not
    /// merged by <see cref="AddProfile(Profile)"/>: register global converters on the top-level
    /// configuration, not inside a <see cref="Profile"/>.
    /// </summary>
    public MapperConfigurationExpression RegisterConverter<TSource, TDestination>(IValueConverter<TSource, TDestination> converter)
    {
        ArgumentGuard.ThrowIfNull(converter, nameof(converter));
        _globalConverters[(typeof(TSource), typeof(TDestination))] = (converter.GetType(), converter);
        return this;
    }

    /// <summary>
    /// Type-based sibling of <see cref="RegisterConverter{TSource,TDestination}(IValueConverter{TSource,TDestination})"/>:
    /// registers <typeparamref name="TConverter"/> to be resolved per the same DI-first/Activator-fallback
    /// rule every other resolver/converter type already uses, instead of sharing one fixed instance --
    /// use this overload when the converter has per-resolution dependencies (DI-injected services) rather
    /// than being safe to construct once and reuse forever.
    /// </summary>
    public MapperConfigurationExpression RegisterConverter<TSource, TDestination, TConverter>()
        where TConverter : IValueConverter<TSource, TDestination>
    {
        _globalConverters[(typeof(TSource), typeof(TDestination))] = (typeof(TConverter), null);
        return this;
    }

    /// <inheritdoc/>
    public bool TryGetGlobalConverter(Type sourceValueType, Type destinationValueType, out (Type ConverterType, object? Instance) converter)
        => _globalConverters.TryGetValue((sourceValueType, destinationValueType), out converter);

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
