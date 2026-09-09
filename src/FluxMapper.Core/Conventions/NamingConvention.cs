namespace FluxMapper.Core.Conventions;

/// <summary>
/// Configurable naming conventions (prefixes, suffixes, replacements).
/// Immutable, fluent-built, attached to a MapperConfiguration.
/// </summary>
public sealed class NamingConvention
{
    private readonly List<string> _prefixes = [];
    private readonly List<(string From, string To)> _replacements = [];

    public static NamingConvention Default { get; } = new();

    /// <summary>
    /// Ready-made preset for a source whose members are <c>snake_case</c> (e.g. <c>user_name</c>) mapping
    /// to a destination with ordinary PascalCase members (<c>UserName</c>) -- the common "our DTOs are
    /// PascalCase, the wire format/legacy schema is snake_case" case. Equivalent to
    /// <c>new NamingConvention().Replace("_")</c>: <see cref="NamesMatch"/> is already
    /// case-insensitive, so stripping underscores from one side is sufficient (<c>user_name</c> normalizes
    /// to <c>username</c>, which matches <c>UserName</c> case-insensitively) -- no need to also touch
    /// casing. Returns a fresh instance on every call; do not share one across configurations you intend
    /// to customize differently; each returned instance is independent (see <see cref="RecognizePrefix"/>'s
    /// remarks below on why that matters).
    /// </summary>
    public static NamingConvention SnakeCase() => new NamingConvention().Replace("_");

    /// <summary>Alias for <see cref="SnakeCase"/>, matching AutoMapper's <c>LowerUnderscoreNamingConvention</c>
    /// name for anyone migrating and searching for the familiar term.</summary>
    public static NamingConvention LowerUnderscore() => SnakeCase();

    /// <remarks>
    /// <see cref="RecognizePrefix"/> and <see cref="Replace"/> both mutate this instance in place and
    /// return <c>this</c> -- despite this class's own summary calling it "Immutable," it is not
    /// copy-on-write. Chaining calls on a fresh <c>new NamingConvention()</c> (or on what
    /// <see cref="SnakeCase"/>/<see cref="LowerUnderscore"/> return) is safe; chaining further calls onto
    /// the shared <see cref="Default"/> singleton would mutate it for every other caller in the process and
    /// must never be done.
    /// </remarks>
    public NamingConvention RecognizePrefix(string prefix)
    {
        _prefixes.Add(prefix);
        return this;
    }

    public NamingConvention Replace(string from, string to = "")
    {
        _replacements.Add((from, to));
        return this;
    }

    /// <summary>
    /// Canonicalizes a member name for comparison: strips any recognized prefix, applies configured
    /// replacements, and returns the result for case-insensitive comparison by the caller. This is the
    /// one place "what does this name mean" is decided, so exact-match and flattening both see the
    /// same normalized identity.
    /// </summary>
    public string Normalize(string memberName)
    {
        var name = memberName;
        foreach (var prefix in _prefixes)
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal))
            {
                name = name.Substring(prefix.Length);
                break;
            }
        }
        foreach (var (from, to) in _replacements)
        {
            name = name.Replace(from, to, StringComparison.OrdinalIgnoreCase);
        }
        return name;
    }

    public bool NamesMatch(string a, string b)
        => string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
}
