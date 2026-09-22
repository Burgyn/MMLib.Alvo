using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data.Common;

namespace MMLib.Alvo.Identity.Internal;

/// <summary>
/// Brings an identity database created by an older build up to the current model.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all.</b> <c>AlvoIdentityBootstrap</c> creates the identity tables when
/// they are absent and does nothing when they are present — which is correct exactly once. The
/// moment the model gains a column, a database created by the previous build has the tables and
/// not the column, so the probe says "present", nothing is created, and the first read fails at
/// runtime with a missing-column error nobody can act on. <c>{prefix}_identity_users.TenantId</c> was the
/// first such column; this is the mechanism so it is not also the first incident.
/// </para>
/// <para>
/// <b>It is reconciliation, not a migration history.</b> There is no journal table and no ordered
/// list of steps: the model is the desired state, the database is probed for it, and what is
/// missing is added. That is far less than EF migrations offer and it is deliberate — migrations
/// are generated per provider, so shipping them would mean one migration assembly per engine and
/// would put the provider choice inside this package, which is the same reason
/// <c>EnsureTablesAsync</c> does not use them. What it cannot do is rename, retype or drop, and it
/// says so rather than guessing.
/// </para>
/// <para>
/// <b>No engine-specific DDL is written here.</b> The type and the quoting come from the active
/// provider through EF's own <see cref="IMigrationsSqlGenerator"/>, so this file holds no
/// <c>if (sqlite)</c> — the per-engine knowledge stays behind the provider, which is where the
/// repository's own rule puts it.
/// </para>
/// <para>
/// <b>It refuses what it cannot do safely.</b> A missing column that is required and carries no
/// default cannot be added to a table that already has rows; every engine rejects it, and
/// inventing a value would be worse than failing. Such a column fails the start with a message
/// naming the table, the column and the one thing to do about it. It refuses without asking whether
/// the table is in fact empty — the check would be a second round trip to widen an error path that
/// should not be reachable, and an operator told to add the column by hand is not misled by it.
/// </para>
/// <para>
/// <b>Two limits worth knowing before the second column.</b> The column it adds carries the type,
/// nullability, length and defaults the model declares, and <em>not</em> precision, scale, unicode,
/// fixed length, collation or computed SQL — so a reconciled column can differ in those from the
/// same column on a freshly created database. And this adds only: a rename, a retype and a drop are
/// all outside what a model-versus-database comparison can tell apart from an addition and a
/// removal, which is exactly the information a migration history carries and this does not.
/// </para>
/// </remarks>
internal static class AlvoIdentitySchema
{
    /// <summary>Adds every column the model declares and the database does not have.</summary>
    /// <param name="store">The identity store.</param>
    /// <param name="cancellationToken">Cancels the reconciliation.</param>
    /// <returns>The columns that were added, newest model first, for the log.</returns>
    public static async Task<IReadOnlyList<string>> EnsureColumnsAsync(
        AlvoIdentityDbContext store, CancellationToken cancellationToken)
    {
        var added = new List<string>();

        foreach (var table in Tables(store))
        {
            await EnsureTableExistsAsync(store, table, cancellationToken).ConfigureAwait(false);

            var missing = await MissingAsync(store, table, cancellationToken).ConfigureAwait(false);
            foreach (var column in missing)
            {
                await AddAsync(store, table, column, cancellationToken).ConfigureAwait(false);
                added.Add($"{table.Name}.{column.Name}");
            }
        }

        return added;
    }

    /// <summary>Every mapped table in the model, with the columns it should have.</summary>
    /// <param name="store">The identity store.</param>
    /// <returns>One entry per table.</returns>
    private static IEnumerable<MappedTable> Tables(AlvoIdentityDbContext store)
    {
        foreach (var entity in store.Model.GetEntityTypes())
        {
            if (entity.GetTableName() is not { Length: > 0 } name)
            {
                continue;
            }

            var target = StoreObjectIdentifier.Table(name, entity.GetSchema());
            var columns = entity.GetProperties()
                .Select(property => new MappedColumn(
                    property.GetColumnName(target) ?? property.Name, property))
                .Where(column => column.Name.Length > 0)
                .ToList();

            yield return new MappedTable(name, entity.GetSchema(), columns);
        }
    }

    /// <summary>Refuses a table the model maps and the database does not have.</summary>
    /// <remarks>
    /// <b>Asked before the columns, because otherwise a missing table is diagnosed as a missing
    /// column.</b> The bootstrap probes only the users table, so a database whose
    /// users table exists while a sibling identity table does not reaches here — and a
    /// per-column probe would then report every column of that table as missing and fail on the
    /// first required one with a message naming a column when the problem is the table.
    /// </remarks>
    /// <param name="store">The identity store.</param>
    /// <param name="table">The table the model maps.</param>
    /// <param name="cancellationToken">Cancels the probe.</param>
    /// <returns>A task that completes when the table is known to be there.</returns>
    /// <exception cref="InvalidOperationException">The table is absent.</exception>
    private static async Task EnsureTableExistsAsync(
        AlvoIdentityDbContext store, MappedTable table, CancellationToken cancellationToken)
    {
        /* A constant rather than a column list: this question is about the table, and naming a
           column would make a missing column look like a missing table. */
        if (!await SelectsAsync(store, table, ["1"], quoted: false, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"The identity database has some of Alvo's identity tables and not '{table.Name}'. "
                + "Adding a missing table to a half-created identity schema is not something this "
                + "reconciliation does — restore the table, or start against an empty identity "
                + "database.");
        }
    }

