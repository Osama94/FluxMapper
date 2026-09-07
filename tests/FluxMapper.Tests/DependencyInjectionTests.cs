namespace FluxMapper.Tests;

using FluxMapper.Abstractions;
using FluxMapper.Core.Configuration;
using FluxMapper.Extensions.DependencyInjection;
using FluxMapper.Tests.Model;
using Microsoft.Extensions.DependencyInjection;

// DI integration tests -- drive the real Microsoft.Extensions.DependencyInjection
// ServiceCollection/ServiceProvider, not a hand-rolled substitute. GreetingResolver has no
// parameterless constructor at all, so this is a genuine test of DI-driven resolver construction, not
// something Activator.CreateInstance could ever satisfy.
public class DependencyInjectionTests
{
    private static readonly (bool mapperResolved, string greeting, string first, bool configResolved,
        bool failsWithoutServices, bool distinctPerScope, string scopedGreeting) Result = Compute();

    private static (bool, string, string, bool, bool, bool, string) Compute()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IGreetingService, GreetingService>();
        services.AddFluxMapper(cfg =>
            cfg.CreateMap<Contact, GreetingDto>().ResolveUsing<string, GreetingResolver>(d => d.Greeting));

        using var provider = services.BuildServiceProvider();
        var diMapper = provider.GetRequiredService<IMapper>();

        var contact = new Contact { First = "Ada", Last = "Lovelace" };
        var greeted = diMapper.Map<GreetingDto>(contact);
        var mapperResolved = diMapper is not null;
        var configResolved = provider.GetRequiredService<MapperConfiguration>() is not null;

        // Negative control: the exact same configuration, built WITHOUT a services provider, must fail --
        // proving the positive result above came from real DI resolution, not some other accidental path
        // (GreetingResolver's constructor genuinely cannot run via Activator.CreateInstance).
        var noDiConfig = MapperConfiguration.Create(cfg =>
            cfg.CreateMap<Contact, GreetingDto>().ResolveUsing<string, GreetingResolver>(d => d.Greeting));
        var noDiMapper = noDiConfig.CreateMapper(); // services: null
        Exception? noDiError = null;
        try { noDiMapper.Map<GreetingDto>(contact); } catch (Exception ex) { noDiError = ex; }
        var failsWithoutServices = noDiError is not null;

        // Scoped lifetime: two scopes must get two distinct IMapper instances (proving the lifetime argument
        // to AddFluxMapper is actually honored, not silently always-singleton).
        var scopedServices = new ServiceCollection();
        scopedServices.AddSingleton<IGreetingService, GreetingService>();
        scopedServices.AddFluxMapper(
            cfg => cfg.CreateMap<Contact, GreetingDto>().ResolveUsing<string, GreetingResolver>(d => d.Greeting),
            ServiceLifetime.Scoped);
        using var scopedProvider = scopedServices.BuildServiceProvider();
        using var scopeA = scopedProvider.CreateScope();
        using var scopeB = scopedProvider.CreateScope();
        var mapperA = scopeA.ServiceProvider.GetRequiredService<IMapper>();
        var mapperB = scopeB.ServiceProvider.GetRequiredService<IMapper>();
        var distinctPerScope = !ReferenceEquals(mapperA, mapperB);
        var scopedGreeting = mapperA.Map<GreetingDto>(contact).Greeting;

        return (mapperResolved, greeted.Greeting, greeted.First, configResolved, failsWithoutServices, distinctPerScope, scopedGreeting);
    }

    [Fact]
    public void AddFluxMapper_ResolvesIMapperFromTheContainer() => Assert.True(Result.mapperResolved);

    [Fact]
    public void ConstructorInjectedResolverDependency_IsActuallyUsed() => Assert.Equal("Hello, Ada!", Result.greeting);

    [Fact]
    public void PlainMembersAlongsideADiResolver_StillMapNormally() => Assert.Equal("Ada", Result.first);

    [Fact]
    public void MapperConfiguration_IsAlsoResolvable() => Assert.True(Result.configResolved);

    [Fact]
    public void SameResolverWithoutServicesProvider_FailsInsteadOfSilentlySucceeding() => Assert.True(Result.failsWithoutServices);

    [Fact]
    public void ScopedLifetime_YieldsDistinctIMapperPerScope() => Assert.True(Result.distinctPerScope);

    [Fact]
    public void ScopedIMapper_StillMapsCorrectly() => Assert.Equal("Hello, Ada!", Result.scopedGreeting);
}
