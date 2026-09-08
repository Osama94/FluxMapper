using System.Diagnostics.CodeAnalysis;

namespace FluxMapper.Extensions.DependencyInjection;

/// <summary>
/// Equivalent of <c>ArgumentNullException.ThrowIfNull</c>, needed because that BCL helper was only added
/// in .NET 6 and this assembly also targets netstandard2.0. <c>paramName</c> is passed explicitly at
/// every call site rather than relying on <c>CallerArgumentExpression</c> (itself only added in .NET 5's
/// BCL), so this type needs no further polyfilling of its own.
/// </summary>
internal static class ArgumentGuard
{
    /// <summary>
    /// <paramref name="argument"/> is <c>NotNullAttribute</c>-annotated so that, exactly like the
    /// real <c>ArgumentNullException.ThrowIfNull</c>, nullable flow analysis treats it as non-null in the
    /// caller's code after a call that didn't throw -- without this, every call site would need its own
    /// null-forgiving <c>!</c> or trigger a spurious CS8604 downstream.
    /// </summary>
    public static void ThrowIfNull([NotNull] object? argument, string paramName)
    {
        if (argument is null)
        {
            throw new ArgumentNullException(paramName);
        }
    }
}
