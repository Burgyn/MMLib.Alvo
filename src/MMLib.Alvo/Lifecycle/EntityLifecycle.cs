using MMLib.Alvo.Expressions;

namespace MMLib.Alvo.Lifecycle;

/// <summary>
/// One entity's compiled <c>stateMachine</c> and <c>timers</c>, carried on its <c>EntityPolicy</c> so both are judged
/// against the schema revision the rules judging the same write were compiled from.
/// </summary>
/// <param name="StateMachine">The compiled state machine, or <see langword="null"/> when the entity declares none.</param>
/// <param name="Timers">The compiled timers, in declaration order.</param>
internal sealed record EntityLifecycle(CompiledStateMachine? StateMachine, IReadOnlyList<CompiledTimer> Timers)
{
    /// <summary>The one instance every entity declaring neither member shares.</summary>
    internal static EntityLifecycle None { get; } = new(null, []);
}

/// <summary>A compiled <c>stateMachine</c>.</summary>
/// <param name="Field">The enum field that holds the state.</param>
/// <param name="Initial">The state every create must store, or <see langword="null"/> when any is admitted.</param>
/// <param name="Default">The field's literal <c>default</c>, which a create that sends no state stores.</param>
/// <param name="Transitions">The transitions, in declaration order.</param>
internal sealed record CompiledStateMachine(
    string Field, string? Initial, string? Default, IReadOnlyList<CompiledTransition> Transitions);

/// <summary>One compiled transition.</summary>
/// <param name="Name">The transition's declared name.</param>
/// <param name="From">The states it leaves, or <see langword="null"/> for <c>"*"</c> — any state, including none.</param>
/// <param name="To">The state it enters.</param>
/// <param name="Roles">The roles of which the caller must hold one, or <see langword="null"/> when any caller may.</param>
/// <param name="When">The compiled condition, or <see langword="null"/> when it has none.</param>
internal sealed record CompiledTransition(
    string Name, IReadOnlySet<string>? From, string To, IReadOnlySet<Role>? Roles, CompiledExpression? When);

/// <summary>One compiled timer.</summary>
/// <param name="Name">The timer's declared name.</param>
/// <param name="At">The compiled timestamp it fires at.</param>
/// <param name="When">The compiled condition it is armed under, or <see langword="null"/> when always.</param>
/// <param name="EventType">The custom event it fires.</param>
internal sealed record CompiledTimer(string Name, CompiledExpression At, CompiledExpression? When, string EventType);
