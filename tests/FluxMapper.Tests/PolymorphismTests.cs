namespace FluxMapper.Tests;

using FluxMapper.Core.Configuration;
using FluxMapper.Core.Execution;
using FluxMapper.Tests.Model;

// Polymorphic dispatch -- a base-typed member (Animal)
// whose actual runtime type (Dog/Cat) has its own registered map dispatches to that map instead of
// the base Animal -> AnimalDto plan; an actual-base-type instance still falls back to the base plan.
public class PolymorphismTests
{
    private static readonly (bool dogDtoDispatched, string dogBreed, string dogName, bool catDtoDispatched, bool catIndoor, bool basePlanFallback) Result = Compute();

    private static (bool, string, string, bool, bool, bool) Compute()
    {
        var config = MapperConfiguration.Create(cfg =>
        {
            cfg.CreateMap<Animal, AnimalDto>();
            cfg.CreateMap<Dog, DogDto>();
            cfg.CreateMap<Cat, CatDto>();
            cfg.CreateMap<Shelter, ShelterDto>();
        });

        config.AssertConfigurationIsValid();
        var mapper = new Mapper(config);

        var dogShelter = mapper.Map<ShelterDto>(new Shelter { Pet = new Dog { Name = "Rex", Breed = "Husky" } });
        var dogDtoDispatched = dogShelter.Pet is DogDto;
        var dogBreed = ((DogDto)dogShelter.Pet).Breed;
        var dogName = dogShelter.Pet.Name;

        var catShelter = mapper.Map<ShelterDto>(new Shelter { Pet = new Cat { Name = "Tom", Indoor = true } });
        var catDtoDispatched = catShelter.Pet is CatDto;
        var catIndoor = ((CatDto)catShelter.Pet).Indoor;

        var plainShelter = mapper.Map<ShelterDto>(new Shelter { Pet = new Animal { Name = "Generic" } });
        var basePlanFallback = plainShelter.Pet.GetType() == typeof(AnimalDto);

        return (dogDtoDispatched, dogBreed, dogName, catDtoDispatched, catIndoor, basePlanFallback);
    }

    [Fact]
    public void DispatchesToDogDto_ForADog() => Assert.True(Result.dogDtoDispatched);

    [Fact]
    public void DogDtoSpecificMember_IsPopulated() => Assert.Equal("Husky", Result.dogBreed);

    [Fact]
    public void BaseMember_StillPopulatedOnDispatchedSubtype() => Assert.Equal("Rex", Result.dogName);

    [Fact]
    public void DispatchesToCatDto_ForACat() => Assert.True(Result.catDtoDispatched);

    [Fact]
    public void CatDtoSpecificMember_IsPopulated() => Assert.True(Result.catIndoor);

    [Fact]
    public void ExactBaseTypeInstance_FallsBackToBasePlan() => Assert.True(Result.basePlanFallback);
}
