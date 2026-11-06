using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Schema;
using System.Collections.Frozen;

namespace MMLib.Alvo.Rules.Internal;

/// <summary>
/// The default <see cref="IPolicyEngine"/>: resolves the <see cref="IPolicyCatalogProvider.Current"/>
/// catalog against one entity/operation/caller triple, with no blocking wait and no caching of a
/// build failure — the catalog is built and primed elsewhere (the boot, <c>RuntimeSchemaService</c>, <c>SchemaMigrationRunner</c>),
/// never lazily, by <see cref="Resolve(string, DataOperation, AlvoContext)"/> itself. Reads as five steps, in order: not-yet-primed (deny,
/// the correct default-deny answer for "no descriptor has been applied yet"), look up the entity (deny
/// if unknown or blank, never throw), the tenant guard (deny before any rule is consulted when a
/// tenant-scoped entity's caller carries no tenant), the operation lookup (deny when the relevant rule
/// was never configured), the required-context gate (deny when the operation's predicates read a
/// context value this caller does not have), then assemble the allow decision from the catalog's
/// compiled predicates and the field masks resolved against this call's context — where the same
/// gate applies once more, in the other direction (see <see cref="IsMasked"/>).
/// </summary>
/// <remarks>
/// <para>
/// The tenant guard and the required-context gate are two different questions and both are needed.
/// The guard asks "is this entity tenant-scoped while the caller has no tenant" — an entity-level
/// question, answered before an operation is even looked up. The gate asks "does the rule this
/// operation would hand out read a context value this caller cannot supply" — which fires for a
/// <b>global</b> entity too, where the guard by definition never speaks, and is the only thing
/// standing between a rule like <c>!(region_id == @tenant.id)</c> and a tenantless caller reading
/// every row (the absent operand collapses to false in the backends, and the negation inverts it).
/// </para>
/// <para>
/// The gate covers <b>every</b> compiled expression this engine hands out or evaluates — the
/// operation's <c>USING</c>/<c>WITH CHECK</c>, the entity's tenant scope, <em>and</em> the
/// <c>hidden</c>/<c>readOnly</c> masks — measured once per apply into a
/// <see cref="RequiredContext"/>. The two channels differ only in what "the caller lacks it" means:
/// a predicate denies the call, a mask stays on. Never one without the other; a mask evaluated
/// against an absent operand is the same two-valued collapse one channel over, on the one invariant
/// that has to fail the other way.
/// </para>
/// </remarks>
internal sealed class PolicyEngine : IPolicyEngine
{
    private static readonly FrozenSet<string> _emptyFieldMask = FrozenSet<string>.Empty;

    private readonly IPolicyCatalogProvider _provider;

    private readonly RelationBinder _binder;

    /// <summary>Initializes a new instance of the <see cref="PolicyEngine"/> class.</summary>
    /// <param name="provider">Holds the catalog this engine resolves against; read once per <see cref="Resolve(string, DataOperation, AlvoContext)"/> call.</param>
    public PolicyEngine(IPolicyCatalogProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _provider = provider;
        _binder = new RelationBinder((catalog, entity, context, depth) => Resolve(catalog, entity, DataOperation.List, context, depth));
    }

    /// <inheritdoc/>
    public PolicyDecision Resolve(string entity, DataOperation operation, AlvoContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var catalog = _provider.Current;
        if (catalog is null)
        {
            return PolicyDecision.Deny("No descriptor has been applied yet; no policy is configured.");
        }

        return Resolve(catalog, entity, operation, context, depth: 0);
    }

    /// <summary>
    /// Resolves against one catalog snapshot, read once by the public <see cref="Resolve(string, DataOperation, AlvoContext)"/>
    /// and passed down every relation's recursion, so a re-apply landing mid-call cannot mix two revisions' rules in one
    /// decision.
    /// </summary>
    /// <param name="catalog">The catalog snapshot.</param>
    /// <param name="entity">The entity.</param>
    /// <param name="operation">The operation.</param>
    /// <param name="context">The caller.</param>
    /// <param name="depth">How many relations deep this decision is; 0 for a caller's own request.</param>
    internal PolicyDecision Resolve(PolicyCatalog catalog, string entity, DataOperation operation, AlvoContext context, int depth)
    {
        if (string.IsNullOrWhiteSpace(entity) || !catalog.TryGetEntity(entity, out var policy))
        {
            return PolicyDecision.Deny(DenyReasonForOperation(operation));
        }

        return CheckTenantGuard(policy, context)
            ?? ResolveOperation(new Resolution(catalog, entity, operation, context, depth), policy);
    }

    /// <summary>
    /// Denies a tenant-scoped entity's tenantless caller before any rule is consulted. The deny
    /// reason deliberately names "tenant" — distinct from <see cref="DenyReasonForOperation"/>'s
    /// generic text — which is a conscious decision, not an oversight: it gives a tenantless caller
    /// a narrow oracle (whether the named entity is tenant-scoped at all), but an operator debugging
    /// "why was this call refused" needs that distinction, and a test depends on the guard's reason
    /// staying distinguishable from a missing-rule denial.
    /// </summary>
    private static PolicyDecision? CheckTenantGuard(EntityPolicy policy, AlvoContext context)
    {
        if (policy.Tenancy != TenancyMode.Scoped || context.Tenant is not null)
        {
            return null;
        }

        return PolicyDecision.Deny("The caller has no tenant, and this entity is tenant-scoped.");
    }

