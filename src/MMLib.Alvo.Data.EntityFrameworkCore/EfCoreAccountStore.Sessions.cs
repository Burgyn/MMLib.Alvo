using MMLib.Alvo.Accounts;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;

using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>The session half of the account store: refresh rotation (spec §5.5, D6), sign-out and disabling (§5.6).</summary>
/// <remarks>
/// <para>
/// <b>A refresh never forks the family.</b> The rotation is <c>UPDATE … WHERE rotated_at IS NULL</c>, and only the refresh
/// whose update matched a row inserts a child; the <c>parent_id</c> uniqueness is the backstop. A refresh whose update
/// matched nothing lost to a concurrent one — on PostgreSQL it waited on the winner's row lock, and its re-read sees the
/// committed child — and answers that child as a replay inside the window. On SQLite the write gate serialises the two,
/// so the second reads the rotated token outright.
/// </para>
/// <para>
/// <b>Every answer but a refusal commits.</b> A rotation and a reuse write their own rows; a replay writes only the personal
/// tenant it may ensure (S3 plan D6), and nothing when it ensures none. A refusal rolls back, so a rotation whose session
/// died meanwhile leaves no child.
/// </para>
/// <para>
/// <b>A zero grace window is strict reuse detection</b>: any second presentation revokes the session, even at the
/// rotation's own instant. The window is otherwise inclusive — 10 s after the rotation still answers the child.
/// </para>
/// </remarks>
internal sealed partial class EfCoreAccountStore
{
    private static readonly RefreshOutcome _refused = new(RefreshStatus.Refused, null, null, null, null);

    /// <inheritdoc/>
    public Task<RefreshOutcome> RefreshAsync(RefreshRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        return InTransactionAsync(
            (connection, transaction, token) => RefreshedAsync(connection, transaction, request, token),
            outcome => outcome.Status is not RefreshStatus.Refused,
            ct);
    }

    /// <inheritdoc/>
    public Task<bool> RevokeSessionAsync(Guid session, string reason, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        return InTransactionAsync(
            async (connection, transaction, token) =>
                await RevokeAsync(connection, transaction, session, reason, now, token).ConfigureAwait(false) == 1,
            _ => true,
            ct);
    }

    /// <inheritdoc/>
    public Task<bool> SetDisabledAsync(UserId account, bool disabled, DateTimeOffset now, CancellationToken ct) =>
        InTransactionAsync(
            (connection, transaction, token) => disabled
                ? DisabledAsync(connection, transaction, account, now, token)
                : EnabledAsync(connection, transaction, account, now, token),
            changed => changed,
            ct);

    private async Task<RefreshOutcome> RefreshedAsync(
        DbConnection connection, DbTransaction transaction, RefreshRequest request, CancellationToken ct)
    {
        var outcome = await AnsweredAsync(connection, transaction, request, ct).ConfigureAwait(false);
        if (outcome is { Status: RefreshStatus.Rotated or RefreshStatus.Replayed, Account: { } account })
        {
            await PersonalTenantEnsuredAsync(connection, transaction, account, request.PersonalTenant, request.Now, ct).ConfigureAwait(false);
        }

        return outcome;
    }

    private async Task<RefreshOutcome> AnsweredAsync(
        DbConnection connection, DbTransaction transaction, RefreshRequest request, CancellationToken ct)
    {
        var token = await TokenAsync(connection, transaction, request.TokenId, ct).ConfigureAwait(false);
        if (token is null || !token.IsUsable(request.Now))
        {
            return _refused;
        }

        return token.RotatedAt is null
            ? await RotatedAsync(connection, transaction, token, request, ct).ConfigureAwait(false)
            : await PresentedAgainAsync(connection, transaction, token, request, ct).ConfigureAwait(false);
    }

    private async Task<RefreshOutcome> RotatedAsync(
        DbConnection connection, DbTransaction transaction, TokenRow token, RefreshRequest request, CancellationToken ct)
    {
        if (token.CreatedAt + request.Idle <= request.Now)
        {
            return _refused;
        }

        if (await ExecuteAsync(
            connection, transaction, _tables.RotateSql,
            command => Bind(command, ("@now", Instant(request.Now)), ("@token_id", Id(request.TokenId))), ct).ConfigureAwait(false) != 1)
        {
            return await AfterALostRotationAsync(connection, transaction, request, ct).ConfigureAwait(false);
        }

        await ExecuteAsync(
            connection, transaction, _tables.InsertRefreshTokenSql,
            command => BindRefreshToken(command, request.ChildId, token.Session, request.TokenId, request.Now), ct).ConfigureAwait(false);

        return await SessionTouchedAsync(connection, transaction, token.Session, request.Now, ct).ConfigureAwait(false)
            ? token.Answer(RefreshStatus.Rotated, request.ChildId)
            : _refused;
    }

    private async Task<RefreshOutcome> AfterALostRotationAsync(
        DbConnection connection, DbTransaction transaction, RefreshRequest request, CancellationToken ct)
    {
        var winner = await TokenAsync(connection, transaction, request.TokenId, ct).ConfigureAwait(false);

        return winner is { RotatedAt: not null } && winner.IsUsable(request.Now)
            ? await PresentedAgainAsync(connection, transaction, winner, request, ct).ConfigureAwait(false)
            : _refused;
    }

