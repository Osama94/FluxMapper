using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using FluxMapper.Abstractions;
using FluxMapper.Core.Ir;

namespace FluxMapper.Core.Execution;

/// <summary>
/// The compiled-expression execution tier: compiles a
/// <see cref="MappingPlan"/> into a cached delegate via <see cref="System.Linq.Expressions"/>. Source
/// generation is a separate, AOT-safe tier (see FluxMapper.SourceGenerator), and the reflection-only
/// tier is a documented fallback name in the IR (<see cref="ExecutionEligibility.ForReflectionOnly"/>)
/// without a working implementation behind it yet. Because this tier goes through
/// <see cref="LambdaExpression.Compile()"/>, it is JIT-dependent and therefore never AOT-safe —
/// <see cref="Ir.ExecutionEligibility"/> on every plan built here correctly reports <c>AotSafe: false</c>.
///
/// Handles dictionary mapping, immutable-collection materialization, polymorphic dispatch, and
/// <see cref="ReferenceHandling.Preserve"/>. Reference preservation is threaded through as an optional
/// <c>refContext</c> expression (a <c>Dictionary&lt;object,object&gt;</c> local declared once per
/// compiled delegate invocation, keyed by reference identity) rather than a delegate parameter —
/// because every nested/collection/dictionary construction for one root <c>Map()</c> call is inlined
/// into a single compiled expression tree already (not a call to another separately-compiled delegate),
/// one context variable captured by every builder method in this class is sufficient to give identity
/// semantics across the *whole* object graph reached from that one call, with zero overhead when
/// <see cref="ReferenceHandling.Preserve"/> isn't requested (the parameter is simply <c>null</c> and
/// every check short-circuits away).
///
/// An optional <see cref="IServiceProvider"/> is threaded through the same builder methods, purely as a
/// plain build-time value (never as an expression-tree node) — it decides how <c>IValueResolver&lt;&gt;</c>
/// and <c>IValueConverter&lt;&gt;</c> instances are obtained (<c>services?.GetService(type) ??
/// Activator.CreateInstance(type)</c>) at the moment their compiled delegate is first built, and is also
/// handed to every <see cref="ResolutionContext"/> constructed at runtime via its <c>Services</c> property
/// so a resolver can pull further dependencies out of it manually inside <c>Resolve(...)</c>. Scoping
/// caveat (see FluxMapper.Extensions.DependencyInjection's AddFluxMapper): because the compiled
/// delegate for a given (source,destination) pair is built once and cached for the lifetime of the
/// owning <see cref="Mapper"/>, a resolver's constructor-injected dependencies are resolved once, from
/// whichever <see cref="IServiceProvider"/> that <see cref="Mapper"/> was constructed with — register
/// <c>IMapper</c> as Scoped (not Singleton) if a resolver has scoped dependencies it must re-resolve per
/// unit of work.
/// </summary>
public static class CompiledMapperFactory
{
    /// <summary>Builds a "construct a new destination" delegate: <c>object? -&gt; object?</c>, boxed at the edges so callers don't need per-type-pair generic instantiation to cache it.</summary>
    [RequiresDynamicCode("Compiles a System.Linq.Expressions tree via Expression.Compile(), which requires a JIT and is not supported when publishing Native AOT.")]
    [RequiresUnreferencedCode("Builds the compiled delegate using reflection (MemberInfo/ConstructorInfo/MethodInfo) over the mapped types, which trimming can remove.")]
    public static Func<object?, object?> BuildConstructDelegate(MappingPlan plan, IServiceProvider? services = null)
    {
        if (!plan.IsBuildable)
        {
            var diagnostic = plan.Diagnostics.First(d => d.Severity == DiagnosticSeverity.Error);
            return _ => throw new MappingException(diagnostic);
        }

        var sourceParam = Expression.Parameter(typeof(object), "sourceObj");
        var typedSource = Expression.Variable(plan.SourceType, "src");
        var blockVars = new List<ParameterExpression> { typedSource };
        var prelude = new List<Expression>
        {
            Expression.Assign(typedSource, Expression.Convert(sourceParam, plan.SourceType)),
        };

        var refContext = DeclareReferenceContextIfNeeded(plan, blockVars, prelude);
        var body = BuildRoot(plan, typedSource, refContext, services);

        prelude.Add(Expression.Condition(
            Expression.Equal(sourceParam, Expression.Constant(null, typeof(object))),
            Expression.Constant(null, typeof(object)),
            Expression.Convert(body, typeof(object))));

        var block = Expression.Block(typeof(object), blockVars, prelude);
        return Expression.Lambda<Func<object?, object?>>(block, sourceParam).Compile();
    }

