using DescFieldType = MMLib.Alvo.Descriptor.FieldType;

namespace MMLib.Alvo.Descriptor.Internal;

/// <summary>
/// The apply-time findings for an entity's <c>stateMachine</c>: it guards one stored, visible enum field, names only that
/// field's declared values, gives <c>"*"</c> alone, never leads a transition into a state it leaves, and agrees with the
/// field's literal <c>default</c> — and it is declared on a physical entity.
/// </summary>
/// <remarks>
/// <b>Read from the parsed descriptor, before the mapper,</b> as <see cref="ExclusionIndexCheck"/> is, so every finding sits
/// at the author's own pointer. Roles and <c>when</c> are compiled with the rules, by <c>LifecycleCompiler</c>. A hidden state
/// field is refused because a refused move tells the caller which state the record is in.
/// </remarks>
internal static class StateMachineCheck
{
    /// <summary>The source that stands for any state, including none.</summary>
    internal const string AnyState = "*";

    /// <summary>Every finding about every declared <c>stateMachine</c>.</summary>
    /// <param name="descriptor">The parsed descriptor.</param>
    internal static IReadOnlyList<DescriptorValidationError> Check(AlvoDescriptor descriptor) =>
    [
        .. from entity in descriptor.Entities
           where entity.Value.StateMachine is not null
           from error in Errors(new Declared(entity.Key, entity.Value, entity.Value.StateMachine!))
           select error,
    ];

    /// <summary>The refusal of a lifecycle <paramref name="member"/> on a <c>storage: dynamic</c> entity.</summary>
    /// <param name="entity">The dynamic entity.</param>
    /// <param name="member"><c>stateMachine</c> or <c>timers</c>.</param>
    internal static DescriptorValidationError DynamicError(string entity, string member) => new(
        $"/entities/{entity}/{member}",
        $"'{entity}' declares '{member}' and 'storage: dynamic' — the write path enforces a state machine and arms timers "
        + "only over a physical entity's table.",
        $"Remove '{member}', or make '{entity}' a physical entity.",
        DescriptorValidationSeverity.Error);

    private static IEnumerable<DescriptorValidationError> Errors(Declared declared)
    {
        if (!DescriptorToSchemaMapper.IsPhysical(declared.Entity))
        {
            return [DynamicError(declared.Name, "stateMachine")];
        }

        if (FieldError(declared) is { } fieldError)
        {
            return [fieldError];
        }

        return StateErrors(declared);
    }

    private static IEnumerable<DescriptorValidationError> StateErrors(Declared declared)
    {
        IEnumerable<DescriptorValidationError?> findings =
        [
            InitialError(declared),
            .. declared.Machine.Transitions.SelectMany(transition => TransitionErrors(declared, transition.Key, transition.Value)),
        ];

        return findings.OfType<DescriptorValidationError>();
    }

    private static DescriptorValidationError? FieldError(Declared declared)
    {
        var name = declared.Machine.Field;
        if (!declared.Entity.Fields.TryGetValue(name, out var field))
        {
            return declared.FieldAt($"'{declared.Name}' does not declare.");
        }

        if (field.Type != DescFieldType.Enum || field.Values is null)
        {
            return declared.FieldAt("is not an enum.");
        }

        return MaintainedError(declared, field) ?? HiddenError(declared, field);
    }

    private static DescriptorValidationError? MaintainedError(Declared declared, FieldDescriptor field)
    {
        var kind = field switch
        {
            { Computed: not null } => "computed",
            { Rollup: not null } => "a rollup",
            { Virtual: true } => "virtual",
            _ => null,
        };

        return kind is null ? null : declared.FieldAt($"is {kind}, so no write stores it.");
    }

    private static DescriptorValidationError? HiddenError(Declared declared, FieldDescriptor field) =>
        field.Hidden is { IsExpression: true } or { Boolean: true }
            ? declared.At(
                "/field",
                $"guards '{declared.Machine.Field}', which is hidden: a refused move would tell a caller the state a record they cannot see is in.",
                $"Unhide '{declared.Machine.Field}', or hold the state in a field every reader may see.")
            : null;

    private static DescriptorValidationError? InitialError(Declared declared)
    {
        if (declared.Machine.Initial is not { } initial)
        {
            return null;
        }

        return UndeclaredError(declared, initial, "/initial")
            ?? (declared.DefaultState is { } fallback && !string.Equals(fallback, initial, StringComparison.Ordinal)
                ? declared.At(
                    "/initial",
                    $"starts at '{initial}', but '{declared.Machine.Field}' defaults to '{fallback}' — a create that sends no state would be refused.",
                    $"Make 'initial' '{fallback}', or change the field's default to '{initial}'.")
                : null);
    }

    private static IEnumerable<DescriptorValidationError?> TransitionErrors(Declared declared, string name, StateTransition transition)
    {
        var at = $"/transitions/{name}";
        return
        [
            WildcardError(declared, at, transition),
            .. transition.From.Select((source, position) =>
                source == AnyState ? null : UndeclaredError(declared, source, $"{at}/from/{position}")),
            UndeclaredError(declared, transition.To, $"{at}/to") ?? LoopError(declared, at, transition),
        ];
    }

    private static DescriptorValidationError? WildcardError(Declared declared, string at, StateTransition transition) =>
        transition.From.Count > 1 && transition.From.Contains(AnyState, StringComparer.Ordinal)
            ? declared.At(
                $"{at}/from",
                $"leaves \"{AnyState}\" beside other states — \"{AnyState}\" already is every state.",
                $"Give [\"{AnyState}\"] alone, or list the states it leaves without \"{AnyState}\".")
            : null;

    private static DescriptorValidationError? LoopError(Declared declared, string at, StateTransition transition) =>
        transition.From.Contains(transition.To, StringComparer.Ordinal)
            ? declared.At(
                $"{at}/to",
                $"has a transition into '{transition.To}' from '{transition.To}' — keeping a state is no transition, so it would never apply.",
                $"Remove '{transition.To}' from 'from'.")
            : null;

    private static DescriptorValidationError? UndeclaredError(Declared declared, string state, string at) =>
        declared.Values.Contains(state, StringComparer.Ordinal)
            ? null
            : declared.At(
                at,
                $"names the state '{state}', which is not one of the values of '{declared.Machine.Field}'.",
                $"Use one of {string.Join(", ", declared.Values.Select(value => $"'{value}'"))}, or add '{state}' to the field's values.");

    private sealed record Declared(string Name, EntityDescriptor Entity, EntityStateMachine Machine)
    {
        internal IReadOnlyList<string> Values => Entity.Fields[Machine.Field].Values!;

        internal string? DefaultState =>
            Entity.Fields[Machine.Field].Default?.Literal is { ValueKind: System.Text.Json.JsonValueKind.String } literal ? literal.GetString() : null;

        internal DescriptorValidationError FieldAt(string reason) =>
            At("/field", $"guards '{Machine.Field}', which {reason}", $"Name a stored enum field of '{Name}' that every reader may see.");

        internal DescriptorValidationError At(string suffix, string reason, string fix) => new(
            $"/entities/{Name}/stateMachine{suffix}",
            $"The state machine of '{Name}' {reason}",
            fix,
            DescriptorValidationSeverity.Error);
    }
}
