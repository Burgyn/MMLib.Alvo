using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Events;
using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// <see cref="ICommandLedger"/> over the relational drivers: every operation is autocommit on its own connection, behind
/// the database's write gate (which refuses inside an open unit, HX-07 Ruling 33).
/// </summary>
/// <remarks>
/// A claim is a read, then an insert or a guarded update, in one transaction that commits only when it claimed; each grant
/// mints a fresh fencing token (Ruling 36). Two concurrent first claims both read nothing; the primary key lets one insert
/// and fails the other with a unique violation — and only that violation, decoded by the dialect, is read as
/// <see cref="CommandClaimState.InFlight"/> after rolling its transaction back (any other failure surfaces, Ruling 46). The
/// table is created once per process by <see cref="OnceDdl"/> (Ruling 47), and a key is swept a day after its claim
/// (Ruling 41).
/// </remarks>
internal sealed class EfCoreCommandLedger : ICommandLedger
{
    private readonly RelationalConnectionFactory _connections;
    private readonly IAlvoSqlDialect _dialect;
    private readonly string _table;
    private readonly string _outbox;
    private readonly OnceDdl _ddl;

    public EfCoreCommandLedger(RelationalConnectionFactory connections, IAlvoSqlDialect dialect, AlvoOptions options)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(options);
        _connections = connections;
        _dialect = dialect;
        _table = CommandLedgerTable.NameFor(options.SchemaPrefix);
        _outbox = OutboxTable.NameFor(options.SchemaPrefix);
        _ddl = new OnceDdl(EnsureTablesAsync);
    }

    /// <inheritdoc/>
    public Task<CommandClaim> ClaimAsync(CommandKey key, DateTimeOffset now, CancellationToken cancellationToken) =>
        InTransactionAsync(
            (connection, transaction, ct) => ClaimedAsync(connection, transaction, key, now, ct),
            claim => claim.Token is not null,
            cancellationToken);

    /// <inheritdoc/>
    public Task AnswerAsync(CommandKey key, CommandAnswer answer, CancellationToken cancellationToken) =>
        InTransactionAsync(
            (connection, transaction, ct) => CommandLedgerTable.AnswerAsync(connection, transaction, _table, key, answer, ct),
            _ => true,
            cancellationToken);

    /// <inheritdoc/>
    public async Task ReleaseAsync(CommandKey key, CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        using (await _connections.WriteGate.EnterAsync(cancellationToken).ConfigureAwait(false))
        {
            await CommandLedgerTable.ReleaseAsync(connection, _table, key, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public Task CompleteAsync(
        AlvoEvent completion, CommandKey? key, CommandAnswer? answer, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(completion);
        if (key is not null)
        {
            CommandLedgerTable.Claimed(key);
        }

        return InTransactionAsync(
            async (connection, transaction, ct) =>
            {
                await OutboxTable.InsertAsync(connection, transaction, _outbox, completion, ct).ConfigureAwait(false);
                await CommittedAsync(connection, transaction, key, answer, now, ct).ConfigureAwait(false);
                return true;
            },
            _ => true,
            cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<int> SweepAsync(DateTimeOffset claimedBefore, CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        using (await _connections.WriteGate.EnterAsync(cancellationToken).ConfigureAwait(false))
        {
            return await CommandLedgerTable.SweepAsync(connection, _table, claimedBefore, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CommittedAsync(
        DbConnection connection, DbTransaction transaction, CommandKey? key, CommandAnswer? answer, DateTimeOffset now,
        CancellationToken ct)
    {
        if (key is null)
        {
            return;
        }

        if (await CommandLedgerTable.CommitAsync(connection, transaction, _table, key, now, ct).ConfigureAwait(false) != 1)
        {
            throw new CommandClaimLostException();
        }

        if (answer is not null)
        {
            await CommandLedgerTable.AnswerAsync(connection, transaction, _table, key, answer, ct).ConfigureAwait(false);
        }
    }

    private async Task<CommandClaim> ClaimedAsync(
        DbConnection connection, DbTransaction transaction, CommandKey key, DateTimeOffset now, CancellationToken ct)
    {
        var found = await CommandLedgerTable.FindAsync(connection, transaction, _table, key, now, ct).ConfigureAwait(false);
        var token = Guid.NewGuid().ToString("N");

        return found is null ? await InsertedAsync(connection, transaction, key, token, now, ct).ConfigureAwait(false)
            : found.State == CommandClaimState.Claimed ? await ReclaimedAsync(connection, transaction, key, token, now, ct).ConfigureAwait(false)
            : found;
    }

    private async Task<CommandClaim> InsertedAsync(
        DbConnection connection, DbTransaction transaction, CommandKey key, string token, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            await CommandLedgerTable.InsertClaimAsync(connection, transaction, _table, key, token, now, ct).ConfigureAwait(false);
            return CommandClaim.Granted(token);
        }
        catch (DbException failure) when (_dialect.DecodeConstraintViolation(failure) is { Kind: AlvoConstraintKind.Unique })
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            return CommandClaim.InFlight;
        }
    }

    private async Task<CommandClaim> ReclaimedAsync(
        DbConnection connection, DbTransaction transaction, CommandKey key, string token, DateTimeOffset now, CancellationToken ct) =>
        await CommandLedgerTable.ReclaimAsync(connection, transaction, _table, key, token, now, ct).ConfigureAwait(false) == 1
            ? CommandClaim.Granted(token)
            : CommandClaim.InFlight;

    private async Task<T> InTransactionAsync<T>(
        Func<DbConnection, DbTransaction, CancellationToken, Task<T>> work, Func<T, bool> commits, CancellationToken ct)
    {
        var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        using (await _connections.WriteGate.EnterAsync(ct).ConfigureAwait(false))
        {
            var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var result = await work(connection, transaction, ct).ConfigureAwait(false);
                if (commits(result))
                {
                    await transaction.CommitAsync(ct).ConfigureAwait(false);
                }

                return result;
            }
        }
    }

    private async Task<DbConnection> OpenAsync(CancellationToken ct)
    {
        await _ddl.EnsureAsync(ct).ConfigureAwait(false);
        var connection = _connections.Create();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        return connection;
    }

    /// <summary>The ledger's table and the outbox a unitless completion writes to — both, once (Ruling 47).</summary>
    /// <remarks>
    /// The outbox too: a run that opened no unit appends its completion here, on a connection of the ledger's own, and a
    /// database no write or boot has reached yet has no outbox. Inside the write gate, as <c>EfCoreOutboxStore</c> runs its
    /// own DDL, so it never contends with a gated writer.
    /// </remarks>
    private async Task EnsureTablesAsync(CancellationToken ct)
    {
        var connection = _connections.Create();
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            using (await _connections.WriteGate.EnterAsync(ct).ConfigureAwait(false))
            {
                await CommandLedgerTable.EnsureAsync(connection, _table, ct).ConfigureAwait(false);
                await OutboxTable.EnsureAsync(connection, _outbox, ct).ConfigureAwait(false);
            }
        }
    }
}