    /// <summary>Builds an update-in-place delegate: <c>(object? source, object destination) -&gt; void</c>.</summary>
    [RequiresDynamicCode("Compiles a System.Linq.Expressions tree via Expression.Compile(), which requires a JIT and is not supported when publishing Native AOT.")]
    [RequiresUnreferencedCode("Builds the compiled delegate using reflection (MemberInfo/ConstructorInfo/MethodInfo) over the mapped types, which trimming can remove.")]
    public static Action<object?, object> BuildUpdateDelegate(MappingPlan plan, IServiceProvider? services = null)
    {
        if (!plan.IsBuildable)
        {
            var diagnostic = plan.Diagnostics.First(d => d.Severity == DiagnosticSeverity.Error);
            return (_, _) => throw new MappingException(diagnostic);
        }

        var sourceParam = Expression.Parameter(typeof(object), "sourceObj");
        var destinationParam = Expression.Parameter(typeof(object), "destinationObj");
        var typedSource = Expression.Variable(plan.SourceType, "src");
        var typedDestination = Expression.Variable(plan.DestinationType, "dst");

        var blockVars = new List<ParameterExpression> { typedSource, typedDestination };
        var body = new List<Expression>
        {
            Expression.Assign(typedSource, Expression.Convert(sourceParam, plan.SourceType)),
            Expression.Assign(typedDestination, Expression.Convert(destinationParam, plan.DestinationType)),
        };

        var refContext = DeclareReferenceContextIfNeeded(plan, blockVars, body);
        if (refContext is not null)
        {
            // The destination is already the mapped result for the source root on an update -- register
            // it up front so a member elsewhere in the graph referring back to the root source object
            // resolves to this same destination instance instead of constructing a new one.
            body.Add(Expression.Call(
                refContext,
                DictionaryItemSetter,
                Expression.Convert(typedSource, typeof(object)),
                Expression.Convert(typedDestination, typeof(object))));
        }

        var assignments = plan.MemberPlans
            .Where(m => m.Strategy is not (MemberStrategy.Ignored or MemberStrategy.ConstructorArgument))
            .Where(m => m.Source is not ResolvedSource.Unresolved)
            .Select(member => BuildUpdateAssignment(member, typedSource, typedDestination, refContext, services))
            .ToList();

        body.AddRange(assignments);

        var block = Expression.Block(blockVars, body);
        return Expression.Lambda<Action<object?, object>>(block, sourceParam, destinationParam).Compile();
    }

    private static readonly MethodInfo DictionaryItemSetter =
        typeof(Dictionary<object, object>).GetProperty("Item")!.GetSetMethod()!;

    private static readonly MethodInfo DictionaryTryGetValue =
        typeof(Dictionary<object, object>).GetMethod(nameof(Dictionary<object, object>.TryGetValue))!;

    private static readonly ConstructorInfo DictionaryWithComparerCtor =
        typeof(Dictionary<object, object>).GetConstructor([typeof(IEqualityComparer<object>)])!;

    private static readonly PropertyInfo ResolutionContextServicesProperty =
        typeof(ResolutionContext).GetProperty(nameof(ResolutionContext.Services))!;

    /// <summary>
    /// The reference-preservation feature: when this plan opted into
    /// <see cref="ReferenceHandling.Preserve"/>, declares a
    /// <c>Dictionary&lt;object,object&gt;</c> local (keyed by reference identity) in the compiled delegate
    /// and returns an expression referring to it; every other builder method in this class threads that
    /// expression through so the *entire* object graph reached from this one call shares one identity map.
    /// Returns null (no-op everywhere downstream) when Preserve wasn't requested, with zero overhead.
    /// </summary>
    private static Expression? DeclareReferenceContextIfNeeded(MappingPlan plan, List<ParameterExpression> blockVars, List<Expression> statements)
    {
        if (plan.ReferencePlan.Handling != ReferenceHandling.Preserve) return null;

        var ctxVar = Expression.Variable(typeof(Dictionary<object, object>), "refCtx");
        blockVars.Add(ctxVar);
        statements.Add(Expression.Assign(ctxVar,
            Expression.New(DictionaryWithComparerCtor, Expression.Constant(ReferenceEqualityComparer.Instance, typeof(IEqualityComparer<object>)))));
        return ctxVar;
    }

    private static Expression BuildUpdateAssignment(MemberPlan member, Expression typedSource, Expression typedDestination, Expression? refContext, IServiceProvider? services)
    {
        var destinationMemberType = MemberValueTypeHelper.GetMemberType(member.DestinationMember);
        var valueExpr = Expression.Convert(BuildMemberValueExpression(member, typedSource, refContext, services), destinationMemberType);
        var assign = Expression.Assign(
            member.DestinationMember switch
            {
                PropertyInfo p => Expression.Property(typedDestination, p),
                FieldInfo f => Expression.Field(typedDestination, f),
                _ => throw new NotSupportedException(),
            },
            valueExpr);

        if (member.Condition is null)
            return assign;

        // .Condition() on update: a false condition leaves the existing
        // destination value untouched rather than overwriting it with a mapped-but-discarded value.
        var conditionCall = Expression.Invoke(Expression.Constant(member.Condition), Expression.Convert(typedSource, typeof(object)));
        return Expression.IfThen(conditionCall, assign);
    }

