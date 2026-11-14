using MMLib.Alvo.Auth;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Tenants;

using System.Data.Common;
using System.Globalization;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// <see cref="IAppTokenStore"/> over the relational drivers: every write is one statement or one transaction on a connection
/// of its own, behind the database's write gate, over <see cref="AppTokenTables"/>' SQL — identical on both engines.
/// </summary>
/// <remarks>
/// <para>
/// <b>A mint locks before it counts or reads</b> (S5 plan D10, the S3 D3 order): the minter's account row first
/// (<see cref="TenantTables.LockAccountSql"/>, a write that changes nothing — zero rows is a disabled or unknown account),
/// so two mints of one person are serialised and the live cap holds; then, for a token with a tenant, the tenant's
/// <c>version</c> is bumped (<see cref="TenantTables.BumpVersionSql"/>) before the minter's membership is read, so a mint
/// racing a removal, a demotion or a ban either sees it or is swept by it. On SQLite the write gate and the provider's
/// <c>BEGIN IMMEDIATE</c> serialise them outright.
/// </para>
/// <para>
/// <b>Revoking is one conditional write</b>: who may revoke is in its <c>WHERE</c>, so an unknown token and one the actor may
/// not touch are one answer, and a repeat keeps the first instant.
/// </para>
/// <para>
/// The tables are created once per process by <see cref="OnceDdl"/> for a composition that never booted — the account and
/// tenant tables too, which the locks and the list read; <c>SystemSchemaInitializer</c> creates them all at boot.
/// </para>
/// </remarks>
internal sealed class EfCoreAppTokenStore : IAppTokenStore
{
    private readonly RelationalConnectionFactory _connections;
    private readonly AppTokenTables _tables;
    private readonly TenantTables _tenantTables;
    private readonly AuthTables _authTables;
    private readonly OnceDdl _ddl;

