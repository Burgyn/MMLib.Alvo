using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary><see cref="IInboundWebhookLedger"/> over the relational drivers, autocommit behind the write gate, fenced per claim.</summary>
internal sealed class EfCoreInboundWebhookLedger : IInboundWebhookLedger
{
    private readonly RelationalConnectionFactory _connections;
    private readonly IAlvoSqlDialect _dialect;
    private readonly string _table;
    private readonly OnceDdl _ddl;

    public EfCoreInboundWebhookLedger(RelationalConnectionFactory connections, IAlvoSqlDialect dialect, AlvoOptions options)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(options);
        _connections = connections;
        _dialect = dialect;
        _table = InboundWebhookTable.NameFor(options.SchemaPrefix);
        _ddl = new OnceDdl(EnsureTableAsync);
    }

    /// <inheritdoc/>
    public async Task<WebhookClaimResult> ClaimAsync(
        string receiver, string webhookId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        using (await _connections.WriteGate.EnterAsync(cancellationToken).ConfigureAwait(false))
        {
            var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var claim = await ClaimedAsync(connection, transaction, receiver, webhookId, now, cancellationToken).ConfigureAwait(false);
                if (claim.Token is not null)
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }

                return claim;
            }
        }
    }

    /// <inheritdoc/>
    public async Task<bool> CompleteAsync(
        string receiver, string webhookId, string token, DateTimeOffset now, CancellationToken cancellationToken) =>
        await AffectedAsync(
            connection => InboundWebhookTable.CompleteAsync(connection, _table, receiver, webhookId, token, now, cancellationToken),
            cancellationToken).ConfigureAwait(false) == 1;

    /// <inheritdoc/>
    public Task ReleaseAsync(string receiver, string webhookId, string token, CancellationToken cancellationToken) =>
        AffectedAsync(
            connection => InboundWebhookTable.ReleaseAsync(connection, _table, receiver, webhookId, token, cancellationToken),
            cancellationToken);

    /// <inheritdoc/>
    public Task<int> SweepAsync(DateTimeOffset receivedBefore, CancellationToken cancellationToken) =>
        AffectedAsync(connection => InboundWebhookTable.SweepAsync(connection, _table, receivedBefore, cancellationToken), cancellationToken);

    private async Task<WebhookClaimResult> ClaimedAsync(
        DbConnection connection, DbTransaction transaction, string receiver, string id, DateTimeOffset now, CancellationToken ct)
    {
        var token = Guid.NewGuid().ToString("N");
        switch (await InboundWebhookTable.FindAsync(connection, transaction, _table, receiver, id, now, ct).ConfigureAwait(false))
        {
            case null:
                return await InsertedAsync(connection, transaction, receiver, id, token, now, ct).ConfigureAwait(false);
            case WebhookClaim.Claimed:
                return await InboundWebhookTable.ReclaimAsync(connection, transaction, _table, receiver, id, token, now, ct).ConfigureAwait(false) == 1
                    ? new WebhookClaimResult(WebhookClaim.Claimed, token)
                    : new WebhookClaimResult(WebhookClaim.InFlight);
            case var found:
                return new WebhookClaimResult(found.Value);
        }
    }

    private async Task<WebhookClaimResult> InsertedAsync(
        DbConnection connection, DbTransaction transaction, string receiver, string id, string token, DateTimeOffset now,
        CancellationToken ct)
    {
        try
        {
            await InboundWebhookTable.InsertAsync(connection, transaction, _table, receiver, id, token, now, ct).ConfigureAwait(false);
            return new WebhookClaimResult(WebhookClaim.Claimed, token);
        }
        catch (DbException failure) when (_dialect.DecodeConstraintViolation(failure) is { Kind: AlvoConstraintKind.Unique })
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            return new WebhookClaimResult(WebhookClaim.InFlight);
        }
    }

    private async Task<int> AffectedAsync(Func<DbConnection, Task<int>> work, CancellationToken ct)
    {
        var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        using (await _connections.WriteGate.EnterAsync(ct).ConfigureAwait(false))
        {
            return await work(connection).ConfigureAwait(false);
        }
    }

    private async Task<DbConnection> OpenAsync(CancellationToken ct)
    {
        await _ddl.EnsureAsync(ct).ConfigureAwait(false);
        var connection = _connections.Create();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        return connection;
    }

    private async Task EnsureTableAsync(CancellationToken ct)
    {
        var connection = _connections.Create();
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await InboundWebhookTable.EnsureAsync(connection, _table, ct).ConfigureAwait(false);
        }
    }
}
