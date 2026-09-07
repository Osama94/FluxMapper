using FluxMapper.Abstractions;
using FluxMapper.Core.Ir;

namespace FluxMapper.Core.Conventions;

/// <summary>
/// Pick the single top-confidence candidate for a member, UNLESS the top two are
/// tied — in which case this is a hard failure with a diagnostic listing every candidate, never a
/// silent pick. This same function is used for both forward member resolution and (via the reverse
/// pipeline) reverse-mapping reconstruction, so the "never guess" invariant is enforced identically
/// in both directions.
/// </summary>
public static class AmbiguityResolver
{
    private const double Epsilon = 1e-9;

    public static AmbiguityResult Resolve(string destinationPath, IReadOnlyList<CandidateSource> candidates)
    {
        if (candidates.Count == 0)
        {
            return new AmbiguityResult(null, PlanDiagnostic.Create(
                code: "MAP2001",
                severity: DiagnosticSeverity.Error,
                sourcePath: "",
                destinationPath: destinationPath,
                reason: "No source member, method, or flattened path could be resolved for this destination member.",
                suggestedFixes: [$".Map(d => d.{destinationPath}, s => /* explicit source expression */)"]));
        }

        var ranked = candidates.OrderByDescending(c => c.Confidence).ToList();
        var top = ranked[0];
        var tiedWithTop = ranked.Where(c => Math.Abs(c.Confidence - top.Confidence) < Epsilon).ToList();

        if (tiedWithTop.Count > 1)
        {
            return new AmbiguityResult(null, PlanDiagnostic.Create(
                code: "MAP2007",
                severity: DiagnosticSeverity.Error,
                sourcePath: "",
                destinationPath: destinationPath,
                reason: "Multiple candidates have equal confidence.",
                candidates: tiedWithTop.Select(c => c.DisplayPath).ToList(),
                suggestedFixes: [$".Map(d => d.{destinationPath}, s => s.{tiedWithTop[0].DisplayPath})"]));
        }

        return new AmbiguityResult(top, null);
    }
}

public sealed record AmbiguityResult(CandidateSource? Winner, PlanDiagnostic? Diagnostic)
{
    public bool IsResolved => Winner is not null;
}
