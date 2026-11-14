using MMLib.Alvo.Tenants;

using System.Data.Common;
using System.Globalization;
using System.Text.Json;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// The self-service tenant registry's tables for one schema prefix — tenants and their members — their DDL and every
/// statement <see cref="EfCoreTenantStore"/> runs over them (accounts spec §6.1, S3 plan D2). Identical SQL on both
/// shipped engines.
/// </summary>
/// <remarks>
/// <para>
/// <b>One DDL text per table for both engines</b>, as <see cref="AuthTables"/>: ids and instants are <c>TEXT</c> (instants
/// in <see cref="StoredInstant.Text"/>'s fixed-width UTC form), booleans are <c>INTEGER</c> 0/1, roles are a JSON array of
/// names. No foreign keys — no framework table carries one.
/// </para>
/// <para>
/// <b><c>personal_owner</c> is the constraint, not decoration</b> (D6): the account id of a personal tenant and
/// <c>NULL</c> for a shared one, under a <c>UNIQUE</c> that both engines satisfy with many <c>NULL</c>s — so a person has
/// at most one personal tenant however many sign-ins race to create it. <c>version</c> is the row every membership write
/// bumps first (D3), which is what serialises two owners of one tenant. <c>invitation_id</c> stays <c>NULL</c> until
/// invitations exist (spec D9's provenance column).
/// </para>
/// <para>
/// The bind-parameter names are not in <see cref="PolicyParameterPrefix"/> for the reason <see cref="OutboxTable"/>
/// records: hand-written, single-fragment statements over no entity table.
/// </para>
/// </remarks>
/// <param name="schemaPrefix">The validated <see cref="AlvoOptions.SchemaPrefix"/>.</param>
internal sealed class TenantTables(string schemaPrefix)
{
    /// <summary>The stored <c>kind</c> of a personal tenant.</summary>
    internal const string PersonalKind = "personal";

    /// <summary>The stored <c>kind</c> of a shared tenant.</summary>
    internal const string SharedKind = "shared";

    /// <summary>The columns a membership is read from, in <see cref="ReadMembership"/>'s order, over <c>m</c> ⨝ <c>t</c>.</summary>
    private const string MembershipColumns = "t.id, t.kind, t.name, m.roles, m.is_owner, m.joined_at";

    /// <summary>Gets the tenants table, e.g. <c>alvo_tenants</c>.</summary>
    internal string Tenants { get; } = schemaPrefix + AlvoFrameworkTables.TenantsSuffix;

    /// <summary>Gets the members table, e.g. <c>alvo_tenant_members</c>.</summary>
    internal string Members { get; } = schemaPrefix + AlvoFrameworkTables.TenantMembersSuffix;

    /// <summary>Gets the accounts table the creator's lock and the member list read, e.g. <c>alvo_auth_accounts</c>.</summary>
    internal string Accounts { get; } = schemaPrefix + AlvoFrameworkTables.AuthAccountsSuffix;

    /// <summary>Creates the two tables and their indexes if any is missing — on a connection with no transaction in flight.</summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    internal async Task EnsureAsync(DbConnection connection, CancellationToken ct)
    {
        await IfNotExistsDdl.CreateTableAsync(connection, Tenants, TenantsDdl, ct).ConfigureAwait(false);
        await IfNotExistsDdl.CreateIndexAsync(connection, AuthTables.IndexDdl(Tenants, "created_by"), ct).ConfigureAwait(false);
        await IfNotExistsDdl.CreateTableAsync(connection, Members, MembersDdl, ct).ConfigureAwait(false);
        await IfNotExistsDdl.CreateIndexAsync(connection, AuthTables.IndexDdl(Members, "user_id"), ct).ConfigureAwait(false);
    }