    private static Expression BuildRoot(MappingPlan plan, Expression typedSourceExpr, Expression? refContext, IServiceProvider? services)
        => plan.CollectionPlan is not null
            ? BuildCollectionExpression(plan.CollectionPlan, typedSourceExpr, plan.DestinationType, refContext, services)
            : plan.DictionaryPlan is not null
                ? BuildDictionaryExpression(plan.DictionaryPlan, typedSourceExpr, plan.DestinationType, refContext, services)
                : BuildDispatchExpression(plan, typedSourceExpr, refContext, services);

    /// <summary>
    /// If <paramref name="plan"/> carries a <see cref="PolymorphismPlan"/> (one or more
    /// registered subtype pairs), compiles a runtime <c>GetType()</c> check chain that dispatches to the
    /// most specific registered plan; otherwise constructs via the base plan directly. Cases are checked
    /// in registration order and are mutually exclusive on exact runtime type, so there is never an
    /// ambiguous match to begin with, because
    /// <c>GetType()</c> equality can match at most one case).
    /// </summary>
    private static Expression BuildDispatchExpression(MappingPlan plan, Expression rawSourceExpr, Expression? refContext, IServiceProvider? services)
    {
        var baseConstruction = Expression.Convert(BuildConstructExpression(plan, rawSourceExpr, refContext, services), plan.DestinationType);
        if (plan.PolymorphismPlan is null || plan.PolymorphismPlan.Cases.Count == 0)
            return baseConstruction;

        var getTypeMethod = typeof(object).GetMethod(nameof(GetType))!;
        Expression chain = baseConstruction;

        foreach (var polyCase in plan.PolymorphismPlan.Cases.Reverse())
        {
            var typedSrc = Expression.Convert(rawSourceExpr, polyCase.SourceSubtype);
            var subConstructed = Expression.Convert(BuildConstructExpression(polyCase.Plan, typedSrc, refContext, services), plan.DestinationType);
            var typeCheck = Expression.Equal(
                Expression.Call(rawSourceExpr, getTypeMethod),
                Expression.Constant(polyCase.SourceSubtype, typeof(Type)));
            chain = Expression.Condition(typeCheck, subConstructed, chain);
        }

        return chain;
    }

    private static Expression BuildConstructExpression(MappingPlan plan, Expression typedSourceExpr, Expression? refContext, IServiceProvider? services)
    {
        var ctorPlan = plan.ConstructorPlan;

        // Only a parameterless-constructible destination can be registered in
        // the identity map *before* its members are assigned, which is what makes true cycles (A -> B -> A)
        // safe rather than just stack-overflowing. A destination requiring constructor arguments cannot be
        // registered before it exists, so it falls through to ordinary construction below -- a *shared*
        // (non-cyclic) reference to such a type is still correctly deduped, because the identity check runs
        // at the call site (BuildCachedComplexValue) regardless of which construction path is used here;
        // only a genuine cycle through a constructor-bound type remains unsupported. Documented, not silent.
        if (refContext is not null && !typedSourceExpr.Type.IsValueType
            && (ctorPlan.Constructor is null || ctorPlan.ParameterBindings.Count == 0))
        {
            return BuildConstructExpressionWithEarlyRegistration(plan, typedSourceExpr, refContext, services);
        }

        NewExpression newExpr;

        if (ctorPlan.Constructor is not null && ctorPlan.ParameterBindings.Count > 0)
        {
            var args = ctorPlan.ParameterBindings.Select(binding =>
            {
                var matchingMember = plan.MemberPlans.FirstOrDefault(m =>
                    m.Strategy == MemberStrategy.ConstructorArgument &&
                    string.Equals(m.DestinationMember.Name, binding.Parameter.Name, StringComparison.OrdinalIgnoreCase));

                var valueExpr = matchingMember is not null
                    ? BuildMemberValueExpression(matchingMember, typedSourceExpr, refContext, services)
                    : BuildScalarValueExpression(null, binding.Source, typedSourceExpr, binding.Parameter.ParameterType, services);

                return (Expression)Expression.Convert(valueExpr, binding.Parameter.ParameterType);
            }).ToList();

            newExpr = Expression.New(ctorPlan.Constructor, args);
        }
        else
        {
            newExpr = Expression.New(plan.DestinationType);
        }

        var bindings = new List<MemberBinding>();
        foreach (var member in plan.MemberPlans)
        {
            if (member.Strategy is MemberStrategy.ConstructorArgument or MemberStrategy.Ignored) continue;
            if (member.Source is ResolvedSource.Unresolved) continue;

            var destinationMemberType = MemberValueTypeHelper.GetMemberType(member.DestinationMember);
            var valueExpr = Expression.Convert(BuildMemberValueExpression(member, typedSourceExpr, refContext, services), destinationMemberType);

            if (member.Condition is not null)
            {
                // Construction has no "existing value" to preserve, so a false condition yields
                // default(TMember) instead -- update-in-place
                // (BuildUpdateAssignment) is where .Condition() has its most useful, lossless behavior.
                var conditionCall = Expression.Invoke(Expression.Constant(member.Condition), Expression.Convert(typedSourceExpr, typeof(object)));
                valueExpr = Expression.Convert(Expression.Condition(conditionCall, valueExpr, Expression.Default(destinationMemberType)), destinationMemberType);
            }

            bindings.Add(Expression.Bind(member.DestinationMember, valueExpr));
        }

        return bindings.Count > 0 ? Expression.MemberInit(newExpr, bindings) : newExpr;
    }

