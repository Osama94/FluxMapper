using System.Reflection;
using FluxMapper.Abstractions;
using FluxMapper.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FluxMapper.Extensions.DependencyInjection;

/// <summary>
/// Registers a <see cref="MapperConfiguration"/>
/// (always a singleton -- it is immutable once built, per <see cref="MapperConfiguration"/>'s own doc
/// comment, so there is never a reason to rebuild it per request) and an <see cref="IMapper"/> resolved
/// from it via <see cref="MapperConfiguration.CreateMapper"/>, passing the container's
/// <see cref="IServiceProvider"/> through so <c>IValueResolver&lt;&gt;</c>/<c>IValueConverter&lt;&gt;</c>
/// instances configured with <c>.ResolveUsing&lt;&gt;()</c>/<c>ConvertUsing&lt;&gt;()</c> can themselves
/// take constructor-injected dependencies instead of always requiring a parameterless constructor.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers FluxMapper against <paramref name="services"/>. <paramref name="mapperLifetime"/>
    /// defaults to <see cref="ServiceLifetime.Singleton"/> (the common case: no resolver has a scoped
    /// dependency). Pick <see cref="ServiceLifetime.Scoped"/> instead when at least one
    /// <c>IValueResolver&lt;&gt;</c>/<c>IValueConverter&lt;&gt;</c> registered in <paramref name="configure"/>
    /// takes a scoped dependency (e.g. a scoped DbContext) in its constructor -- see the scoping caveat on
    /// <c>CompiledMapperFactory</c>'s doc comment: a resolver's dependencies are resolved once, when the
    /// compiled delegate for its (source,destination) pair is first built by a given <see cref="IMapper"/>
    /// instance, so a Singleton-lifetime <see cref="IMapper"/> would otherwise capture request 1's scoped
    /// instance and keep reusing it for every later request.
    /// </summary>
    public static IServiceCollection AddFluxMapper(
        this IServiceCollection services,
        Action<MapperConfigurationExpression> configure,
        ServiceLifetime mapperLifetime = ServiceLifetime.Singleton)
    {
        ArgumentGuard.ThrowIfNull(services, nameof(services));
        ArgumentGuard.ThrowIfNull(configure, nameof(configure));

        var configuration = MapperConfiguration.Create(configure);
        services.AddSingleton(configuration);
        services.Add(new ServiceDescriptor(
            typeof(IMapper),
            sp => configuration.CreateMapper(sp),
            mapperLifetime));

        return services;
    }

    /// <summary>
    /// Overload for a caller who already built a <see cref="MapperConfiguration"/> elsewhere (e.g. shared
    /// between a web app and a background worker) and just wants it (and an <see cref="IMapper"/> over
    /// it) registered, rather than building a new one from a configuration callback.
    /// </summary>
    public static IServiceCollection AddFluxMapper(
        this IServiceCollection services,
        MapperConfiguration configuration,
        ServiceLifetime mapperLifetime = ServiceLifetime.Singleton)
    {
        ArgumentGuard.ThrowIfNull(services, nameof(services));
        ArgumentGuard.ThrowIfNull(configuration, nameof(configuration));

        services.AddSingleton(configuration);
        services.Add(new ServiceDescriptor(
            typeof(IMapper),
            sp => configuration.CreateMapper(sp),
            mapperLifetime));

        return services;
    }

    /// <summary>
    /// Scans <paramref name="assemblies"/> for <see cref="Profile"/> types and registers every map they
    /// contain, mirroring AutoMapper's <c>services.AddAutoMapper(Assembly.GetExecutingAssembly())</c>
    /// convention — the common case is "one call, at startup, with the executing assembly."
    /// </summary>
    public static IServiceCollection AddFluxMapper(
        this IServiceCollection services,
        ServiceLifetime mapperLifetime,
        params Assembly[] assemblies)
        => services.AddFluxMapper(cfg => cfg.AddMaps(assemblies), mapperLifetime);

    /// <summary>Same as the <see cref="ServiceLifetime"/>-taking overload, defaulting to <see cref="ServiceLifetime.Singleton"/>.</summary>
    public static IServiceCollection AddFluxMapper(this IServiceCollection services, params Assembly[] assemblies)
        => services.AddFluxMapper(ServiceLifetime.Singleton, assemblies);
}
