namespace MMLib.Alvo.Management;

/// <summary>What one transition of an entity's state machine answers for a simulated caller.</summary>
/// <remarks>Init-only rather than positional, so a later version adds members without breaking a reader.</remarks>
public sealed record ManagementTransitionVerdict
{
    /// <summary>Gets the transition's declared name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the states it leaves, ordinally sorted; <c>["*"]</c> when it leaves any state.</summary>
    public required IReadOnlyList<string> From { get; init; }

    /// <summary>Gets the state it enters.</summary>
    public required string To { get; init; }

    /// <summary>Gets the roles of which the caller must hold one; empty when any caller the update rule admits may take it.</summary>
    public IReadOnlyList<string> Roles { get; init; } = [];

    /// <summary>Gets the transition's condition, as CEL text, or <see langword="null"/> when it has none.</summary>
    public string? When { get; init; }

    /// <summary>
    /// Gets whether the caller may take the transition: <see langword="false"/> when the caller holds none of its roles;
    /// <see langword="null"/> when the roles admit the caller and a <see cref="When"/> decides it per record;
    /// <see langword="true"/> when the roles admit the caller and no condition remains.
    /// </summary>
    public bool? Allowed { get; init; }
}
