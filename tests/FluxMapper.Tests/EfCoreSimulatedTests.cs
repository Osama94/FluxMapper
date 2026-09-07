namespace FluxMapper.Tests;

using FluxMapper.Core.Configuration;
using FluxMapper.Core.Projection;
using FluxMapper.Tests.Model;

// EF Core integration -- FluxMapper's projection tier takes zero EF-Core-specific code:
// ProjectTo<TDestination>() operates purely against System.Linq.IQueryable/Expression<Func<,>>, and EF
// Core's DbSet<T> already IS an IQueryable<T>, so `dbContext.Set<Contact>().ProjectTo<ContactDto>(config)`
// needs nothing beyond the projection engine itself. See StrictQueryProvider.cs's doc comment for why
// this uses a hand-rolled strict IQueryable/IQueryProvider rather than a live EF Core provider.
public class EfCoreSimulatedTests
{
    private static readonly (bool noError, int rowCount, bool nestedAndResolvedMemberCorrect, bool negativeControlRejects) Result = Compute();

    private static (bool, int, bool, bool) Compute()
    {
        var config = MapperConfiguration.Create(cfg =>
        {
            cfg.CreateMap<Address, AddressDto>();
            cfg.CreateMap<Order, OrderDto>();
            cfg.CreateMap<Contact, ContactDto>().ProjectUsing<string, FullNameProjectionResolver>(d => d.FullName);
        });
        config.AssertConfigurationIsValid();

        var strictContacts = StrictTranslationQueryable.Wrap(new List<Contact>
        {
            new Contact { First = "Ada", Last = "Lovelace", Address = new Address { City = "London", Street = "X" }, Orders = [new Order { Id = 1, Total = 5m }] },
            new Contact { First = "Grace", Last = "Hopper", Address = new Address { City = "NYC", Street = "Y" }, Orders = [] },
        });

        Exception? strictError = null;
        List<ContactDto> strictResults = [];
        try { strictResults = strictContacts.ProjectTo<ContactDto>(config).ToList(); }
        catch (Exception ex) { strictError = ex; }

        var noError = strictError is null;
        var rowCount = strictResults.Count;
        var nestedAndResolvedMemberCorrect = strictResults.Count == 2 && strictResults[0].FullName == "Ada Lovelace" && strictResults[0].Address.City == "London";

        // Negative control: the strict provider must actually be capable of catching a genuinely
        // untranslatable shape. Contains() is real LINQ but deliberately absent from the allowlist.
        Exception? negativeControlError = null;
        try { _ = strictContacts.Select(c => c.Orders.Contains(null!)).ToList(); }
        catch (Exception ex) { negativeControlError = ex; }
        var negativeControlRejects = negativeControlError is NotSupportedException;

        return (noError, rowCount, nestedAndResolvedMemberCorrect, negativeControlRejects);
    }

    [Fact]
    public void Projection_SurvivesAStrictEfTranslatorShapedStructuralCheck() => Assert.True(Result.noError);

    [Fact]
    public void RowCount_IsPreservedThroughTheStrictProvider() => Assert.Equal(2, Result.rowCount);

    [Fact]
    public void NestedAndCustomResolvedMember_CorrectThroughTheStrictProvider() => Assert.True(Result.nestedAndResolvedMemberCorrect);

    [Fact]
    public void StrictProvider_GenuinelyRejectsAnOutOfAllowlistMethodCall_NegativeControl() => Assert.True(Result.negativeControlRejects);
}