    private static Expression BuildConstructExpressionWithEarlyRegistration(MappingPlan plan, Expression typedSourceExpr, Expression refContext, IServiceProvider? services)
    {
        var destVar = Expression.Variable(plan.DestinationType, "dst");
        var statements = new List<Expression>
        {
            Expression.Assign(destVar, Expression.New(plan.DestinationType)),
            Expression.Call(refContext, DictionaryItemSetter,
                Expression.Convert(typedSourceExpr, typeof(object)), Expression.Convert(destVar, typeof(object))),
        };

        foreach (var member in plan.MemberPlans)
        {
            if (member.Strategy is MemberStrategy.ConstructorArgument or MemberStrategy.Ignored) continue;
            if (member.Source is ResolvedSource.Unresolved) continue;

            var destinationMemberType = MemberValueTypeHelper.GetMemberType(member.DestinationMember);
            var valueExpr = Expression.Convert(BuildMemberValueExpression(member, typedSourceExpr, refContext, services), destinationMemberType);

            Expression assign = Expression.Assign(
                member.DestinationMember switch
                {
                    PropertyInfo p => Expression.Property(destVar, p),
                    FieldInfo f => Expression.Field(destVar, f),
                    _ => throw new NotSupportedException(),
                },
                valueExpr);

            if (member.Condition is not null)
            {
                var conditionCall = Expression.Invoke(Expression.Constant(member.Condition), Expression.Convert(typedSourceExpr, typeof(object)));
                assign = Expression.IfThen(conditionCall, assign);
            }

            statements.Add(assign);
        }

        statements.Add(destVar);
        return Expression.Block(plan.DestinationType, [destVar], statements);
    }

    private static Expression BuildMemberValueExpression(MemberPlan member, Expression sourceRoot, Expression? refContext, IServiceProvider? services)
    {
        var destinationType = MemberValueTypeHelper.GetMemberType(member.DestinationMember);

        return member.Strategy switch
        {
            MemberStrategy.NestedMapping => BuildNestedExpression(member.NestedPlan!, GetRawSourceExpression(member.Source, sourceRoot), destinationType, refContext, services),
            MemberStrategy.CollectionMapping => BuildCollectionExpression(member.CollectionPlan!, GetRawSourceExpression(member.Source, sourceRoot), destinationType, refContext, services),
            MemberStrategy.DictionaryMapping => BuildDictionaryExpression(member.DictionaryPlan!, GetRawSourceExpression(member.Source, sourceRoot), destinationType, refContext, services),
            _ => BuildScalarValueExpression(member, member.Source, sourceRoot, destinationType, services),
        };
    }

    private static Expression GetRawSourceExpression(ResolvedSource source, Expression sourceRoot) => source switch
    {
        ResolvedSource.MemberChain mc => ChainExpressionBuilder.BuildSafeAccess(sourceRoot, mc.Members),
        ResolvedSource.MethodCall m => Expression.Call(sourceRoot, m.Method),
        ResolvedSource.InlineExpression ie => ReplaceParameter(ie.Expression, sourceRoot),
        _ => throw new NotSupportedException("Nested/collection/dictionary mapping requires a member-chain, method-call, or inline-expression source."),
    };

    private static Expression BuildNestedExpression(MappingPlan nestedPlan, Expression rawSourceExpr, Type destinationType, Expression? refContext, IServiceProvider? services)
    {
        if (!IsNullableGuardCandidate(rawSourceExpr.Type))
            return BuildCachedComplexValue(nestedPlan, rawSourceExpr, destinationType, refContext, services);

        var temp = Expression.Variable(rawSourceExpr.Type, "nestedSrc");

        return Expression.Block(
            destinationType,
            [temp],
            Expression.Assign(temp, rawSourceExpr),
            Expression.Condition(
                Expression.Equal(temp, Expression.Constant(null, rawSourceExpr.Type)),
                Expression.Default(destinationType),
                BuildCachedComplexValue(nestedPlan, temp, destinationType, refContext, services)));
    }

