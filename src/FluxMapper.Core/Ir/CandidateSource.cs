namespace FluxMapper.Core.Ir;

/// <summary>
/// One option the candidate-discovery stage considered for a destination member, with a confidence
/// score from the priority ladder below.
/// Every candidate is kept (not just the winner) so ambiguity detection can compare the top two and
/// so diagnostics can list every option that was considered.
/// </summary>
public sealed record CandidateSource(ResolvedSource Source, double Confidence, string DisplayPath)
{
    // Priority ladder. Kept as named constants rather than magic numbers so the
    // ordering itself is reviewable in one place.
    public const double ExplicitConfiguration = 1.0;
    public const double ExactMatch = 0.95;
    public const double NamingConvention = 0.8;
    public const double Flattening = 0.6;
    public const double NestedMapping = 0.5;
    public const double CollectionMapping = 0.5;
    public const double ConstructorMatch = 0.4;
    public const double RegisteredConverter = 0.3;
    public const double Fallback = 0.1;
}
