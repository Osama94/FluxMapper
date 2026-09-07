namespace FluxMapper.Core.Ir;

/// <summary>
/// One registered subtype pair that participates in a base-type mapping's polymorphic dispatch
///. <paramref name="Plan"/> is the fully-built <see cref="MappingPlan"/>
/// for the concrete (<paramref name="SourceSubtype"/>, <paramref name="DestinationSubtype"/>) pair --
/// itself built through the exact same <see cref="Building.MappingPlanBuilder"/> pipeline as everything
/// else, not a special case.
/// </summary>
public sealed record PolymorphicCase(Type SourceSubtype, Type DestinationSubtype, MappingPlan Plan);

/// <summary>
/// Attached to a base-type <see cref="MappingPlan"/>
/// when one or more subtypes of its <see cref="MappingPlan.SourceType"/> have their own explicit
/// <c>CreateMap</c> registration in the same <see cref="Configuration.MapperConfigurationExpression"/>,
/// with a destination subtype assignable to this plan's <see cref="MappingPlan.DestinationType"/>.
/// The execution tier (<see cref="Execution.CompiledMapperFactory"/>) compiles this into a runtime
/// <c>GetType()</c> check chain: a "resolved as statically as possible; ambiguity
/// always fails" principle is honored by resolving purely from registered configuration at plan-build
/// time (no runtime reflection-based guessing), and by cases being mutually exclusive on exact runtime
/// type (never a partial/ambiguous match).
/// </summary>
public sealed record PolymorphismPlan(IReadOnlyList<PolymorphicCase> Cases);
