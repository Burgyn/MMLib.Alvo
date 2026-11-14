using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Accounts;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Tests.Ef;
using Shouldly;
using System.Data.Common;
using System.Globalization;
using Xunit;

#pragma warning disable EF1001

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// PostgreSQL's leg of <see cref="AccountStoreFacts"/> — the leg where two concurrent sign-ins, refreshes and first key
/// inserts really overlap in the engine, since no in-process write gate serialises them — plus the races only a row lock
/// can stage deterministically.
/// </summary>
/// <remarks>
/// <b>The race facts hold a row lock with an uncommitted transaction</b>, start the store call, wait until PostgreSQL
/// reports it waiting on that lock, and only then commit. So the store's read has certainly seen the row as it was, and
/// only a decision taken inside the writing statement (the <c>WHERE</c> PostgreSQL re-checks after the lock is released)
/// can notice the change. A read-then-write implementation passes every sequential fact and fails these.
/// </remarks>
public sealed class PostgreSqlAccountStoreTests : AccountStoreFacts, IAsyncLifetime
{
    private static readonly TimeSpan _lockWait = TimeSpan.FromSeconds(30);

    private readonly PostgreSqlAlvoDataFixture _fixture = new();

    /// <inheritdoc/>
    public ValueTask InitializeAsync() => _fixture.InitializeAsync();

    [Theory]
    [InlineData("a sign-out")]
    [InlineData("a disable")]
    public async Task A_refresh_racing_a_revocation_commits_no_child(string revocation)
    {
        var (store, services) = await StoreAsync();
        var (account, session) = await SignedInAsync(store);
        await using var holder = await HoldAsync(services, Revocation(services, revocation, account, session.SessionId));

        var refresh = store.RefreshAsync(Refresh(session.RefreshTokenId, Start.AddMinutes(1)), Ct);
        await WaitingOnALockAsync(services);
        await holder.CommitAsync(Ct);

        (await refresh).Status.ShouldBe(RefreshStatus.Refused);
        (await ChildrenAsync(services, session.RefreshTokenId)).ShouldBe(0);
        (await ScalarAsync(services, $"SELECT rotated_at FROM {TableOf(services, AlvoFrameworkTables.AuthRefreshTokensSuffix)} WHERE id = '{session.RefreshTokenId:D}'"))
            .ShouldBe(DBNull.Value, "the rotation rolled back with the refused touch");
    }

    [Fact]
    public async Task A_sign_in_racing_a_disable_starts_no_session()
    {
        var (store, services) = await StoreAsync();
        var (account, session) = await SignedInAsync(store);
        await using var holder = await HoldAsync(services, Revocation(services, "a disable", account, session.SessionId));

        var signIn = store.SignInAsync(SignIn(session.Identity), Ct);
        await WaitingOnALockAsync(services);
        await holder.CommitAsync(Ct);

        (await signIn).ShouldBe(new SignInOutcome(SignInStatus.Disabled, null));
        Convert.ToInt64(
            await ScalarAsync(services, $"SELECT COUNT(*) FROM {TableOf(services, AlvoFrameworkTables.AuthSessionsSuffix)} WHERE account_id = '{account.Value:D}'"),
            CultureInfo.InvariantCulture).ShouldBe(1, "only the session that existed before the disable");
    }

    /// <summary>
    /// A sign-in racing a ban, held open across it, leaves no session an enable brings back. Holding the account's row
    /// lock (the store's own sign-in takes it with its profile refresh), the ban waits for the sign-in and revokes its
    /// session. Inserting the session alone takes no lock the ban meets, so it commits after the ban's revocation and
    /// the ban never sees it: only the enable's own revocation keeps it dead.
    /// </summary>
    [Theory]
    [InlineData("holding the account's row lock", true)]
    [InlineData("committing after the ban's revocation", false)]
    public async Task A_sign_in_racing_a_ban_leaves_no_session_an_enable_revives(string race, bool banWaits)
    {
        var (store, services) = await StoreAsync();
        var (account, session) = await SignedInAsync(store);
        var late = Guid.NewGuid();
        await using var holder = await HoldAsync(services, LateSignIn(services, account, session.SessionId, late, banWaits));

        var ban = store.SetDisabledAsync(account, disabled: true, Start.AddMinutes(1), Ct);
        if (banWaits)
        {
            await WaitingOnALockAsync(services);
        }
        else
        {
            (await ban).ShouldBeTrue(race);
        }

        await holder.CommitAsync(Ct);
        (await ban).ShouldBeTrue(race);
        (await RevokedReasonAsync(services, late) is not null).ShouldBe(banWaits, race);
        (await store.SetDisabledAsync(account, disabled: false, Start.AddMinutes(2), Ct)).ShouldBeTrue();

        (await IsLiveAsync(store, account, late, Start.AddMinutes(3))).ShouldBeFalse(race);
        (await IsLiveAsync(store, account, session.SessionId, Start.AddMinutes(3))).ShouldBeFalse(race);
    }

