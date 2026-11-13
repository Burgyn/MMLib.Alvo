using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Management;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Lifecycle;

/// <summary>
/// What each transition of an entity's state machine answers for a simulated caller: the roles are decided here, as the
/// guard decides them; a <c>when</c> reads the record, and the simulator reads none, so it leaves that transition undecided.
/// </summary>
/// <param name="catalog">The catalog the guard reads its compiled machines from.</param>
internal sealed class TransitionSimulator(IPolicyCatalogProvider catalog)
{
    private const string Update = "update";

    private static readonly IReadOnlyList<string> _anyState = ["*"];

    /// <summary>One verdict per transition, in declaration order; none unless <paramref name="operation"/> is <c>update</c>.</summary>
    /// <param name="entity">The simulated entity.</param>
    /// <param name="operation">The simulated operation, as the simulation names it.</param>
    /// <param name="caller">The simulated caller.</param>
    internal IReadOnlyList<ManagementTransitionVerdict> Simulate(string entity, string operation, AlvoContext caller)
    {
        if (!string.Equals(operation, Update, StringComparison.Ordinal)
            || catalog.Current is not { } current
            || !current.TryGetEntity(entity, out var policy)
            || policy.Lifecycle.StateMachine is not { } machine)
        {
            return [];
        }

        var held = RoleProvenance.Visible(caller, machine.Tenancy);
        return [.. machine.Transitions.Select(transition => Verdict(transition, held))];
    }

    private static ManagementTransitionVerdict Verdict(CompiledTransition transition, IReadOnlySet<Role> held) => new()
    {
        Name = transition.Name,
        From = transition.From is null ? _anyState : [.. transition.From.Order(StringComparer.Ordinal)],
        To = transition.To,
        Roles = transition.Roles is null ? [] : [.. transition.Roles.Select(role => role.Name).Order(StringComparer.Ordinal)],
        When = transition.When?.Source,
        Allowed = Allowed(transition, held),
    };

    private static bool? Allowed(CompiledTransition transition, IReadOnlySet<Role> held) =>
        transition.Roles is null || transition.Roles.Overlaps(held)
            ? (transition.When is null ? true : null)
            : false;
}
