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
                name = name[prefix.Length..];
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
