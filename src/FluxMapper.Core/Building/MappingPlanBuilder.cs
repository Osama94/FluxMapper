using System.Reflection;
using FluxMapper.Abstractions;
using FluxMapper.Core.Configuration;
using FluxMapper.Core.Construction;
using FluxMapper.Core.Conventions;
using FluxMapper.Core.Ir;
using FluxMapper.Core.Nullability;

namespace FluxMapper.Core.Building;

public interface IMappingPlanBuilder
{
    MappingPlan Build(Type sourceType, Type destinationType);
}

/// <summary>
/// The one pipeline: candidate discovery -&gt; ambiguity resolution -&gt; nullability -&gt; constructor/collection
/// sub-planning -&gt; diagnostics. This implementation drives the pipeline from
/// <see cref="System.Reflection"/> input; a future compile-time (semantic-model) input would run through
/// the same stages, not a parallel copy of them.
/// </summary>
public sealed class MappingPlanBuilder(ITypeMapConfigurationProvider configProvider) : IMappingPlanBuilder
{
    // Matches ReferencePlan.Default.MaxDepth -- used here to stop *building* a plan for an excessively deep (but non-repeating) type
    // graph.
    private const int MaxDepth = 24;

    /// <summary>
    /// How many times the *same* (source, destination) type pair may be
    /// unrolled into fresh, fully-populated nested plans before the builder falls back to a degenerate
    /// stub. A self-referencing type (Employee.Manager: Employee, TreeNode.Parent: TreeNode, and so on)
    /// is structurally infinite from the plan builder's purely-static point of view -- it has no way to
    /// know from types alone that a *specific* runtime object graph is actually finite or cyclic -- so
    /// unrolling has to stop somewhere. Stopping at the very first repeat
    /// made the second-and-therefore-every occurrence of the recursive member an empty stub with no
    /// members mapped at all, which silently produced blank objects for the extremely common one-hop
    /// self-reference shape even *without* ReferenceHandling.Preserve involved. A small positive cap
    /// keeps plan size bounded (branching is small -- typically 1-2 self-referencing members per type --
    /// so even this cap's worst case is a handful of extra plan copies, not a blowup) while unrolling far
    /// enough that <see cref="ReferenceHandling.Preserve"/>'s runtime identity map (see
    /// <see cref="Execution.CompiledMapperFactory"/>) gets the chance it needs: the identity check at
    /// each construction call site runs *before* the (possibly-degenerate) nested plan would ever be
    /// invoked, so a genuine cycle back to an already-registered ancestor object resolves via the cached
    /// instance long before hitting this cap in practice. Only a *new, never-before-seen* object at a
    /// depth beyond this cap would ever actually observe the degenerate/MAP0099 fallback below.
    /// </summary>
    private const int MaxSelfReferenceRepeats = 3;

    public MappingPlan Build(Type sourceType, Type destinationType)
        => BuildInternal(sourceType, destinationType, new Dictionary<(Type, Type), int>(), depth: 0);

