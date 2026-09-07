using System.Reflection;

namespace FluxMapper.Core.Ir;

/// <summary>
/// One leaf assignment inside a <c>ForPath</c> tree: set <see cref="Member"/> (a member of the owning
/// <see cref="PathPlan.DestinationType"/>) from <see cref="Source"/> -- resolved and evaluated exactly
/// like a normal scalar/complex member value (see <c>Execution.CompiledMapperFactory.BuildScalarValueExpression</c>),
/// just assigned somewhere other than the mapping's own top-level destination member.
/// </summary>
public sealed record PathLeaf(MemberInfo Member, ResolvedSource Source, Type MemberType);

/// <summary>
/// A named intermediate segment inside a <c>ForPath</c> tree: descend into (constructing fresh if
/// necessary -- see <see cref="PathPlan"/>) <see cref="Member"/>, then apply <see cref="Plan"/> to it.
/// </summary>
public sealed record PathBranch(MemberInfo Member, PathPlan Plan);

/// <summary>
/// The destination-side tree built from one or more <c>.ForPath(dst =&gt; dst.A.B.C, ...)</c>
/// registrations that share a common root -- e.g. <c>dst.SaudiAddress.ShortAddress</c> and
/// <c>dst.SaudiAddress.City</c> (hypothetically) both contribute leaves under one shared "SaudiAddress"
/// <see cref="PathPlan"/>. A destination subtree reached through <c>ForPath</c> is always freshly
/// constructed (<see cref="DestinationType"/> must have a public parameterless constructor), never
/// merged into a pre-existing instance -- including on update-in-place -- which is what keeps this tree
/// entirely independent of ordinary convention-based member resolution and nested-mapping: there is no
/// requirement that a matching source object exists for <see cref="DestinationType"/> at all, since every
/// leaf supplies its own, independently resolved source expression (<c>ForPath</c> exists specifically for
/// destination subtrees pieced together from source paths that don't mirror the destination's own
/// structure -- see <see cref="Building.MappingPlanBuilder"/>). Any member of <see cref="DestinationType"/>
/// (or an intermediate branch type) not covered by a <see cref="PathLeaf"/>/<see cref="PathBranch"/> here
/// is simply left at its default value; it is not separately validated or convention-matched.
/// </summary>
public sealed record PathPlan(Type DestinationType, IReadOnlyList<PathLeaf> Leaves, IReadOnlyList<PathBranch> Branches);
