namespace MMLib.Alvo.Descriptor;

/// <summary>
/// Which values an enum field may move between (schema <c>entity.stateMachine</c>): named transitions, each from a set of
/// states to one state, optionally gated by roles and a CEL condition. An update that changes <see cref="Field"/> must match
/// one of them; a delete is not a transition.
/// </summary>
public sealed record EntityStateMachine
{
    /// <summary>The stored enum field that holds the state.</summary>
    public required string Field { get; init; }

    /// <summary>The value every create must store in <see cref="Field"/>; <see langword="null"/> admits any.</summary>
    public string? Initial { get; init; }

    /// <summary>The named transitions; the move is allowed when any one of those reaching the new state admits it.</summary>
    public required IReadOnlyDictionary<string, StateTransition> Transitions { get; init; }
}

/// <summary>One named transition of an <see cref="EntityStateMachine"/> (schema <c>entity.stateMachine.transitions.&lt;name&gt;</c>).</summary>
public sealed record StateTransition
{
    /// <summary>The states it leaves; <c>["*"]</c> alone is any state, including none.</summary>
    public required IReadOnlyList<string> From { get; init; }

    /// <summary>The state it enters.</summary>
    public required string To { get; init; }

    /// <summary>The roles of which the caller must hold one; <see langword="null"/> admits every caller.</summary>
    public IReadOnlyList<string>? Roles { get; init; }

    /// <summary>A CEL condition over <c>old</c>, <c>new</c>, <c>changed()</c> and <c>@user</c>; false refuses the move.</summary>
    public string? When { get; init; }

    /// <summary>Human-readable description of the transition.</summary>
    public string? Description { get; init; }
}

/// <summary>
/// A durable per-record timer (schema <c>entity.timers.&lt;name&gt;</c>): armed, moved and cancelled in the write's own
/// transaction, it fires its event once at its latest time.
/// </summary>
public sealed record EntityTimer
{
    /// <summary>A CEL timestamp over the record's own fields, <c>now()</c> and durations; null leaves it unarmed.</summary>
    public required string At { get; init; }

    /// <summary>A CEL condition over the record's own fields; the timer is armed only while it holds.</summary>
    public string? When { get; init; }

    /// <summary>The custom event it fires; <see langword="null"/> fires <c>&lt;entity&gt;.&lt;timer&gt;</c> with '-' as '_'.</summary>
    public string? Event { get; init; }

    /// <summary>Human-readable description of the timer.</summary>
    public string? Description { get; init; }
}