    private MappingPlan BuildInternal(Type sourceType, Type destinationType, Dictionary<(Type, Type), int> stack, int depth)
    {
        var pairKey = (sourceType, destinationType);
        var config = configProvider.Get(sourceType, destinationType);
        var naming = config?.Naming ?? NamingConvention.Default;
        var visits = stack.GetValueOrDefault(pairKey);

        if (depth > MaxDepth || visits >= MaxSelfReferenceRepeats)
        {
            return new MappingPlan(sourceType, destinationType, MappingKind.Nested,
                new ConstructorPlan(null, []), [], ReferencePlan.Default, ExecutionEligibility.ForReflectionOnly(),
                [PlanDiagnostic.Create("MAP0099", DiagnosticSeverity.Warning, sourceType.Name, destinationType.Name,
                    "Circular or excessively deep type graph detected while building a nested mapping plan; " +
                    "stopped recursing at this point. This branch maps no further members -- if this is reached " +
                    "at runtime for an object NOT already seen earlier in the same Map() call, enable " +
                    ".PreserveReferences() so genuinely cyclic/shared object graphs resolve " +
                    "via runtime identity instead of relying on unrolled plan depth.")]);
        }

        stack[pairKey] = visits + 1;
        try
        {
            var referencePlan = BuildReferencePlan(config);

            if (TypeClassification.IsDictionary(sourceType, out var rootSourceKey, out var rootSourceValue)
                && TypeClassification.IsDictionary(destinationType, out var rootDestKey, out var rootDestValue))
            {
                var rootDictionaryPlan = BuildDictionaryPlan(rootSourceKey, rootSourceValue, rootDestKey, rootDestValue, destinationType, stack, depth);
                return new MappingPlan(sourceType, destinationType, MappingKind.Dictionary,
                    new ConstructorPlan(null, []), [], referencePlan,
                    ExecutionEligibility.ForCompiledExpression(), [], DictionaryPlan: rootDictionaryPlan);
            }

            if (TypeClassification.IsCollection(sourceType, out var rootSourceElement)
                && TypeClassification.IsCollection(destinationType, out var rootDestElement))
            {
                var rootCollectionPlan = BuildCollectionPlan(sourceType, destinationType, rootSourceElement, rootDestElement, stack, depth);
                return new MappingPlan(sourceType, destinationType, MappingKind.Collection,
                    new ConstructorPlan(null, []), [], referencePlan,
                    ExecutionEligibility.ForCompiledExpression(), [], rootCollectionPlan);
            }

            var diagnostics = new List<PlanDiagnostic>();

            // A ConstructUsing expression on the map's configuration replaces normal constructor selection
            // entirely -- the destination is built by evaluating that expression at runtime, so there are no
            // constructor-argument member bindings to discover here.
            ConstructorPlan? ctorPlan;
            if (config?.CustomConstructor is { } customConstructor)
            {
                ctorPlan = new ConstructorPlan(null, [], customConstructor);
            }
            else
            {
                var (selectedCtorPlan, ctorDiagnostic) = ConstructorSelector.Select(destinationType, sourceType, naming);
                ctorPlan = selectedCtorPlan;
                if (ctorDiagnostic is not null) diagnostics.Add(ctorDiagnostic);
            }

            var boundMemberNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var memberPlans = new List<MemberPlan>();

            if (ctorPlan is not null)
            {
                foreach (var binding in ctorPlan.ParameterBindings)
                {
                    var matchingProperty = (MemberInfo?)destinationType.GetProperty(
                        binding.Parameter.Name ?? string.Empty, BindingFlags.Public | BindingFlags.Instance);
                    if (matchingProperty is null) continue;

                    boundMemberNames.Add(matchingProperty.Name);
                    memberPlans.Add(BuildMemberPlan(
                        matchingProperty, binding.Source, MemberStrategy.ConstructorArgument,
                        [new CandidateSource(binding.Source, CandidateSource.ConstructorMatch, matchingProperty.Name)],
                        config, stack, depth, diagnostics));
                }
            }

            if (config is not null && config.PathMaps.Count > 0)
            {
                foreach (var group in config.PathMaps.GroupBy(pm => pm.Path[0].Name))
                {
                    var rootMember = group.First().Path[0];
                    boundMemberNames.Add(rootMember.Name);

                    var entries = group.Select(pm => (Path: (IReadOnlyList<MemberInfo>)pm.Path.Skip(1).ToList(), pm.Source)).ToList();
                    var rootPathPlan = BuildPathPlan(MemberValueTypeHelper.GetMemberType(rootMember), rootMember.Name, entries, diagnostics);

                    memberPlans.Add(new MemberPlan(rootMember, new ResolvedSource.Unresolved(),
                        MemberStrategy.PathMapping, NullPolicy.Ignore, [], PathPlan: rootPathPlan));
                }
            }

            foreach (var destinationMember in TypeClassification.GetWritableMembers(destinationType))
            {
                if (boundMemberNames.Contains(destinationMember.Name)) continue;

                if (config?.IsIgnored(destinationMember.Name) == true)
                {
                    memberPlans.Add(new MemberPlan(destinationMember, new ResolvedSource.Unresolved(),
                        MemberStrategy.Ignored, NullPolicy.Ignore, []));
                    continue;
                }

                IReadOnlyList<CandidateSource> candidates = config is not null
                    && config.TryGetExplicitSource(destinationMember.Name, out var explicitSource)
                        ? [new CandidateSource(explicitSource, CandidateSource.ExplicitConfiguration, "explicit")]
                        : CandidateDiscovery.Discover(destinationMember, sourceType, naming);

                var resolution = AmbiguityResolver.Resolve(destinationMember.Name, candidates);
                if (!resolution.IsResolved)
                {
                    diagnostics.Add(resolution.Diagnostic!);
                    memberPlans.Add(new MemberPlan(destinationMember, new ResolvedSource.Unresolved(),
                        MemberStrategy.Ignored, NullPolicy.Throw, candidates));
                    continue;
                }

                memberPlans.Add(BuildMemberPlan(
                    destinationMember, resolution.Winner!.Source, forcedStrategy: null, candidates,
                    config, stack, depth, diagnostics));
            }

            var kind = memberPlans.Any(m => m.Strategy is MemberStrategy.NestedMapping or MemberStrategy.CollectionMapping or MemberStrategy.DictionaryMapping)
                ? MappingKind.Nested
                : MappingKind.Flat;

            var polymorphismPlan = BuildPolymorphismPlan(sourceType, destinationType, stack, depth);

            return new MappingPlan(sourceType, destinationType, kind, ctorPlan ?? new ConstructorPlan(null, []),
                memberPlans, referencePlan, ExecutionEligibility.ForCompiledExpression(), diagnostics,
                PolymorphismPlan: polymorphismPlan,
                BeforeMap: config?.BeforeMap,
                AfterMap: config?.AfterMap);
        }
        finally
        {
            stack[pairKey] = visits;
        }
    }

