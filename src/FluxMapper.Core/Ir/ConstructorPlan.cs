using System.Linq.Expressions;
using System.Reflection;

namespace FluxMapper.Core.Ir;

/// <summary>
/// How the destination instance gets constructed.
/// </summary>
public sealed record ConstructorPlan(
    ConstructorInfo? Constructor,
    IReadOnlyList<ConstructorParameterBinding> ParameterBindings,
    LambdaExpression? CustomExpression = null)
{
    /// <summary>True when the destination is constructed parameterless and populated via settable members instead.</summary>
    public bool UsesParameterlessConstruction => Constructor is not null && ParameterBindings.Count == 0;

    /// <summary>True when a <c>ConstructUsing</c> expression replaces normal constructor selection entirely.</summary>
    public bool UsesCustomConstruction => CustomExpression is not null;
}

public sealed record ConstructorParameterBinding(ParameterInfo Parameter, ResolvedSource Source);
