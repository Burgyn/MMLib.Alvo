using MMLib.Alvo.Data;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Rules.Internal;

/// <summary>
/// The default <see cref="IPolicyEngine"/>: resolves a <see cref="PolicyCatalog"/> built once at
/// apply time against one entity/operation/caller triple. Reads as four steps, in order: look up the
/// entity (deny if unknown, never throw), the tenant guard (deny before any rule is consulted when a
/// tenant-scoped entity's caller carries no tenant), the operation lookup (deny when the relevant
/// rule was never configured), then assemble the allow decision from the catalog's compiled
/// predicates and the field masks resolved against this call's context.
/// </summary>
internal sealed class PolicyEngine : IPolicyEngine
{
    private static readonly IReadOnlySet<string> _emptyFieldMask = new HashSet<string>();

    private readonly Lazy<PolicyCatalog> _catalog;

    /// <summary>Initializes a new instance of the <see cref="PolicyEngine"/> class.</summary>
    /// <param name="catalogFactory">
    /// Builds the <see cref="PolicyCatalog"/> this engine resolves against. Invoked at most once, on
    /// the first <see cref="Resolve"/> call — never at construction — so an <see cref="IPolicyEngine"/>
    /// can be registered in DI before a descriptor is available (the <c>FromDescriptor</c> chicken/egg).
    /// </param>
    public PolicyEngine(Func<PolicyCatalog> catalogFactory)
    {
        ArgumentNullException.ThrowIfNull(catalogFactory);
        _catalog = new Lazy<PolicyCatalog>(catalogFactory, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <inheritdoc/>
    public PolicyDecision Resolve(string entity, DataOperation operation, AlvoContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entity);
        ArgumentNullException.ThrowIfNull(context);

        if (!_catalog.Value.TryGetEntity(entity, out var policy))
        {
            return PolicyDecision.Deny($"Entity '{entity}' has no policy configured.");
        }

        return CheckTenantGuard(policy, context)
            ?? ResolveOperation(entity, operation, policy, context);
    }

    private static PolicyDecision? CheckTenantGuard(EntityPolicy policy, AlvoContext context)
    {
        if (policy.Tenancy != TenancyMode.Scoped || context.Tenant is not null)
        {
            return null;
        }

        return PolicyDecision.Deny("The caller has no tenant, and this entity is tenant-scoped.");
    }

    private static PolicyDecision ResolveOperation(string entity, DataOperation operation, EntityPolicy policy, AlvoContext context)
    {
        if (!policy.Operations.TryGetValue(operation, out var operationPolicy) || IsUnconfigured(operation, operationPolicy))
        {
            return PolicyDecision.Deny($"No '{OperationName(operation)}' rule is configured for entity '{entity}'.");
        }

        return PolicyDecision.Allow(
            operationPolicy.Using,
            operationPolicy.WithCheck,
            policy.TenantScope,
            ResolveFieldMask(policy.Hidden, context),
            ResolveFieldMask(policy.ReadOnly, context));
    }

    private static bool IsUnconfigured(DataOperation operation, OperationPolicy policy) => operation switch
    {
        DataOperation.Create => policy.WithCheck is null,
        DataOperation.Update => policy.Using is null || policy.WithCheck is null,
        _ => policy.Using is null,
    };

    private static IReadOnlySet<string> ResolveFieldMask(IReadOnlyDictionary<string, FieldMask> masks, AlvoContext context)
    {
        if (masks.Count == 0)
        {
            return _emptyFieldMask;
        }

        var resolved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (field, mask) in masks)
        {
            if (mask.AlwaysOn || EvaluatesTrue(mask.Expression!, context))
            {
                resolved.Add(field);
            }
        }

        return resolved;
    }

    private static bool EvaluatesTrue(CompiledExpression expression, AlvoContext context) =>
        CelInterpreter.EvaluatePredicate(expression, AlvoRecord.Empty, null, context);

    private static string OperationName(DataOperation operation) => operation switch
    {
        DataOperation.List => "list",
        DataOperation.Get => "get",
        DataOperation.Create => "create",
        DataOperation.Update => "update",
        DataOperation.Delete => "delete",
        _ => operation.ToString(),
    };
}
