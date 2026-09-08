using System.Reflection;
using FluxMapper.Abstractions;
using FluxMapper.Core.Conventions;
using FluxMapper.Core.Ir;

namespace FluxMapper.Core.Construction;

/// <summary>
/// Constructor selection. <c>PreferBestMatch</c> is the only implemented policy so far: a public
/// parameterless constructor always wins when present (destination is then populated via settable
/// members instead); otherwise the public constructor with the most parameters that can ALL be
/// resolved from the source type wins. Non-public constructors are never selected automatically as a
/// security measure against silent private-constructor invocation — an explicit <c>ConstructUsing</c>
/// escape hatch is the documented path for that case, not silently unsupported.
/// </summary>
public static class ConstructorSelector
{
    public static (ConstructorPlan? Plan, PlanDiagnostic? Diagnostic) Select(Type destinationType, Type sourceType, NamingConvention naming)
    {
        var parameterless = destinationType.GetConstructor(BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
        if (parameterless is not null)
        {
            return (new ConstructorPlan(parameterless, []), null);
        }

        var candidateConstructors = destinationType
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Where(c => c.GetParameters().Length > 0)
            .OrderByDescending(c => c.GetParameters().Length);

        foreach (var ctor in candidateConstructors)
        {
            var bindings = new List<ConstructorParameterBinding>();
            var resolvedAll = true;

            foreach (var parameter in ctor.GetParameters())
            {
                var name = parameter.Name ?? string.Empty;
                var paramCandidates = CandidateDiscovery.Discover(name, sourceType, naming);
                var resolution = AmbiguityResolver.Resolve(name, paramCandidates);
                if (!resolution.IsResolved)
                {
                    resolvedAll = false;
                    break;
                }
                bindings.Add(new ConstructorParameterBinding(parameter, resolution.Winner!.Source));
            }

            if (resolvedAll)
            {
                return (new ConstructorPlan(ctor, bindings), null);
            }
        }

        return (null, PlanDiagnostic.Create(
            code: "MAPSG002",
            severity: DiagnosticSeverity.Error,
            sourcePath: sourceType.Name,
            destinationPath: destinationType.Name,
            reason: $"No usable public constructor found for immutable destination {destinationType.Name}: " +
                    "no parameterless constructor exists, and no parameterized constructor's parameters " +
                    "could all be resolved from the source type.",
            suggestedFixes: [$"cfg.CreateMap<{sourceType.Name},{destinationType.Name}>().ConstructUsing((src, ctx) => new {destinationType.Name}(/* ... */))"]));
    }
}
