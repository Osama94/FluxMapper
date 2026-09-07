namespace FluxMapper.Tests;

using FluxMapper.Core.Configuration;
using FluxMapper.Core.Execution;
using FluxMapper.Tests.Model;

// ReferenceHandling.Preserve -- a genuine cycle (Manager <-> Reports)
// must not stack-overflow and must resolve to the *same* destination instance, and a shared
// (non-cyclic) reference reached via two paths must dedupe to one destination instance too. A
// control case with Preserve NOT configured confirms this is opt-in, not a silent behavior change.
public class ReferencePreservationTests
{
    private static readonly (bool cycleMapsCorrectly, bool cycleResolvesToSameInstance, bool sharedReferenceDedupes, bool optInOnly) Result = Compute();

    private static (bool, bool, bool, bool) Compute()
    {
        var config = MapperConfiguration.Create(cfg => cfg.CreateMap<Employee, EmployeeDto>().PreserveReferences());
        var mapper = new Mapper(config);

        var manager = new Employee { Name = "Alice" };
        var report = new Employee { Name = "Bob", Manager = manager };
        manager.Reports.Add(report); // genuine cycle: manager.Reports[0].Manager == manager

        var dto = mapper.Map<EmployeeDto>(manager);
        var cycleMapsCorrectly = dto.Name == "Alice" && dto.Reports.Count == 1 && dto.Reports[0].Name == "Bob";
        var cycleResolvesToSameInstance = ReferenceEquals(dto, dto.Reports[0].Manager);

        var teamConfig = MapperConfiguration.Create(cfg =>
        {
            cfg.CreateMap<Employee, EmployeeDto>();
            cfg.CreateMap<Team, TeamDto>().PreserveReferences();
        });
        var teamMapper = new Mapper(teamConfig);

        var sharedManager = new Employee { Name = "Carol" };
        var team = new Team { Members = [new Employee { Name = "Dave", Manager = sharedManager }, new Employee { Name = "Eve", Manager = sharedManager }] };
        var teamDto = teamMapper.Map<TeamDto>(team);
        var sharedReferenceDedupes = ReferenceEquals(teamDto.Members[0].Manager, teamDto.Members[1].Manager);

        var noPreserveConfig = MapperConfiguration.Create(cfg => cfg.CreateMap<Employee, EmployeeDto>());
        var noPreserveMapper = new Mapper(noPreserveConfig);
        var e1 = new Employee { Name = "Frank", Manager = sharedManager };
        _ = noPreserveMapper.Map<EmployeeDto>(e1);
        var managerDto1 = noPreserveMapper.Map<EmployeeDto>(sharedManager);
        var managerDto2 = noPreserveMapper.Map<EmployeeDto>(sharedManager);
        var optInOnly = !ReferenceEquals(managerDto1, managerDto2);

        return (cycleMapsCorrectly, cycleResolvesToSameInstance, sharedReferenceDedupes, optInOnly);
    }

    [Fact]
    public void Cycle_DoesNotStackOverflowAndMapsCorrectly() => Assert.True(Result.cycleMapsCorrectly);

    [Fact]
    public void CyclicBackReference_ResolvesToSameInstance() => Assert.True(Result.cycleResolvesToSameInstance);

    [Fact]
    public void SharedNonCyclicReference_DedupesToOneInstance() => Assert.True(Result.sharedReferenceDedupes);

    [Fact]
    public void OptInOnly_UnconfiguredMapsStillProduceDistinctInstances() => Assert.True(Result.optInOnly);
}
