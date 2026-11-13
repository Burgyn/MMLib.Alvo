using MMLib.Alvo.Accounts;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;

using System.Data.Common;
using System.Globalization;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// <see cref="IAccountStore"/> over the relational drivers: every write is one transaction on a connection of its own,
/// behind the database's write gate, over <see cref="AuthTables"/>'s SQL — identical on both engines.
/// </summary>
/// <remarks>
/// <para>
/// <b>Atomic conditional writes, never read-then-write.</b> Where two callers can race, the decision is made by the
/// statement that writes: an <c>INSERT … ON CONFLICT DO NOTHING</c> whose row count says who won, or an <c>UPDATE … WHERE</c>
/// whose predicate is the precondition. On PostgreSQL's <c>READ COMMITTED</c> the loser's statement waits on the winner's
/// row lock and re-checks its predicate against the committed row; its next statement sees what the winner committed. On
/// SQLite the write gate and the provider's <c>BEGIN IMMEDIATE</c> serialise the two outright.
/// </para>
/// <para>
/// <b>The disabled check of a sign-in is the profile refresh itself</b> (<c>UPDATE … WHERE disabled_at IS NULL</c>), run
/// before the session is inserted. A deviation in order from the plan's step list, which refreshes the profile last: a
/// read of <c>disabled_at</c> followed by an insert would let an operator's concurrent disable commit between the two and
/// leave a session it never saw — one that an enable would later revive. The update takes the account's row lock, so a
/// disable either waits for the sign-in and then revokes its session, or commits first and the sign-in matches no row.
/// </para>
/// <para>
/// The tables are created once per process by <see cref="OnceDdl"/> (Ruling 47) for a composition that never booted;
/// <c>SystemSchemaInitializer</c> creates them at boot.
/// </para>
/// </remarks>
internal sealed partial class EfCoreAccountStore : IAccountStore
{
    private readonly RelationalConnectionFactory _connections;
    private readonly AuthTables _tables;
    private readonly SecretCipher? _cipher;
    private readonly OnceDdl _ddl;

