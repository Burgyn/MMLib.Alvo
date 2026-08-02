using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using MMLib.Alvo.Schema;
using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// Turns a storage write failure into <see cref="AlvoConstraintViolationException"/> when — and only when —
/// the driver's own dialect recognises it as a constraint a caller can act on, and resolves whatever the
/// engine named into the entity's own field names.
/// </summary>
/// <remarks>
/// <para>
/// <b>The split of labour is the point.</b> <see cref="IAlvoSqlDialect.DecodeConstraintViolation"/> owns
/// everything engine-specific (which exception type, which code, whether a name or columns come back) and
/// this type owns everything model-specific (which index that name belongs to, which fields those columns
/// are, which of them the framework manages). Neither half can be written correctly in the other's place:
/// the dialect has no model and the data path must not know a provider's error codes.
/// </para>
/// <para>
/// <b>An unresolvable or framework-only conflict keeps propagating.</b> If the engine names a constraint
/// this model does not have, or one made up entirely of framework-managed columns (<c>id</c>,
/// <c>tenant_id</c>), the original exception is rethrown untouched and the host renders its 500 with the
/// stack trace intact. That is deliberate rather than defensive: a caller cannot change a column they may
/// not write, so telling them to is worse than telling them nothing, and a primary-key collision on a
/// framework-minted <see cref="Guid"/> really is a broken invariant.
/// </para>
/// <para>
/// <b>Only the entity's own write is wrapped.</b> The idempotency table's primary key is a constraint too,
/// and a rival create losing that race is the mechanism <c>EfAlvoData.ReplayableCreateAsync</c> retries on —
/// so that insert is deliberately <em>not</em> routed through here, and its raw provider exception still
/// reaches the retry loop.
/// </para>
/// </remarks>
internal static class ConstraintViolationTranslator
{
    /// <summary>
    /// Runs <paramref name="write"/>, translating a recognised constraint violation on
    /// <paramref name="rows"/>' own table.
    /// </summary>
    /// <param name="write">The write to perform.</param>
    /// <param name="dialect">The driver's dialect, which owns the engine-specific decoding.</param>
    /// <param name="rows">The entity type being written, resolved against for field names.</param>
    /// <param name="schema">The entity as the applied schema declares it, for its managed-column set.</param>
    internal static async Task<T> TranslatedAsync<T>(
        Func<Task<T>> write, IAlvoSqlDialect dialect, IEntityType rows, EntitySchema schema)
    {
        try
        {
            return await write();
        }
        catch (Exception failure) when (Translate(failure, dialect, rows, schema) is { } violation)
        {
            throw violation;
        }
    }

    /// <inheritdoc cref="TranslatedAsync{T}"/>
    internal static async Task TranslatedAsync(
        Func<Task> write, IAlvoSqlDialect dialect, IEntityType rows, EntitySchema schema)
    {
        try
        {
            await write();
        }
        catch (Exception failure) when (Translate(failure, dialect, rows, schema) is { } violation)
        {
            throw violation;
        }
    }

    /// <summary>
    /// The translated exception, or <see langword="null"/> when <paramref name="failure"/> is not a
    /// constraint violation this model can name — in which case the original propagates untouched.
    /// </summary>
    /// <remarks>
    /// Evaluated in the <c>when</c> filter rather than in the <c>catch</c> body so that a failure this cannot
    /// translate is never caught at all: the stack trace a host logs is then the provider's own, unwound from
    /// where it was raised, rather than one rethrown from here.
    /// </remarks>
    private static AlvoConstraintViolationException? Translate(
        Exception failure, IAlvoSqlDialect dialect, IEntityType rows, EntitySchema schema)
    {
        if (ProviderException(failure) is not { } provider)
        {
            return null;
        }

        if (dialect.DecodeConstraintViolation(provider) is not { } violation)
        {
            return null;
        }

        if (violation.Kind == AlvoConstraintKind.Referenced)
        {
            // The engine names the referencing table at best, and that is a fact about data the caller may
            // not be able to read — see AlvoConstraintViolationException. The refusal names nothing.
            return new AlvoConstraintViolationException(AlvoConstraintKind.Referenced, [], failure);
        }

        var fields = CallerFields(violation, rows, schema);
        return fields.Count == 0
            ? null
            : new AlvoConstraintViolationException(AlvoConstraintKind.Unique, fields, failure);
    }

    /// <summary>
    /// The provider's own exception inside whatever EF wrapped it in — <see cref="DbUpdateException"/> from
    /// <c>SaveChanges</c>, nothing at all from <c>ExecuteUpdate</c>/<c>ExecuteDelete</c>.
    /// </summary>
    /// <remarks>
    /// The walk is over <see cref="Exception.InnerException"/> rather than a single unwrap because EF's
    /// wrapper is not guaranteed to be exactly one level deep, and because a dialect must be handed the type
    /// it recognises — handing it the wrapper would make every driver unwrap identically, which is a shared
    /// concern and belongs here.
    /// </remarks>
    /// <param name="failure">The exception the write raised.</param>
    private static DbException? ProviderException(Exception failure)
    {
        for (var current = failure; current is not null; current = current.InnerException)
        {
            if (current is DbException provider)
            {
                return provider;
            }
        }

        return null;
    }

    /// <summary>
    /// The entity's own fields the violated unique constraint spans, with every framework-managed column
    /// removed.
    /// </summary>
    /// <remarks>
    /// Columns win over the constraint name when the engine gave both, because a column list needs no lookup
    /// and cannot be defeated by an index the model spells differently from the database. The name is matched
    /// against each index's <em>database</em> name, which is what the engine reports and what the migration
    /// created.
    /// </remarks>
    /// <param name="violation">What the dialect decoded.</param>
    /// <param name="rows">The entity type being written.</param>
    /// <param name="schema">The entity as the applied schema declares it.</param>
    private static IReadOnlyList<string> CallerFields(
        SqlConstraintViolation violation, IEntityType rows, EntitySchema schema)
    {
        var columns = violation.Columns.Count > 0
            ? violation.Columns.Where(column => rows.FindProperty(column) is not null)
            : IndexColumns(violation.ConstraintName, rows);

        var managed = AlvoManagedColumns.For(schema);
        return [.. columns.Where(column => !managed.Contains(column))];
    }

    /// <summary>The properties of the index the engine named, or nothing when this model has no such index.</summary>
    /// <param name="constraintName">The engine's own constraint name, or <see langword="null"/>.</param>
    /// <param name="rows">The entity type being written.</param>
    private static IEnumerable<string> IndexColumns(string? constraintName, IEntityType rows) =>
        constraintName is null
            ? []
            : rows.GetIndexes()
                .Where(index => string.Equals(index.GetDatabaseName(), constraintName, StringComparison.Ordinal))
                .SelectMany(index => index.Properties)
                .Select(property => property.Name);
}
