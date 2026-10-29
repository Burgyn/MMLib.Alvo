using MMLib.Alvo.Data;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Commands.Internal;

/// <summary>
/// The one judge of a command's access rule (Ruling 33): the HTTP filter, <c>IAlvoCommands</c> and the policy simulator all
/// ask it, so the simulator cannot answer otherwise than production does (spec 2026-10-14 §6.2 "simulator parity").
/// </summary>
/// <remarks>
/// Like <c>PolicyEngine</c>'s required-context gate, it refuses <b>before</b> evaluating when the rule reads
/// <c>@tenant.id</c> and the caller has no tenant, or <c>@user.id</c> and the caller has no identity: an absent operand
/// collapses to false, and a negation — <c>!(string(@tenant.id) == '…')</c> — would invert that into an admission.
/// </remarks>
internal sealed class CommandAccessJudge(ExtensionBinder binder, IPolicyCatalogProvider catalogs, IPredicateEvaluator evaluator)
{
    /// <summary>The refusal when the rule evaluates to false.</summary>
    internal const string Refused = "This command's access rule does not admit the caller.";

    /// <summary>The refusal when no applied descriptor binds the command.</summary>
    internal const string NotBound =
        "This command is not bound to an applied descriptor, so its access rule cannot be judged. It answers once a descriptor it fits is applied.";

    /// <summary>The refusal when the rule reads <c>@tenant.id</c> and the caller has no tenant.</summary>
    internal const string MissingTenant = "The caller has no tenant, and this command's access rule reads one.";

    /// <summary>The refusal when the rule reads <c>@user.id</c> and the caller has no identity.</summary>
    internal const string MissingUser = "The caller has no identity, and this command's access rule reads one.";

    /// <summary>Admits the caller, or throws the refusal.</summary>
    /// <param name="name">The command.</param>
    /// <param name="row">The bound row as the caller reads it; null for a command bound to none.</param>
    /// <param name="caller">The caller.</param>
    /// <exception cref="AlvoAuthorizationException">The rule — or its required context — refuses the caller.</exception>
    internal void EnsureAdmitted(string name, AlvoRecord? row, AlvoContext caller)
    {
        if (Refusal(Bound(name), row, caller) is { } refusal)
        {
            throw new AlvoAuthorizationException(refusal);
        }
    }

    /// <summary>What the rule answers with no row: false when refused, null when it reads the row and so is decided per row.</summary>
    /// <param name="name">The command.</param>
    /// <param name="caller">The caller.</param>
    internal bool? Preview(string name, AlvoContext caller) =>
        Bound(name) is not { } bound || Missing(bound, caller) is not null ? false
        : bound.ReadsRow ? null
        : evaluator.Evaluate(bound.Access, AlvoRecord.Empty, null, caller);

    private string? Refusal(BoundCommand? bound, AlvoRecord? row, AlvoContext caller) =>
        bound is null ? NotBound
        : Missing(bound, caller) ?? (evaluator.Evaluate(bound.Access, row ?? AlvoRecord.Empty, null, caller) ? null : Refused);

    private static string? Missing(BoundCommand bound, AlvoContext caller) =>
        bound.Required.TenantIdMissingFrom(caller) ? MissingTenant
        : bound.Required.UserIdMissingFrom(caller) ? MissingUser
        : null;

    private BoundCommand? Bound(string name) =>
        catalogs.Current is { } catalog ? binder.For(catalog).Command(name) : null;
}
