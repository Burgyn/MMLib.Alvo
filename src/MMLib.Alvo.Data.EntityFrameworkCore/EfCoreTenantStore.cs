using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Tenants;

using System.Data.Common;
using System.Globalization;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// <see cref="ITenantStore"/> over the relational drivers: every write is one transaction on a connection of its own,
/// behind the database's write gate, over <see cref="TenantTables"/>' SQL — identical on both engines.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lock first, then read, then judge</b> (S3 plan D3). A decision that counts rows — the creator's cap, the tenant's
/// last owner — cannot be taken by one conditional write the way <see cref="EfCoreAccountStore"/>'s are, so every such
/// write first takes one row lock with a write: the creator's account row for a creation, the tenant's own row (its
/// <c>version</c> bumped) for a membership write. On PostgreSQL's <c>READ COMMITTED</c> a second caller's lock statement
/// waits until the first commits, and its reads then see what the first wrote; on SQLite the write gate and the
/// provider's <c>BEGIN IMMEDIATE</c> serialise them outright. No <c>FOR UPDATE</c> (SQLite has none) and no advisory lock
/// (dialect-specific): the same SQL on both engines.
/// </para>
/// <para>
/// The tables are created once per process by <see cref="OnceDdl"/> (Ruling 47) for a composition that never booted —
/// the account tables too, which the creator's lock and the member list read, the invitations table and the tokens table
/// a membership change sweeps;
/// <c>SystemSchemaInitializer</c> creates them all at boot.
/// </para>
/// </remarks>
internal sealed partial class EfCoreTenantStore : ITenantStore
{
    private readonly RelationalConnectionFactory _connections;
    private readonly TenantTables _tables;
    private readonly AuthTables _authTables;
    private readonly InvitationTables _invitations;
    private readonly AppTokenTables _appTokens;
    private readonly string _idempotency;
    private readonly OnceDdl _ddl;