    private static ReferencePlan BuildReferencePlan(TypeMapConfiguration? config)
        => config?.ReferenceHandling == ReferenceHandling.Preserve
            ? ReferencePlan.Default with { Handling = ReferenceHandling.Preserve }
            : ReferencePlan.Default;

    /// <summary>
    /// Does any *other* registered map have a source type assignable to <paramref name="sourceType"/>
    /// and a destination type assignable to <paramref name="destinationType"/>? If so, this base-type pair
    /// gets a <see cref="PolymorphismPlan"/> so the execution tier can dispatch to the more specific plan at
    /// runtime based on the source object's actual type. Sealed source types are skipped
    /// outright -- they cannot have subtypes, so there is nothing to discover.
    /// </summary>
    private PolymorphismPlan? BuildPolymorphismPlan(Type sourceType, Type destinationType, Dictionary<(Type, Type), int> stack, int depth)
    {
        if (sourceType.IsSealed || sourceType.IsValueType) return null;

        var subtypeConfigs = configProvider.AllRegisteredMaps
            .Where(m => m.SourceType != sourceType
                     && sourceType.IsAssignableFrom(m.SourceType)
                     && destinationType.IsAssignableFrom(m.DestinationType))
            .ToList();

        if (subtypeConfigs.Count == 0) return null;

        var cases = subtypeConfigs
            .Select(m => new PolymorphicCase(m.SourceType, m.DestinationType, BuildInternal(m.SourceType, m.DestinationType, stack, depth + 1)))
            .ToList();

        return new PolymorphismPlan(cases);
    }