    /// <summary>Gets the tenants table's DDL.</summary>
    internal string TenantsDdl =>
        $"""
        CREATE TABLE IF NOT EXISTS {Tenants} (
            id TEXT NOT NULL PRIMARY KEY,
            kind TEXT NOT NULL,
            name TEXT NOT NULL,
            created_by TEXT NOT NULL,
            created_at TEXT NOT NULL,
            personal_owner TEXT NULL UNIQUE,
            version INTEGER NOT NULL
        )
        """;

    /// <summary>Gets the members table's DDL.</summary>
    internal string MembersDdl =>
        $"""
        CREATE TABLE IF NOT EXISTS {Members} (
            tenant_id TEXT NOT NULL,
            user_id TEXT NOT NULL,
            roles TEXT NOT NULL,
            is_owner INTEGER NOT NULL,
            joined_at TEXT NOT NULL,
            invitation_id TEXT NULL,
            PRIMARY KEY (tenant_id, user_id)
        )
        """;

    /// <summary>
    /// Gets the statement taking an enabled account's row lock with a write that changes nothing (D3). Matching no row means
    /// there is no such enabled account; on PostgreSQL a second caller waits here until the first commits.
    /// </summary>
    internal string LockAccountSql =>
        $"UPDATE {Accounts} SET last_sign_in_at = last_sign_in_at WHERE id = @account_id AND disabled_at IS NULL";

    /// <summary>Gets the statement counting the shared tenants one account created (D9).</summary>
    internal string CreatedSharedCountSql =>
        $"SELECT COUNT(*) FROM {Tenants} WHERE kind = '{SharedKind}' AND created_by = @account_id";

    /// <summary>Gets the statement inserting a tenant at version 0; <c>@personal_owner</c> is <c>NULL</c> for a shared one.</summary>
    internal string InsertTenantSql =>
        $"""
        INSERT INTO {Tenants} (id, kind, name, created_by, created_at, personal_owner, version)
        VALUES (@tenant_id, @kind, @name, @created_by, @now, @personal_owner, 0)
        """;

    /// <summary>Gets the statement inserting a membership; its <c>invitation_id</c> is <c>NULL</c> until invitations exist.</summary>
    internal string InsertMemberSql =>
        $"""
        INSERT INTO {Members} (tenant_id, user_id, roles, is_owner, joined_at, invitation_id)
        VALUES (@tenant_id, @user_id, @roles, @is_owner, @now, NULL)
        """;

    /// <summary>Gets the statement reading one account's memberships: the personal one first, then by joining, then by id.</summary>
    internal string MembershipsSql =>
        $"""
        SELECT {MembershipColumns} FROM {Members} m JOIN {Tenants} t ON t.id = m.tenant_id
         WHERE m.user_id = @user_id
         ORDER BY CASE WHEN t.kind = '{PersonalKind}' THEN 0 ELSE 1 END, m.joined_at, t.id
        """;

    /// <summary>Gets the statement reading one account's membership in one tenant.</summary>
    internal string MembershipSql =>
        $"""
        SELECT {MembershipColumns} FROM {Members} m JOIN {Tenants} t ON t.id = m.tenant_id
         WHERE m.user_id = @user_id AND t.id = @tenant_id
        """;

    /// <summary>Gets the statement reading one account's membership in its personal tenant.</summary>
    internal string PersonalMembershipSql =>
        $"""
        SELECT {MembershipColumns} FROM {Members} m JOIN {Tenants} t ON t.id = m.tenant_id
         WHERE m.user_id = @user_id AND t.personal_owner = @user_id
        """;

    /// <summary>Gets the statement reading every tenant's id, by creation and then id.</summary>
    internal string TenantIdsSql => $"SELECT id FROM {Tenants} ORDER BY created_at, id";

    /// <summary>
    /// Gets the statement every membership write runs first (D3): it takes the tenant's row lock, so on PostgreSQL a second
    /// writer of one tenant waits here until the first commits and then reads what it wrote. Matching no row means there is
    /// no such tenant.
    /// </summary>
    internal string BumpVersionSql => $"UPDATE {Tenants} SET version = version + 1 WHERE id = @tenant_id";

