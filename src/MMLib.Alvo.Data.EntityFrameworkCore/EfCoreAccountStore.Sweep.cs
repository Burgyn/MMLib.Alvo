using MMLib.Alvo.Accounts;

using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// The sweep half of the account store (#527): the bounded deletes the framework sweeper's accounts jobs run. Every
/// threshold is an instant the caller computed and binds, so each statement is one SQL text on both engines, and each call
/// is one short transaction behind the write gate. Nothing here deletes a live row: every predicate names a row that is
/// revoked, expired, idle, rotated or used up.
/// </summary>
internal sealed partial class EfCoreAccountStore
{
    /// <inheritdoc/>
    public Task<int> SweepSessionsAsync(SessionSweep sweep, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sweep);
        var bind = (DbCommand command) => Bind(
            command,
            ("@revoked_before", Instant(sweep.RevokedBefore)),
            ("@expired_before", Instant(sweep.ExpiredBefore)),
            ("@idle_before", Instant(sweep.IdleBefore)),
            ("@born_before", Instant(sweep.BornBefore)),
            ("@limit", sweep.Limit));

        return InTransactionAsync(
            async (connection, transaction, token) =>
            {
                await ExecuteAsync(connection, transaction, _tables.SweepSessionTokensSql, bind, token).ConfigureAwait(false);
                return await ExecuteAsync(connection, transaction, _tables.SweepSessionsSql, bind, token).ConfigureAwait(false);
            },
            _ => true,
            ct);
    }

    /// <inheritdoc/>
    public Task<int> SweepRotatedRefreshTokensAsync(DateTimeOffset rotatedBefore, int limit, CancellationToken ct) =>
        SweptAsync(_tables.SweepRotatedTokensSql, ("@rotated_before", Instant(rotatedBefore)), limit, ct);

    /// <inheritdoc/>
    public Task<int> SweepAppTokensAsync(DateTimeOffset deadBefore, int limit, CancellationToken ct) =>
        SweptAsync(_appTokenTables.SweepDeadSql, ("@dead_before", Instant(deadBefore)), limit, ct);

    /// <inheritdoc/>
    public Task<int> SweepInvitationsAsync(DateTimeOffset deadBefore, int limit, CancellationToken ct) =>
        SweptAsync(_invitationTables.SweepDeadSql, ("@dead_before", Instant(deadBefore)), limit, ct);

    private Task<int> SweptAsync(string sql, (string Name, object? Value) threshold, int limit, CancellationToken ct) =>
        InTransactionAsync(
            (connection, transaction, token) =>
                ExecuteAsync(connection, transaction, sql, command => Bind(command, threshold, ("@limit", limit)), token),
            _ => true,
            ct);
}
