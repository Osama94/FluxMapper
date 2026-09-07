namespace FluxMapper.Tests;

using FluxMapper.Core.Configuration;
using FluxMapper.Core.Projection;
using FluxMapper.Tests.Model;
using Microsoft.EntityFrameworkCore;

// A REAL Microsoft.EntityFrameworkCore.InMemory DbContext/DbSet<Contact>, not the
// hand-rolled StrictQueryProvider stand-in EfCoreSimulatedTests.cs uses. This is the strongest available
// verification that FluxMapper's projection tier (ProjectTo<T>()) actually works against a genuine EF
// core query provider end to end, not just a structural approximation of one.
//
// Caveat, stated plainly: EF Core's InMemory provider is notably more permissive than a relational
// provider's actual SQL translator (it never has to produce SQL at all), so a pass here is real signal
// but not as strong a guarantee as running against SQL Server or Sqlite would be. It is still a genuinely
// different, and in some ways stricter, code path than LINQ-to-Objects' List<T>.AsQueryable().
internal sealed class ContactContext(DbContextOptions<ContactContext> options) : DbContext(options)
{
    public DbSet<Contact> Contacts => Set<Contact>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Contact>(b =>
        {
            // Contact has no natural key in this test model; a shadow key is the standard EF Core way to
            // make an otherwise key-less POCO a valid entity type.
            b.Property<int>("Id");
            b.HasKey("Id");
            b.OwnsOne(c => c.Address);
            b.OwnsMany(c => c.Orders); // EF Core's default convention supplies a shadow key here.
        });
    }
}

public class EfCoreRealTests
{
    private static readonly (int rowCount, string firstName, string firstCity, int firstOrderCount,
        int firstOrderId, string fullName0, string fullName1) Result = Compute();

    private static (int, string, string, int, int, string, string) Compute()
    {
        var config = MapperConfiguration.Create(cfg =>
        {
            cfg.CreateMap<Address, AddressDto>();
            cfg.CreateMap<Order, OrderDto>();
            cfg.CreateMap<Contact, ContactDto>().ProjectUsing<string, FullNameProjectionResolver>(d => d.FullName);
        });
        config.AssertConfigurationIsValid();

        var options = new DbContextOptionsBuilder<ContactContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using var context = new ContactContext(options);
        context.Contacts.AddRange(
            new Contact { First = "Ada", Last = "Lovelace", Address = new Address { City = "London", Street = "X" }, Orders = [new Order { Id = 1, Total = 5m }] },
            new Contact { First = "Grace", Last = "Hopper", Address = new Address { City = "NYC", Street = "Y" }, Orders = [] });
        context.SaveChanges();

        // The real point of this test: ProjectTo<ContactDto>() runs against context.Contacts, a genuine
        // dbSet<Contact> whose IQueryable.Provider is EF Core's own -- not List<T>.AsQueryable() and not
        // the hand-rolled StrictQueryProvider.
        var projected = context.Contacts.OrderBy(c => c.First).ProjectTo<ContactDto>(config).ToList();

        return (projected.Count, projected[0].First, projected[0].Address.City, projected[0].Orders.Count,
            projected[0].Orders.Count > 0 ? projected[0].Orders[0].Id : -1,
            projected[0].FullName, projected[1].FullName);
    }

    [Fact]
    public void RowCount_IsPreserved() => Assert.Equal(2, Result.rowCount);

    [Fact]
    public void FlatMember_IsCopied() => Assert.Equal("Ada", Result.firstName);

    [Fact]
    public void OwnedNestedMember_IsProjected() => Assert.Equal("London", Result.firstCity);

    [Fact]
    public void OwnedCollectionMember_IsProjected()
    {
        Assert.Equal(1, Result.firstOrderCount);
        Assert.Equal(1, Result.firstOrderId);
    }

    [Fact]
    public void ProjectionValueResolverMember_TranslatedByRealEfCore()
    {
        Assert.Equal("Ada Lovelace", Result.fullName0);
        Assert.Equal("Grace Hopper", Result.fullName1);
    }
}
