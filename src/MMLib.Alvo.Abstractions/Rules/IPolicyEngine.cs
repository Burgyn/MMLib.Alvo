namespace MMLib.Alvo.Rules;

/// <summary>
/// Turns a descriptor's per-operation authorization rules, tenant scope, and field masks into an
/// enforceable <see cref="PolicyDecision"/> — the last checkpoint before a data port touches storage.
/// Default-deny throughout: a missing rule, an unknown entity, or a missing tenant on a tenant-scoped
/// entity all deny, and an unknown entity denies rather than throwing, so it is not distinguishable
/// from an unauthorized one at this layer.
/// </summary>
/// <remarks>
/// Deliberately narrow: <see cref="Resolve"/> does not consult an API key's scopes. Whether an
/// endpoint is reachable at all is checked above this layer (the HTTP tier's <c>ScopeGate</c>,
/// PR3) — this engine only answers "what may this caller do to this entity's rows", never
/// "is this endpoint reachable by this caller at all".
/// </remarks>
public interface IPolicyEngine
{
    /// <summary>Resolves the enforceable policy for one entity/operation/caller triple.</summary>
    /// <param name="entity">The entity name.</param>
    /// <param name="operation">The data operation being attempted.</param>
    /// <param name="context">The caller/tenant identity performing the operation.</param>
    /// <returns>A denial, or a decision carrying every predicate and field mask a data port must apply.</returns>
    PolicyDecision Resolve(string entity, DataOperation operation, AlvoContext context);
}