    /// <summary>Creates the store.</summary>
    /// <param name="connections">The database's connections and write gate.</param>
    /// <param name="options">The options whose schema prefix names the tables.</param>
    /// <param name="cipher">
    /// The deployment's secret cipher, which seals every key's material; <see langword="null"/> when no key file is mounted,
    /// and then every key operation is refused naming the setting.
    /// </param>
    public EfCoreAccountStore(RelationalConnectionFactory connections, AlvoOptions options, SecretCipher? cipher)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(options);
        _connections = connections;
        _tables = new AuthTables(options.SchemaPrefix);
        _cipher = cipher;
        _ddl = new OnceDdl(EnsureTablesAsync);
    }

    /// <inheritdoc/>
    public Task<SignInOutcome> SignInAsync(SignInRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        return InTransactionAsync(
            (connection, transaction, token) => SignedInAsync(connection, transaction, request, token),
            outcome => outcome.Account is not null,
            ct);
    }

    /// <inheritdoc/>
    public async Task<bool> IsSessionLiveAsync(UserId account, Guid session, DateTimeOffset now, CancellationToken ct)
    {
        var counts = await ReadAsync(
            _tables.LiveSessionSql,
            command => Bind(command, ("@session_id", Id(session)), ("@account_id", Id(account)), ("@now", Instant(now))),
            reader => Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
            ct).ConfigureAwait(false);

        return counts.Single() == 1;
    }

    /// <inheritdoc/>
    public async Task<AccountView?> GetAccountAsync(UserId account, CancellationToken ct)
    {
        var bind = (DbCommand command) => Bind(command, ("@account_id", Id(account)));
        var found = await ReadAsync(_tables.AccountSql, bind, ReadAccount, ct).ConfigureAwait(false);
        if (found.Count == 0)
        {
            return null;
        }

        var providers = await ReadAsync(_tables.ProvidersSql, bind, reader => reader.GetString(0), ct).ConfigureAwait(false);
        return found[0] with { Providers = [.. providers.Order(StringComparer.Ordinal)] };
    }

    /// <inheritdoc/>
    public Task<bool> SetDisplayNameAsync(UserId account, string name, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(name);

        return InTransactionAsync(
            async (connection, transaction, token) => await ExecuteAsync(
                connection, transaction, _tables.SetDisplayNameSql,
                command => Bind(command, ("@display_name", name), ("@account_id", Id(account))), token).ConfigureAwait(false) == 1,
            updated => updated,
            ct);
    }

    private async Task<SignInOutcome> SignedInAsync(
        DbConnection connection, DbTransaction transaction, SignInRequest request, CancellationToken ct)
    {
        var found = await AccountOfAsync(connection, transaction, request.Identity, ct).ConfigureAwait(false);
        if (found is null && !request.MayCreate)
        {
            return new SignInOutcome(SignInStatus.SignUpRefused, null);
        }

        var (account, created) = found is { } existing
            ? (existing, false)
            : await CreatedAsync(connection, transaction, request, ct).ConfigureAwait(false);

        if (!await ProfileRefreshedAsync(connection, transaction, account, request, ct).ConfigureAwait(false))
        {
            return new SignInOutcome(SignInStatus.Disabled, null);
        }

        return await SessionStartedAsync(connection, transaction, account, request, ct).ConfigureAwait(false)
            ? new SignInOutcome(created ? SignInStatus.Created : SignInStatus.SignedIn, account)
            : new SignInOutcome(SignInStatus.SubjectTokenReplayed, null);
    }

    private async Task<UserId?> AccountOfAsync(
        DbConnection connection, DbTransaction transaction, VerifiedIdentity identity, CancellationToken ct)
    {
        var found = await RelationalSqlBatch.ReadRowsAsync(
            connection, transaction, _tables.FindIdentitySql,
            command => Bind(command, ("@provider", identity.Provider), ("@subject", identity.Subject)),
            reader => UserIdOf(reader.GetString(0)),
            ct).ConfigureAwait(false);

        return found.Count == 0 ? null : found[0];
    }

    /// <summary>
    /// Claims the identity for a fresh account and inserts the account only when the claim won; a concurrent first sign-in
    /// that won instead is read back, so both converge on its account.
    /// </summary>
    private async Task<(UserId Account, bool Created)> CreatedAsync(
        DbConnection connection, DbTransaction transaction, SignInRequest request, CancellationToken ct)
    {
        var account = UserId.New();
        var identity = request.Identity;
        var claimed = await ExecuteAsync(
            connection, transaction, _tables.InsertIdentitySql,
            command => BindIdentity(command, identity, account, request.Now), ct).ConfigureAwait(false) == 1;

        if (!claimed)
        {
            var winner = await AccountOfAsync(connection, transaction, identity, ct).ConfigureAwait(false);
            return (winner ?? throw new InvalidOperationException("An identity claim lost to a row that is not there."), false);
        }

        await ExecuteAsync(
            connection, transaction, _tables.InsertAccountSql,
            command => BindNewAccount(command, identity, account, request.Now), ct).ConfigureAwait(false);
        return (account, true);
    }

    private async Task<bool> ProfileRefreshedAsync(
        DbConnection connection, DbTransaction transaction, UserId account, SignInRequest request, CancellationToken ct)
    {
        var identity = request.Identity;
        if (await ExecuteAsync(
            connection, transaction, _tables.RefreshProfileSql,
            command => BindProfile(command, identity, account, request.Now), ct).ConfigureAwait(false) != 1)
        {
            return false;
        }

        await ExecuteAsync(
            connection, transaction, _tables.RefreshIdentitySql,
            command => BindIdentity(command, identity, account, request.Now), ct).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> SessionStartedAsync(
        DbConnection connection, DbTransaction transaction, UserId account, SignInRequest request, CancellationToken ct)
    {
        if (await ExecuteAsync(
            connection, transaction, _tables.InsertSessionSql,
            command => BindSession(command, account, request), ct).ConfigureAwait(false) != 1)
        {
            return false;
        }

        await ExecuteAsync(
            connection, transaction, _tables.InsertRefreshTokenSql,
            command => BindRefreshToken(command, request.RefreshTokenId, request.SessionId, parent: null, request.Now),
            ct).ConfigureAwait(false);
        return true;
    }

    private static void BindIdentity(DbCommand command, VerifiedIdentity identity, UserId account, DateTimeOffset now) => Bind(
        command,
        ("@provider", identity.Provider),
        ("@subject", identity.Subject),
        ("@account_id", Id(account)),
        ("@email", identity.Email),
        ("@email_trusted", Flag(identity.EmailTrusted)),
        ("@now", Instant(now)));

    private static void BindNewAccount(DbCommand command, VerifiedIdentity identity, UserId account, DateTimeOffset now) => Bind(
        command,
        ("@account_id", Id(account)),
        ("@email", identity.Email),
        ("@email_trusted", Flag(identity.EmailTrusted)),
        ("@display_name", identity.Name),
        ("@avatar_url", identity.Picture),
        ("@now", Instant(now)));

    private static void BindProfile(DbCommand command, VerifiedIdentity identity, UserId account, DateTimeOffset now) => Bind(
        command,
        ("@email", identity.Email),
        ("@email_trusted", Flag(identity.EmailTrusted)),
        ("@avatar_url", identity.Picture),
        ("@now", Instant(now)),
        ("@account_id", Id(account)));

    private static void BindSession(DbCommand command, UserId account, SignInRequest request) => Bind(
        command,
        ("@session_id", Id(request.SessionId)),
        ("@account_id", Id(account)),
        ("@provider", request.Identity.Provider),
        ("@subject_token_id", request.SubjectTokenId),
        ("@now", Instant(request.Now)),
        ("@expires_at", Instant(request.SessionExpiresAt)));

    private static void BindRefreshToken(DbCommand command, Guid token, Guid session, Guid? parent, DateTimeOffset now) => Bind(
        command,
        ("@token_id", Id(token)),
        ("@session_id", Id(session)),
        ("@parent_id", parent is { } id ? Id(id) : null),
        ("@now", Instant(now)));

    private static AccountView ReadAccount(DbDataReader reader) => new(
        UserIdOf(reader.GetString(0)),
        TextOrNull(reader, 1),
        IsSet(reader, 2),
        TextOrNull(reader, 3),
        TextOrNull(reader, 4),
        [],
        StoredInstant.Of(reader.GetString(5)));

    private static void Bind(DbCommand command, params (string Name, object? Value)[] parameters)
    {
        foreach (var (name, value) in parameters)
        {
            RelationalSqlBatch.AddParameter(command, name, value ?? DBNull.Value);
        }
    }

    private static string Id(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);

    private static string Id(UserId id) => Id(id.Value);

    private static UserId UserIdOf(string text) => new(GuidOf(text));

    private static Guid GuidOf(string text) => Guid.Parse(text, CultureInfo.InvariantCulture);

    private static string Instant(DateTimeOffset instant) => StoredInstant.Text(instant.ToUniversalTime());

    private static int Flag(bool value) => value ? 1 : 0;

    private static bool IsSet(DbDataReader reader, int ordinal) =>
        Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture) != 0;

    private static string? TextOrNull(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static async Task<int> ExecuteAsync(
        DbConnection connection, DbTransaction transaction, string sql, Action<DbCommand> bind, CancellationToken ct)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.Transaction = transaction;
            command.CommandText = sql;
            bind(command);

            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One transaction behind the database's write gate, committed only when <paramref name="commits"/> says so — otherwise
    /// rolled back on dispose, so a refusal leaves nothing behind.
    /// </summary>
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

    private async Task<List<T>> ReadAsync<T>(string sql, Action<DbCommand> bind, Func<DbDataReader, T> read, CancellationToken ct)
    {
        var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            return await RelationalSqlBatch.ReadRowsAsync(connection, transaction: null, sql, bind, read, ct).ConfigureAwait(false);
        }
    }

    private async Task<DbConnection> OpenAsync(CancellationToken ct)
    {
        await _ddl.EnsureAsync(ct).ConfigureAwait(false);
        var connection = _connections.Create();
        await RelationalSqlBatch.OpenAsync(connection, ct).ConfigureAwait(false);
        return connection;
    }

    private async Task EnsureTablesAsync(CancellationToken ct)
    {
        var connection = _connections.Create();
        await using (connection.ConfigureAwait(false))
        {
            await RelationalSqlBatch.OpenAsync(connection, ct).ConfigureAwait(false);
            using (await _connections.WriteGate.EnterAsync(ct).ConfigureAwait(false))
            {
                await _tables.EnsureAsync(connection, ct).ConfigureAwait(false);
            }
        }
    }
}
