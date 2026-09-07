namespace FluxMapper.Abstractions;

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// One entry in the diagnostic catalog. This is the single record
/// shape every consumer — compiler, runtime exception, log sink, test assertion — formats differently;
/// none of them construct diagnostic text independently.
/// </summary>
public sealed record PlanDiagnostic(
    string Code,
    DiagnosticSeverity Severity,
    string SourcePath,
    string DestinationPath,
    string Reason,
    IReadOnlyList<string> Candidates,
    IReadOnlyList<string> SuggestedFixes)
{
    public static PlanDiagnostic Create(
        string code,
        DiagnosticSeverity severity,
        string sourcePath,
        string destinationPath,
        string reason,
        IReadOnlyList<string>? candidates = null,
        IReadOnlyList<string>? suggestedFixes = null)
        => new(code, severity, sourcePath, destinationPath, reason,
               candidates ?? [], suggestedFixes ?? []);

    /// <summary>Renders the code, reason, source/destination paths, and any candidates or suggested fixes as one readable block.</summary>
    public override string ToString()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(Code).Append(' ').Append(Reason).AppendLine();
        if (!string.IsNullOrEmpty(SourcePath))
            sb.Append("Source: ").AppendLine(SourcePath);
        if (!string.IsNullOrEmpty(DestinationPath))
            sb.Append("Destination: ").AppendLine(DestinationPath);
        if (Candidates.Count > 0)
        {
            sb.AppendLine("Possible source candidates:");
            foreach (var c in Candidates) sb.Append("    ").AppendLine(c);
        }
        if (SuggestedFixes.Count > 0)
        {
            sb.AppendLine("Suggested fix:");
            foreach (var f in SuggestedFixes) sb.Append("    ").AppendLine(f);
        }
        return sb.ToString();
    }
}
