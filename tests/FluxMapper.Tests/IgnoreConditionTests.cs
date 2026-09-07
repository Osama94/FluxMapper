namespace FluxMapper.Tests;

using FluxMapper.Core.Configuration;
using FluxMapper.Core.Execution;
using FluxMapper.Tests.Model;

// 4) Ignore + Condition on update-in-place mapping.
public class IgnoreConditionTests
{
    private static readonly (string secretAfterFirstUpdate, string nameAfterFirstUpdate, int valueAfterFirstUpdate, string nameAfterSecondUpdate) Result = Compute();

    private static (string, string, int, string) Compute()
    {
        var config = MapperConfiguration.Create(cfg =>
            cfg.CreateMap<Patch, Entity>()
               .Ignore(d => d.Secret)
               .Condition(d => d.Name, s => s.AllowNameChange));

        config.AssertConfigurationIsValid();
        var mapper = new Mapper(config);

        var entity = new Entity { Name = "Original", Secret = "keep-me", Value = 1 };
        mapper.Map(new Patch { Name = "Changed", AllowNameChange = false, Value = 2 }, entity);
        var secretAfterFirstUpdate = entity.Secret;
        var nameAfterFirstUpdate = entity.Name;
        var valueAfterFirstUpdate = entity.Value;

        mapper.Map(new Patch { Name = "Changed", AllowNameChange = true, Value = 3 }, entity);
        var nameAfterSecondUpdate = entity.Name;

        return (secretAfterFirstUpdate, nameAfterFirstUpdate, valueAfterFirstUpdate, nameAfterSecondUpdate);
    }

    [Fact]
    public void Ignore_SecretUntouchedByUpdate() => Assert.Equal("keep-me", Result.secretAfterFirstUpdate);

    [Fact]
    public void ConditionFalse_NameUntouchedByUpdate() => Assert.Equal("Original", Result.nameAfterFirstUpdate);

    [Fact]
    public void UnconditionedMember_StillUpdates() => Assert.Equal(2, Result.valueAfterFirstUpdate);

    [Fact]
    public void ConditionTrue_NameUpdates() => Assert.Equal("Changed", Result.nameAfterSecondUpdate);
}
