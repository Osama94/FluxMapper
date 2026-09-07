using System.Reflection;

namespace FluxMapper.Core.Ir;

/// <summary>
/// How the destination instance gets constructed.
/// </summary>
public sealed record ConstructorPlan(
    ConstructorInfo? Constructor,
    IReadOnlyList<ConstructorParameterBinding> ParameterBindings)
{
    /// <summary>True when the destination is constructed parameterless and populated via settable members instead.</summary>
    public bool UsesParameterlessConstruction => Constructor is not null && ParameterBindings.Count == 0;
}

public sealed record ConstructorParameterBinding(ParameterInfo Parameter, ResolvedSource Source);