    /// <summary>The columns the model declares that the table does not have.</summary>
    /// <remarks>
    /// One probe for the whole table first, because the answer is "nothing is missing" on every
    /// start but the one after an upgrade, and that path should cost a single statement. Only when
    /// the table probe fails is each column asked about individually.
    /// </remarks>
    /// <param name="store">The identity store.</param>
    /// <param name="table">The table to probe.</param>
    /// <param name="cancellationToken">Cancels the probe.</param>
    /// <returns>The missing columns, in model order.</returns>
    private static async Task<IReadOnlyList<MappedColumn>> MissingAsync(
        AlvoIdentityDbContext store, MappedTable table, CancellationToken cancellationToken)
    {
        var names = table.Columns.Select(column => column.Name).ToList();
        if (names.Count == 0
            || await SelectsAsync(store, table, names, quoted: true, cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        var missing = new List<MappedColumn>();
        foreach (var column in table.Columns)
        {
            if (!await SelectsAsync(store, table, [column.Name], quoted: true, cancellationToken)
                .ConfigureAwait(false))
            {
                missing.Add(column);
            }
        }

        return missing;
    }

    /// <summary>Whether the named columns can be selected from the table.</summary>
    /// <remarks>
    /// <c>WHERE 1 = 0</c> so the engine parses and resolves the column list and then reads no rows.
    /// The catch is narrowed to <see cref="DbException"/>: an unresolved column is the only failure
    /// this statement can raise, and anything else must still fail the start.
    /// </remarks>
    /// <param name="store">The identity store.</param>
    /// <param name="table">The table to read from.</param>
    /// <param name="columns">The columns to name, or a literal when <paramref name="quoted"/> is false.</param>
    /// <param name="quoted">
    /// Whether <paramref name="columns"/> are identifiers to delimit. <see langword="false"/> passes
    /// them through, which only the table probe uses and only for the constant <c>1</c>.
    /// </param>
    /// <param name="cancellationToken">Cancels the probe.</param>
    /// <returns><see langword="true"/> when every named column resolved.</returns>
    private static async Task<bool> SelectsAsync(
        AlvoIdentityDbContext store, MappedTable table, IReadOnlyList<string> columns, bool quoted,
        CancellationToken cancellationToken)
    {
        var sql = store.GetService<ISqlGenerationHelper>();
        var list = string.Join(", ", quoted ? columns.Select(sql.DelimitIdentifier) : columns);
        var target = sql.DelimitIdentifier(table.Name, table.Schema);

        var connection = store.Database.GetDbConnection();
        await store.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {list} FROM {target} WHERE 1 = 0";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbException)
        {
            return false;
        }
        finally
        {
            await store.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Adds one column, in the active provider's own DDL.</summary>
    /// <param name="store">The identity store.</param>
    /// <param name="table">The table to add it to.</param>
    /// <param name="column">The column the model declares.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the column is there.</returns>
    /// <exception cref="InvalidOperationException">
    /// The column is required and has no default, so it cannot be added to a table with rows.
    /// </exception>
    private static async Task AddAsync(
        AlvoIdentityDbContext store, MappedTable table, MappedColumn column,
        CancellationToken cancellationToken)
    {
        var property = column.Property;
        var defaultValue = property.GetDefaultValue();

        if (!property.IsNullable && defaultValue is null && property.GetDefaultValueSql() is null)
        {
            throw new InvalidOperationException(
                $"The identity database is missing the required column '{table.Name}.{column.Name}', "
                + "which cannot be added to a table that already has rows without a value for them. "
                + "Add the column by hand, or start against an empty identity database.");
        }

        var operation = new AddColumnOperation
        {
            Table = table.Name,
            Schema = table.Schema,
            Name = column.Name,
            ClrType = property.ClrType,
            ColumnType = property.GetColumnType(),
            IsNullable = property.IsNullable,
            MaxLength = property.GetMaxLength(),
            DefaultValue = defaultValue,
            DefaultValueSql = property.GetDefaultValueSql(),
        };

        var generator = store.GetService<IMigrationsSqlGenerator>();
        foreach (var command in generator.Generate([operation], store.Model))
        {
            await store.Database
                .ExecuteSqlRawAsync(command.CommandText, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>A table the model maps, with the columns it should have.</summary>
    /// <param name="Name">The table name.</param>
    /// <param name="Schema">The schema, when the provider has one.</param>
    /// <param name="Columns">The columns the model declares.</param>
    private sealed record MappedTable(string Name, string? Schema, IReadOnlyList<MappedColumn> Columns);

    /// <summary>One column the model declares.</summary>
    /// <param name="Name">The column name in the database.</param>
    /// <param name="Property">The property it is mapped from.</param>
    private sealed record MappedColumn(string Name, IProperty Property);
}
