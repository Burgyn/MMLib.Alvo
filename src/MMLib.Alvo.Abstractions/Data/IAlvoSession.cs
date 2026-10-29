using System.Diagnostics.CodeAnalysis;

namespace MMLib.Alvo.Data;

/// <summary>
/// <see cref="IAlvoData"/> bound to one caller: every read and write runs as <see cref="Context"/>, through the same
/// policy, tenancy and chain-depth checks, and each call commits on its own.
/// </summary>
/// <remarks>
/// <para>
/// What host code running on Alvo's behalf is handed as its data access — an event handler's
/// <c>AlvoEventContext.Data</c> — so it cannot pass a different caller by accident. A session from
/// <see cref="IAlvoSessions"/> commits each call on its own, and no call takes an idempotency key: a handler deduplicates
/// on its event's id. An <see cref="IAlvoTransaction"/> is the session of a transaction unit, whose calls commit together.
/// </para>
/// <para>
/// The exceptions are <see cref="IAlvoData"/>'s, family for family.
/// </para>
/// <para>
/// Alvo implements this interface and may add members to it; it is not meant to be implemented by host code.
/// </para>
/// </remarks>
public interface IAlvoSession
{
    /// <summary>Gets the caller every call runs as.</summary>
    AlvoContext Context { get; }

    /// <summary>Lists one entity's records visible to <see cref="Context"/>.</summary>
    /// <param name="query">The entity, filter, sort and page.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The page.</returns>
    Task<AlvoPage> QueryAsync(AlvoQuery query, CancellationToken cancellationToken = default);

    /// <summary>Reads one record visible to <see cref="Context"/>, or <see langword="null"/>.</summary>
    /// <param name="entity">The entity name.</param>
    /// <param name="id">The record's id.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The record, or <see langword="null"/> when it does not exist or is not visible.</returns>
    Task<AlvoRecord?> GetAsync(string entity, Guid id, CancellationToken cancellationToken = default);

    /// <summary>Creates a record as <see cref="Context"/>.</summary>
    /// <param name="entity">The entity name.</param>
    /// <param name="values">The fields to write.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>The stored record.</returns>
    Task<AlvoRecord> CreateAsync(
        string entity, IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default);

    /// <summary>Updates the named fields of a record as <see cref="Context"/>.</summary>
    /// <param name="entity">The entity name.</param>
    /// <param name="id">The record's id.</param>
    /// <param name="values">The fields to change.</param>
    /// <param name="precondition">An optional version the record must still have.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>The stored record.</returns>
    Task<AlvoRecord> UpdateAsync(
        string entity,
        Guid id,
        IReadOnlyDictionary<string, object?> values,
        AlvoPrecondition? precondition = null,
        CancellationToken cancellationToken = default);

    /// <summary>Replaces a record, or creates it under <paramref name="id"/>, as <see cref="Context"/>.</summary>
    /// <param name="entity">The entity name.</param>
    /// <param name="id">The record's id.</param>
    /// <param name="values">The complete record.</param>
    /// <param name="precondition">An optional version the record must still have.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>Whether the record was created or replaced, and its stored form.</returns>
    Task<AlvoReplaceResult> ReplaceAsync(
        string entity,
        Guid id,
        IReadOnlyDictionary<string, object?> values,
        AlvoPrecondition? precondition = null,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes a record as <see cref="Context"/>.</summary>
    /// <param name="entity">The entity name.</param>
    /// <param name="id">The record's id.</param>
    /// <param name="precondition">An optional version the record must still have.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the record is gone.</returns>
    Task DeleteAsync(
        string entity, Guid id, AlvoPrecondition? precondition = null, CancellationToken cancellationToken = default);
}

/// <summary>Hands out <see cref="IAlvoSession"/>s, one per caller.</summary>
public interface IAlvoSessions
{
    /// <summary>Binds a session to <paramref name="context"/>.</summary>
    /// <param name="context">The caller every call of the session runs as.</param>
    /// <returns>The session.</returns>
    [SuppressMessage(
        "Naming",
        "CA1716:Identifiers should not match keywords",
        Justification = "The host-extensibility design names it For; Alvo targets C# hosts, and a VB implementer can still escape the name.")]
    IAlvoSession For(AlvoContext context);
}
