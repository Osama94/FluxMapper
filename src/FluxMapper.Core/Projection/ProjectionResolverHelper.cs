using System.Linq.Expressions;

namespace FluxMapper.Core.Projection;

/// <summary>
/// Shared by both the compiled-expression execution tier (<see cref="Execution.CompiledMapperFactory"/>)
/// and the projection compiler (<see cref="ProjectionExpressionBuilder"/>) so a
/// <see cref="Abstractions.IProjectionValueResolver{TSource,TMember}"/> is instantiated and its
/// expression extracted exactly the same way in both places.
/// </summary>
internal static class ProjectionResolverHelper
{
    public static LambdaExpression GetResolverExpression(Type resolverType)
    {
        var resolverInterface = resolverType.GetInterfaces()
            .First(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(Abstractions.IProjectionValueResolver<,>));
        var getExpressionMethod = resolverInterface.GetMethod(nameof(Abstractions.IProjectionValueResolver<object, object>.GetExpression))!;

        var instance = Activator.CreateInstance(resolverType)
            ?? throw new InvalidOperationException($"Projection resolver {resolverType} has no usable parameterless constructor.");

        return (LambdaExpression)getExpressionMethod.Invoke(instance, null)!;
    }
}
