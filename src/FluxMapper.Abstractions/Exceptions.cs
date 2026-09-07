namespace FluxMapper.Abstractions;

/// <summary>
/// Thrown by a runtime <c>Map</c> call. Never carries a bare "Mapping failed." message
/// — always carries the offending diagnostic.
/// </summary>
public class MappingException(PlanDiagnostic diagnostic)
    : Exception(diagnostic.ToString())
{
    public PlanDiagnostic Diagnostic { get; } = diagnostic;
}

/// <summary>Thrown by <c>AssertConfigurationIsValid()</c> — aggregates every diagnostic found across every registered map.</summary>
public sealed class ConfigurationValidationException(IReadOnlyList<PlanDiagnostic> diagnostics)
    : Exception(BuildMessage(diagnostics))
{
    public IReadOnlyList<PlanDiagnostic> Diagnostics { get; } = diagnostics;

    private static string BuildMessage(IReadOnlyList<PlanDiagnostic> diagnostics)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("Mapping configuration validation failed with ").Append(diagnostics.Count).AppendLine(" diagnostic(s):");
        foreach (var d in diagnostics)
        {
            sb.AppendLine("---");
            sb.Append(d);
        }
        return sb.ToString();
    }
}

/// <summary>
/// Thrown when a resolved plan is not legal to execute in the current runtime environment
/// (e.g. reflection fallback under a NativeAOT-published host).
/// </summary>
public sealed class AotIncompatibleMappingException(PlanDiagnostic diagnostic) : MappingException(diagnostic);

/// <summary>Thrown when a projection-eligible plan is required but the resolved plan isn't translatable to a query provider.</summary>
public sealed class ProjectionTranslationException(PlanDiagnostic diagnostic) : MappingException(diagnostic);