    /// <summary>
    /// The identity-check wrapper shared by nested members, collection elements, and
    /// dictionary values. When reference preservation is active, checks the identity map first; a hit
    /// returns the already-mapped instance (correct for both a genuine cycle already registered by an
    /// ancestor frame, and a plain shared/DAG reference reached a second time), a miss falls through to
    /// <see cref="BuildDispatchExpression"/> (which, for parameterless-constructible destinations,
    /// registers itself in the map before recursing into its own members — see
    /// <see cref="BuildConstructExpressionWithEarlyRegistration"/>). No-op wrapper when
    /// <paramref name="refContext"/> is null (Preserve not requested) or the source is a value type
    /// (value types cannot participate in reference identity).
    /// </summary>
    private static Expression BuildCachedComplexValue(MappingPlan plan, Expression rawSourceExpr, Type destinationType, Expression? refContext, IServiceProvider? services)
    {
        if (refContext is null || rawSourceExpr.Type.IsValueType)
            return Expression.Convert(BuildDispatchExpression(plan, rawSourceExpr, refContext, services), destinationType);

        var existingVar = Expression.Variable(typeof(object), "existing");
        var srcAsObject = Expression.Convert(rawSourceExpr, typeof(object));

        return Expression.Block(
            destinationType,
            [existingVar],
            Expression.Condition(
                Expression.Call(refContext, DictionaryTryGetValue, srcAsObject, existingVar),
                Expression.Convert(existingVar, destinationType),
                Expression.Convert(BuildDispatchExpression(plan, rawSourceExpr, refContext, services), destinationType)));
    }

    private static Expression BuildCollectionExpression(CollectionPlan collectionPlan, Expression sourceCollectionExpr, Type destinationType, Expression? refContext, IServiceProvider? services)
    {
        var sourceElementType = collectionPlan.SourceElementType;
        var destinationElementType = collectionPlan.ElementType;

        Expression BuildFrom(Expression typedRoot)
        {
            var enumerableSourceType = typeof(IEnumerable<>).MakeGenericType(sourceElementType);
            var asEnumerable = Expression.Convert(typedRoot, enumerableSourceType);

            var elementParam = Expression.Parameter(sourceElementType, "e");
            var elementBody = collectionPlan.ElementPlan is not null
                ? BuildCachedComplexValue(collectionPlan.ElementPlan, elementParam, destinationElementType, refContext, services)
                : Expression.Convert(elementParam, destinationElementType);
            var elementLambda = Expression.Lambda(elementBody, elementParam);

            var selectMethod = typeof(Enumerable).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(m => m.Name == nameof(Enumerable.Select) && m.GetParameters().Length == 2)
                .MakeGenericMethod(sourceElementType, destinationElementType);
            Expression selected = Expression.Call(selectMethod, asEnumerable, elementLambda);

            Expression materialized = collectionPlan.TargetKind switch
            {
                CollectionTargetKind.Array => Expression.Call(
                    typeof(Enumerable).GetMethod(nameof(Enumerable.ToArray))!.MakeGenericMethod(destinationElementType), selected),
                CollectionTargetKind.HashSet => Expression.New(
                    typeof(HashSet<>).MakeGenericType(destinationElementType)
                        .GetConstructor([typeof(IEnumerable<>).MakeGenericType(destinationElementType)])!,
                    selected),
                CollectionTargetKind.ImmutableArray => Expression.Call(
                    FindEnumerableExtensionMethod(typeof(System.Collections.Immutable.ImmutableArray), "ToImmutableArray")
                        .MakeGenericMethod(destinationElementType), selected),
                CollectionTargetKind.ImmutableList => Expression.Call(
                    FindEnumerableExtensionMethod(typeof(System.Collections.Immutable.ImmutableList), "ToImmutableList")
                        .MakeGenericMethod(destinationElementType), selected),
                CollectionTargetKind.ImmutableHashSet => Expression.Call(
                    FindEnumerableExtensionMethod(typeof(System.Collections.Immutable.ImmutableHashSet), "ToImmutableHashSet")
                        .MakeGenericMethod(destinationElementType), selected),
                _ => Expression.Call(
                    typeof(Enumerable).GetMethod(nameof(Enumerable.ToList))!.MakeGenericMethod(destinationElementType), selected),
            };

            return destinationType.IsAssignableFrom(materialized.Type)
                ? materialized
                : Expression.Convert(materialized, destinationType);
        }

        if (!IsNullableGuardCandidate(sourceCollectionExpr.Type))
            return BuildFrom(sourceCollectionExpr);

        var temp = Expression.Variable(sourceCollectionExpr.Type, "srcColl");
        return Expression.Block(
            destinationType,
            [temp],
            Expression.Assign(temp, sourceCollectionExpr),
            Expression.Condition(
                Expression.Equal(temp, Expression.Constant(null, sourceCollectionExpr.Type)),
                Expression.Default(destinationType),
                BuildFrom(temp)));
    }

