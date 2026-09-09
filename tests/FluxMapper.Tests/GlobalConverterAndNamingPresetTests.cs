namespace FluxMapper.Tests;

using FluxMapper.Abstractions;
using FluxMapper.Core.Configuration;
using FluxMapper.Core.Conventions;
using FluxMapper.Core.Execution;
using FluxMapper.Tests.Model;

// Coverage for two v1.2.0 additions: NamingConvention's ready-made presets (SnakeCase/LowerUnderscore),
// and MapperConfigurationExpression.RegisterConverter's global type-pair converters (both the
// instance-based and the type-based/DI-resolved overload).
public class NamingConventionPresetTests
{
    [Fact]
    public void SnakeCase_MatchesUnderscoredSourceToPascalCaseDestination()
    {
        var config = MapperConfiguration.Create(cfg =>
        {
            cfg.UseNamingConvention(NamingConvention.SnakeCase());
            cfg.CreateMap<SnakeCaseUser, SnakeCaseUserDto>();
        });

        var ex = Record.Exception(config.AssertConfigurationIsValid);
        Assert.Null(ex);

        IMapper mapper = new Mapper(config);
        var dto = mapper.Map<SnakeCaseUserDto>(new SnakeCaseUser { user_name = "Ada", user_age = 36 });

        Assert.Equal("Ada", dto.UserName);
        Assert.Equal(36, dto.UserAge);
    }

    [Fact]
    public void LowerUnderscore_IsEquivalentToSnakeCase()
    {
        var config = MapperConfiguration.Create(cfg =>
        {
            cfg.UseNamingConvention(NamingConvention.LowerUnderscore());
            cfg.CreateMap<SnakeCaseUser, SnakeCaseUserDto>();
        });

        IMapper mapper = new Mapper(config);
        var dto = mapper.Map<SnakeCaseUserDto>(new SnakeCaseUser { user_name = "Grace", user_age = 40 });

        Assert.Equal("Grace", dto.UserName);
        Assert.Equal(40, dto.UserAge);
    }

    [Fact]
    public void WithoutThePreset_UnderscoredNamesDoNotMatch()
    {
        // Control: without SnakeCase()/LowerUnderscore(), NamingConvention.Default has no idea "user_name"
        // and "UserName" are the same identity, so this configuration is NOT valid -- proving the preset
        // above is actually doing something, not just matching by coincidence (both types already declare
        // every member, but the mismatched names are otherwise ordinary source/destination candidates).
        var config = MapperConfiguration.Create(cfg => cfg.CreateMap<SnakeCaseUser, SnakeCaseUserDto>());
        var plan = config.GetPlan<SnakeCaseUser, SnakeCaseUserDto>();

        Assert.False(plan.IsBuildable);
    }
}

public class GlobalConverterTests
{
    [Fact]
    public void InstanceBasedConverter_AppliesToEveryMatchingMemberTypePair()
    {
        var converter = new MoneyToDecimalConverter();
        var config = MapperConfiguration.Create(cfg =>
        {
            cfg.RegisterConverter(converter);
            cfg.CreateMap<Invoice, InvoiceDto>();
        });

        var ex = Record.Exception(config.AssertConfigurationIsValid);
        Assert.Null(ex);

        IMapper mapper = new Mapper(config);
        var dto = mapper.Map<InvoiceDto>(new Invoice { Total = new Money { Amount = 199.99m } });

        Assert.Equal(199.99m, dto.Total);
    }

    [Fact]
    public void InstanceBasedConverter_UsesTheExactRegisteredInstance()
    {
        MoneyToDecimalConverter.InvocationCount = 0;
        var converter = new MoneyToDecimalConverter();
        var config = MapperConfiguration.Create(cfg =>
        {
            cfg.RegisterConverter(converter);
            cfg.CreateMap<Invoice, InvoiceDto>();
        });

        IMapper mapper = new Mapper(config);
        mapper.Map<InvoiceDto>(new Invoice { Total = new Money { Amount = 5m } });
        mapper.Map<InvoiceDto>(new Invoice { Total = new Money { Amount = 10m } });

        Assert.Equal(2, MoneyToDecimalConverter.InvocationCount);
    }

    [Fact]
    public void WithoutARegisteredConverter_IncompatibleTypesFailValidation()
    {
        // Control: Money -> decimal has no implicit conversion and no converter registered here, so this
        // must be reported as MAP0002 rather than silently producing a buildable-but-wrong plan.
        var config = MapperConfiguration.Create(cfg => cfg.CreateMap<Invoice, InvoiceDto>());
        var plan = config.GetPlan<Invoice, InvoiceDto>();

        Assert.False(plan.IsBuildable);
        Assert.Contains(plan.Diagnostics, d => d.Code == "MAP0002");
    }

    [Fact]
    public void TypeBasedConverter_IsResolvedAndApplied()
    {
        var config = MapperConfiguration.Create(cfg =>
        {
            cfg.RegisterConverter<LegacyStatus, string, LegacyStatusConverter>();
            cfg.CreateMap<LegacyStatusHolder, LegacyStatusHolderDto>();
        });

        var ex = Record.Exception(config.AssertConfigurationIsValid);
        Assert.Null(ex);

        IMapper mapper = new Mapper(config);
        var active = mapper.Map<LegacyStatusHolderDto>(new LegacyStatusHolder { Status = new LegacyStatus { Code = 1 } });
        var inactive = mapper.Map<LegacyStatusHolderDto>(new LegacyStatusHolder { Status = new LegacyStatus { Code = 0 } });

        Assert.Equal("Active", active.Status);
        Assert.Equal("Inactive", inactive.Status);
    }
}