    private MemberPlan BuildMemberPlan(
        MemberInfo destinationMember,
        ResolvedSource source,
        MemberStrategy? forcedStrategy,
        IReadOnlyList<CandidateSource> candidates,
        TypeMapConfiguration? config,
        Dictionary<(Type, Type), int> stack,
        int depth,
        List<PlanDiagnostic> diagnostics)
    {
        var destinationValueType = MemberValueTypeHelper.GetMemberType(destinationMember);
        var sourceValueType = GetSourceValueType(source, destinationValueType);

        MappingPlan? nestedPlan = null;
        CollectionPlan? collectionPlan = null;
        DictionaryPlan? dictionaryPlan = null;
        var strategy = forcedStrategy ?? MemberStrategy.DirectAssignment;

        if (source is ResolvedSource.ProjectionResolver)
        {
            strategy = forcedStrategy ?? MemberStrategy.ProjectionResolver;
        }
        else if (source is ResolvedSource.ValueResolver)
        {
            strategy = forcedStrategy ?? MemberStrategy.CustomResolver;
        }
        else if (source is ResolvedSource.ValueConverter)
        {
            strategy = forcedStrategy ?? MemberStrategy.CustomConverter;
        }
        else if (TypeClassification.IsDictionary(sourceValueType, out var sourceKey, out var sourceValue)
                 && TypeClassification.IsDictionary(destinationValueType, out var destKey, out var destValue))
        {
            strategy = forcedStrategy ?? MemberStrategy.DictionaryMapping;
            dictionaryPlan = BuildDictionaryPlan(sourceKey, sourceValue, destKey, destValue, destinationValueType, stack, depth + 1);
        }
        else if (TypeClassification.IsCollection(sourceValueType, out var sourceElement)
                 && TypeClassification.IsCollection(destinationValueType, out var destElement))
        {
            strategy = forcedStrategy ?? MemberStrategy.CollectionMapping;
            collectionPlan = BuildCollectionPlan(sourceValueType, destinationValueType, sourceElement, destElement, stack, depth + 1);
        }
        else if (TypeClassification.IsComplex(sourceValueType) && TypeClassification.IsComplex(destinationValueType)
                 && !TypeConversion.CanConvertDirectly(sourceValueType, destinationValueType))
        {
            strategy = forcedStrategy ?? MemberStrategy.NestedMapping;
            nestedPlan = BuildInternal(sourceValueType, destinationValueType, stack, depth + 1);
        }
        else if (source is ResolvedSource.MemberChain { Members.Count: > 1 })
        {
            // A resolved chain longer than one hop that converts directly is a flattened leaf
            //, distinct from a single-hop DirectAssignment -- this is what lets
            // reverseMap's reversibility check tell "trivially reversible" apart from
            // "collapsed information, needs an explicit reverse mapping."
            strategy = forcedStrategy ?? MemberStrategy.Flattening;
        }
        else if (!TypeConversion.CanConvertDirectly(sourceValueType, destinationValueType) && source is not ResolvedSource.InlineExpression)
        {
            diagnostics.Add(PlanDiagnostic.Create(
                code: "MAP0002",
                severity: DiagnosticSeverity.Error,
                sourcePath: DisplayPathFor(source),
                destinationPath: destinationMember.Name,
                reason: $"Cannot convert {sourceValueType.Name} to {destinationValueType.Name} " +
                        "without a registered converter or resolver.",
                suggestedFixes: [$".UseConverter<{sourceValueType.Name},{destinationValueType.Name}>(...)"]));
        }

        var explicitPolicy = config?.GetExplicitNullPolicy(destinationMember.Name);
        var nullPolicy = ResolveNullPolicy(source, destinationMember, explicitPolicy, diagnostics);
        var condition = config?.GetCondition(destinationMember.Name);
        object? nullSubstitute = null;
        config?.TryGetNullSubstitute(destinationMember.Name, out nullSubstitute);

        return new MemberPlan(destinationMember, source, strategy, nullPolicy, candidates,
            condition, nullSubstitute, nestedPlan, collectionPlan, dictionaryPlan);
    }

    private CollectionPlan BuildCollectionPlan(
        Type sourceCollectionType, Type destinationCollectionType,
        Type sourceElementType, Type destinationElementType,
        Dictionary<(Type, Type), int> stack, int depth)
    {
        MappingPlan? elementPlan = null;
        if (TypeClassification.IsComplex(sourceElementType) && TypeClassification.IsComplex(destinationElementType)
            && !TypeConversion.CanConvertDirectly(sourceElementType, destinationElementType))
        {
            elementPlan = BuildInternal(sourceElementType, destinationElementType, stack, depth + 1);
        }

        var targetKind = ClassifyCollectionTargetKind(destinationCollectionType);
        var hasCapacityHint = sourceCollectionType.IsArray
            || typeof(System.Collections.ICollection).IsAssignableFrom(sourceCollectionType)
            || sourceCollectionType.GetInterfaces().Any(i =>
                i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICollection<>));