    /// <summary>
    /// Sibling of <see cref="BuildCollectionExpression"/> for keyed collections. Keys convert
    /// directly (see <see cref="DictionaryPlan"/> for why); values may recurse through a nested
    /// <see cref="MappingPlan"/> exactly like a collection element does.
    /// </summary>
    private static Expression BuildDictionaryExpression(DictionaryPlan dictionaryPlan, Expression sourceDictExpr, Type destinationType, Expression? refContext, IServiceProvider? services)
    {
        var sourceKvpType = typeof(KeyValuePair<,>).MakeGenericType(dictionaryPlan.SourceKeyType, dictionaryPlan.SourceValueType);

        Expression BuildFrom(Expression typedRoot)
        {
            var enumerableSourceType = typeof(IEnumerable<>).MakeGenericType(sourceKvpType);
            var asEnumerable = Expression.Convert(typedRoot, enumerableSourceType);

            var kvpParam = Expression.Parameter(sourceKvpType, "kvp");
            var keyProp = sourceKvpType.GetProperty(nameof(KeyValuePair<object, object>.Key))!;
            var valueProp = sourceKvpType.GetProperty(nameof(KeyValuePair<object, object>.Value))!;

            var keyExpr = Expression.Convert(Expression.Property(kvpParam, keyProp), dictionaryPlan.KeyType);
            var rawValueExpr = Expression.Property(kvpParam, valueProp);
            var valueExpr = dictionaryPlan.ValuePlan is not null
                ? BuildCachedComplexValue(dictionaryPlan.ValuePlan, rawValueExpr, dictionaryPlan.ValueType, refContext, services)
                : Expression.Convert(rawValueExpr, dictionaryPlan.ValueType);

            var keyLambda = Expression.Lambda(keyExpr, kvpParam);
            var valueLambda = Expression.Lambda(valueExpr, kvpParam);

            var toDictionaryMethod = typeof(Enumerable).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(m => m.Name == nameof(Enumerable.ToDictionary)
                         && m.GetGenericArguments().Length == 3
                         && m.GetParameters().Length == 3
                         && m.GetParameters()[2].ParameterType.IsGenericType
                         && m.GetParameters()[2].ParameterType.GetGenericTypeDefinition() == typeof(Func<,>))
                .MakeGenericMethod(sourceKvpType, dictionaryPlan.KeyType, dictionaryPlan.ValueType);

            Expression materialized = Expression.Call(toDictionaryMethod, asEnumerable, keyLambda, valueLambda);

            if (dictionaryPlan.TargetKind == DictionaryTargetKind.ImmutableDictionary)
            {
                var toImmutableDictMethod = typeof(System.Collections.Immutable.ImmutableDictionary).GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .First(m => m.Name == "ToImmutableDictionary" && m.GetParameters().Length == 1)
                    .MakeGenericMethod(dictionaryPlan.KeyType, dictionaryPlan.ValueType);
                materialized = Expression.Call(toImmutableDictMethod, materialized);
            }

            return destinationType.IsAssignableFrom(materialized.Type)
                ? materialized
                : Expression.Convert(materialized, destinationType);
        }

        if (!IsNullableGuardCandidate(sourceDictExpr.Type))
            return BuildFrom(sourceDictExpr);

        var temp = Expression.Variable(sourceDictExpr.Type, "srcDict");
        return Expression.Block(
            destinationType,
            [temp],
            Expression.Assign(temp, sourceDictExpr),
            Expression.Condition(
                Expression.Equal(temp, Expression.Constant(null, sourceDictExpr.Type)),
                Expression.Default(destinationType),
                BuildFrom(temp)));
    }

    /// <summary>
    /// Finds the <c>To&lt;Foo&gt;(this IEnumerable&lt;T&gt; source)</c> overload on one of the
    /// <c>System.Collections.Immutable</c> static factory classes -- newer .NET versions also expose a
    /// <c>ReadOnlySpan&lt;T&gt;</c> overload with the same name and arity, so filtering by parameter
    /// *type* (not just name/arity) is required to reliably pick the one that accepts our
    /// <c>IEnumerable&lt;T&gt;</c>-typed <c>Select()</c> result across .NET versions.
    /// </summary>
    private static MethodInfo FindEnumerableExtensionMethod(Type declaringType, string methodName)
        => declaringType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .First(m => m.Name == methodName
                     && m.GetParameters().Length == 1
                     && m.GetParameters()[0].ParameterType.IsGenericType
                     && m.GetParameters()[0].ParameterType.GetGenericTypeDefinition() == typeof(IEnumerable<>));

    private static bool IsNullableGuardCandidate(Type type) => !type.IsValueType || Nullable.GetUnderlyingType(type) is not null;

