using MMLib.Alvo.Auth;

using System.Data.Common;
using System.Globalization;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// The app-token registry's table for one schema prefix — one row per personal or service token — its DDL and every
/// statement <see cref="EfCoreAppTokenStore"/> runs over it (accounts spec §7.1, S5 plan D3, D4, D10, D14). Identical SQL on
/// both shipped engines.
/// </summary>
/// <remarks>
/// <para>
/// <b>One DDL text for both engines</b>, as <see cref="TenantTables"/>: ids and instants are <c>TEXT</c> (instants in
/// <see cref="StoredInstant.Text"/>'s fixed-width UTC form, so they compare as text), roles and scopes are JSON arrays of
/// strings. No foreign keys — no framework table carries one.
/// </para>
/// <para>
/// <b><c>secret_hash</c> holds the SHA-256 of the secret and nothing else</b>: the secret never reaches the store.
/// <c>user_id</c> is who the token acts as — the person for a personal token, the token's own fresh id for a service one —
/// and <c>minted_by</c> is always the person who minted it, the column the live cap counts and every sweep (D10) narrows by.
/// </para>
/// <para>
/// The bind-parameter names are not in <see cref="PolicyParameterPrefix"/> for the reason <see cref="OutboxTable"/>
/// records: hand-written, single-fragment statements over no entity table.
/// </para>
/// </remarks>
/// <param name="schemaPrefix">The validated <see cref="AlvoOptions.SchemaPrefix"/>.</param>
internal sealed class AppTokenTables(string schemaPrefix)
{
    /// <summary>The stored <c>kind</c> of a personal token.</summary>
    internal const string PersonalKind = "personal";

    /// <summary>The stored <c>kind</c> of a service token.</summary>
    internal const string ServiceKind = "service";

    /// <summary>The columns a token is resolved from, in <see cref="ReadRecord"/>'s order.</summary>
    private const string RecordColumns =
        "key_id, kind, secret_hash, user_id, minted_by, tenant_id, roles, scopes, expires_at, revoked_at, last_used_at";

    /// <summary>The columns a token is listed from, in <see cref="ReadView"/>'s order, over <c>k</c> ⟕ <c>a</c>.</summary>
    private const string ViewColumns =
        "k.key_id, k.kind, k.name, k.minted_by, a.display_name, k.tenant_id, k.roles, k.scopes, k.created_at, k.expires_at, "
        + "k.last_used_at, k.revoked_at";

    /// <summary>Gets the tokens table, e.g. <c>alvo_api_keys</c>.</summary>
    internal string ApiKeys { get; } = schemaPrefix + AlvoFrameworkTables.ApiKeysSuffix;

    /// <summary>Gets the accounts table the list reads the minter's name from.</summary>
    private string Accounts { get; } = schemaPrefix + AlvoFrameworkTables.AuthAccountsSuffix;

    /// <summary>Gets the tenants table an owner's shared tenants are read from.</summary>
    private string Tenants { get; } = schemaPrefix + AlvoFrameworkTables.TenantsSuffix;

    /// <summary>Gets the members table an owner's memberships are read from.</summary>
    private string Members { get; } = schemaPrefix + AlvoFrameworkTables.TenantMembersSuffix;

    /// <summary>Creates the table and its indexes if any is missing — on a connection with no transaction in flight.</summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    internal async Task EnsureAsync(DbConnection connection, CancellationToken ct)
    {
        await IfNotExistsDdl.CreateTableAsync(connection, ApiKeys, ApiKeysDdl, ct).ConfigureAwait(false);
        await IfNotExistsDdl.CreateIndexAsync(connection, AuthTables.IndexDdl(ApiKeys, "minted_by"), ct).ConfigureAwait(false);
        await IfNotExistsDdl.CreateIndexAsync(connection, AuthTables.IndexDdl(ApiKeys, "tenant_id"), ct).ConfigureAwait(false);
    }

    /// <summary>Gets the tokens table's DDL.</summary>
    internal string ApiKeysDdl =>
        $"""
        CREATE TABLE IF NOT EXISTS {ApiKeys} (
            key_id TEXT NOT NULL PRIMARY KEY,
            kind TEXT NOT NULL,
            secret_hash TEXT NOT NULL,
            name TEXT NOT NULL,
            user_id TEXT NOT NULL,
            minted_by TEXT NOT NULL,
            tenant_id TEXT NULL,
            roles TEXT NOT NULL,
            scopes TEXT NOT NULL,
            created_at TEXT NOT NULL,
            expires_at TEXT NOT NULL,
            revoked_at TEXT NULL,
            last_used_at TEXT NULL
        )
        """;

    /// <summary>Gets the statement reading one token, in any state.</summary>
    internal string FindSql => $"SELECT {RecordColumns} FROM {ApiKeys} WHERE key_id = @key_id";

    /// <summary>Gets the statement recording one token's last use.</summary>
    internal string TouchSql => $"UPDATE {ApiKeys} SET last_used_at = @used_at WHERE key_id = @key_id";

    /// <summary>Gets the statement counting one account's live tokens — unrevoked and unexpired at <c>@now</c> (D13).</summary>
    internal string LiveCountSql =>
        $"SELECT COUNT(*) FROM {ApiKeys} WHERE minted_by = @minted_by AND revoked_at IS NULL AND expires_at > @now";

    /// <summary>Gets the statement inserting one token.</summary>
    internal string InsertSql =>
        $"""
        INSERT INTO {ApiKeys}
            (key_id, kind, secret_hash, name, user_id, minted_by, tenant_id, roles, scopes, created_at, expires_at, revoked_at, last_used_at)
        VALUES (@key_id, @kind, @secret_hash, @name, @user_id, @minted_by, @tenant_id, @roles, @scopes, @now, @expires_at, NULL, NULL)
        """;

