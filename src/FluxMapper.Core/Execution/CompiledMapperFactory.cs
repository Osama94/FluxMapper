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

    /// <summary>
    /// The typed sibling of <see cref="BuildConstructDelegate"/>, backing <see cref="Mapper.GetTypedMapper{TSource,TDestination}"/>.
    /// Compiles a lambda parameterized directly on <typeparamref name="TSource"/>/<typeparamref name="TDestination"/>
    /// instead of <c>object</c> -- when <typeparamref name="TSource"/> is exactly <see cref="MappingPlan.SourceType"/>
    /// (the overwhelmingly common case), the parameter is fed straight into <see cref="BuildRoot"/> with no
    /// intermediate local variable or cast at all, unlike <see cref="BuildConstructDelegate"/>'s
    /// object-boundary shape, which always declares and assigns a typed local from a
    /// <c>Convert(sourceParam, plan.SourceType)</c>. Every downstream builder method
    /// (<see cref="BuildRoot"/>, <see cref="BuildDispatchExpression"/>, etc.) already takes a plain
    /// <see cref="Expression"/> for "the current source", so this reuses that exact same, already-tested
    /// tree-building code -- only the outer parameter/null-check/return shell differs from the
    /// object-boxed version above.
    /// </summary>
    [RequiresDynamicCode("Compiles a System.Linq.Expressions tree via Expression.Compile(), which requires a JIT and is not supported when publishing Native AOT.")]
    [RequiresUnreferencedCode("Builds the compiled delegate using reflection (MemberInfo/ConstructorInfo/MethodInfo) over the mapped types, which trimming can remove.")]
    public static Func<TSource, TDestination> BuildTypedConstructDelegate<TSource, TDestination>(MappingPlan plan, IServiceProvider? services = null)
    {
        if (!plan.IsBuildable)
        {
            var diagnostic = plan.Diagnostics.First(d => d.Severity == DiagnosticSeverity.Error);
            return _ => throw new MappingException(diagnostic);
        }

        var sourceParam = Expression.Parameter(typeof(TSource), "source");
        Expression typedSourceExpr = typeof(TSource) == plan.SourceType
            ? sourceParam
            : Expression.Convert(sourceParam, plan.SourceType);

        var blockVars = new List<ParameterExpression>();
        var prelude = new List<Expression>();
        var refContext = DeclareReferenceContextIfNeeded(plan, blockVars, prelude);
        var body = BuildRoot(plan, typedSourceExpr, refContext, services);
        var convertedBody = Expression.Convert(body, typeof(TDestination));

        Expression finalExpr = IsNullableGuardCandidate(typeof(TSource))
            ? Expression.Condition(
                Expression.Equal(sourceParam, Expression.Constant(null, typeof(TSource))),
                Expression.Default(typeof(TDestination)),
                convertedBody)
            : convertedBody;

        prelude.Add(finalExpr);

        Expression lambdaBody = blockVars.Count == 0 && prelude.Count == 1
            ? prelude[0]
            : Expression.Block(typeof(TDestination), blockVars, prelude);

        return Expression.Lambda<Func<TSource, TDestination>>(lambdaBody, sourceParam).Compile();
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

        if (plan.BeforeMap is not null)
        {
            body.Add(Expression.Invoke(Expression.Constant(plan.BeforeMap),
                Expression.Convert(typedSource, typeof(object)), Expression.Convert(typedDestination, typeof(object))));
        }

        var assignments = plan.MemberPlans
            .Where(m => m.Strategy is not (MemberStrategy.Ignored or MemberStrategy.ConstructorArgument))
            .Where(m => m.Source is not ResolvedSource.Unresolved || m.Strategy is MemberStrategy.PathMapping)
            .Select(member => BuildUpdateAssignment(member, typedSource, typedDestination, refContext, services))
            .ToList();

        body.AddRange(assignments);

        if (plan.AfterMap is not null)
        {
            body.Add(Expression.Invoke(Expression.Constant(plan.AfterMap),
                Expression.Convert(typedSource, typeof(object)), Expression.Convert(typedDestination, typeof(object))));
        }

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

    private static readonly PropertyInfo ResolutionContextCurrentProperty =
        typeof(ResolutionContext).GetProperty(nameof(ResolutionContext.Current))!;

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

        // A ConstructUsing expression and/or BeforeMap/AfterMap hooks need statement-by-statement control
        // over construction (invoke the custom expression, or splice a delegate call between construction
        // and member assignment) that a single MemberInit expression cannot express -- route the whole
        // plan through the block-based builder below instead. The common case (none of the three) never
        // pays for this: it falls straight through to the existing MemberInit path.
        if (ctorPlan.UsesCustomConstruction || plan.BeforeMap is not null || plan.AfterMap is not null)
        {
            return BuildConstructExpressionWithHooks(plan, typedSourceExpr, refContext, services);
        }

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

        var newExpr = BuildNewExpression(plan, ctorPlan, typedSourceExpr, refContext, services);

        var bindings = new List<MemberBinding>();
        foreach (var member in plan.MemberPlans)
        {
            if (member.Strategy is MemberStrategy.ConstructorArgument or MemberStrategy.Ignored) continue;
            // PathMapping members deliberately carry an Unresolved Source sentinel (their
            // real value comes from member.PathPlan instead) -- skip the general Unresolved
            // guard for them specifically, or a ForPath-targeted member would silently never
            // get assigned at all.
            if (member.Source is ResolvedSource.Unresolved && member.Strategy is not MemberStrategy.PathMapping) continue;

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
            // PathMapping members deliberately carry an Unresolved Source sentinel (their
            // real value comes from member.PathPlan instead) -- skip the general Unresolved
            // guard for them specifically, or a ForPath-targeted member would silently never
            // get assigned at all.
            if (member.Source is ResolvedSource.Unresolved && member.Strategy is not MemberStrategy.PathMapping) continue;

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

    /// <summary>
    /// Builds the <c>new Foo(...)</c> (or parameterless <c>new Foo()</c>) node for a plan's ordinary,
    /// non-custom construction path. Shared by <see cref="BuildConstructExpression"/>'s fast MemberInit
    /// path and <see cref="BuildConstructExpressionWithHooks"/>'s block-based path so a parameterized
    /// constructor behaves identically whether or not the plan also carries BeforeMap/AfterMap hooks.
    /// Never called when <see cref="ConstructorPlan.UsesCustomConstruction"/> is true -- that case is
    /// handled by evaluating <see cref="ConstructorPlan.CustomExpression"/> directly instead.
    /// </summary>
    private static NewExpression BuildNewExpression(MappingPlan plan, ConstructorPlan ctorPlan, Expression typedSourceExpr, Expression? refContext, IServiceProvider? services)
    {
        if (ctorPlan.Constructor is null || ctorPlan.ParameterBindings.Count == 0)
        {
            return Expression.New(plan.DestinationType);
        }

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

        return Expression.New(ctorPlan.Constructor, args);
    }

    /// <summary>
    /// The block-based construction path used whenever a plan has a <c>ConstructUsing</c> expression
    /// and/or BeforeMap/AfterMap hooks -- none of which fit inside a single MemberInit expression the way
    /// <see cref="BuildConstructExpression"/>'s fast path does. Statement order mirrors AutoMapper:
    /// construct, run BeforeMap against the freshly-constructed (not yet populated) instance, assign every
    /// member, then run AfterMap against the fully-populated instance. Early reference-map registration
    /// (see <see cref="BuildConstructExpressionWithEarlyRegistration"/>) still happens immediately after
    /// construction, before BeforeMap runs, under the exact same "parameterless-constructible" gate as the
    /// fast path -- a plan using ConstructUsing always satisfies that gate too, since
    /// <see cref="Building.MappingPlanBuilder"/> never populates <see cref="ConstructorPlan.ParameterBindings"/> for
    /// a custom-constructed plan.
    /// </summary>
    private static Expression BuildConstructExpressionWithHooks(MappingPlan plan, Expression typedSourceExpr, Expression? refContext, IServiceProvider? services)
    {
        var ctorPlan = plan.ConstructorPlan;
        var destVar = Expression.Variable(plan.DestinationType, "dst");

        Expression constructExpr = ctorPlan.UsesCustomConstruction
            ? Expression.Invoke(ctorPlan.CustomExpression!, typedSourceExpr)
            : BuildNewExpression(plan, ctorPlan, typedSourceExpr, refContext, services);

        var statements = new List<Expression>
        {
            Expression.Assign(destVar, Expression.Convert(constructExpr, plan.DestinationType)),
        };

        if (refContext is not null && !typedSourceExpr.Type.IsValueType
            && (ctorPlan.Constructor is null || ctorPlan.ParameterBindings.Count == 0))
        {
            statements.Add(Expression.Call(refContext, DictionaryItemSetter,
                Expression.Convert(typedSourceExpr, typeof(object)), Expression.Convert(destVar, typeof(object))));
        }

        if (plan.BeforeMap is not null)
        {
            statements.Add(Expression.Invoke(Expression.Constant(plan.BeforeMap),
                Expression.Convert(typedSourceExpr, typeof(object)), Expression.Convert(destVar, typeof(object))));
        }

        foreach (var member in plan.MemberPlans)
        {
            if (member.Strategy is MemberStrategy.ConstructorArgument or MemberStrategy.Ignored) continue;
            // PathMapping members deliberately carry an Unresolved Source sentinel (their
            // real value comes from member.PathPlan instead) -- skip the general Unresolved
            // guard for them specifically, or a ForPath-targeted member would silently never
            // get assigned at all.
            if (member.Source is ResolvedSource.Unresolved && member.Strategy is not MemberStrategy.PathMapping) continue;

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

        if (plan.AfterMap is not null)
        {
            statements.Add(Expression.Invoke(Expression.Constant(plan.AfterMap),
                Expression.Convert(typedSourceExpr, typeof(object)), Expression.Convert(destVar, typeof(object))));
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
            MemberStrategy.PathMapping => BuildPathPlanExpression(member.PathPlan!, sourceRoot, refContext, services),
            _ => BuildScalarValueExpression(member, member.Source, sourceRoot, destinationType, services),
        };
    }

    /// <summary>
    /// Materializes one <see cref="Ir.PathPlan"/> subtree as a single composite expression: construct a
    /// fresh instance of <see cref="Ir.PathPlan.DestinationType"/> (see that type's own doc comment for
    /// why this is always a fresh construction, never a merge into an existing instance -- even during
    /// update-in-place), assign every leaf value and recursively-built branch value onto it, then yield
    /// the constructed instance as one bindable value -- the same "build one Expression.Block, return one
    /// value" shape already used by <see cref="BuildNestedExpression"/>, <see cref="BuildCollectionExpression"/>,
    /// and <see cref="BuildDictionaryExpression"/>. Every <see cref="Ir.PathLeaf.Source"/> is resolved
    /// against <paramref name="sourceRoot"/> -- the whole mapped source object, never some narrower object
    /// reached by walking the destination path -- because a <c>ForPath</c> registration's source expression
    /// is always written in terms of the original source type, not an intermediate destination segment's
    /// own (often unrelated) source counterpart.
    /// </summary>
    private static Expression BuildPathPlanExpression(PathPlan plan, Expression sourceRoot, Expression? refContext, IServiceProvider? services)
    {
        var instanceVar = Expression.Variable(plan.DestinationType, "pathTarget");
        var statements = new List<Expression>
        {
            Expression.Assign(instanceVar, Expression.New(plan.DestinationType)),
        };

        foreach (var leaf in plan.Leaves)
        {
            var leafValue = BuildScalarValueExpression(null, leaf.Source, sourceRoot, leaf.MemberType, services);
            var target = leaf.Member switch
            {
                PropertyInfo p => (Expression)Expression.Property(instanceVar, p),
                FieldInfo f => Expression.Field(instanceVar, f),
                _ => throw new NotSupportedException(),
            };
            statements.Add(Expression.Assign(target, Expression.Convert(leafValue, MemberValueTypeHelper.GetMemberType(leaf.Member))));
        }

        foreach (var branch in plan.Branches)
        {
            var branchValue = BuildPathPlanExpression(branch.Plan, sourceRoot, refContext, services);
            var target = branch.Member switch
            {
                PropertyInfo p => (Expression)Expression.Property(instanceVar, p),
                FieldInfo f => Expression.Field(instanceVar, f),
                _ => throw new NotSupportedException(),
            };
            statements.Add(Expression.Assign(target, branchValue));
        }

        statements.Add(instanceVar);
        return Expression.Block(plan.DestinationType, [instanceVar], statements);
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

    /// <summary>
    /// Builds a collection member as a compiled <c>for</c>/<c>while</c> loop over the source elements,
    /// splicing each element's mapping expression directly into the loop body instead of calling a
    /// separately-compiled per-element delegate. This replaced an earlier <c>Enumerable.Select(...).ToList()</c>
    /// (or <c>.ToArray()</c>/etc.) pipeline: correct, but paying for a LINQ iterator allocation, a
    /// closure-capturing delegate for the per-element lambda, and virtual <c>MoveNext()</c>/<c>Current</c>
    /// dispatch on every element, on top of the actual per-element mapping work -- overhead that a
    /// benchmark (see <c>benchmarks/FluxMapper.Benchmarks</c> and <c>COMPETITIVE_GAP_ANALYSIS.md</c>) showed
    /// dominating small collections badly enough to make this tier the slowest of three real-world mappers
    /// on a nested+collection shape.
    ///
    /// Two shapes are generated, chosen once per member at plan-build time from the source's static type
    /// (never re-checked per call):
    /// <list type="bullet">
    /// <item>Source is an array, a <see cref="List{T}"/>, or implements <see cref="IList{T}"/>/
    /// <see cref="IReadOnlyList{T}"/> (arrays and <see cref="List{T}"/> resolve to a direct, non-virtual
    /// <c>Length</c>/indexer or <c>Count</c>/indexer access -- no interface dispatch at all for the two most
    /// common cases): <see cref="BuildIndexedLoop"/> emits a counted <c>for</c> loop writing straight into a
    /// pre-sized destination array via <c>Expression.ArrayAccess</c>, which
    /// <see cref="MaterializeCollection"/> then wraps (or, for an <see cref="Array"/> target, returns
    /// as-is -- zero extra copies) into whatever collection kind the destination member actually needs.</item>
    /// <item>Any other <see cref="IEnumerable{T}"/> (a computed property, a query result, a
    /// <see cref="HashSet{T}"/> source, etc.): <see cref="BuildEnumeratorLoop"/> emits the same
    /// <c>GetEnumerator()</c>/<c>MoveNext()</c>/<c>Current</c>/<c>Dispose()</c> shape the C# compiler itself
    /// emits for <c>foreach</c>, appending each mapped element straight onto a destination
    /// <see cref="List{T}"/> (pre-sized from <see cref="ICollection{T}.Count"/> when the source has one) --
    /// still no per-element delegate call, just no known count to index by ahead of time.</item>
    /// </list>
    /// </summary>
    private static Expression BuildCollectionExpression(CollectionPlan collectionPlan, Expression sourceCollectionExpr, Type destinationType, Expression? refContext, IServiceProvider? services)
    {
        var sourceElementType = collectionPlan.SourceElementType;
        var destinationElementType = collectionPlan.ElementType;

        Expression BuildFrom(Expression typedRoot)
        {
            var isIndexed = TryGetIndexedAccessors(typedRoot, sourceElementType, out var count, out var itemAt);

            // A List<T> destination built from an indexable source used to go through BuildIndexedLoop
            // (array) followed by MaterializeCollection wrapping that array in `new List<T>(array)` --
            // correct, but a full second allocation-and-copy on top of the array already built, since that
            // constructor sizes its own backing array to Count and CopyTo's into it rather than adopting
            // the array it was given. Building directly into the List<T> here instead (BuildIndexedLoopIntoList)
            // skips the intermediate array entirely -- one allocation (the list's own backing array, sized
            // once via its capacity constructor) instead of two.
            if (isIndexed && collectionPlan.TargetKind == CollectionTargetKind.List)
            {
                var list = BuildIndexedLoopIntoList(count, itemAt, destinationElementType, collectionPlan, refContext, services);
                return destinationType.IsAssignableFrom(list.Type) ? list : Expression.Convert(list, destinationType);
            }

            var sequence = isIndexed
                ? BuildIndexedLoop(count, itemAt, destinationElementType, collectionPlan, refContext, services)
                : BuildEnumeratorLoop(typedRoot, sourceElementType, destinationElementType, collectionPlan, refContext, services);

            var materialized = MaterializeCollection(sequence, collectionPlan.TargetKind, destinationElementType);

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
    /// Build-time (not per-call) check for whether <paramref name="coll"/>'s static type supports
    /// <c>Count</c> + an integer indexer -- an array, an exact <see cref="List{T}"/>, or anything
    /// implementing <see cref="IList{T}"/> or <see cref="IReadOnlyList{T}"/> for <paramref name="elementType"/>.
    /// Arrays and the exact <see cref="List{T}"/> case bind directly to those types' own (non-virtual)
    /// members rather than going through an interface, since those two cover the overwhelming majority of
    /// real-world collection members. Returns the accessors as build-time values (an <see cref="Expression"/>
    /// for the count, and a delegate from an index expression to an element-access expression) rather than
    /// performing any of the actual indexing here -- this method only decides whether the fast, indexed loop
    /// shape applies at all.
    /// </summary>
    private static bool TryGetIndexedAccessors(Expression coll, Type elementType, out Expression count, out Func<Expression, Expression> itemAt)
    {
        var collType = coll.Type;

        if (collType.IsArray)
        {
            count = Expression.ArrayLength(coll);
            itemAt = index => Expression.ArrayIndex(coll, index);
            return true;
        }

        var listType = typeof(List<>).MakeGenericType(elementType);
        if (collType == listType)
        {
            var countProperty = listType.GetProperty(nameof(List<object>.Count))!;
            var itemProperty = listType.GetProperty("Item")!;
            count = Expression.Property(coll, countProperty);
            itemAt = index => Expression.Property(coll, itemProperty, index);
            return true;
        }

        var listInterface = typeof(IList<>).MakeGenericType(elementType);
        if (listInterface.IsAssignableFrom(collType))
        {
            var typed = collType == listInterface ? coll : Expression.Convert(coll, listInterface);
            var countProperty = typeof(ICollection<>).MakeGenericType(elementType).GetProperty(nameof(ICollection<object>.Count))!;
            var itemProperty = listInterface.GetProperty("Item")!;
            count = Expression.Property(typed, countProperty);
            itemAt = index => Expression.Property(typed, itemProperty, index);
            return true;
        }

        var readOnlyListInterface = typeof(IReadOnlyList<>).MakeGenericType(elementType);
        if (readOnlyListInterface.IsAssignableFrom(collType))
        {
            var typed = collType == readOnlyListInterface ? coll : Expression.Convert(coll, readOnlyListInterface);
            var countProperty = typeof(IReadOnlyCollection<>).MakeGenericType(elementType).GetProperty(nameof(IReadOnlyCollection<object>.Count))!;
            var itemProperty = readOnlyListInterface.GetProperty("Item")!;
            count = Expression.Property(typed, countProperty);
            itemAt = index => Expression.Property(typed, itemProperty, index);
            return true;
        }

        count = null!;
        itemAt = null!;
        return false;
    }

    /// <summary>
    /// Emits <c>var arr = new TDest[count]; for (var i = 0; i &lt; count; i++) arr[i] = &lt;element&gt;;</c>
    /// directly as an expression tree, evaluating to the destination array. <paramref name="count"/> and
    /// <paramref name="itemAt"/> come from <see cref="TryGetIndexedAccessors"/>, so this never re-derives
    /// how to read the source -- it only owns the loop and destination-array shape.
    /// </summary>
    private static Expression BuildIndexedLoop(Expression count, Func<Expression, Expression> itemAt, Type destinationElementType, CollectionPlan collectionPlan, Expression? refContext, IServiceProvider? services)
    {
        var countVar = Expression.Variable(typeof(int), "count");
        var indexVar = Expression.Variable(typeof(int), "i");
        var destArrayVar = Expression.Variable(destinationElementType.MakeArrayType(), "destArr");
        var breakLabel = Expression.Label("collectionLoopBreak");

        var elementSource = itemAt(indexVar);
        var elementValue = collectionPlan.ElementPlan is not null
            ? BuildCachedComplexValue(collectionPlan.ElementPlan, elementSource, destinationElementType, refContext, services)
            : Expression.Convert(elementSource, destinationElementType);

        var loopBody = Expression.IfThenElse(
            Expression.LessThan(indexVar, countVar),
            Expression.Block(
                typeof(void),
                Expression.Assign(Expression.ArrayAccess(destArrayVar, indexVar), elementValue),
                Expression.Assign(indexVar, Expression.Add(indexVar, Expression.Constant(1)))),
            Expression.Break(breakLabel));

        return Expression.Block(
            destArrayVar.Type,
            [countVar, indexVar, destArrayVar],
            Expression.Assign(countVar, count),
            Expression.Assign(destArrayVar, Expression.NewArrayBounds(destinationElementType, countVar)),
            Expression.Assign(indexVar, Expression.Constant(0)),
            Expression.Loop(loopBody, breakLabel),
            destArrayVar);
    }

    /// <summary>
    /// Same indexed-source shape as <see cref="BuildIndexedLoop"/>, but for a <see cref="CollectionTargetKind.List"/>
    /// destination specifically: builds directly into a <see cref="List{T}"/> pre-sized via its capacity
    /// constructor, writing each element with <c>Add</c>, instead of building an array and handing it to
    /// <see cref="MaterializeCollection"/> to wrap in a *second* <see cref="List{T}"/> via the
    /// <c>List{T}(IEnumerable{T})</c> constructor -- which, given an array, still allocates its own backing
    /// array sized to <c>Count</c> and copies into it, a full second allocation on top of the array already
    /// built. This skips that: one allocation (the list's own backing array) instead of two.
    /// </summary>
    private static Expression BuildIndexedLoopIntoList(Expression count, Func<Expression, Expression> itemAt, Type destinationElementType, CollectionPlan collectionPlan, Expression? refContext, IServiceProvider? services)
    {
        var listType = typeof(List<>).MakeGenericType(destinationElementType);
        var countVar = Expression.Variable(typeof(int), "count");
        var indexVar = Expression.Variable(typeof(int), "i");
        var listVar = Expression.Variable(listType, "destList");
        var breakLabel = Expression.Label("collectionLoopBreak");

        var elementSource = itemAt(indexVar);
        var elementValue = collectionPlan.ElementPlan is not null
            ? BuildCachedComplexValue(collectionPlan.ElementPlan, elementSource, destinationElementType, refContext, services)
            : Expression.Convert(elementSource, destinationElementType);

        var addMethod = listType.GetMethod(nameof(List<object>.Add))!;

        var loopBody = Expression.IfThenElse(
            Expression.LessThan(indexVar, countVar),
            Expression.Block(
                typeof(void),
                Expression.Call(listVar, addMethod, elementValue),
                Expression.Assign(indexVar, Expression.Add(indexVar, Expression.Constant(1)))),
            Expression.Break(breakLabel));

        return Expression.Block(
            listType,
            [countVar, indexVar, listVar],
            Expression.Assign(countVar, count),
            Expression.Assign(listVar, Expression.New(listType.GetConstructor([typeof(int)])!, countVar)),
            Expression.Assign(indexVar, Expression.Constant(0)),
            Expression.Loop(loopBody, breakLabel),
            listVar);
    }

    /// <summary>
    /// Fallback for a source with no known count/indexer: the same <c>GetEnumerator()</c>/<c>MoveNext()</c>/
    /// <c>Current</c>/<c>try</c>-<c>finally</c>-<c>Dispose()</c> shape the C# compiler emits for
    /// <c>foreach</c>, appending each mapped element onto a destination <see cref="List{T}"/> (pre-sized from
    /// <see cref="ICollection{T}"/>'s <c>Count</c> when the source happens to have one, e.g. a
    /// <see cref="HashSet{T}"/>, even though it has no indexer to loop by). Still splices the element's
    /// mapping expression directly into the loop rather than calling a per-element delegate.
    /// </summary>
    private static Expression BuildEnumeratorLoop(Expression coll, Type sourceElementType, Type destinationElementType, CollectionPlan collectionPlan, Expression? refContext, IServiceProvider? services)
    {
        var enumerableType = typeof(IEnumerable<>).MakeGenericType(sourceElementType);
        var enumeratorType = typeof(IEnumerator<>).MakeGenericType(sourceElementType);
        var getEnumeratorMethod = enumerableType.GetMethod(nameof(IEnumerable<object>.GetEnumerator))!;
        var moveNextMethod = typeof(System.Collections.IEnumerator).GetMethod(nameof(System.Collections.IEnumerator.MoveNext))!;
        var currentProperty = enumeratorType.GetProperty(nameof(IEnumerator<object>.Current))!;
        var disposeMethod = typeof(IDisposable).GetMethod(nameof(IDisposable.Dispose))!;

        var listType = typeof(List<>).MakeGenericType(destinationElementType);
        var listVar = Expression.Variable(listType, "destList");
        var enumeratorVar = Expression.Variable(enumeratorType, "srcEnumerator");
        var breakLabel = Expression.Label("collectionLoopBreak");

        var elementSource = Expression.Property(enumeratorVar, currentProperty);
        var elementValue = collectionPlan.ElementPlan is not null
            ? BuildCachedComplexValue(collectionPlan.ElementPlan, elementSource, destinationElementType, refContext, services)
            : Expression.Convert(elementSource, destinationElementType);

        var genericCollectionInterface = typeof(ICollection<>).MakeGenericType(sourceElementType);
        Expression newList = genericCollectionInterface.IsAssignableFrom(coll.Type)
            ? Expression.New(
                listType.GetConstructor([typeof(int)])!,
                Expression.Property(Expression.Convert(coll, genericCollectionInterface), nameof(ICollection<object>.Count)))
            : Expression.New(listType);

        var addMethod = listType.GetMethod(nameof(List<object>.Add))!;

        var loop = Expression.TryFinally(
            Expression.Loop(
                Expression.IfThenElse(
                    Expression.Call(enumeratorVar, moveNextMethod),
                    Expression.Call(listVar, addMethod, elementValue),
                    Expression.Break(breakLabel)),
                breakLabel),
            Expression.Call(enumeratorVar, disposeMethod));

        return Expression.Block(
            listType,
            [listVar, enumeratorVar],
            Expression.Assign(listVar, newList),
            Expression.Assign(enumeratorVar, Expression.Call(Expression.Convert(coll, enumerableType), getEnumeratorMethod)),
            loop,
            listVar);
    }

    /// <summary>
    /// Turns the array (from <see cref="BuildIndexedLoop"/>) or <see cref="List{T}"/> (from
    /// <see cref="BuildEnumeratorLoop"/>) already holding every mapped element into the destination shape
    /// <paramref name="kind"/> actually calls for. The two cheapest cases are handled without any further
    /// copy at all: an <see cref="CollectionTargetKind.Array"/> target backed by the indexed-loop's array
    /// returns it unchanged, and the default (list/enumerable) target backed by the enumerator-loop's list
    /// likewise returns it unchanged -- a copy only happens when the sequence's own shape doesn't already
    /// match what's needed (e.g. building a <see cref="List{T}"/> from the indexed loop's array, via the
    /// single-allocation <c>List{T}(IEnumerable{T})</c> constructor's <see cref="ICollection{T}"/>
    /// fast path, rather than growing one element at a time).
    /// </summary>
    private static Expression MaterializeCollection(Expression sequence, CollectionTargetKind kind, Type destinationElementType)
    {
        var isArraySequence = sequence.Type.IsArray;

        return kind switch
        {
            CollectionTargetKind.Array => isArraySequence
                ? sequence
                : Expression.Call(typeof(Enumerable).GetMethod(nameof(Enumerable.ToArray))!.MakeGenericMethod(destinationElementType), sequence),

            CollectionTargetKind.HashSet => Expression.New(
                typeof(HashSet<>).MakeGenericType(destinationElementType)
                    .GetConstructor([typeof(IEnumerable<>).MakeGenericType(destinationElementType)])!,
                sequence),

            CollectionTargetKind.ImmutableArray => Expression.Call(
                FindEnumerableExtensionMethod(typeof(System.Collections.Immutable.ImmutableArray), "ToImmutableArray")
                    .MakeGenericMethod(destinationElementType), sequence),

            CollectionTargetKind.ImmutableList => Expression.Call(
                FindEnumerableExtensionMethod(typeof(System.Collections.Immutable.ImmutableList), "ToImmutableList")
                    .MakeGenericMethod(destinationElementType), sequence),

            CollectionTargetKind.ImmutableHashSet => Expression.Call(
                FindEnumerableExtensionMethod(typeof(System.Collections.Immutable.ImmutableHashSet), "ToImmutableHashSet")
                    .MakeGenericMethod(destinationElementType), sequence),

            // List (reached here only via a non-indexed source -- an indexed source headed for a List<T>
            // destination is intercepted earlier in BuildFrom by BuildIndexedLoopIntoList, before ever
            // building an array to wrap) and Enumerable both land here, and both are already satisfied by
            // whatever BuildIndexedLoop/BuildEnumeratorLoop produced without any further copy: an array
            // (from the indexed loop) implements IEnumerable<T> directly, and a List<T> (from the
            // enumerator loop, which always builds one) satisfies both kinds as-is.
            _ => sequence,
        };
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

            case ResolvedSource.ContextualResolver cr:
                return BuildContextualResolverCall(cr, sourceRoot, targetType, services);

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
    /// Invokes an inline, boxed <c>Func&lt;object?,object?,object?,ResolutionContext,object?&gt;</c>
    /// resolver (see <see cref="ResolvedSource.ContextualResolver"/>) against the source object, a
    /// default destination value, and a default "current value" -- FluxMapper does not yet thread the
    /// real in-progress destination instance to this call site, matching <see cref="BuildResolverCall"/>'s
    /// same, documented limitation for <c>IValueResolver&lt;&gt;</c> -- and the per-call
    /// <see cref="ResolutionContext"/> built by <see cref="BuildResolutionContextExpression"/>, which is
    /// where this feature earns its keep: reading <see cref="ResolutionContext.Items"/> populated via
    /// <see cref="IMappingOperationOptions{TSource,TDestination}.Items"/> lets a conditional expression see
    /// per-call state a compile-time <see cref="Expression"/> cannot.
    /// </summary>
    private static Expression BuildContextualResolverCall(ResolvedSource.ContextualResolver resolver, Expression sourceRoot, Type targetType, IServiceProvider? services)
    {
        var call = Expression.Invoke(
            Expression.Constant(resolver.Resolver),
            Expression.Convert(sourceRoot, typeof(object)),
            Expression.Default(typeof(object)),
            Expression.Default(typeof(object)),
            BuildResolutionContextExpression(services));

        return Expression.Convert(call, targetType);
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
    /// Builds <c>ResolutionContext.Current ?? new ResolutionContext { Services = services }</c> as an
    /// expression — evaluated fresh on every single invocation of the compiled delegate (never hoisted to
    /// a shared constant). <see cref="ResolutionContext.Current"/> is only non-null when the caller
    /// supplied per-call options with at least one <see cref="ResolutionContext.Items"/> entry (see
    /// FluxMapper.Core's <c>Mapper</c>), in which case every resolver/contextual-<c>MapFrom</c> reached
    /// from that one call shares that single context instead of each getting its own fresh, empty one —
    /// this is how a per-call <c>opt.Items["key"] = value</c> reaches a resolver anywhere in the object
    /// graph without threading an extra parameter through every builder method in this class. The plain
    /// (no ambient context) case is unchanged from before this existed: a fresh, empty context per call
    /// site, never hoisted, so nothing here adds shared mutable state <see cref="ResolutionContext"/>'s own
    /// doc comment rules out.
    /// </summary>
    private static Expression BuildResolutionContextExpression(IServiceProvider? services)
        => Expression.Coalesce(
            Expression.Property(null, ResolutionContextCurrentProperty),
            Expression.MemberInit(
                Expression.New(typeof(ResolutionContext)),
                Expression.Bind(ResolutionContextServicesProperty, Expression.Constant(services, typeof(IServiceProvider)))));

    private sealed class ParameterReplacer(ParameterExpression from, Expression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
    }
}
