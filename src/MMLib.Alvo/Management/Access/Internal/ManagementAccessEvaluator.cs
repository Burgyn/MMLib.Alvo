using MMLib.Alvo.Data;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Management.Internal;

/// <summary>
/// Resolves a caller to a <see cref="ManagementLevel"/>, and answers whether that level reaches an
/// operation.
/// </summary>
/// <remarks>
/// <para>
/// <b>All three predicates are evaluated, and the highest match wins.</b> Not short-circuited: the levels
/// are independent by design, and evaluating them all is what makes that structural rather than argued.
/// The cost is two extra evaluations of a context-only expression, which is nothing beside a management
/// call. A first-match scan from the top would answer identically on every input — it is correct only
/// because the levels are totally ordered and the required level is monotone in that order, which a fourth
/// level, a deny level or a "which levels matched?" diagnostic would end — so the property is held by a
/// fact that records which predicates reached the evaluator, never by one that compares verdicts.
/// </para>
/// <para>
/// <b>Everything fails closed.</b> An unprimed catalog, a descriptor with no <c>access</c> block, a
/// level the descriptor declares nothing for, and an expression that throws all resolve to
/// <see cref="ManagementLevel.None"/> — <see cref="IPredicateEvaluator"/> already collapses a failed
/// evaluation to <see langword="false"/>, which is deny for a predicate.
/// </para>
/// <para>
/// <b>The bootstrap administrator is above the descriptor, deliberately.</b> It is infrastructure
/// configuration, never the descriptor (<c>docs/PLAN.md</c> invariant 4), so a project whose
/// <c>access</c> block locks everyone out still has exactly one person who can fix it. The reserved
/// all-zero <see cref="UserId"/> never reaches that port at all — see <see cref="IsBootstrapAdmin"/>.
/// </para>
/// </remarks>
/// <param name="catalogs">The holder the applied <c>access</c> levels are read from.</param>
/// <param name="evaluator">The evaluator every level is judged by.</param>
/// <param name="bootstrapAdmin">The deployment's bootstrap administrator, the one identity <c>access</c> does not govern.</param>
internal sealed class ManagementAccessEvaluator(
    IPolicyCatalogProvider catalogs,
    IPredicateEvaluator evaluator,
    IAlvoBootstrapAdmin bootstrapAdmin)
{
    /// <summary>Whether <paramref name="context"/> may perform <paramref name="operation"/>.</summary>
    /// <param name="operation">The operation about to be performed.</param>
    /// <param name="context">The caller.</param>
    internal bool Allows(ManagementOperation operation, AlvoContext context) =>
        Allows(ManagementOperations.RequiredLevel(operation), context);

    /// <summary>Whether <paramref name="context"/> reaches <paramref name="required"/>.</summary>
    /// <remarks>
    /// <b><see cref="ManagementLevel.None"/> is refused rather than satisfied.</b> A plain
    /// <c>Resolve(context) &gt;= required</c> answers <see langword="true"/> for every caller when the
    /// requirement is zero, so a single mistyped table entry would open an operation to everyone. No entry
    /// is <c>None</c> today and a fact asserts none ever is — but a fail-closed invariant belongs in the
    /// code, with the completeness fact as the second line rather than the only one.
    /// </remarks>
    /// <param name="required">The level the operation needs.</param>
    /// <param name="context">The caller.</param>
    internal bool Allows(ManagementLevel required, AlvoContext context) =>
        required != ManagementLevel.None && Resolve(context) >= required;

    /// <summary>The highest level <paramref name="context"/> matches.</summary>
    /// <param name="context">The caller.</param>
    internal ManagementLevel Resolve(AlvoContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return IsBootstrapAdmin(context.User) ? ManagementLevel.Admin : HighestMatch(context);
    }

    /// <summary>
    /// Whether <paramref name="user"/> is the deployment's bootstrap administrator.
    /// </summary>
    /// <remarks>
    /// <b>The reserved all-zero <see cref="UserId"/> is refused here, not merely forbidden in the port's
    /// contract.</b> <see cref="IAlvoBootstrapAdmin"/> is public, so a host writes the implementation, and
    /// every unauthenticated management request arrives as <see cref="AlvoContext.Anonymous"/> — whose id
    /// is that reserved value. A host answering <see langword="true"/> for it would turn every anonymous
    /// request into full administration, project deletion included, with no second check anywhere. Both
    /// shipped implementations honour the contract; this is the gate making that structural rather than
    /// conventional, which is the security-core checklist's own standard.
    /// </remarks>
    /// <param name="user">The caller's internal identifier.</param>
    private bool IsBootstrapAdmin(UserId user) =>
        user != default && bootstrapAdmin.IsBootstrapAdmin(user);

    /// <summary>The highest of the three levels the caller matches, or none.</summary>
    /// <param name="context">The caller.</param>
    private ManagementLevel HighestMatch(AlvoContext context)
    {
        if (catalogs.Current?.ManagementAccess is not { } access)
        {
            return ManagementLevel.None;
        }

        var viewer = MatchedLevel(ManagementLevel.Viewer, access.Viewer, context);
        var developer = MatchedLevel(ManagementLevel.Developer, access.Developer, context);
        var admin = MatchedLevel(ManagementLevel.Admin, access.Admin, context);

        return Higher(admin, Higher(developer, viewer));
    }

    /// <summary><paramref name="level"/> when its predicate admits the caller, otherwise none.</summary>
    /// <param name="level">The level the predicate was declared for.</param>
    /// <param name="predicate">The compiled level, or <see langword="null"/> when the descriptor declares none.</param>
    /// <param name="context">The caller.</param>
    private ManagementLevel MatchedLevel(
        ManagementLevel level, CompiledExpression? predicate, AlvoContext context) =>
        Matches(predicate, context) ? level : ManagementLevel.None;

    /// <summary>The greater of two levels.</summary>
    /// <param name="left">One level.</param>
    /// <param name="right">The other level.</param>
    private static ManagementLevel Higher(ManagementLevel left, ManagementLevel right) =>
        left > right ? left : right;

    /// <summary>
    /// An access level is a predicate over the caller alone, so it is evaluated against
    /// <see cref="AlvoRecord.Empty"/> and no previous row — the same shape a <c>hidden</c>/<c>readOnly</c>
    /// mask uses, in the opposite fail-safe direction: a mask fails closed to "masked", an authorization
    /// predicate fails closed to "deny".
    /// </summary>
    /// <param name="predicate">The compiled level, or <see langword="null"/> when the descriptor declares none.</param>
    /// <param name="context">The caller.</param>
    private bool Matches(CompiledExpression? predicate, AlvoContext context) =>
        predicate is not null && evaluator.Evaluate(predicate, AlvoRecord.Empty, null, context);
}
