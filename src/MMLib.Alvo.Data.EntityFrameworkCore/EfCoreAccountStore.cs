using MMLib.Alvo.Accounts;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Tenants;

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
/// leave a session it never saw. The update takes the account's row lock, so a disable either waits for the sign-in and
/// then revokes its session, or commits first and the sign-in matches no row. Enabling a disabled account revokes every
/// session it still has as well (a clean slate), so a session that ever slipped past a ban still could not come back with
/// the account; this order is what makes the ban's own revocation complete. Enabling an account that is not disabled
/// matches no row, revokes nothing, and still answers that the account exists.
/// </para>
/// <para>
/// <b>The personal tenant is born in the transaction that needs it</b> (S3 plan D6): a sign-in or a refresh that asks for
/// one ensures it with two <c>INSERT … SELECT … ON CONFLICT DO NOTHING</c>s over <see cref="TenantTables"/> — the tenant,
/// unique by <c>personal_owner</c>, then its owner membership — so concurrent first sign-ins converge on one tenant and
/// one membership, and a refusal, which commits nothing, leaves none.
/// </para>
/// <para>
/// <b>An invited sign-in accepts in its own transaction</b> (S4 plan D5, D7): after the session and the personal tenant,
/// <see cref="InvitationAcceptance"/> — the tenant store's own routine — accepts the sign-in's invitation for the account.
/// A dead one answers <see cref="SignInStatus.InvitationRefused"/>, which carries no account, so the transaction rolls back
/// the account, identity, session, refresh token and personal tenant with it: all or nothing, for a new account and an
/// existing one alike.
/// </para>
/// <para>
/// <b>One read answers session and membership</b> (D4): <see cref="ReadCallerAsync"/> is one statement in each of its three
/// shapes, the membership left-joined onto the live session.
/// </para>
/// <para>
/// The tables — the tenant registry's too, which the personal tenant, the invitation and the caller read reach — are
/// created once per process by <see cref="OnceDdl"/> (Ruling 47) for a composition that never booted;
/// <c>SystemSchemaInitializer</c> creates them at boot.
/// </para>
/// </remarks>
internal sealed partial class EfCoreAccountStore : IAccountStore
{
    private readonly RelationalConnectionFactory _connections;
    private readonly AuthTables _tables;
    private readonly TenantTables _tenantTables;
    private readonly InvitationTables _invitationTables;
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
        _tenantTables = new TenantTables(options.SchemaPrefix);
        _invitationTables = new InvitationTables(options.SchemaPrefix);
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
    public async Task<CallerRead> ReadCallerAsync(CallerQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var rows = query.Membership is { } membership
            ? await ReadAsync(
                _tenantTables.CallerSql(_tables, personal: membership.Tenant is null),
                command => BindCaller(command, query),
                reader => reader.IsDBNull(0) ? null : TenantTables.ReadMembership(reader),
                ct).ConfigureAwait(false)
            : await ReadAsync(_tables.LiveSessionSql, command => BindCaller(command, query), _ => (StoredMembership?)null, ct)
                .ConfigureAwait(false);

        return rows.Count == 0 ? new CallerRead(false, null) : new CallerRead(true, rows[0]);
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

        if (!await SessionStartedAsync(connection, transaction, account, request, ct).ConfigureAwait(false))
        {
            return new SignInOutcome(SignInStatus.SubjectTokenReplayed, null);
        }

        await PersonalTenantEnsuredAsync(connection, transaction, account, request.PersonalTenant, request.Now, ct).ConfigureAwait(false);
        if (!await InvitationAcceptedAsync(connection, transaction, account, request, ct).ConfigureAwait(false))
        {
            return new SignInOutcome(SignInStatus.InvitationRefused, null);
        }

        return new SignInOutcome(created ? SignInStatus.Created : SignInStatus.SignedIn, account);
    }

    /// <summary>
    /// Accepts the sign-in's invitation, if it carries one, for <paramref name="account"/> with the tenant store's own
    /// routine (S4 plan D4, D5); <see langword="false"/> when it is dead or the account could not be locked, and then the
    /// caller answers <see cref="SignInStatus.InvitationRefused"/> and nothing commits. The account's email was refreshed
    /// earlier in this transaction, so an email binding is judged by this sign-in's identity.
    /// </summary>
    private async Task<bool> InvitationAcceptedAsync(
        DbConnection connection, DbTransaction transaction, UserId account, SignInRequest request, CancellationToken ct)
    {
        if (request.Invitation is not { } invitation)
        {
            return true;
        }

        var accepted = await InvitationAcceptance.AcceptAsync(
            connection, transaction, _invitationTables, _tenantTables, _tables, invitation.CodeHash, account, request.Now, ct)
            .ConfigureAwait(false);

        return accepted.Status is AcceptInvitationStatus.Joined or AcceptInvitationStatus.AlreadyMember;
    }

    /// <summary>
    /// Ensures <paramref name="account"/>'s personal tenant and its owner membership when <paramref name="personal"/> asks
    /// for one (D6); whichever tenant a concurrent caller committed first stands.
    /// </summary>
    private async Task PersonalTenantEnsuredAsync(
        DbConnection connection,
        DbTransaction transaction,
        UserId account,
        PersonalTenantRequest? personal,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (personal is null)
        {
            return;
        }

        await ExecuteAsync(
            connection, transaction, _tenantTables.EnsurePersonalTenantSql,
            command => Bind(command, ("@tenant_id", Id(personal.Id.Value)), ("@now", Instant(now)), ("@account_id", Id(account))),
            ct).ConfigureAwait(false);
        await ExecuteAsync(
            connection, transaction, _tenantTables.EnsurePersonalMemberSql,
            command => Bind(command, ("@now", Instant(now)), ("@account_id", Id(account))), ct).ConfigureAwait(false);
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

    private static void BindCaller(DbCommand command, CallerQuery query)
    {
        Bind(command, ("@session_id", Id(query.Session)), ("@account_id", Id(query.Account)), ("@now", Instant(query.Now)));
        if (query.Membership is { Tenant: { } tenant })
        {
            Bind(command, ("@tenant_id", Id(tenant.Value)));
        }
    }

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
                await _tenantTables.EnsureAsync(connection, ct).ConfigureAwait(false);
                await _invitationTables.EnsureAsync(connection, ct).ConfigureAwait(false);
            }
        }
    }
}