    private async Task<RefreshOutcome> PresentedAgainAsync(
        DbConnection connection, DbTransaction transaction, TokenRow token, RefreshRequest request, CancellationToken ct)
    {
        if (IsWithinGrace(token.RotatedAt!.Value, request))
        {
            return token.Child is { } child ? token.Answer(RefreshStatus.Replayed, child) : _refused;
        }

        await RevokeAsync(connection, transaction, token.Session, SessionRevocations.Reuse, request.Now, ct).ConfigureAwait(false);
        return new RefreshOutcome(RefreshStatus.Reused, null, null, null, null);
    }

    private static bool IsWithinGrace(DateTimeOffset rotatedAt, RefreshRequest request) =>
        request.Grace > TimeSpan.Zero && request.Now - rotatedAt <= request.Grace;

    private async Task<bool> SessionTouchedAsync(
        DbConnection connection, DbTransaction transaction, Guid session, DateTimeOffset now, CancellationToken ct) =>
        await ExecuteAsync(
            connection, transaction, _tables.TouchSessionSql,
            command => Bind(command, ("@now", Instant(now)), ("@session_id", Id(session))), ct).ConfigureAwait(false) == 1;

    private Task<int> RevokeAsync(
        DbConnection connection, DbTransaction transaction, Guid session, string reason, DateTimeOffset now, CancellationToken ct) =>
        ExecuteAsync(
            connection, transaction, _tables.RevokeSessionSql,
            command => Bind(command, ("@now", Instant(now)), ("@reason", reason), ("@session_id", Id(session))), ct);

    private async Task<bool> DisabledAsync(
        DbConnection connection, DbTransaction transaction, UserId account, DateTimeOffset now, CancellationToken ct)
    {
        if (await ExecuteAsync(
            connection, transaction, _tables.DisableAccountSql,
            command => Bind(command, ("@now", Instant(now)), ("@account_id", Id(account))), ct).ConfigureAwait(false) != 1)
        {
            return false;
        }

        await RevokeAccountSessionsAsync(connection, transaction, account, now, ct).ConfigureAwait(false);
        await ExecuteAsync(
            connection, transaction, _invitationTables.RevokeCreatedByAccountSql,
            command => Bind(command, ("@now", Instant(now)), ("@user_id", Id(account))), ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Clears the stamp of a disabled account, then revokes every session it still has — a clean slate, so a session that
    /// reached the table after the ban's revocation never comes back with the account. The stamp's update takes the
    /// account's row lock first, so a sign-in that waited on it inserts its session after this revocation and keeps it.
    /// An account that is not disabled is left alone: enabling it changes nothing and logs nobody out.
    /// </summary>
    private async Task<bool> EnabledAsync(
        DbConnection connection, DbTransaction transaction, UserId account, DateTimeOffset now, CancellationToken ct)
    {
        if (await ExecuteAsync(
            connection, transaction, _tables.EnableAccountSql,
            command => Bind(command, ("@account_id", Id(account))), ct).ConfigureAwait(false) != 1)
        {
            return await ExistsAsync(connection, transaction, account, ct).ConfigureAwait(false);
        }

        await RevokeAccountSessionsAsync(connection, transaction, account, now, ct).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> ExistsAsync(DbConnection connection, DbTransaction transaction, UserId account, CancellationToken ct) =>
        (await RelationalSqlBatch.ReadRowsAsync(
            connection, transaction, _tables.AccountExistsSql,
            command => Bind(command, ("@account_id", Id(account))), static _ => true, ct).ConfigureAwait(false)).Count is 1;

    /// <summary>Revokes every live session of <paramref name="account"/> as the ban's (<see cref="SessionRevocations.Disabled"/>).</summary>
    private Task<int> RevokeAccountSessionsAsync(
        DbConnection connection, DbTransaction transaction, UserId account, DateTimeOffset now, CancellationToken ct) =>
        ExecuteAsync(
            connection, transaction, _tables.RevokeAccountSessionsSql,
            command => Bind(command, ("@now", Instant(now)), ("@reason", SessionRevocations.Disabled), ("@account_id", Id(account))),
            ct);

    private async Task<TokenRow?> TokenAsync(DbConnection connection, DbTransaction transaction, Guid token, CancellationToken ct)
    {
        var rows = await RelationalSqlBatch.ReadRowsAsync(
            connection, transaction, _tables.RefreshTokenSql,
            command => Bind(command, ("@token_id", Id(token))), ReadToken, ct).ConfigureAwait(false);

        return rows.Count == 0 ? null : rows[0];
    }

    private static TokenRow ReadToken(DbDataReader reader) => new(
        GuidOf(reader.GetString(0)),
        StoredInstant.Of(reader.GetString(1)),
        reader.IsDBNull(2) ? null : StoredInstant.Of(reader.GetString(2)),
        reader.IsDBNull(3) ? null : GuidOf(reader.GetString(3)),
        UserIdOf(reader.GetString(4)),
        reader.GetString(5),
        !reader.IsDBNull(6),
        StoredInstant.Of(reader.GetString(7)),
        !reader.IsDBNull(8));

    /// <summary>A refresh token as a refresh judges it: its own stamps, its child, its session and its account.</summary>
    private sealed record TokenRow(
        Guid Session,
        DateTimeOffset CreatedAt,
        DateTimeOffset? RotatedAt,
        Guid? Child,
        UserId Account,
        string Provider,
        bool SessionRevoked,
        DateTimeOffset SessionExpiresAt,
        bool AccountDisabled)
    {
        internal bool IsUsable(DateTimeOffset now) => !SessionRevoked && !AccountDisabled && SessionExpiresAt > now;

        internal RefreshOutcome Answer(RefreshStatus status, Guid child) => new(status, child, Account, Session, Provider);
    }
}
