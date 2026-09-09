; Unshipped analyzer releases
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
FLUX0001 | FluxMapper.SourceGenerator | Error | MapFromAnalyzer, [MapFrom] target must be declared partial
FLUX0002 | FluxMapper.SourceGenerator | Warning | MapFromAnalyzer, [MapFrom] found no mappable members
FLUX0003 | FluxMapper.SourceGenerator | Error | MapFromAnalyzer, [MapFrom] target has no usable constructor