    private static Expression BuildScalarValueExpression(MemberPlan? owner, ResolvedSource source, Expression sourceRoot, Type targetType, IServiceProvider? services)
    {
        switch (source)
        {
            case ResolvedSource.MemberChain mc:
                var chainAccess = ChainExpressionBuilder.BuildSafeAccess(sourceRoot, mc.Members);
                return ApplyNullPolicy(owner, chainAccess, targetType);

            case ResolvedSource.MethodCall m:
                var call = Expression.Call(sourceRoot, m.Method);
                return ApplyNullPolicy(owner, call, targetType);

            case ResolvedSource.InlineExpression ie:
                var invoked = ReplaceParameter(ie.Expression, sourceRoot);
                return invoked.Type == targetType ? invoked : Expression.Convert(invoked, targetType);

            case ResolvedSource.ConstantOrDefault c:
                return Expression.Constant(c.Value, targetType);

            case ResolvedSource.ValueResolver vr:
                return BuildResolverCall(vr, sourceRoot, targetType, services);

            case ResolvedSource.ValueConverter vc:
                return BuildConverterCall(vc, sourceRoot, targetType, services);

            case ResolvedSource.ProjectionResolver pr:
                return BuildProjectionResolverSplice(pr, sourceRoot, targetType);

            default:
                return Expression.Default(targetType);
        }
    }

    private static Expression ApplyNullPolicy(MemberPlan? owner, Expression rawValue, Type targetType)
    {
        var converted = rawValue.Type == targetType ? rawValue : SafeConvert(rawValue, targetType);

        if (owner is null || owner.NullPolicy is NullPolicy.Map or NullPolicy.Throw)
            return converted; // Throw is enforced naturally: assigning null into a non-nullable value
                               // Type member throws NullReferenceException/InvalidOperationException
                               // from the CLR itself at the Convert step, which is an acceptable, if
                               // blunt stand-in for a dedicated MappingException -- tracked
                               // as a follow-up to wrap with a proper diagnostic-bearing throw.

        if (!IsNullableGuardCandidate(rawValue.Type))
            return converted; // can't be null at this type, nothing to substitute

        var isRefLike = !rawValue.Type.IsValueType;
        var nullConst = Expression.Constant(null, rawValue.Type);

        return owner.NullPolicy switch
        {
            NullPolicy.Ignore or NullPolicy.Default => Expression.Condition(
                Expression.Equal(rawValue, nullConst), Expression.Default(targetType), converted),
            NullPolicy.Substitute => Expression.Condition(
                Expression.Equal(rawValue, nullConst),
                Expression.Constant(owner.NullSubstitute, targetType),
                converted),
            _ => converted,
        };
    }

    private static Expression SafeConvert(Expression value, Type targetType)
    {
        if (Nullable.GetUnderlyingType(value.Type) is { } underlying && !targetType.IsAssignableFrom(value.Type))
            return Expression.Convert(Expression.Convert(value, underlying), targetType);
        return Expression.Convert(value, targetType);
    }

    private static Expression ReplaceParameter(LambdaExpression lambda, Expression replacement)
        => new ParameterReplacer(lambda.Parameters[0], replacement).Visit(lambda.Body)!;

    /// <summary>
    /// <see cref="ResolvedSource.ProjectionResolver"/> members are spliced directly
    /// into the tree (same technique as <see cref="ReplaceParameter"/> for a plain inline expression),
    /// never invoked as an opaque compiled delegate — this is what keeps a projection-resolved member
    /// translatable by <see cref="Projection.ProjectionExpressionBuilder"/> too, since both tiers reuse
    /// the exact same resolver expression body rather than each having their own execution path for it.
    /// Deliberately NOT given access to <see cref="IServiceProvider"/> (unlike <see cref="BuildResolverCall"/>
    /// below): a projection resolver's <c>GetExpression()</c> body is spliced directly into a query tree a
    /// LINQ provider must translate (e.g. to SQL) — a DI-resolved instance captured there would not
    /// survive translation, so these are documented as always
    /// stateless/parameterless, unlike the runtime-only <see cref="IValueResolver{TSource,TDestination,TMember}"/>.
    /// </summary>
    private static Expression BuildProjectionResolverSplice(ResolvedSource.ProjectionResolver resolver, Expression sourceRoot, Type targetType)
    {
        var lambda = Projection.ProjectionResolverHelper.GetResolverExpression(resolver.ResolverType);
        var inlined = ReplaceParameter(lambda, sourceRoot);
        return inlined.Type == targetType ? inlined : Expression.Convert(inlined, targetType);
    }