        return new CollectionPlan(elementPlan, targetKind, sourceElementType, destinationElementType, hasCapacityHint);
    }

    private static CollectionTargetKind ClassifyCollectionTargetKind(Type destinationType)
    {
        if (destinationType.IsArray) return CollectionTargetKind.Array;

        if (destinationType.IsGenericType)
        {
            var def = destinationType.GetGenericTypeDefinition();
            if (def == typeof(System.Collections.Immutable.ImmutableArray<>)) return CollectionTargetKind.ImmutableArray;
            if (def == typeof(System.Collections.Immutable.ImmutableList<>) || def == typeof(System.Collections.Immutable.IImmutableList<>))
                return CollectionTargetKind.ImmutableList;
            if (def == typeof(System.Collections.Immutable.ImmutableHashSet<>) || def == typeof(System.Collections.Immutable.IImmutableSet<>))
                return CollectionTargetKind.ImmutableHashSet;
            if (def == typeof(HashSet<>) || def == typeof(ISet<>)) return CollectionTargetKind.HashSet;
            if (def == typeof(List<>) || def == typeof(IList<>) || def == typeof(ICollection<>)
                || def == typeof(IEnumerable<>) || def == typeof(IReadOnlyList<>) || def == typeof(IReadOnlyCollection<>))
                return CollectionTargetKind.List;
        }

        return CollectionTargetKind.Enumerable;
    }

    /// <summary>Sibling of <see cref="BuildCollectionPlan"/> for keyed collections.</summary>
    private DictionaryPlan BuildDictionaryPlan(
        Type sourceKeyType, Type sourceValueType, Type destinationKeyType, Type destinationValueType,
        Type destinationDictionaryType, Dictionary<(Type, Type), int> stack, int depth)
    {
        MappingPlan? valuePlan = null;
        if (TypeClassification.IsComplex(sourceValueType) && TypeClassification.IsComplex(destinationValueType)
            && !TypeConversion.CanConvertDirectly(sourceValueType, destinationValueType))
        {
            valuePlan = BuildInternal(sourceValueType, destinationValueType, stack, depth + 1);
        }

        var targetKind = ClassifyDictionaryTargetKind(destinationDictionaryType);
        return new DictionaryPlan(valuePlan, targetKind, sourceKeyType, sourceValueType, destinationKeyType, destinationValueType);
    }

    private static DictionaryTargetKind ClassifyDictionaryTargetKind(Type destinationType)
    {
        if (destinationType.IsGenericType)
        {
            var def = destinationType.GetGenericTypeDefinition();
            if (def == typeof(System.Collections.Immutable.ImmutableDictionary<,>)
                || def == typeof(System.Collections.Immutable.IImmutableDictionary<,>))
                return DictionaryTargetKind.ImmutableDictionary;
        }

        return DictionaryTargetKind.Dictionary;
    }

    private static Type GetSourceValueType(ResolvedSource source, Type destinationFallback) => source switch
    {
        ResolvedSource.MemberChain mc => mc.ValueType,
        ResolvedSource.MethodCall m => m.Method.ReturnType,
        ResolvedSource.ValueConverter vc => vc.DestinationType,
        ResolvedSource.ConstantOrDefault c => c.Value?.GetType() ?? destinationFallback,
        ResolvedSource.InlineExpression ie => ie.Expression.ReturnType,
        ResolvedSource.ProjectionResolver pr => pr.MemberType,
        _ => destinationFallback, // ValueResolver: the resolver author is responsible for TMember compatibility
    };

    private static string DisplayPathFor(ResolvedSource source) => source switch
    {
        ResolvedSource.MemberChain mc => mc.PathText,
        ResolvedSource.MethodCall m => m.Method.Name + "()",
        ResolvedSource.ValueResolver r => r.ResolverType.Name,
        ResolvedSource.ValueConverter c => c.ConverterType.Name,
        ResolvedSource.ProjectionResolver pr => pr.ResolverType.Name,
        ResolvedSource.InlineExpression => "<inline expression>",
        ResolvedSource.ConstantOrDefault => "<constant>",
        _ => "<unresolved>",
    };

    /// <summary>
    /// A nullable source feeding a non-nullable destination with no
    /// explicit policy is a validation ERROR (MAP0001), not a silent runtime default -- fail at the
    /// earliest point the information exists. Every other combination is fine as-is.
    /// </summary>
    private static NullPolicy ResolveNullPolicy(
        ResolvedSource source, MemberInfo destinationMember, NullPolicy? explicitPolicy, List<PlanDiagnostic> diagnostics)
    {
        var sourceNullable = source switch
        {
            ResolvedSource.MemberChain mc => NullabilityAnalysis.IsNullable(mc.Members[^1]),
            ResolvedSource.MethodCall m => NullabilityAnalysis.IsNullable(m.Method),
            ResolvedSource.ConstantOrDefault c => c.Value is null,
            _ => false,
        };

        var destinationNullable = NullabilityAnalysis.IsNullable(destinationMember);

        if (sourceNullable && !destinationNullable)
        {
            if (explicitPolicy is not null) return explicitPolicy.Value;

            diagnostics.Add(PlanDiagnostic.Create(
                code: "MAP0001",
                severity: DiagnosticSeverity.Error,
                sourcePath: DisplayPathFor(source),
                destinationPath: destinationMember.Name,
                reason: $"Nullable source cannot safely satisfy non-nullable destination member '{destinationMember.Name}'.",
                suggestedFixes:
                [
                    $".NullSubstitute(d => d.{destinationMember.Name}, /* value */)",
                    $"Make {destinationMember.Name} nullable",
                ]));
            return NullPolicy.Throw;
        }

        return explicitPolicy ?? NullPolicy.Map;
    }

    /// <summary>
    /// Builds one <see cref="PathPlan"/> node (and, recursively, every nested branch) purely from
    /// explicit <c>ForPath</c> registrations -- there is no fallback to convention-based member
    /// discovery here, because ForPath exists precisely for the case where a matching source member
    /// does not exist for an intermediate destination segment (see the type's own doc comment).
    /// Every subtree this produces is always freshly constructed at execution time (never merged
    /// into an existing instance, even during update-in-place), so a destination type with no public
    /// parameterless constructor can never be satisfied and is reported as MAP0004 here rather than
    /// deferred to a runtime failure.
    /// </summary>
    private static PathPlan BuildPathPlan(
        Type destinationType,
        string pathLabel,
        List<(IReadOnlyList<MemberInfo> Path, ResolvedSource Source)> entries,
        List<PlanDiagnostic> diagnostics)
    {
        if (destinationType.GetConstructor(Type.EmptyTypes) is null)
        {
            diagnostics.Add(PlanDiagnostic.Create(
                code: "MAP0004",
                severity: DiagnosticSeverity.Error,
                sourcePath: "",
                destinationPath: pathLabel,
                reason: $"ForPath targets '{pathLabel}' ({destinationType.Name}), which has no public " +
                        "parameterless constructor -- ForPath always freshly constructs the destination " +
                        "subtree it touches.",
                suggestedFixes: [$"Give {destinationType.Name} a public parameterless constructor, or map this member without ForPath."]));
        }

        var leaves = new List<PathLeaf>();
        var branches = new List<PathBranch>();

        foreach (var group in entries.GroupBy(e => e.Path.Count == 1 ? null : e.Path[0].Name))
        {
            if (group.Key is null)
            {
                foreach (var (path, source) in group)
                {
                    var leafMember = path[0];
                    leaves.Add(new PathLeaf(leafMember, source, MemberValueTypeHelper.GetMemberType(leafMember)));
                }
            }
            else
            {
                var branchMember = group.First().Path[0];
                var childEntries = group
                    .Select(e => (Path: (IReadOnlyList<MemberInfo>)e.Path.Skip(1).ToList(), e.Source))
                    .ToList();
                var childPlan = BuildPathPlan(
                    MemberValueTypeHelper.GetMemberType(branchMember), $"{pathLabel}.{branchMember.Name}", childEntries, diagnostics);
                branches.Add(new PathBranch(branchMember, childPlan));
            }
        }

        return new PathPlan(destinationType, leaves, branches);
    }
}
