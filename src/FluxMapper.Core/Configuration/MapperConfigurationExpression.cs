using FluxMapper.Core.Conventions;

namespace FluxMapper.Core.Configuration;

/// <summary>
/// The <c>cfg</c> parameter of <c>MapperConfiguration.Create(cfg => ...)</c>
///. Also the <see cref="ITypeMapConfigurationProvider"/>
/// the plan builder queries while recursing into nested/collection type pairs.
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
}
