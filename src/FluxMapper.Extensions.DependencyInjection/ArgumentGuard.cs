namespace FluxMapper.Extensions.DependencyInjection;

/// <summary>
/// Equivalent of <c>ArgumentNullException.ThrowIfNull</c>, needed because that BCL helper was only added
/// in .NET 6 and this assembly also targets netstandard2.0. <paramref name="paramName"/> is passed
/// explicitly at every call site rather than relying on <c>CallerArgumentExpression</c> (itself only
/// added in .NET 5's BCL), so this type needs no further polyfilling of its own.
/// </summary>
internal static class ArgumentGuard
{
    public static void ThrowIfNull(object? argument, string paramName)
    {
        if (argument is null)
        {
            throw new ArgumentNullException(paramName);
        }
    }
}
