using MMLib.Alvo.Data;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Lifecycle;

/// <summary>
/// Judges one write's state move against a compiled state machine: a create must store the machine's <c>initial</c>; an
/// update that changes the state must match a declared transition whose roles the caller holds and whose <c>when</c> holds.
/// </summary>
/// <remarks>
/// <para>
/// <b>Fails closed.</b> A move no transition declares is the 422, never "allowed" — <c>Any</c> over no permitted transition is
/// false — and a <c>when</c> that throws propagates its exception, refusing the write. The 403 is reserved for a move that
/// is declared but needs a role the caller lacks.
/// </para>
/// <para>
/// <b>What the 403 discloses is the caller's own and the author's.</b> It names the state the caller sent and the declared
/// roles and transition names; the 422 names nothing but the field.
/// </para>
/// <para>
/// <b>A transition's roles read <c>@user.roles</c>'s set.</b> The caller's tenant roles count only on a
/// <see cref="Schema.TenancyMode.Scoped"/> entity's machine, exactly as they do in that entity's rules (spec §6.2).
/// </para>
/// </remarks>
internal static class StateMachineGuard
{
    /// <summary>Refuses the write unless the machine admits its state move.</summary>
    /// <param name="machine">The compiled state machine.</param>
    /// <param name="operation">The write's operation.</param>
    /// <param name="candidate">The complete row the write would store.</param>
    /// <param name="previous">The stored row, or <see langword="null"/> on a create.</param>
    /// <param name="caller">The caller.</param>
    internal static void Ensure(
        CompiledStateMachine machine, DataOperation operation, AlvoRecord candidate, AlvoRecord? previous, AlvoContext caller)
    {
        var to = StateOf(candidate, machine.Field);
        if (operation == DataOperation.Create)
        {
            EnsureInitial(machine, candidate.TryGetValue(machine.Field, out _) ? to : machine.Default);
            return;
        }

        if (operation != DataOperation.Update || previous is null || string.Equals(StateOf(previous, machine.Field), to, StringComparison.Ordinal))
        {
            return;
        }

        EnsureDeclaredMove(machine, Declared(machine, StateOf(previous, machine.Field), to), candidate, previous, caller);
    }

    private static void EnsureDeclaredMove(
        CompiledStateMachine machine, List<CompiledTransition> declared, AlvoRecord candidate, AlvoRecord previous, AlvoContext caller)
    {
        var held = RoleProvenance.Visible(caller, machine.Tenancy);
        var permitted = declared.Where(transition => transition.Roles is null || transition.Roles.Overlaps(held)).ToList();
        if (declared.Count > 0 && permitted.Count == 0)
        {
            throw new AlvoAuthorizationException(RolesRefusal(declared, StateOf(candidate, machine.Field)));
        }

        if (!permitted.Any(transition => transition.When is null || CelInterpreter.EvaluatePredicate(transition.When, candidate, previous, caller)))
        {
            throw new AlvoConstraintViolationException(AlvoConstraintKind.Transition, [machine.Field]);
        }
    }

    private static string? StateOf(AlvoRecord row, string field) => row.TryGetValue(field, out var value) ? value switch
    {
        null => null,
        string state => state,
        _ => throw new InvalidOperationException($"The state field '{field}' holds a {value.GetType().Name}, not an enum value; the move cannot be judged, so the write is refused."),
    }
        : null;

    private static List<CompiledTransition> Declared(CompiledStateMachine machine, string? from, string? to) =>
        [.. machine.Transitions.Where(transition => string.Equals(transition.To, to, StringComparison.Ordinal) && Leaves(transition, from))];

    private static bool Leaves(CompiledTransition transition, string? from) =>
        transition.From is null || (from is not null && transition.From.Contains(from));

    private static void EnsureInitial(CompiledStateMachine machine, string? stored)
    {
        if (machine.Initial is { } initial && !string.Equals(initial, stored, StringComparison.Ordinal))
        {
            throw new AlvoConstraintViolationException(AlvoConstraintKind.Transition, [machine.Field]);
        }
    }

    private static string RolesRefusal(IReadOnlyList<CompiledTransition> declared, string? to) =>
        $"Moving to '{to}' needs one of the roles {string.Join(", ", declared.SelectMany(transition => transition.Roles!).Select(role => role.Name).Distinct().Select(name => $"'{name}'"))} "
        + $"(transition {string.Join(", ", declared.Select(transition => $"'{transition.Name}'"))} of the entity's state machine).";
}
