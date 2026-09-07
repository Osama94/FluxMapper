namespace FluxMapper.Tests;

using System.Reflection;
using FluxMapper.Abstractions;
using FluxMapper.Core.Configuration;
using FluxMapper.Core.Execution;
using FluxMapper.Tests.Model;

// Coverage for the AutoMapper-parity features added on top of the original convention-based engine:
// ForMember, ConstructUsing, BeforeMap/AfterMap (both configured-once and per-call), Profile +
// AddProfile/AddMaps assembly scanning, and MapAsync (the async-safe alternative to AutoMapper's
// Action<TSource,TDestination> AfterMap, which silently drops an async lambda's Task).
public class AutoMapperParityTests
{
    // ---- ForMember / ConstructUsing / BeforeMap / AfterMap, registered via a Profile --------------

    private static readonly (WidgetDto dto, bool validates) ProfileResult = Compute();

    private static (WidgetDto, bool) Compute()
    {
        var config = MapperConfiguration.Create(cfg => cfg.AddProfile<WidgetProfile>());

        Exception? validationError = null;
        try { config.AssertConfigurationIsValid(); } catch (Exception ex) { validationError = ex; }

        var mapper = new Mapper(config);
        var widget = new Widget { Id = 7, Sku = "SKU-7", Price = 12.5m };
        return (mapper.Map<WidgetDto>(widget), validationError is null);
    }

    [Fact]
    public void Profile_RegisteredMap_ValidatesCleanly() => Assert.True(ProfileResult.validates);

    [Fact]
    public void ForMember_MapsFromDifferentlyNamedSourceMember() => Assert.Equal("SKU-7", ProfileResult.dto.ProductCode);

    [Fact]
    public void ConstructUsing_ReplacesAutomaticConstruction() => Assert.Equal("ctor:SKU-7", ProfileResult.dto.ConstructedBy);

    [Fact]
    public void BeforeMap_RunsBeforeMemberAssignment() => Assert.True(ProfileResult.dto.BeforeMapRan);

    [Fact]
    public void AfterMap_RunsAfterMemberAssignment() => Assert.True(ProfileResult.dto.AfterMapRan);

    [Fact]
    public void OrdinaryMembers_StillMapAlongsideCustomConstruction()
    {
        Assert.Equal(7, ProfileResult.dto.Id);
        Assert.Equal(12.5m, ProfileResult.dto.Price);
    }

    // ---- AddProfile(instance) and AddMaps(assembly) reach the same registration -------------------

    [Fact]
    public void AddProfile_InstanceOverload_RegistersTheSameMap()
    {
        var config = MapperConfiguration.Create(cfg => cfg.AddProfile(new WidgetProfile()));
        var mapper = new Mapper(config);

        var dto = mapper.Map<WidgetDto>(new Widget { Id = 1, Sku = "X", Price = 1m });

        Assert.Equal("X", dto.ProductCode);
        Assert.Equal("ctor:X", dto.ConstructedBy);
    }

    [Fact]
    public void AddMaps_ScansAssemblyForProfiles()
    {
        var config = MapperConfiguration.Create(cfg => cfg.AddMaps(Assembly.GetExecutingAssembly()));
        var mapper = new Mapper(config);

        var dto = mapper.Map<WidgetDto>(new Widget { Id = 2, Sku = "Y", Price = 2m });

        Assert.Equal("Y", dto.ProductCode);
        Assert.True(dto.BeforeMapRan);
        Assert.True(dto.AfterMapRan);
    }

    // ---- BeforeMap/AfterMap without ConstructUsing: hooks must also work on the plain-`new()` path ----

