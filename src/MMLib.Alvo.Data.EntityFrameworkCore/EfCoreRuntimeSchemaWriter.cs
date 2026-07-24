using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Migrations;
using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// The atomic <see cref="IRuntimeSchemaWriter"/>: applies a plan's DDL and appends the resulting
/// descriptor version in ONE transaction on one connection, so a lost optimistic-lock race rolls
/// back the DDL together with the refused version insert. Shares the descriptor-versions write path
/// (<see cref="VersionRowWriter"/>) and SQL-batch execution (<see cref="RelationalSqlBatch"/>) with
/// the non-atomic store and the migrator, so all three speak identical SQL.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Ordering — version-row insert first, then DDL, then commit.</strong> The version-row
/// insert is the optimistic-lock gate: the composite PRIMARY KEY (project, revision) lets exactly
/// one of two concurrent writers past it, so a loser is rejected <em>before</em> it runs any DDL and
/// never mutates the schema at all. Running the DDL first would let two independent transactions
/// execute conflicting schema changes concurrently and could surface a lock-contention error (e.g.
/// SQLite BUSY) that the re-read translation would then have to rethrow as a spurious failure. With
/// insert-first, the only writer that reaches the DDL is the confirmed winner.
/// </para>
/// <para>
/// Both the insert and the DDL run inside the one transaction, so if the DDL itself fails the whole
/// transaction — insert included — rolls back: the schema and the version history can never drift
/// apart. The destructive/dry-run guardrail is the caller's responsibility (see
/// <see cref="IRuntimeSchemaWriter"/>); this writer executes unconditionally.
/// </para>
/// </remarks>
internal sealed class EfCoreRuntimeSchemaWriter : IRuntimeSchemaWriter, IDisposable
{
    private readonly RelationalConnectionFactory _connections;
    private readonly VersionRowWriter _rows;

    public EfCoreRuntimeSchemaWriter(RelationalConnectionFactory connections, AlvoOptions options)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(options);
        _connections = connections;
        _rows = new VersionRowWriter(connections, options);
    }

    /// <inheritdoc/>
    public async Task<DescriptorVersion> ApplyAndAppendAsync(
        string project, MigrationPlan plan, DescriptorVersion candidate,
        int expectedRevision, MigrationOptions options, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(candidate);

        var connection = _connections.Create();
        await using (connection.ConfigureAwait(false))
        {
            await _rows.EnsureReadyAsync(connection, ct).ConfigureAwait(false);
            var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                return await ApplyAndAppendInTransactionAsync(
                    connection, transaction, project, plan, candidate, expectedRevision, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Disposes the shared <see cref="VersionRowWriter"/> (its ensure-once gate).</summary>
    public void Dispose() => _rows.Dispose();

    private async Task<DescriptorVersion> ApplyAndAppendInTransactionAsync(
        DbConnection connection, DbTransaction transaction, string project, MigrationPlan plan,
        DescriptorVersion candidate, int expectedRevision, CancellationToken ct)
    {
        var current = await _rows.ReadCurrentRevisionAsync(connection, transaction, project, ct).ConfigureAwait(false);
        if (current != expectedRevision)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            throw new DescriptorConcurrencyException(project, expectedRevision, current);
        }

        var appended = candidate with { Revision = expectedRevision + 1 };
        try
        {
            // Version-row insert first (the optimistic-lock gate), then the plan's DDL, then commit —
            // all in this one transaction, so a loser rolls back before touching the schema and a DDL
            // failure rolls the version row back too. See the type remarks for why order matters.
            await _rows.InsertAsync(connection, transaction, project, appended, ct).ConfigureAwait(false);
            await RelationalSqlBatch.ExecuteAsync(connection, plan.Sql, transaction, ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        catch (DbException)
        {
            await _rows.ThrowIfConcurrencyConflictAsync(transaction, project, expectedRevision, ct).ConfigureAwait(false);
            throw;
        }

        return appended;
    }
}