    /// <summary>
    /// Instantiates <paramref name="resolver"/>'s type via <paramref name="services"/> when an
    /// <see cref="IServiceProvider"/> is available (falling back to the parameterless-constructor
    /// path otherwise, so nothing changes for a caller who never touches DI), then closes over that one
    /// instance as a compiled constant — see the scoping caveat on this class's own doc comment.
    /// </summary>
    private static Expression BuildResolverCall(ResolvedSource.ValueResolver resolver, Expression sourceRoot, Type targetType, IServiceProvider? services)
    {
        var resolverInterface = resolver.ResolverType.GetInterfaces()
            .First(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IValueResolver<,,>));
        var args = resolverInterface.GetGenericArguments(); // TSource, TDestination, TMember
        var resolveMethod = resolverInterface.GetMethod(nameof(IValueResolver<object, object, object>.Resolve))!;

        var resolverInstance = ResolveInstance(services, resolver.ResolverType);

        var call = Expression.Call(
            Expression.Constant(resolverInstance, resolver.ResolverType),
            resolveMethod,
            Expression.Convert(sourceRoot, args[0]),
            Expression.Default(args[1]),
            Expression.Default(args[2]),
            BuildResolutionContextExpression(services));

        return Expression.Convert(call, targetType);
    }

    private static Expression BuildConverterCall(ResolvedSource.ValueConverter converter, Expression sourceRoot, Type targetType, IServiceProvider? services)
    {
        var converterInterface = typeof(IValueConverter<,>).MakeGenericType(converter.SourceType, converter.DestinationType);
        var convertMethod = converterInterface.GetMethod(nameof(IValueConverter<object, object>.Convert))!;

        var converterInstance = ResolveInstance(services, converter.ConverterType);

        var call = Expression.Call(
            Expression.Constant(converterInstance, converter.ConverterType),
            convertMethod,
            Expression.Convert(sourceRoot, converter.SourceType),
            BuildResolutionContextExpression(services));

        return Expression.Convert(call, targetType);
    }

    /// <summary>
    /// Shared by <see cref="BuildResolverCall"/>/<see cref="BuildConverterCall"/>: DI-first,
    /// constructor-injection-fallback, Activator-last-resort instantiation.
    ///
    /// Tries, in order: (1) <paramref name="services"/>.GetService(<paramref name="type"/>) -- the
    /// resolver/converter was registered directly in the container; (2) if not registered directly but
    /// <paramref name="services"/> is available, resolves the type's most-parameters public constructor
    /// by looking up each parameter's type in <paramref name="services"/> -- a small, self-contained
    /// stand-in for <c>Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance</c>,
    /// deliberately hand-rolled rather than taking a second package dependency (on top of
    /// FluxMapper.Extensions.DependencyInjection's own Microsoft.Extensions.DependencyInjection.Abstractions
    /// reference) purely for this one call -- FluxMapper.Core itself depends on nothing but the
    /// BCL-native <see cref="IServiceProvider"/>, so a caller who never touches DI never pulls in any DI
    /// package transitively; (3) a bare parameterless constructor via <see cref="Activator.CreateInstance(Type)"/>,
    /// unconditionally available so this call never regresses for a
    /// caller who never supplies a services provider at all.
    /// </summary>
    private static object ResolveInstance(IServiceProvider? services, Type type)
    {
        var direct = services?.GetService(type);
        if (direct is not null) return direct;

        if (services is not null)
        {
            var constructor = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .OrderByDescending(c => c.GetParameters().Length)
                .FirstOrDefault(c => c.GetParameters().Length > 0);

            if (constructor is not null)
            {
                var parameters = constructor.GetParameters();
                var args = new object?[parameters.Length];
                var resolvable = true;

                for (var i = 0; i < parameters.Length; i++)
                {
                    var arg = services.GetService(parameters[i].ParameterType);
                    if (arg is null && !parameters[i].HasDefaultValue)
                    {
                        resolvable = false;
                        break;
                    }

                    args[i] = arg ?? parameters[i].DefaultValue;
                }

                if (resolvable) return constructor.Invoke(args);
            }
        }

        return Activator.CreateInstance(type)
            ?? throw new InvalidOperationException($"{type} has no usable parameterless constructor and was not resolvable (directly, or via a constructor whose parameters are all resolvable) from the configured IServiceProvider.");
    }

    /// <summary>
    /// Builds <c>new ResolutionContext { Services = services }</c> as an expression — still evaluated
    /// fresh on every single invocation of the compiled delegate (never hoisted to a shared constant),
    /// which is what keeps <see cref="ResolutionContext"/> free of the cross-call shared mutable state
    /// its own doc comment rules out; only the closed-over <paramref name="services"/> reference itself
    /// is a compile-time constant.
    /// </summary>
    private static Expression BuildResolutionContextExpression(IServiceProvider? services)
        => Expression.MemberInit(
            Expression.New(typeof(ResolutionContext)),
            Expression.Bind(ResolutionContextServicesProperty, Expression.Constant(services, typeof(IServiceProvider))));

    private sealed class ParameterReplacer(ParameterExpression from, Expression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
    }
}
