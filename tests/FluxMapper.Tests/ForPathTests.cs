namespace FluxMapper.Tests;

using FluxMapper.Abstractions;
using FluxMapper.Core.Configuration;
using FluxMapper.Core.Execution;
using FluxMapper.Tests.Model;

// Coverage for ForPath -- destination-path mapping (d => d.Address.City) for the case ordinary
// convention-based nested mapping cannot handle at all: a destination-side nested type with no
// corresponding source object (dest.Company.SaudiAddress vs src.Company.NationalAddress is the real
// usage this models). See Model.cs's CompanySource/CompanyDestination/SaudiAddress for the shapes.
public class ForPathTests
{
    private static MapperConfiguration BuildConfig() => MapperConfiguration.Create(cfg => cfg
        .CreateMap<CompanySource, CompanyDestination>()
        .ForPath(d => d.SaudiAddress.City, opt => opt.MapFrom(s => s.NationalAddress.CityName))
        .ForPath(d => d.SaudiAddress.PostalCode, opt => opt.MapFrom(s => s.NationalAddress.Zip)));

    [Fact]
    public void Config_ValidatesCleanly()
    {
        var config = BuildConfig();
        var ex = Record.Exception(config.AssertConfigurationIsValid);
        Assert.Null(ex);
    }

    [Fact]
    public void ForPath_MapsIntoMismatchedNestedDestinationPath()
    {
        IMapper mapper = new Mapper(BuildConfig());

        var dto = mapper.Map<CompanySource, CompanyDestination>(new CompanySource
        {
            Name = "Acme",
            NationalAddress = new NationalAddress { CityName = "Riyadh", Zip = "12345" },
        });

        // The ordinary top-level member (Name) still maps by convention alongside the two
        // ForPath-registered leaves -- ForPath only takes over the one destination member it targets.
        Assert.Equal("Acme", dto.Name);
        Assert.Equal("Riyadh", dto.SaudiAddress.City);
        Assert.Equal("12345", dto.SaudiAddress.PostalCode);
    }

    [Fact]
    public void ForPath_LeavesUncoveredSiblingMemberAtItsDefault()
    {
        // SaudiAddress.Region has no ForPath registration of its own -- it must keep the value its
        // own field initializer gives it on the freshly-constructed instance, never validated or
        // convention-matched against anything on the source side.
        IMapper mapper = new Mapper(BuildConfig());

        var dto = mapper.Map<CompanySource, CompanyDestination>(new CompanySource
        {
            NationalAddress = new NationalAddress { CityName = "Jeddah", Zip = "54321" },
        });

        Assert.Equal("unmapped-default", dto.SaudiAddress.Region);
    }

    [Fact]
    public void MultipleForPathCallsSharingPrefix_ConstructTheSharedSubtreeOnlyOnce()
    {
        CountingAddress.ConstructedCount = 0;

        var config = MapperConfiguration.Create(cfg => cfg
            .CreateMap<CompanySource, CompanyWithCountingAddress>()
            .ForPath(d => d.SaudiAddress.City, opt => opt.MapFrom(s => s.NationalAddress.CityName))
            .ForPath(d => d.SaudiAddress.PostalCode, opt => opt.MapFrom(s => s.NationalAddress.Zip)));

        IMapper mapper = new Mapper(config);
        var dto = mapper.Map<CompanySource, CompanyWithCountingAddress>(new CompanySource
        {
            NationalAddress = new NationalAddress { CityName = "Dammam", Zip = "99999" },
        });

        Assert.Equal("Dammam", dto.SaudiAddress.City);
        Assert.Equal("99999", dto.SaudiAddress.PostalCode);

        // If each ForPath leaf built its own SaudiAddress instead of merging into one shared subtree,
        // this would be 2 -- one construction per leaf -- instead of 1.
        Assert.Equal(1, CountingAddress.ConstructedCount);
    }

    [Fact]
    public void ForPath_TargetWithNoParameterlessConstructor_ReportsMAP0004()
    {
        var config = MapperConfiguration.Create(cfg => cfg
            .CreateMap<CompanySource, CompanyWithUnconstructibleDestination>()
            .ForPath(d => d.SaudiAddress.City, opt => opt.MapFrom(s => s.NationalAddress.CityName)));

        var ex = Assert.Throws<ConfigurationValidationException>(config.AssertConfigurationIsValid);

        Assert.Contains(ex.Diagnostics, d => d.Code == "MAP0004");
    }

    [Fact]
    public void GetMemberPath_RejectsASingleHopSelector()
    {
        // A single-member "path" doesn't need ForPath semantics at all -- GetMemberPath (the helper
        // ForPath itself is built on) must reject it with a clear message rather than let the rest of
        // the ForPath machinery try to treat the member's own type as an intermediate branch object.
        var ex = Record.Exception(() => MapperConfiguration.Create(cfg => cfg
            .CreateMap<CompanySource, CompanyDestination>()
            .ForPath(d => d.Name, opt => opt.MapFrom(s => s.Name))));

        Assert.IsType<ArgumentException>(ex);
    }
}
