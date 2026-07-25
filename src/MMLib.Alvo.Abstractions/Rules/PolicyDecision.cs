using MMLib.Alvo.Expressions;

namespace MMLib.Alvo.Rules;

/// <summary>
/// The verdict <see cref="IPolicyEngine.Resolve"/> returns for one entity/operation/caller triple:
/// either a denial, or every compiled predicate and resolved field mask a data port must apply
/// unconditionally. Follows the same anti-forgery shape as <see cref="CompiledExpression"/> and
/// <see cref="SqlPredicate"/> (get-only properties, no public parameterless construction path) so a
/// caller can never assemble a permissive decision by hand, nor <c>with</c>-mutate a denial into an
/// allow — the only public way to produce one is <see cref="Deny"/>; an allow is only ever produced
/// by an <see cref="IPolicyEngine"/> implementation in the core, via <c>InternalsVisibleTo</c>.
/// </summary>
public sealed record PolicyDecision
{
    private static readonly IReadOnlySet<string> _empty = new HashSet<string>();

    /// <summary>Initializes a new instance of the <see cref="PolicyDecision"/> class.</summary>
    /// <param name="isDenied">Whether the operation is denied.</param>
    /// <param name="using">The <c>USING</c>-equivalent predicate, or <see langword="null"/> when this operation does not consult one.</param>
    /// <param name="withCheck">The <c>WITH CHECK</c>-equivalent predicate, or <see langword="null"/> when this operation does not consult one.</param>
    /// <param name="tenantScope">The synthesized tenant scope, or <see langword="null"/> on a global entity.</param>
    /// <param name="hiddenFields">The field names to omit from the response entirely.</param>
    /// <param name="readOnlyFields">The field names the caller may read but never write.</param>
    /// <param name="denyReason">Why the operation was denied, when <paramref name="isDenied"/> is <see langword="true"/>.</param>
    internal PolicyDecision(
        bool isDenied,
        CompiledExpression? @using,
        CompiledExpression? withCheck,
        CompiledExpression? tenantScope,
        IReadOnlySet<string> hiddenFields,
        IReadOnlySet<string> readOnlyFields,
        string? denyReason)
    {
        ArgumentNullException.ThrowIfNull(hiddenFields);
        ArgumentNullException.ThrowIfNull(readOnlyFields);
        IsDenied = isDenied;
        Using = @using;
        WithCheck = withCheck;
        TenantScope = tenantScope;
        HiddenFields = hiddenFields;
        ReadOnlyFields = readOnlyFields;
        DenyReason = denyReason;
    }

    /// <summary>Gets a value indicating whether the operation is denied.</summary>
    public bool IsDenied { get; }

    /// <summary>
    /// Gets the <c>USING</c>-equivalent predicate a data port filters existing rows by — set for
    /// <c>list</c>/<c>get</c>/<c>delete</c>/<c>update</c>, <see langword="null"/> for <c>create</c>
    /// (there is no stored row to filter) and for any denied decision.
    /// </summary>
    public CompiledExpression? Using { get; }

    /// <summary>
    /// Gets the <c>WITH CHECK</c>-equivalent predicate a data port evaluates against the candidate
    /// row's post-image — set for <c>create</c>/<c>update</c> (the same source as <see cref="Using"/>
    /// on <c>update</c>), <see langword="null"/> otherwise and for any denied decision.
    /// </summary>
    public CompiledExpression? WithCheck { get; }

    /// <summary>
    /// Gets the synthesized <c>tenant_id == @tenant.id</c> scope for a tenant-scoped entity;
    /// <see langword="null"/> on a global entity or for any denied decision.
    /// </summary>
    public CompiledExpression? TenantScope { get; }

    /// <summary>Gets the field names to omit from the response entirely; empty for any denied decision.</summary>
    public IReadOnlySet<string> HiddenFields { get; }

    /// <summary>Gets the field names the caller may read but never write; empty for any denied decision.</summary>
    public IReadOnlySet<string> ReadOnlyFields { get; }

    /// <summary>Gets why the operation was denied; <see langword="null"/> when <see cref="IsDenied"/> is <see langword="false"/>.</summary>
    public string? DenyReason { get; }

    /// <summary>Creates a denial — the only publicly constructible <see cref="PolicyDecision"/>, and the safe default.</summary>
    /// <param name="reason">Why the operation is denied; surfaced to callers building a structured error.</param>
    public static PolicyDecision Deny(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new PolicyDecision(true, null, null, null, _empty, _empty, reason);
    }

    /// <summary>Creates an allow decision. Only reachable from the core (<c>InternalsVisibleTo</c>) — never from outside the trust boundary.</summary>
    /// <param name="using">The <c>USING</c>-equivalent predicate, when this operation consults one.</param>
    /// <param name="withCheck">The <c>WITH CHECK</c>-equivalent predicate, when this operation consults one.</param>
    /// <param name="tenantScope">The synthesized tenant scope, on a tenant-scoped entity.</param>
    /// <param name="hiddenFields">The resolved set of hidden field names.</param>
    /// <param name="readOnlyFields">The resolved set of read-only field names.</param>
    internal static PolicyDecision Allow(
        CompiledExpression? @using,
        CompiledExpression? withCheck,
        CompiledExpression? tenantScope,
        IReadOnlySet<string> hiddenFields,
        IReadOnlySet<string> readOnlyFields)
        => new(false, @using, withCheck, tenantScope, hiddenFields, readOnlyFields, null);
}
