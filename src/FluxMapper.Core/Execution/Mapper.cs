using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using FluxMapper.Abstractions;
using FluxMapper.Core.Configuration;
using FluxMapper.Core.Explain;

namespace FluxMapper.Core.Execution;

/// <summary>
/// The <see cref="IMapper"/> implementation. Never a global static — obtained from a <see cref="MapperConfiguration"/> (directly via
/// <see cref="MapperConfiguration.CreateMapper"/>, or via DI through
/// FluxMapper.Extensions.DependencyInjection's <c>AddFluxMapper</c>) and held by the caller.
/// The compiled-delegate cache here is scoped to this
/// <see cref="Mapper"/> instance (in turn scoped to one <see cref="MapperConfiguration"/>), which
/// already satisfies "no single process-wide static cache" for the common case; a pluggable/clearable
/// cache for collectible-<c>AssemblyLoadContext</c> hosts is a possible future extension.
///
/// <paramref name="services"/> is optional and defaults to null so a caller that never touches DI is
/// unaffected. When supplied, it is handed to <see cref="CompiledMapperFactory"/> so <c>IValueResolver&lt;&gt;</c>
/// and <c>IValueConverter&lt;&gt;</c> instances are obtained via <c>services.GetService(type)</c> (falling
/// back to a parameterless constructor) instead of always going straight to
/// <see cref="Activator.CreateInstance(Type)"/> — see that class's doc comment for the scoping caveat
/// this implies (a resolver's dependencies are captured once, from whichever provider this specific
/// <see cref="Mapper"/> instance was built with).
/// </summary>
public sealed class Mapper(MapperConfiguration configuration, IServiceProvider? services = null) : IMapper
{
    private readonly ConcurrentDictionary<(Type, Type), Func<object?, object?>> _constructCache = new();
    private readonly ConcurrentDictionary<(Type, Type), Action<object?, object>> _updateCache = new();

    [RequiresDynamicCode("Mode A/B mapping compiles System.Linq.Expressions trees via Expression.Compile(), which requires a JIT and is not supported when publishing Native AOT. Use the FluxMapper.SourceGenerator [MapFrom] path for an AOT-safe alternative.")]
    [RequiresUnreferencedCode("Mode A/B mapping discovers mapped members via reflection over the source/destination types, which trimming can remove. Use the FluxMapper.SourceGenerator [MapFrom] path for a trim-safe alternative.")]
    public TDestination Map<TDestination>(object source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Map<object, TDestination>(source);
    }

    [RequiresDynamicCode("Mode A/B mapping compiles System.Linq.Expressions trees via Expression.Compile(), which requires a JIT and is not supported when publishing Native AOT. Use the FluxMapper.SourceGenerator [MapFrom] path for an AOT-safe alternative.")]
    [RequiresUnreferencedCode("Mode A/B mapping discovers mapped members via reflection over the source/destination types, which trimming can remove. Use the FluxMapper.SourceGenerator [MapFrom] path for a trim-safe alternative.")]
    public TDestination Map<TSource, TDestination>(TSource source)
    {
        if (source is null) return default!;

        var sourceType = source.GetType();
        var destinationType = typeof(TDestination);
        var del = _constructCache.GetOrAdd((sourceType, destinationType),
            key => CompiledMapperFactory.BuildConstructDelegate(configuration.GetPlan(key.Item1, key.Item2), services));

        return (TDestination)del(source)!;
    }

    [RequiresDynamicCode("Update-in-place mapping compiles System.Linq.Expressions trees via Expression.Compile(), which requires a JIT and is not supported when publishing Native AOT.")]
    [RequiresUnreferencedCode("Update-in-place mapping discovers mapped members via reflection over the source/destination types, which trimming can remove.")]
    public TDestination Map<TSource, TDestination>(TSource source, TDestination destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (source is null) return destination;

        var sourceType = source.GetType();
        var destinationType = typeof(TDestination);
        var del = _updateCache.GetOrAdd((sourceType, destinationType),
            key => CompiledMapperFactory.BuildUpdateDelegate(configuration.GetPlan(key.Item1, key.Item2), services));

        del(source, destination);
        return destination;
    }

    [RequiresDynamicCode("Mode A/B mapping compiles System.Linq.Expressions trees via Expression.Compile(), which requires a JIT and is not supported when publishing Native AOT. Use the FluxMapper.SourceGenerator [MapFrom] path for an AOT-safe alternative.")]
    [RequiresUnreferencedCode("Mode A/B mapping discovers mapped members via reflection over the source/destination types, which trimming can remove. Use the FluxMapper.SourceGenerator [MapFrom] path for a trim-safe alternative.")]
    public TDestination Map<TSource, TDestination>(TSource source, Action<IMappingOperationOptions<TSource, TDestination>> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(configureOptions);

        var options = new MappingOperationOptions<TSource, TDestination>();
        configureOptions(options);

        TDestination destination;
        if (options.Items.Count > 0)
        {
            // Only when the caller actually populated Items do we build and push a ResolutionContext --
            // a plain Map() call (or per-call options that only set AfterMap) costs nothing extra, since
            // ResolutionContext.Current stays null and every resolver call site falls back to its existing
            // fresh-context behavior (see CompiledMapperFactory.BuildResolutionContextExpression).
            var context = new ResolutionContext { Services = services };
            foreach (var (key, value) in options.Items)
            {
                context.Items[key] = value;
            }

            using (ResolutionContext.Push(context))
            {
                destination = Map<TSource, TDestination>(source);
            }
        }
        else
        {
            destination = Map<TSource, TDestination>(source);
        }

        if (source is not null)
        {
            options.AfterMapAction?.Invoke(source, destination);
        }

        return destination;
    }

    [RequiresDynamicCode("Mode A/B mapping compiles System.Linq.Expressions trees via Expression.Compile(), which requires a JIT and is not supported when publishing Native AOT. Use the FluxMapper.SourceGenerator [MapFrom] path for an AOT-safe alternative.")]
    [RequiresUnreferencedCode("Mode A/B mapping discovers mapped members via reflection over the source/destination types, which trimming can remove. Use the FluxMapper.SourceGenerator [MapFrom] path for a trim-safe alternative.")]
    public async Task<TDestination> MapAsync<TSource, TDestination>(TSource source, Func<TSource, TDestination, Task> afterMapAsync)
    {
        ArgumentNullException.ThrowIfNull(afterMapAsync);

        var destination = Map<TSource, TDestination>(source);

        if (source is not null)
        {
            await afterMapAsync(source, destination).ConfigureAwait(false);
        }

        return destination;
    }

    public string Explain<TSource, TDestination>()
        => MappingExplanation.Format(configuration.GetPlan(typeof(TSource), typeof(TDestination)));
}

/// <summary>The concrete, mutable options bag <see cref="Mapper.Map{TSource,TDestination}(TSource,Action{IMappingOperationOptions{TSource,TDestination}})"/> hands to the caller's configuration callback.</summary>
internal sealed class MappingOperationOptions<TSource, TDestination> : IMappingOperationOptions<TSource, TDestination>
{
    public Action<TSource, TDestination>? AfterMapAction { get; private set; }

    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>();

    public void AfterMap(Action<TSource, TDestination> afterMap) => AfterMapAction = afterMap;
}