    /// <inheritdoc/>
    protected override async Task<IServiceProvider> StartAsync(Action<IServiceCollection>? configure) =>
        (await _fixture.StartAsync(new SchemaModel([]), configure: configure)).Services;

    /// <inheritdoc/>
    protected override ValueTask DisposeFixtureAsync() => _fixture.DisposeAsync();

    /// <summary>The statements an operator's revocation runs, as the store spells them, with the row's own stamp as the instant.</summary>
    private static string[] Revocation(IServiceProvider services, string revocation, UserId account, Guid session)
    {
        var sessions = TableOf(services, AlvoFrameworkTables.AuthSessionsSuffix);
        return revocation == "a sign-out"
            ? [$"UPDATE {sessions} SET revoked_at = created_at, revoked_reason = 'logout' WHERE id = '{session:D}'"]
            :
            [
                $"UPDATE {TableOf(services, AlvoFrameworkTables.AuthAccountsSuffix)} SET disabled_at = created_at WHERE id = '{account.Value:D}'",
                $"UPDATE {sessions} SET revoked_at = created_at, revoked_reason = 'disabled' WHERE account_id = '{account.Value:D}' AND revoked_at IS NULL",
            ];
    }

    /// <summary>
    /// A sign-in of <paramref name="account"/> starting session <paramref name="late"/>: with <paramref name="locksAccount"/>,
    /// first the profile refresh's row lock on the account, as the store takes it; otherwise the session insert alone.
    /// </summary>
    private static string[] LateSignIn(IServiceProvider services, UserId account, Guid session, Guid late, bool locksAccount)
    {
        var insert = LateSessionSql(services, session, late);
        return locksAccount
            ?
            [
                $"UPDATE {TableOf(services, AlvoFrameworkTables.AuthAccountsSuffix)} SET last_sign_in_at = created_at WHERE id = '{account.Value:D}' AND disabled_at IS NULL",
                insert,
            ]
            : [insert];
    }

    /// <summary>Runs <paramref name="statements"/> in a transaction left open, so their row locks are held.</summary>
    private static async Task<HeldTransaction> HoldAsync(IServiceProvider services, string[] statements)
    {
        var connection = services.GetRequiredService<RelationalConnectionFactory>().Create();
        await connection.OpenAsync(Ct);
        var transaction = await connection.BeginTransactionAsync(Ct);
        foreach (var sql in statements)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            (await command.ExecuteNonQueryAsync(Ct)).ShouldBeGreaterThan(0, sql);
        }

        return new HeldTransaction(connection, transaction);
    }

    /// <summary>Waits until a backend of this database is waiting on a lock — the store call has read and is blocked.</summary>
    private static async Task WaitingOnALockAsync(IServiceProvider services)
    {
        const string Waiting = """
            SELECT COUNT(*) FROM pg_locks l JOIN pg_stat_activity a ON a.pid = l.pid
             WHERE NOT l.granted AND a.datname = current_database()
            """;
        var deadline = DateTimeOffset.UtcNow + _lockWait;
        while (Convert.ToInt64(await ScalarAsync(services, Waiting), CultureInfo.InvariantCulture) == 0)
        {
            DateTimeOffset.UtcNow.ShouldBeLessThan(deadline, "the store call never waited on the held row lock");
            await Task.Delay(TimeSpan.FromMilliseconds(20), Ct);
        }
    }

    /// <summary>An open transaction and its connection, committed by the fact and disposed with it.</summary>
    private sealed class HeldTransaction(DbConnection connection, DbTransaction transaction) : IAsyncDisposable
    {
        public Task CommitAsync(CancellationToken ct) => transaction.CommitAsync(ct);

        public async ValueTask DisposeAsync()
        {
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