    private PolicyDecision ResolveOperation(Resolution resolution, EntityPolicy policy)
    {
        var operation = resolution.Operation;
        if (!policy.Operations.TryGetValue(operation, out var operationPolicy) || IsUnconfigured(operation, operationPolicy))
        {
            return PolicyDecision.Deny(DenyReasonForOperation(operation));
        }

        return CheckRequiredContext(operationPolicy, resolution.Context)
            ?? Allow(policy, Bound(operationPolicy, resolution), resolution.Context);
    }

    /// <summary>The operation's predicates with every relation bound for this caller; untouched when it reads none.</summary>
    private OperationPolicy Bound(OperationPolicy policy, Resolution resolution) =>
        !policy.HasRelations
            ? policy
            : policy with
            {
                Using = Bind(resolution, UsingSlot, policy.Using),
                WithCheck = Bind(resolution, CheckSlot, policy.WithCheck),
            };

    private CompiledExpression? Bind(Resolution resolution, string slot, CompiledExpression? expression) =>
        _binder.Bind(resolution.Catalog, resolution.Entity, resolution.Operation, slot, expression, resolution.Context, resolution.Depth);

    private const string UsingSlot = "using";

    private const string CheckSlot = "check";

    /// <summary>One decision being resolved: the snapshot, what is asked, by whom, and how many relations deep.</summary>
    private readonly record struct Resolution(
        PolicyCatalog Catalog, string Entity, DataOperation Operation, AlvoContext Context, int Depth);

    /// <summary>
    /// Denies when a predicate this operation would hand out reads a caller/tenant context value the
    /// caller does not have. Neither reason names the entity, and neither distinguishes which of the
    /// operation's three predicates asked for the value — an operator only needs to know which half of
    /// the caller's identity was missing.
    /// </summary>
    private static PolicyDecision? CheckRequiredContext(OperationPolicy policy, AlvoContext context)
    {
        if (policy.Required.TenantIdMissingFrom(context))
        {
            return PolicyDecision.Deny("The caller has no tenant, and the policy for this operation reads one.");
        }

        if (policy.Required.UserIdMissingFrom(context))
        {
            return PolicyDecision.Deny("The caller has no identity, and the policy for this operation reads one.");
        }

        return null;
    }

    private static PolicyDecision Allow(EntityPolicy policy, OperationPolicy operationPolicy, AlvoContext context) =>
        PolicyDecision.Allow(
            operationPolicy.Using,
            operationPolicy.WithCheck,
            policy.TenantScope,
            ResolveFieldMask(policy.Hidden, context),
            ResolveFieldMask(policy.ReadOnly, context)) with
        {
            Unnameable = policy.FlaggedHidden,
        };

    private static bool IsUnconfigured(DataOperation operation, OperationPolicy policy) => operation switch
    {
        DataOperation.Create => policy.WithCheck is null,
        DataOperation.Update => policy.Using is null || policy.WithCheck is null,
        _ => policy.Using is null,
    };

    /// <summary>
    /// The one client-facing denial text for both "this entity does not exist" and "this entity
    /// exists but has no rule for this operation" — an unknown entity must stay indistinguishable
    /// from an unauthorized one, so neither the caller-supplied entity name (attacker-controlled, and
    /// a log-injection vector if echoed verbatim) nor which of the two cases occurred is disclosed.
    /// Only <paramref name="operation"/> — a closed enum, never caller-supplied text — is named.
    /// </summary>
    private static string DenyReasonForOperation(DataOperation operation) =>
        $"No policy allows '{operation.ToWireName()}' on this entity.";

    private static FrozenSet<string> ResolveFieldMask(IReadOnlyDictionary<string, FieldMask> masks, AlvoContext context)
    {
        if (masks.Count == 0)
        {
            return _emptyFieldMask;
        }

        var resolved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (field, mask) in masks)
        {
            if (IsMasked(mask, context))
            {
                resolved.Add(field);
            }
        }

        return resolved.ToFrozenSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The required-context gate's mask half. A mask reading a context value this caller lacks stays
    /// <b>on</b> without being evaluated: for a mask, fail-closed means the field stays hidden or
    /// stays read-only, which is the opposite direction from the predicate half (deny the call) and
    /// the reason the two are separate reads of the same precomputed <see cref="RequiredContext"/>.
    /// Evaluating it instead would resolve the absent operand to <see langword="null"/>, collapse the
    /// comparison to <see langword="false"/>, and report a hidden field visible — the one place
    /// <c>CelInterpreter.EvaluateMask</c>'s "masked unless exactly false" rule cannot help, because
    /// an absent context value is not an exception.
    /// </summary>
    private static bool IsMasked(FieldMask mask, AlvoContext context) =>
        mask.AlwaysOn
        || mask.Required.IsMissingFrom(context)
        || CelInterpreter.EvaluateMask(mask.Expression!, context);
}