    /// <summary>Gets the statement reading one token's view, its minter's name left-joined on.</summary>
    internal string ViewSql =>
        $"SELECT {ViewColumns} FROM {ApiKeys} k LEFT JOIN {Accounts} a ON a.id = k.minted_by WHERE k.key_id = @key_id";

    /// <summary>
    /// Gets the statement listing what <c>@person</c> sees (D14): their personal tokens and the service tokens of every shared
    /// tenant they own, newest first and then by key id, the minter's name left-joined on.
    /// </summary>
    internal string ListSql =>
        $"""
        SELECT {ViewColumns} FROM {ApiKeys} k LEFT JOIN {Accounts} a ON a.id = k.minted_by
         WHERE (k.kind = '{PersonalKind}' AND k.minted_by = @person)
            OR (k.kind = '{ServiceKind}' AND k.tenant_id IN ({OwnedSharedTenants("@person")}))
         ORDER BY k.created_at DESC, k.key_id
        """;

    /// <summary>
    /// Gets the statement revoking one token — a personal one only by its minter, a service one by its minter or any owner of
    /// its tenant — keeping the first instant of an already revoked one. No row: no such token, or not the actor's to revoke.
    /// </summary>
    internal string RevokeSql =>
        $"""
        UPDATE {ApiKeys} SET revoked_at = COALESCE(revoked_at, @now)
         WHERE key_id = @key_id
           AND ((kind = '{PersonalKind}' AND minted_by = @actor)
             OR (kind = '{ServiceKind}' AND (minted_by = @actor OR tenant_id IN ({OwnedSharedTenants("@actor")}))))
        """;

    /// <summary>The stored text of <paramref name="kind"/>; only a personal or a service token is ever stored.</summary>
    /// <param name="kind">The token's kind.</param>
    internal static string KindText(ApiKeyKind kind) => kind == ApiKeyKind.Service ? ServiceKind : PersonalKind;

    /// <summary>The kind whose stored text is <paramref name="text"/>.</summary>
    /// <param name="text">The stored <c>kind</c>.</param>
    internal static ApiKeyKind KindOf(string text) => text == ServiceKind ? ApiKeyKind.Service : ApiKeyKind.Personal;

    /// <summary>
    /// Maps a row of <see cref="FindSql"/> — or <see langword="null"/> when a stored scope no longer parses, so a token is
    /// never trusted with fewer scopes than it was minted with (the <c>InMemoryApiKeyStore</c> rule).
    /// </summary>
    /// <param name="reader">A reader positioned on a row.</param>
    internal static ApiKeyRecord? ReadRecord(DbDataReader reader) =>
        ScopesOf(reader.GetString(7)) is { } scopes
            ? new ApiKeyRecord
            {
                KeyId = reader.GetString(0),
                Kind = KindOf(reader.GetString(1)),
                Sha256Hash = reader.GetString(2),
                User = new UserId(GuidOf(reader.GetString(3))),
                MintedBy = new UserId(GuidOf(reader.GetString(4))),
                Tenant = TenantOrNull(reader, 5),
                RoleNames = TenantTables.RolesOf(reader.GetString(6)),
                Scopes = scopes,
                ExpiresAt = StoredInstant.Of(reader.GetString(8)),
                RevokedAt = InstantOrNull(reader, 9),
                LastUsedAt = InstantOrNull(reader, 10),
            }
            : null;

    /// <summary>Maps a row of <see cref="ViewColumns"/>.</summary>
    /// <param name="reader">A reader positioned on a row.</param>
    internal static AppTokenView ReadView(DbDataReader reader) => new(
        reader.GetString(0),
        KindOf(reader.GetString(1)),
        reader.GetString(2),
        new UserId(GuidOf(reader.GetString(3))),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        TenantOrNull(reader, 5),
        TenantTables.RolesOf(reader.GetString(6)),
        TenantTables.RolesOf(reader.GetString(7)),
        StoredInstant.Of(reader.GetString(8)),
        StoredInstant.Of(reader.GetString(9)),
        InstantOrNull(reader, 10),
        InstantOrNull(reader, 11));

    /// <summary>The subquery of the shared tenants <paramref name="account"/> owns.</summary>
    /// <param name="account">The bind parameter naming the account.</param>
    private string OwnedSharedTenants(string account) =>
        $"SELECT m.tenant_id FROM {Members} m JOIN {Tenants} t ON t.id = m.tenant_id "
        + $"WHERE m.user_id = {account} AND m.is_owner = 1 AND t.kind = '{TenantTables.SharedKind}'";

    /// <summary>The scopes a stored JSON array holds, or <see langword="null"/> when any of them does not parse.</summary>
    /// <param name="text">The stored <c>scopes</c>.</param>
    private static HashSet<ApiKeyScope>? ScopesOf(string text)
    {
        var scopes = new HashSet<ApiKeyScope>();
        foreach (var scope in TenantTables.RolesOf(text))
        {
            if (!ApiKeyScope.TryParse(scope, out var parsed))
            {
                return null;
            }

            scopes.Add(parsed);
        }

        return scopes;
    }

    private static Guid GuidOf(string text) => Guid.Parse(text, CultureInfo.InvariantCulture);

    private static TenantId? TenantOrNull(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : new TenantId(GuidOf(reader.GetString(ordinal)));

    private static DateTimeOffset? InstantOrNull(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : StoredInstant.Of(reader.GetString(ordinal));
}
