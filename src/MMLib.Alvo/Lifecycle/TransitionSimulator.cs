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

        return [.. machine.Transitions.Select(transition => Verdict(transition, caller))];
    }

    private static ManagementTransitionVerdict Verdict(CompiledTransition transition, AlvoContext caller) => new()
    {
        Name = transition.Name,
        From = transition.From is null ? _anyState : [.. transition.From.Order(StringComparer.Ordinal)],
        To = transition.To,
        Roles = transition.Roles is null ? [] : [.. transition.Roles.Select(role => role.Name).Order(StringComparer.Ordinal)],
        When = transition.When?.Source,
        Allowed = Allowed(transition, caller),
    };

    private static bool? Allowed(CompiledTransition transition, AlvoContext caller) =>
        transition.Roles is null || transition.Roles.Overlaps(caller.Roles)
            ? (transition.When is null ? true : null)
            : false;
}