    /// <summary>Gets the statement counting one tenant's owners.</summary>
    internal string OwnersSql => $"SELECT COUNT(*) FROM {Members} WHERE tenant_id = @tenant_id AND is_owner = 1";

    /// <summary>Gets the statement renaming one tenant.</summary>
    internal string RenameSql => $"UPDATE {Tenants} SET name = @name WHERE id = @tenant_id";

    /// <summary>Gets the statement replacing one member's roles.</summary>
    internal string SetRolesSql => $"UPDATE {Members} SET roles = @roles WHERE tenant_id = @tenant_id AND user_id = @user_id";

    /// <summary>Gets the statement setting or clearing one member's ownership.</summary>
    internal string SetOwnerSql => $"UPDATE {Members} SET is_owner = @is_owner WHERE tenant_id = @tenant_id AND user_id = @user_id";

    /// <summary>Gets the statement removing one membership.</summary>
    internal string RemoveMemberSql => $"DELETE FROM {Members} WHERE tenant_id = @tenant_id AND user_id = @user_id";

    /// <summary>
    /// Gets the statement reading one tenant's members with their accounts' display names and avatars, by joining and then
    /// account id — no row at all unless <c>@actor_id</c> is a member, so one read answers both. A left join: a membership
    /// whose account is gone still shows.
    /// </summary>
    internal string MembersSql =>
        $"""
        SELECT m.user_id, a.display_name, a.avatar_url, m.roles, m.is_owner, m.joined_at
          FROM {Members} m LEFT JOIN {Accounts} a ON a.id = m.user_id
         WHERE m.tenant_id = @tenant_id
           AND EXISTS (SELECT 1 FROM {Members} x WHERE x.tenant_id = @tenant_id AND x.user_id = @actor_id)
         ORDER BY m.joined_at, m.user_id
        """;

    /// <summary>The stored text of <paramref name="kind"/>.</summary>
    /// <param name="kind">The tenant's kind.</param>
    internal static string KindText(TenantKind kind) => kind == TenantKind.Personal ? PersonalKind : SharedKind;

    /// <summary>The stored text of a role list: a JSON array of names, verbatim.</summary>
    /// <param name="roles">The role names.</param>
    internal static string RolesText(IReadOnlyList<string> roles) => JsonSerializer.Serialize(roles);

    /// <summary>
    /// Maps a row of <see cref="MembershipColumns"/>, read from <paramref name="first"/> on — the store's reads and, from
    /// S3 Task 5, the account store's one caller read share it.
    /// </summary>
    /// <param name="reader">A reader positioned on a row.</param>
    /// <param name="first">The ordinal of the tenant id.</param>
    internal static StoredMembership ReadMembership(DbDataReader reader, int first = 0) => new(
        new TenantId(Guid.Parse(reader.GetString(first), CultureInfo.InvariantCulture)),
        reader.GetString(first + 1) == PersonalKind ? TenantKind.Personal : TenantKind.Shared,
        reader.GetString(first + 2),
        RolesOf(reader.GetString(first + 3)),
        Convert.ToInt64(reader.GetValue(first + 4), CultureInfo.InvariantCulture) != 0,
        StoredInstant.Of(reader.GetString(first + 5)));

    /// <summary>Maps a row of <see cref="MembersSql"/>.</summary>
    /// <param name="reader">A reader positioned on a row.</param>
    internal static MemberView ReadMember(DbDataReader reader) => new(
        new UserId(Guid.Parse(reader.GetString(0), CultureInfo.InvariantCulture)),
        reader.IsDBNull(1) ? null : reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        RolesOf(reader.GetString(3)),
        Convert.ToInt64(reader.GetValue(4), CultureInfo.InvariantCulture) != 0,
        StoredInstant.Of(reader.GetString(5)));

    private static string[] RolesOf(string text) => JsonSerializer.Deserialize<string[]>(text) ?? [];
}
