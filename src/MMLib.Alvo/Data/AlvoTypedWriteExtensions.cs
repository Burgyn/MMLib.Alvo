using MMLib.Alvo.Data.Internal;
using MMLib.Alvo.Schema;

using System.Linq.Expressions;

namespace MMLib.Alvo.Data;

/// <summary>
/// Writes typed records through a session or a transaction unit: the entity a type's <see cref="AlvoEntityAttribute"/> names,
/// its mapped properties as field values, and the same rules, tenancy and hooks as an untyped write.
/// </summary>
/// <remarks>
/// <para>
/// A write sends every mapped property except the fields Alvo manages (<c>id</c>, the audit columns, <c>deleted_at</c>; and
/// <c>tenant_id</c> on a replace) and the fields the store maintains (a <c>computed</c> or <c>rollup</c> field, so a
/// read-model type that maps one stays writable): an enum member as its declared value, a whole number as a
/// <see cref="long"/>. A create leaves out a property that is <see langword="null"/>, so the field's descriptor default
/// applies; a replace sends it. A wrapper (<see cref="IAlvoEntity"/>) sends its record. An <see cref="IAlvoTransaction"/>
/// is a session, so each of these runs inside its unit too.
/// </para>
/// <para>
/// On a tenant-scoped entity a create must carry the row's tenant: map <c>tenant_id</c> and set it. A <c>tenant_id</c> of
/// <see cref="Guid.Empty"/> — an unset non-nullable <see cref="Guid"/> — is the reserved "no tenant" and is refused with an
/// <see cref="ArgumentException"/> before anything is sent, whoever the caller is.
/// </para>
/// <para>
/// Each write answers the stored record as the caller's own read would return it, as the record type — or
/// <see langword="null"/> when that read does not return every field the type maps (a <c>get</c> rule that
/// excludes the row, a hidden field). The write happened either way.
/// </para>
/// </remarks>
public static class AlvoTypedWriteExtensions
{
    /// <summary>Creates <paramref name="record"/> as a row of the entity <typeparamref name="T"/> names.</summary>
    /// <typeparam name="T">A class marked with <see cref="AlvoEntityAttribute"/>.</typeparam>
    /// <param name="session">Data access as the session's caller.</param>
    /// <param name="record">The record; its id is assigned by Alvo.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The stored record, or <see langword="null"/> when the caller may not read every field of it back.</returns>
    /// <exception cref="ArgumentException">The record's <c>tenant_id</c> is <see cref="Guid.Empty"/>.</exception>
    public static async Task<T?> CreateAsync<T>(this IAlvoSession session, T record, CancellationToken cancellationToken = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(session);
        var entity = TypedEntity.NameOf<T>();
        var values = RecordWriter.Values(record, isCreate: true, SchemaOf(session, entity));
        var stored = await session.CreateAsync(entity, values, cancellationToken).ConfigureAwait(false);

        return RecordWriter.Readable<T>(stored);
    }

    /// <summary>Changes the fields <paramref name="patch"/> sets on one row.</summary>
    /// <typeparam name="T">A class marked with <see cref="AlvoEntityAttribute"/>.</typeparam>
    /// <param name="session">Data access as the session's caller.</param>
    /// <param name="id">The row's id.</param>
    /// <param name="patch">The fields to change; at least one.</param>
    /// <param name="precondition">An optional version the row must still have.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The stored record, or <see langword="null"/> when the caller may not read every field of it back.</returns>
    /// <exception cref="ArgumentException"><paramref name="patch"/> sets no field, or a field Alvo manages.</exception>
    /// <exception cref="InvalidOperationException"><typeparamref name="T"/> has a mapping problem no descriptor can cure.</exception>
    public static async Task<T?> UpdateAsync<T>(
        this IAlvoSession session, Guid id, IAlvoPatch<T> patch, AlvoPrecondition? precondition = null, CancellationToken cancellationToken = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(patch);
        RecordWriter.EnsureWritable(RecordShape.Of(typeof(T)));
        if (patch.Values.Count == 0)
        {
            throw new ArgumentException("The patch sets no field, so there is nothing to update. Set at least one with Set(x => x.Field, value).", nameof(patch));
        }

        if (patch.Values.Keys.FirstOrDefault(field => !RecordWriter.IsWritten(field, isUpdate: true)) is { } managed)
        {
            throw new ArgumentException($"'{managed}' is managed by Alvo and cannot be patched.", nameof(patch));
        }

        var stored = await session.UpdateAsync(TypedEntity.NameOf<T>(), id, patch.Values, precondition, cancellationToken).ConfigureAwait(false);

        return RecordWriter.Readable<T>(stored);
    }

    /// <summary>Replaces one row with <paramref name="record"/>, or creates it under <paramref name="id"/>.</summary>
    /// <typeparam name="T">A class marked with <see cref="AlvoEntityAttribute"/>.</typeparam>
    /// <param name="session">Data access as the session's caller.</param>
    /// <param name="id">The row's id; the record's own id is not sent.</param>
    /// <param name="record">The whole record.</param>
    /// <param name="precondition">An optional version the row must still have.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The stored record, or <see langword="null"/> when the caller may not read every field of it back; use the untyped <c>ReplaceAsync</c> to learn whether it created the row.</returns>
    public static async Task<T?> ReplaceAsync<T>(
        this IAlvoSession session, Guid id, T record, AlvoPrecondition? precondition = null, CancellationToken cancellationToken = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(session);
        var entity = TypedEntity.NameOf<T>();
        var values = RecordWriter.Values(record, isCreate: false, SchemaOf(session, entity));
        var result = await session.ReplaceAsync(entity, id, values, precondition, cancellationToken).ConfigureAwait(false);

        return RecordWriter.Readable<T>(result.Row);
    }

    /// <summary>Whether the caller can see at least one row of <typeparamref name="T"/>'s entity matching <paramref name="where"/>.</summary>
    /// <typeparam name="T">A class marked with <see cref="AlvoEntityAttribute"/>.</typeparam>
    /// <param name="session">Data access as the session's caller.</param>
    /// <param name="where">The predicate, translated into a filter as a typed query's is.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns><see langword="true"/> when at least one row the caller can see matches.</returns>
    /// <exception cref="AlvoTranslationException"><paramref name="where"/> holds a construct with no filter equivalent.</exception>
    public static async Task<bool> ExistsAsync<T>(this IAlvoSession session, Expression<Func<T, bool>> where, CancellationToken cancellationToken = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(session);
        var page = await session.QueryAsync(IdOfOneRow(where), cancellationToken).ConfigureAwait(false);

        return page.Items.Count > 0;
    }

    /// <summary>A query for the id of at least one matching row: an existence test reads no other column.</summary>
    private static AlvoQuery IdOfOneRow<T>(Expression<Func<T, bool>> where)
        where T : class =>
        TypedEntity.Query(where, limit: 1) with { Select = [AlvoManagedColumns.Id] };

    /// <summary>The entity's applied schema when the session can name it — Alvo's own sessions can (Ruling 52).</summary>
    private static EntitySchema? SchemaOf(IAlvoSession session, string entity) =>
        session is IAlvoSessionSchema schema ? schema.EntityOf(entity) : null;
}