    [Fact]
    public void BeforeAfterMap_WorkWithoutConstructUsing()
    {
        var config = MapperConfiguration.Create(cfg => cfg.CreateMap<Widget, WidgetDto>()
            .ForMember(d => d.ProductCode, opt => opt.MapFrom(s => s.Sku))
            .Ignore(d => d.BeforeMapRan)
            .Ignore(d => d.AfterMapRan)
            .BeforeMap((s, d) => d.BeforeMapRan = true)
            .AfterMap((s, d) => d.AfterMapRan = true));

        var mapper = new Mapper(config);
        var dto = mapper.Map<WidgetDto>(new Widget { Id = 3, Sku = "Z", Price = 3m });

        Assert.Equal("default-ctor", dto.ConstructedBy); // plain new() ran, not the custom constructor
        Assert.True(dto.BeforeMapRan);
        Assert.True(dto.AfterMapRan);
    }

    // ---- BeforeMap/AfterMap also fire for update-in-place (Map(source, destination)) ---------------

    [Fact]
    public void BeforeAfterMap_FireOnUpdateInPlaceToo()
    {
        var config = MapperConfiguration.Create(cfg => cfg.CreateMap<Widget, WidgetDto>()
            .ForMember(d => d.ProductCode, opt => opt.MapFrom(s => s.Sku))
            .Ignore(d => d.BeforeMapRan)
            .Ignore(d => d.AfterMapRan)
            .BeforeMap((s, d) => d.BeforeMapRan = true)
            .AfterMap((s, d) => d.AfterMapRan = true));

        var mapper = new Mapper(config);
        var existing = new WidgetDto();

        mapper.Map(new Widget { Id = 4, Sku = "W", Price = 4m }, existing);

        Assert.True(existing.BeforeMapRan);
        Assert.True(existing.AfterMapRan);
        Assert.Equal("W", existing.ProductCode);
    }

    // ---- Per-call AfterMap options: a one-off hook that does not apply to every mapping ------------

    [Fact]
    public void PerCallAfterMap_RunsOnlyForThatOneCall()
    {
        var config = MapperConfiguration.Create(cfg => cfg.CreateMap<Widget, WidgetDto>()
            .ForMember(d => d.ProductCode, opt => opt.MapFrom(s => s.Sku))
            .Ignore(d => d.BeforeMapRan)
            .Ignore(d => d.AfterMapRan));

        IMapper mapper = new Mapper(config);
        var seenSku = "";

        var dto = mapper.Map<Widget, WidgetDto>(
            new Widget { Id = 5, Sku = "PER-CALL", Price = 5m },
            opt => opt.AfterMap((s, d) => seenSku = s.Sku));

        Assert.Equal("PER-CALL", seenSku);
        Assert.Equal("PER-CALL", dto.ProductCode);

        // A second, plain call (no options) must not run the one-off hook -- it was never registered
        // on the map itself, only passed for that single call above.
        var second = mapper.Map<Widget, WidgetDto>(new Widget { Id = 6, Sku = "PLAIN", Price = 6m });
        Assert.Equal("PLAIN", second.ProductCode);
    }

    // ---- MapAsync: the async-safe alternative to AutoMapper's fire-and-forget AfterMap --------------

    [Fact]
    public async Task MapAsync_AwaitsTheAsyncContinuation()
    {
        var config = MapperConfiguration.Create(cfg => cfg.CreateMap<Widget, WidgetDto>()
            .ForMember(d => d.ProductCode, opt => opt.MapFrom(s => s.Sku))
            .Ignore(d => d.BeforeMapRan)
            .Ignore(d => d.AfterMapRan));

        IMapper mapper = new Mapper(config);

        var dto = await mapper.MapAsync<Widget, WidgetDto>(
            new Widget { Id = 8, Sku = "ASYNC", Price = 8m },
            async (s, d) =>
            {
                await Task.Delay(1);
                d.ProductCode = $"{d.ProductCode}-confirmed";
            });

        // If the Task returned by the async continuation were dropped (AutoMapper's Action<> AfterMap
        // footgun), this assertion would race and usually fail -- MapAsync awaits it, so it never does.
        Assert.Equal("ASYNC-confirmed", dto.ProductCode);
    }
}
