namespace FluxMapper.Tests;

using FluxMapper.Abstractions;
using FluxMapper.Core.Configuration;
using FluxMapper.Core.Execution;
using FluxMapper.Tests.Model;

// Coverage for per-call ResolutionContext.Items and the four-argument contextual MapFrom -- the feature
// that lets a member's value depend on ambient state set once per Map() call (mirrors AutoMapper's
// opt.Items["key"] = value + .MapFrom((src, dest, current, context) => ...) pattern) rather than only the
// source object, which a plain expression-based .Map()/.ForMember(...).MapFrom(expr) cannot express.
public class ContextualResolverTests
{
    private static MapperConfiguration BuildConfig() => MapperConfiguration.Create(cfg => cfg
        .CreateMap<Article, ArticleDto>()
        .ForMember(d => d.TextArWithParams, opt => opt.MapFrom((src, dest, current, context) =>
            context.Items.TryGetValue("EditReasonId", out var v) && v is int id && id == 7
                ? src.TextWithParams
                : "")));

    [Fact]
    public void Config_ValidatesCleanly()
    {
        var config = BuildConfig();
        var ex = Record.Exception(config.AssertConfigurationIsValid);
        Assert.Null(ex);
    }

    [Fact]
    public void PerCallItems_ReachTheContextualResolver()
    {
        IMapper mapper = new Mapper(BuildConfig());

        var dto = mapper.Map<Article, ArticleDto>(
            new Article { TextWithParams = "Hello" },
            opt => opt.Items["EditReasonId"] = 7);

        Assert.Equal("Hello", dto.TextArWithParams);
    }

    [Fact]
    public void PlainMapCall_SeesNoItems_FallsToDefaultBranch()
    {
        IMapper mapper = new Mapper(BuildConfig());

        var dto = mapper.Map<Article, ArticleDto>(new Article { TextWithParams = "Hello" });

        Assert.Equal("", dto.TextArWithParams);
    }

    [Fact]
    public void WrongItemValue_AlsoFallsToDefaultBranch()
    {
        IMapper mapper = new Mapper(BuildConfig());

        var dto = mapper.Map<Article, ArticleDto>(
            new Article { TextWithParams = "Hello" },
            opt => opt.Items["EditReasonId"] = 99);

        Assert.Equal("", dto.TextArWithParams);
    }

    [Fact]
    public void Items_DoNotLeakAcrossCalls()
    {
        IMapper mapper = new Mapper(BuildConfig());

        var withItems = mapper.Map<Article, ArticleDto>(
            new Article { TextWithParams = "First" },
            opt => opt.Items["EditReasonId"] = 7);
        Assert.Equal("First", withItems.TextArWithParams);

        // A later plain call must not still see the previous call's Items -- each per-call
        // ResolutionContext is scoped to exactly one Map() invocation.
        var withoutItems = mapper.Map<Article, ArticleDto>(new Article { TextWithParams = "Second" });
        Assert.Equal("", withoutItems.TextArWithParams);
    }

    [Fact]
    public void FlatResolveUsing_ContextualOverload_AlsoWorks()
    {
        var config = MapperConfiguration.Create(cfg => cfg
            .CreateMap<Article, ArticleDto>()
            .ResolveUsing<string>(d => d.TextArWithParams, (src, dest, current, context) =>
                context.Items.TryGetValue("Suffix", out var v) ? src.TextWithParams + v : src.TextWithParams));

        IMapper mapper = new Mapper(config);

        var dto = mapper.Map<Article, ArticleDto>(
            new Article { TextWithParams = "Base" },
            opt => opt.Items["Suffix"] = "-suffixed");

        Assert.Equal("Base-suffixed", dto.TextArWithParams);
    }
}
