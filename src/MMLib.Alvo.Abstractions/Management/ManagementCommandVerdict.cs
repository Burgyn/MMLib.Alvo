namespace MMLib.Alvo.Management;

/// <summary>What a command's access rule answers for a simulated caller.</summary>
/// <remarks>Init-only rather than positional, so a later version adds members without breaking a reader.</remarks>
public sealed record ManagementCommandVerdict
{
    /// <summary>Gets the command's name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the entity the command is bound to, or null for a command bound to none.</summary>
    public string? Entity { get; init; }

    /// <summary>Gets the access rule, as CEL text.</summary>
    public required string Access { get; init; }

    /// <summary>
    /// Gets whether the rule admits the caller: <see langword="true"/> or <see langword="false"/> when it reads only
    /// <c>@user</c> and <c>@tenant</c>; <see langword="null"/> when it also reads the row, which is decided per row — and a
    /// row the caller cannot read answers 404 before the rule runs.
    /// </summary>
    public bool? Allowed { get; init; }
}