    /// <summary>Creates the store.</summary>
    /// <param name="connections">The database's connections and write gate.</param>
    /// <param name="options">The options whose schema prefix names the tables.</param>
    public EfCoreAppTokenStore(RelationalConnectionFactory connections, AlvoOptions options)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(options);
        _connections = connections;
        _tables = new AppTokenTables(options.SchemaPrefix);
        _tenantTables = new TenantTables(options.SchemaPrefix);
        _authTables = new AuthTables(options.SchemaPrefix);
        _ddl = new OnceDdl(EnsureTablesAsync);
    }

    /// <inheritdoc/>
    public async ValueTask<ApiKeyRecord?> FindAsync(string keyId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(keyId);

        var found = await ReadAsync(_tables.FindSql, command => Bind(command, ("@key_id", keyId)), AppTokenTables.ReadRecord, ct)
            .ConfigureAwait(false);
        return found.Count == 0 ? null : found[0];
    }

    /// <inheritdoc/>
    public async ValueTask TouchAsync(string keyId, DateTimeOffset usedAt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(keyId);

        await InTransactionAsync(
            (connection, transaction, token) => ExecuteAsync(
                connection, transaction, _tables.TouchSql,
                command => Bind(command, ("@used_at", Instant(usedAt)), ("@key_id", keyId)), token),
            _ => true,
            ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task<MintTokenOutcome> MintAsync(MintTokenRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Kind is not (ApiKeyKind.Personal or ApiKeyKind.Service) || (request.Kind == ApiKeyKind.Service && request.Tenant is null))
        {
            throw new ArgumentException("Only a personal token, or a service token with a tenant, is minted.", nameof(request));
        }

        return InTransactionAsync(
            (connection, transaction, token) => MintedAsync(connection, transaction, request, token),
            outcome => outcome.Status == MintTokenStatus.Minted,
            ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<AppTokenView>> ListAsync(UserId person, CancellationToken ct) =>
        await ReadAsync(_tables.ListSql, command => Bind(command, ("@person", Id(person.Value))), AppTokenTables.ReadView, ct)
            .ConfigureAwait(false);

    /// <inheritdoc/>
    public async Task<RevokeTokenStatus> RevokeAsync(TokenRevocation revocation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(revocation);

        var revoked = await InTransactionAsync(
            (connection, transaction, token) => ExecuteAsync(
                connection, transaction, _tables.RevokeSql,
                command => Bind(
                    command,
                    ("@now", Instant(revocation.Now)),
                    ("@key_id", revocation.KeyId),
                    ("@actor", Id(revocation.Actor.Value))),
                token),
            count => count > 0,
            ct).ConfigureAwait(false);

        return revoked == 0 ? RevokeTokenStatus.NotFound : RevokeTokenStatus.Revoked;
    }

    private async Task<MintTokenOutcome> MintedAsync(
        DbConnection connection, DbTransaction transaction, MintTokenRequest request, CancellationToken ct)
    {
        if (await RefusedAsync(connection, transaction, request, ct).ConfigureAwait(false) is { } refused)
        {
            return new MintTokenOutcome(refused, null);
        }

        await ExecuteAsync(connection, transaction, _tables.InsertSql, command => BindToken(command, request), ct).ConfigureAwait(false);
        var view = await RelationalSqlBatch.ReadRowsAsync(
            connection, transaction, _tables.ViewSql, command => Bind(command, ("@key_id", request.KeyId)), AppTokenTables.ReadView, ct)
            .ConfigureAwait(false);
        return new MintTokenOutcome(MintTokenStatus.Minted, view.Single());
    }

    /// <summary>
    /// The refusal of a mint, judged in the lock order (D10): the minter's account, the live cap, then — for a token with a
    /// tenant — the tenant's lock and the minter's membership; <see langword="null"/> to insert.
    /// </summary>
    private async Task<MintTokenStatus?> RefusedAsync(
        DbConnection connection, DbTransaction transaction, MintTokenRequest request, CancellationToken ct)
    {
        var minter = Id(request.Minter.Value);
        if (await ExecuteAsync(
            connection, transaction, _tenantTables.LockAccountSql, command => Bind(command, ("@account_id", minter)), ct)
            .ConfigureAwait(false) != 1)
        {
            return MintTokenStatus.NoAccount;
        }

        if (await CountAsync(
            connection, transaction, _tables.LiveCountSql,
            command => Bind(command, ("@minted_by", minter), ("@now", Instant(request.Now))), ct).ConfigureAwait(false) >= request.MaxLive)
        {
            return MintTokenStatus.LimitReached;
        }

        return request.Tenant is { } tenant
            ? await TenantRefusalAsync(connection, transaction, request, tenant, ct).ConfigureAwait(false)
            : null;
    }

    /// <summary>
    /// The refusal of a mint into <paramref name="tenant"/>: no such tenant or no membership, and for a service token a
    /// personal tenant, a minter who is no owner, or a role the minter does not hold; <see langword="null"/> to insert.
    /// </summary>
    private async Task<MintTokenStatus?> TenantRefusalAsync(
        DbConnection connection, DbTransaction transaction, MintTokenRequest request, TenantId tenant, CancellationToken ct)
    {
        var tenantId = Id(tenant.Value);
        if (await ExecuteAsync(
            connection, transaction, _tenantTables.BumpVersionSql, command => Bind(command, ("@tenant_id", tenantId)), ct)
            .ConfigureAwait(false) != 1)
        {
            return MintTokenStatus.NotFound;
        }

        var memberships = await RelationalSqlBatch.ReadRowsAsync(
            connection, transaction, _tenantTables.MembershipSql,
            command => Bind(command, ("@user_id", Id(request.Minter.Value)), ("@tenant_id", tenantId)),
            reader => TenantTables.ReadMembership(reader),
            ct).ConfigureAwait(false);

        return memberships.Count == 0 ? MintTokenStatus.NotFound
            : request.Kind == ApiKeyKind.Service ? ServiceRefusal(memberships[0], request.Roles)
            : null;
    }

    private static MintTokenStatus? ServiceRefusal(StoredMembership minter, IReadOnlyList<string> roles) =>
        minter.Kind == TenantKind.Personal ? MintTokenStatus.PersonalTenant
        : !minter.IsOwner ? MintTokenStatus.NotOwner
        : roles.All(role => minter.Roles.Contains(role, StringComparer.Ordinal)) ? null
        : MintTokenStatus.RolesNotHeld;

    private static void BindToken(DbCommand command, MintTokenRequest request) => Bind(
        command,
        ("@key_id", request.KeyId),
        ("@kind", AppTokenTables.KindText(request.Kind)),
        ("@secret_hash", request.SecretHash),
        ("@name", request.Name),
        ("@user_id", Id(request.Actor.Value)),
        ("@minted_by", Id(request.Minter.Value)),
        ("@tenant_id", request.Tenant is { } tenant ? Id(tenant.Value) : null),
        ("@roles", TenantTables.RolesText(request.Roles)),
        ("@scopes", TenantTables.RolesText(request.Scopes)),
        ("@now", Instant(request.Now)),
        ("@expires_at", Instant(request.ExpiresAt)));

    private static void Bind(DbCommand command, params (string Name, object? Value)[] parameters)
    {
        foreach (var (name, value) in parameters)
        {
            RelationalSqlBatch.AddParameter(command, name, value ?? DBNull.Value);
        }
    }

    private static string Id(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);

    private static string Instant(DateTimeOffset instant) => StoredInstant.Text(instant.ToUniversalTime());

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

    private static async Task<long> CountAsync(
        DbConnection connection, DbTransaction transaction, string sql, Action<DbCommand> bind, CancellationToken ct)
    {
        var counts = await RelationalSqlBatch.ReadRowsAsync(
            connection, transaction, sql, bind, reader => Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture), ct)
            .ConfigureAwait(false);

        return counts.Single();
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
                await _authTables.EnsureAsync(connection, ct).ConfigureAwait(false);
                await _tenantTables.EnsureAsync(connection, ct).ConfigureAwait(false);
                await _tables.EnsureAsync(connection, ct).ConfigureAwait(false);
            }
        }
    }
}
