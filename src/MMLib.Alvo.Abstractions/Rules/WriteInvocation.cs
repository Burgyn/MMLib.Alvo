using MMLib.Alvo.Data;

namespace MMLib.Alvo.Rules;

/// <summary>One stage of one write, as a driver hands it to <see cref="IWriteExtensionRunner"/>.</summary>
/// <remarks>Init-only rather than positional, so a later version adds members without breaking a driver.</remarks>
public sealed record WriteInvocation
{
    /// <summary>Gets the entity being written.</summary>
    public required string Entity { get; init; }

    /// <summary>Gets the write.</summary>
    public required AlvoWriteOperation Operation { get; init; }

    /// <summary>Gets the caller the write is performed as.</summary>
    public required AlvoContext Caller { get; init; }

    /// <summary>Gets the instant the write is stamped with.</summary>
    public required DateTimeOffset Now { get; init; }

    /// <summary>Gets the row before the write, unmasked; <see langword="null"/> on a create and on a replace that creates.</summary>
    public AlvoRecord? Old { get; init; }

    /// <summary>Gets the row the write would store, unmasked — validate and before-write stages; <see langword="null"/> on a delete.</summary>
    public AlvoRecord? New { get; init; }

    /// <summary>Gets the row as stored, with its id, defaults and rollups — the after-write stage only.</summary>
    public AlvoRecord? Saved { get; init; }

    /// <summary>
    /// Gets the write's transaction unit, acting as the caller one hop deeper in the causation chain — in-transaction stages
    /// only; <see langword="null"/> for validate, which runs with no transaction open.
    /// </summary>
    public IAlvoTransaction? Transaction { get; init; }

    /// <summary>
    /// Gets the DI scope hooks resolve from — the unit's, in-transaction; <see langword="null"/> for validate, whose hooks the
    /// runner resolves from a scope of its own.
    /// </summary>
    public IServiceProvider? Services { get; init; }
}