    /// <summary>Creates the store.</summary>
    /// <param name="connections">The database's connections and write gate.</param>
    /// <param name="options">The options whose schema prefix names the tables.</param>
    public EfCoreTenantStore(RelationalConnectionFactory connections, AlvoOptions options)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(options);
        _connections = connections;
        _tables = new TenantTables(options.SchemaPrefix);
        _authTables = new AuthTables(options.SchemaPrefix);
        _invitations = new InvitationTables(options.SchemaPrefix);
        _appTokens = new AppTokenTables(options.SchemaPrefix);
        _idempotency = IdempotencyTable.NameFor(options.SchemaPrefix);
        _ddl = new OnceDdl(EnsureTablesAsync);
    }

    /// <inheritdoc/>
    public Task<CreateTenantOutcome> CreateSharedAsync(CreateTenantRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        return InTransactionAsync(
            (connection, transaction, token) => CreatedAsync(connection, transaction, request, token),
            outcome => outcome.Status == CreateTenantStatus.Created,
            ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<StoredMembership>> ListMembershipsAsync(UserId user, CancellationToken ct) =>
        await ReadAsync(
            _tables.MembershipsSql,
            command => Bind(command, ("@user_id", Id(user))),
            reader => TenantTables.ReadMembership(reader),
            ct).ConfigureAwait(false);

    /// <inheritdoc/>
    public async Task<StoredMembership?> FindMembershipAsync(UserId user, TenantId? tenant, CancellationToken ct)
    {
        var found = tenant is { } id
            ? await ReadAsync(
                _tables.HonouredMembershipSql,
                command => Bind(command, ("@user_id", Id(user)), ("@tenant_id", Id(id))),
                reader => TenantTables.ReadMembership(reader),
                ct).ConfigureAwait(false)
            : await ReadAsync(
                _tables.HonouredPersonalMembershipSql,
                command => Bind(command, ("@user_id", Id(user))),
                reader => TenantTables.ReadMembership(reader),
                ct).ConfigureAwait(false);

        return found.Count == 0 ? null : found[0];
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<StoredTenant>> ListTenantsAsync(CancellationToken ct) =>
        await ReadAsync(
            _tables.TenantsSql,
            _ => { },
            reader => new StoredTenant(new TenantId(GuidOf(reader.GetString(0))), TenantTables.KindOf(reader.GetString(1))),
            ct).ConfigureAwait(false);

    private async Task<CreateTenantOutcome> CreatedAsync(
        DbConnection connection, DbTransaction transaction, CreateTenantRequest request, CancellationToken ct)
    {
        var creator = (DbCommand command) => Bind(command, ("@account_id", Id(request.Creator)));
        if (await ExecuteAsync(connection, transaction, _tables.LockAccountSql, creator, ct).ConfigureAwait(false) != 1)
        {
            return new CreateTenantOutcome(CreateTenantStatus.NoAccount, null);
        }

        if (await RecordedAsync(connection, transaction, request, ct).ConfigureAwait(false) is { } recorded)
        {
            return recorded;
        }

        if (await CountAsync(connection, transaction, _tables.CreatedSharedCountSql, creator, ct).ConfigureAwait(false)
            >= request.MaxPerAccount)
        {
            return new CreateTenantOutcome(CreateTenantStatus.LimitReached, null);
        }

        await ExecuteAsync(
            connection, transaction, _tables.InsertTenantSql, command => BindSharedTenant(command, request), ct).ConfigureAwait(false);
        await ExecuteAsync(
            connection, transaction, _tables.InsertMemberSql,
            command => BindMember(command, request.Id, request.Creator, request.CreatorRoles, isOwner: true, request.Now),
            ct).ConfigureAwait(false);
        await RecordAsync(connection, transaction, request, ct).ConfigureAwait(false);

        return new CreateTenantOutcome(
            CreateTenantStatus.Created,
            new StoredMembership(request.Id, TenantKind.Shared, request.Name, [.. request.CreatorRoles], true, request.Now.ToUniversalTime()));
    }

    /// <summary>
    /// What an earlier create with this request's key answers, read under the account lock the create already holds, so
    /// one person's concurrent creates with one key are serialised and the second reads the first's record (#542 item 5);
    /// <see langword="null"/> when the request carries no key or the key is new.
    /// </summary>
    private async Task<CreateTenantOutcome?> RecordedAsync(
        DbConnection connection, DbTransaction transaction, CreateTenantRequest request, CancellationToken ct)
    {
        if (request.Idempotency is not { } token)
        {
            return null;
        }

        var recorded = await IdempotencyTable.FindRecordedAsync(
            connection, transaction, _idempotency, token.Key, CreateTenantRequest.IdempotencyScopeOf(request.Creator), ct)
            .ConfigureAwait(false);
        return recorded switch
        {
            null => null,
            var (fingerprint, _) when !token.Matches(fingerprint) => new CreateTenantOutcome(CreateTenantStatus.IdempotencyConflict, null),
            var (_, tenant) => new CreateTenantOutcome(CreateTenantStatus.Replayed, null, new TenantId(GuidOf(tenant))),
        };
    }

    /// <summary>Records this request's key beside the tenant it just created, in the creation's transaction.</summary>
    private Task RecordAsync(DbConnection connection, DbTransaction transaction, CreateTenantRequest request, CancellationToken ct) =>
        request.Idempotency is { } token
            ? IdempotencyTable.InsertRecordedAsync(
                connection, transaction, _idempotency, token.Key, CreateTenantRequest.IdempotencyScopeOf(request.Creator),
                token.Fingerprint, Id(request.Id), request.Now, ct)
            : Task.CompletedTask;

    private static void BindSharedTenant(DbCommand command, CreateTenantRequest request) => Bind(
        command,
        ("@tenant_id", Id(request.Id)),
        ("@kind", TenantTables.KindText(TenantKind.Shared)),
        ("@name", request.Name),
        ("@created_by", Id(request.Creator)),
        ("@now", Instant(request.Now)),
        ("@personal_owner", null));

    private static void BindMember(
        DbCommand command, TenantId tenant, UserId user, IReadOnlyList<string> roles, bool isOwner, DateTimeOffset now) => Bind(
        command,
        ("@tenant_id", Id(tenant)),
        ("@user_id", Id(user)),
        ("@roles", TenantTables.RolesText(roles)),
        ("@is_owner", isOwner ? 1 : 0),
        ("@now", Instant(now)));

    private static void Bind(DbCommand command, params (string Name, object? Value)[] parameters)
    {
        foreach (var (name, value) in parameters)
        {
            RelationalSqlBatch.AddParameter(command, name, value ?? DBNull.Value);
        }
    }

    private static string Id(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);

    private static string Id(UserId id) => Id(id.Value);

    private static string Id(TenantId id) => Id(id.Value);

    private static Guid GuidOf(string text) => Guid.Parse(text, CultureInfo.InvariantCulture);

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
                await _tables.EnsureAsync(connection, ct).ConfigureAwait(false);
                await _invitations.EnsureAsync(connection, ct).ConfigureAwait(false);
                await _appTokens.EnsureAsync(connection, ct).ConfigureAwait(false);
                await IdempotencyTable.EnsureAsync(connection, _idempotency, ct).ConfigureAwait(false);
            }
        }
    }
}
