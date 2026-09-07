namespace FluxMapper.Tests.Model;

// ---- Happy path: flat + nested + collection -------------------------------------------------
public class Address
{
    public string City { get; set; } = "";
    public string Street { get; set; } = "";
}

public class AddressDto
{
    public string City { get; set; } = "";
}

public class Order
{
    public int Id { get; set; }
    public decimal Total { get; set; }
}

public class OrderDto
{
    public int Id { get; set; }
    public decimal Total { get; set; }
}

public class User
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public Address Address { get; set; } = new();
    public List<Order> Orders { get; set; } = [];
}

public class UserDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public AddressDto Address { get; set; } = new();
    public List<OrderDto> Orders { get; set; } = [];
}

// ---- Ambiguity: two equally-good flattening candidates for "ACName" -------------------------
public class AC
{
    public string Name { get; set; } = "";
}

public class A2
{
    public string CName { get; set; } = "";
}

public class AmbiguousRoot
{
    public AC AC { get; set; } = new();
    public A2 A { get; set; } = new();
}

public class AmbiguousDto
{
    public string ACName { get; set; } = "";
}

// ---- Nullability policy -----------------------------------------------------------------------
public class Person
{
    public string? Nickname { get; set; }
}

public class PersonDto
{
    public string Nickname { get; set; } = "";
}

// ---- Ignore / Condition / update-in-place -----------------------------------------------------
public class Patch
{
    public string Name { get; set; } = "";
    public bool AllowNameChange { get; set; }
    public int Value { get; set; }
}

public class Entity
{
    public string Name { get; set; } = "";
    public string Secret { get; set; } = "";
    public int Value { get; set; }
}

// ---- ReverseMap reversibility -------------------------------------------------------------------
public class Nested
{
    public string Val { get; set; } = "";
}

public class FlattenSrc
{
    public Nested N { get; set; } = new();
}

public class FlattenDst
{
    public string NVal { get; set; } = "";
}

// ---- Dictionaries ---------------------------------------------------
public class Tag
{
    public string Label { get; set; } = "";
}

public class TagDto
{
    public string Label { get; set; } = "";
}

public class Catalog
{
    public Dictionary<string, int> Counts { get; set; } = [];
    public Dictionary<string, Tag> Tags { get; set; } = [];
}

public class CatalogDto
{
    public Dictionary<string, int> Counts { get; set; } = [];
    public Dictionary<string, TagDto> Tags { get; set; } = [];
}

// ---- Immutable collections --------------------------------------------
public class Roster
{
    public List<string> Names { get; set; } = [];
    public List<string> NamesAsList { get; set; } = [];
    public List<string> NamesAsSet { get; set; } = [];
}

public class RosterDto
{
    public System.Collections.Immutable.ImmutableArray<string> Names { get; set; }
    public System.Collections.Immutable.ImmutableList<string> NamesAsList { get; set; } = System.Collections.Immutable.ImmutableList<string>.Empty;
    public System.Collections.Immutable.ImmutableHashSet<string> NamesAsSet { get; set; } = System.Collections.Immutable.ImmutableHashSet<string>.Empty;
}

// ---- Polymorphism -------------------------------------------
public class Animal
{
    public string Name { get; set; } = "";
}

public class Dog : Animal
{
    public string Breed { get; set; } = "";
}

public class Cat : Animal
{
    public bool Indoor { get; set; }
}

public class AnimalDto
{
    public string Name { get; set; } = "";
}

public class DogDto : AnimalDto
{
    public string Breed { get; set; } = "";
}

public class CatDto : AnimalDto
{
    public bool Indoor { get; set; }
}

public class Shelter
{
    public Animal Pet { get; set; } = new();
}

public class ShelterDto
{
    public AnimalDto Pet { get; set; } = new();
}

// ---- Reference preservation -------------------------------------------
public class Employee
{
    public string Name { get; set; } = "";
    public Employee? Manager { get; set; }
    public List<Employee> Reports { get; set; } = [];
}

public class EmployeeDto
{
    public string Name { get; set; } = "";
    public EmployeeDto? Manager { get; set; }
    public List<EmployeeDto> Reports { get; set; } = [];
}

public class Team
{
    public List<Employee> Members { get; set; } = [];
}

public class TeamDto
{
    public List<EmployeeDto> Members { get; set; } = [];
}

// ---- Projection ----------------------------------------------
public class Contact
{
    public string First { get; set; } = "";
    public string Last { get; set; } = "";
    public Address Address { get; set; } = new();
    public List<Order> Orders { get; set; } = [];
}

public class ContactDto
{
    public string First { get; set; } = "";
    public string FullName { get; set; } = "";
    public AddressDto Address { get; set; } = new();
    public List<OrderDto> Orders { get; set; } = [];
}

public sealed class FullNameProjectionResolver : FluxMapper.Abstractions.IProjectionValueResolver<Contact, string>
{
    public System.Linq.Expressions.Expression<Func<Contact, string>> GetExpression() => c => c.First + " " + c.Last;
}

// A destination member whose only candidate is a runtime-only IValueResolver<> -- used to verify
// projectionValidator rejects it with a clear diagnostic instead of ProjectTo() throwing a confusing
// provider-level exception.
public class ScoreLabelResolver : FluxMapper.Abstractions.IValueResolver<Contact, ContactWithScoreDto, string>
{
    public string Resolve(Contact source, ContactWithScoreDto destination, string destinationMember, FluxMapper.Abstractions.ResolutionContext context)
        => source.First.Length > 3 ? "long" : "short";
}

public class ContactWithScoreDto
{
    public string First { get; set; } = "";
    public string ScoreLabel { get; set; } = "";
}

// ---- Source generator -----------------------------
// [MapFrom] is FluxMapper.SourceGenerator's trigger attribute (loaded as an Analyzer in
// FluxMapper.Tests.csproj); the .MapFrom(Order) static method below does not exist anywhere in this
// file -- it is emitted into GeneratedOrderDto.MapFrom.g.cs at compile time. If the generator did not
// run, or generated something that doesn't compile, this project would fail to build, not merely fail
// a runtime assertion -- the strongest verification available for "did source generation actually work."
[FluxMapper.Abstractions.MapFrom(typeof(Order))]
public partial class GeneratedOrderDto
{
    public int Id { get; set; }
    public decimal Total { get; set; }
}

// ---- DI integration ---------------------------
// IGreetingService/GreetingResolver exist specifically to prove AddFluxMapper's IServiceProvider reaches
// real resolver construction: GreetingResolver has NO parameterless constructor at all, so
// activator.CreateInstance(typeof(GreetingResolver)) alone (without DI) would throw --
// only a DI-aware Mapper (built via AddFluxMapper/MapperConfiguration.CreateMapper(services)) can
// construct it, by resolving IGreetingService from the container first.
public interface IGreetingService
{
    string Greet(string name);
}

public sealed class GreetingService : IGreetingService
{
    public string Greet(string name) => $"Hello, {name}!";
}

public sealed class GreetingResolver(IGreetingService greetingService)
    : FluxMapper.Abstractions.IValueResolver<Contact, GreetingDto, string>
{
    public string Resolve(Contact source, GreetingDto destination, string destinationMember, FluxMapper.Abstractions.ResolutionContext context)
        => greetingService.Greet(source.First);
}

public class GreetingDto
{
    public string First { get; set; } = "";
    public string Greeting { get; set; } = "";
}
