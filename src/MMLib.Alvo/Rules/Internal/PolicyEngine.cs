using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Schema;
using System.Collections.Frozen;

namespace MMLib.Alvo.Rules.Internal;

/// <summary>
/// The default <see cref="IPolicyEngine"/>: resolves the <see cref="IPolicyCatalogProvider.Current"/>
/// catalog against one entity/operation/caller triple, with no blocking wait and no caching of a
/// build failure — the catalog is built and primed elsewhere (see <see cref="PolicyCatalogPriming"/>),
/// never lazily, by <see cref="Resolve"/> itself. Reads as five steps, in order: not-yet-primed (deny,
/// the correct default-deny answer for "no descriptor has been applied yet"), look up the entity (deny
/// if unknown or blank, never throw), the tenant guard (deny before any rule is consulted when a
/// tenant-scoped entity's caller carries no tenant), the operation lookup (deny when the relevant rule
/// was never configured), the required-context gate (deny when the operation's predicates read a
/// context value this caller does not have), then assemble the allow decision from the catalog's
/// compiled predicates and the field masks resolved against this call's context.
/// </summary>
/// <remarks>
/// The tenant guard and the required-context gate are two different questions and both are needed.
/// The guard asks "is this entity tenant-scoped while the caller has no tenant" — an entity-level
/// question, answered before an operation is even looked up. The gate asks "does the rule this
/// operation would hand out read a context value this caller cannot supply" — which fires for a
/// <b>global</b> entity too, where the guard by definition never speaks, and is the only thing
/// standing between a rule like <c>!(region_id == @tenant.id)</c> and a tenantless caller reading
/// every row (the absent operand collapses to false in the backends, and the negation inverts it).
/// </remarks>
internal sealed class PolicyEngine : IPolicyEngine
{
    private static readonly FrozenSet<string> _emptyFieldMask = FrozenSet<string>.Empty;

    private readonly IPolicyCatalogProvider _provider;

    /// <summary>Initializes a new instance of the <see cref="PolicyEngine"/> class.</summary>
    /// <param name="provider">Holds the catalog this engine resolves against; read once per <see cref="Resolve"/> call.</param>
    public PolicyEngine(IPolicyCatalogProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _provider = provider;
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

        if (string.IsNullOrWhiteSpace(entity) || !catalog.TryGetEntity(entity, out var policy))
        {
            return PolicyDecision.Deny(DenyReasonForOperation(operation));
        }

        return CheckTenantGuard(policy, context)
            ?? ResolveOperation(operation, policy, context);
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

    private static PolicyDecision ResolveOperation(DataOperation operation, EntityPolicy policy, AlvoContext context)
    {
        if (!policy.Operations.TryGetValue(operation, out var operationPolicy) || IsUnconfigured(operation, operationPolicy))
        {
            return PolicyDecision.Deny(DenyReasonForOperation(operation));
        }

        return CheckRequiredContext(operationPolicy, context)
            ?? Allow(policy, operationPolicy, context);
    }

    /// <summary>
    /// Denies when a predicate this operation would hand out reads a caller/tenant context value the
    /// caller does not have. Neither reason names the entity, and neither distinguishes which of the
    /// operation's three predicates asked for the value — an operator only needs to know which half of
    /// the caller's identity was missing.
    /// </summary>
    private static PolicyDecision? CheckRequiredContext(OperationPolicy policy, AlvoContext context)
    {
        if (policy.RequiresTenantId && context.Tenant is null)
        {
            return PolicyDecision.Deny("The caller has no tenant, and the policy for this operation reads one.");
        }

        if (policy.RequiresUserId && HasNoIdentity(context))
        {
            return PolicyDecision.Deny("The caller has no identity, and the policy for this operation reads one.");
        }

        return null;
    }

    /// <summary>
    /// The reserved all-zero <see cref="UserId"/> means "no identity" (see
    /// <see cref="AlvoContext.Anonymous"/>) — never a real caller who happens to own the all-zero
    /// rows.
    /// </summary>
    private static bool HasNoIdentity(AlvoContext context) => context.User.Value == Guid.Empty;

    private static PolicyDecision Allow(EntityPolicy policy, OperationPolicy operationPolicy, AlvoContext context) =>
        PolicyDecision.Allow(
            operationPolicy.Using,
            operationPolicy.WithCheck,
            policy.TenantScope,
            ResolveFieldMask(policy.Hidden, context),
            ResolveFieldMask(policy.ReadOnly, context));

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
            if (mask.AlwaysOn || CelInterpreter.EvaluateMask(mask.Expression!, context))
            {
                resolved.Add(field);
            }
        }

        return resolved.ToFrozenSet(StringComparer.Ordinal);
    }
}
