namespace FluxMapper.Tests;

using FluxMapper.Abstractions;
using FluxMapper.Core.Configuration;
using FluxMapper.Core.Projection;
using FluxMapper.Tests.Model;

// Projection -- ProjectTo<T>() over IQueryable, exercised
// against .AsQueryable() (real LINQ-to-Objects query translation, not just LINQ-to-Objects.Select()
// called directly -- the whole point is that this goes through IQueryable.Provider.CreateQuery, the
// same path a real EF Core DbSet<T> would take). Flat + nested + collection projection, a
// translatable IProjectionValueResolver<> member, and rejection of a plan containing a runtime-only
// IValueResolver<> member.
public class ProjectionTests
{
    private static readonly (int rowCount, string firstName, string firstCity, int firstOrderCount, int firstOrderId,
        string fullName0, string fullName1, bool lambdaIsMemberInit, bool runtimeResolverRejected,
        bool rejectionNamesMember, bool dictionaryRootRejected) Result = Compute();

    private static (int, string, string, int, int, string, string, bool, bool, bool, bool) Compute()
    {
        var config = MapperConfiguration.Create(cfg =>
        {
            cfg.CreateMap<Address, AddressDto>();
            cfg.CreateMap<Order, OrderDto>();
            cfg.CreateMap<Contact, ContactDto>().ProjectUsing<string, FullNameProjectionResolver>(d => d.FullName);
        });
        config.AssertConfigurationIsValid();

        var contacts = new List<Contact>
        {
            new Contact { First = "Ada", Last = "Lovelace", Address = new Address { City = "London", Street = "X" }, Orders = [new Order { Id = 1, Total = 5m }] },
            new Contact { First = "Grace", Last = "Hopper", Address = new Address { City = "NYC", Street = "Y" }, Orders = [] },
        }.AsQueryable();

        var projected = contacts.ProjectTo<ContactDto>(config).ToList();

        var lambda = config.GetProjectionExpression<Contact, ContactDto>();
        var lambdaIsMemberInit = lambda.Body.NodeType == System.Linq.Expressions.ExpressionType.MemberInit;

        var badConfig = MapperConfiguration.Create(cfg =>
            cfg.CreateMap<Contact, ContactWithScoreDto>().ResolveUsing<string, ScoreLabelResolver>(d => d.ScoreLabel));

        Exception? projectionError = null;
        try { contacts.ProjectTo<ContactWithScoreDto>(badConfig); }
        catch (Exception ex) { projectionError = ex; }
        var runtimeResolverRejected = projectionError is ProjectionTranslationException;
        var rejectionNamesMember = projectionError is ProjectionTranslationException pte && pte.Diagnostic.Candidates.Any(c => c.Contains("ScoreLabel"));

        var dictConfig = MapperConfiguration.Create(cfg => cfg.CreateMap<Tag, TagDto>());
        var dictSource = new Dictionary<string, Tag> { ["a"] = new Tag { Label = "A" } }.AsQueryable();
        Exception? dictProjectionError = null;
        try { ProjectionValidator.EnsureProjectable(dictConfig.GetPlan<Dictionary<string, Tag>, Dictionary<string, TagDto>>()); }
        catch (Exception ex) { dictProjectionError = ex; }
        var dictionaryRootRejected = dictProjectionError is ProjectionTranslationException;

        return (projected.Count, projected[0].First, projected[0].Address.City, projected[0].Orders.Count, projected[0].Orders[0].Id,
            projected[0].FullName, projected[1].FullName, lambdaIsMemberInit, runtimeResolverRejected, rejectionNamesMember, dictionaryRootRejected);
    }

    [Fact]
    public void RowCount_IsPreserved() => Assert.Equal(2, Result.rowCount);

    [Fact]
    public void FlatMember_IsCopied() => Assert.Equal("Ada", Result.firstName);

    [Fact]
    public void NestedMember_IsProjected() => Assert.Equal("London", Result.firstCity);

    [Fact]
    public void CollectionMember_IsProjected()
    {
        Assert.Equal(1, Result.firstOrderCount);
        Assert.Equal(1, Result.firstOrderId);
    }

    [Fact]
    public void ProjectionValueResolverMember_TranslatedAndEvaluated()
    {
        Assert.Equal("Ada Lovelace", Result.fullName0);
        Assert.Equal("Grace Hopper", Result.fullName1);
    }

    [Fact]
    public void GeneratedLambdaBody_IsMemberInit_NotBlock() => Assert.True(Result.lambdaIsMemberInit);

    [Fact]
    public void RuntimeOnlyValueResolverMember_IsRejected() => Assert.True(Result.runtimeResolverRejected);

    [Fact]
    public void RejectionDiagnostic_NamesTheOffendingMember() => Assert.True(Result.rejectionNamesMember);

    [Fact]
    public void DictionaryRootPlan_IsRejected() => Assert.True(Result.dictionaryRootRejected);
}
