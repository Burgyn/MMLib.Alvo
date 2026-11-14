using MMLib.Alvo.Tenants;

using System.Data.Common;
using System.Globalization;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// The tenant registry's invitations table for one schema prefix, its DDL and every statement the store and the acceptance
/// routine run over it (accounts spec §6.1, S4 plan D2, D4, D6). Identical SQL on both shipped engines.
/// </summary>
/// <remarks>
/// <para>
/// <b>The <see cref="TenantTables"/> conventions, unchanged</b>: ids and instants are <c>TEXT</c> (instants in
/// <see cref="StoredInstant.Text"/>'s fixed-width UTC form, so <c>expires_at &gt; @now</c> compares as text), booleans are
/// <c>INTEGER</c> 0/1, roles are a JSON array of names, and there are no foreign keys. <c>created_at</c> is the builder's
/// column (D12), for the owners' list order.
/// </para>
/// <para>
/// <b>Only the code's hash is stored</b>, under a <c>UNIQUE</c>: the code itself never reaches the store (D2).
/// </para>
/// </remarks>
/// <param name="schemaPrefix">The validated <see cref="AlvoOptions.SchemaPrefix"/>.</param>
internal sealed class InvitationTables(string schemaPrefix)
{
    /// <summary>The columns an invitation is read from, in <see cref="ReadView"/>'s order.</summary>
    private const string ViewColumns =
        "id, tenant_id, roles, grants_owner, email, max_uses, uses, expires_at, created_by, created_at, revoked_at";

    /// <summary>Gets the invitations table, e.g. <c>alvo_tenant_invitations</c>.</summary>
    internal string Invitations { get; } = schemaPrefix + AlvoFrameworkTables.TenantInvitationsSuffix;

    /// <summary>Gets the tenants table a live invitation is joined to.</summary>
    private string Tenants { get; } = schemaPrefix + AlvoFrameworkTables.TenantsSuffix;

    /// <summary>Gets the members table a live invitation's creator must still own a membership in.</summary>
    private string Members { get; } = schemaPrefix + AlvoFrameworkTables.TenantMembersSuffix;

    /// <summary>Gets the accounts table the preview reads the inviter's name from.</summary>
    private string Accounts { get; } = schemaPrefix + AlvoFrameworkTables.AuthAccountsSuffix;

    /// <summary>Creates the table and its index if either is missing — on a connection with no transaction in flight.</summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    internal async Task EnsureAsync(DbConnection connection, CancellationToken ct)
    {
        await IfNotExistsDdl.CreateTableAsync(connection, Invitations, InvitationsDdl, ct).ConfigureAwait(false);
        await IfNotExistsDdl.CreateIndexAsync(connection, AuthTables.IndexDdl(Invitations, "tenant_id"), ct).ConfigureAwait(false);
    }

    /// <summary>Gets the invitations table's DDL.</summary>
    internal string InvitationsDdl =>
        $"""
        CREATE TABLE IF NOT EXISTS {Invitations} (
            id TEXT NOT NULL PRIMARY KEY,
            tenant_id TEXT NOT NULL,
            code_hash TEXT NOT NULL UNIQUE,
            roles TEXT NOT NULL,
            grants_owner INTEGER NOT NULL,
            email TEXT NULL,
            max_uses INTEGER NOT NULL,
            uses INTEGER NOT NULL,
            expires_at TEXT NOT NULL,
            created_by TEXT NOT NULL,
            created_at TEXT NOT NULL,
            revoked_at TEXT NULL
        )
        """;

    /// <summary>Gets the statement inserting a new invitation, unused and live (<c>uses</c> 0, <c>revoked_at</c> null).</summary>
    internal string InsertSql =>
        $"""
        INSERT INTO {Invitations} (id, tenant_id, code_hash, roles, grants_owner, email, max_uses, uses, expires_at, created_by, created_at, revoked_at)
        VALUES (@id, @tenant_id, @code_hash, @roles, @grants_owner, @email, @max_uses, 0, @expires_at, @created_by, @now, NULL)
        """;

    /// <summary>Gets the statement reading every invitation of one tenant, newest first, then by id.</summary>
    internal string ListSql =>
        $"SELECT {ViewColumns} FROM {Invitations} WHERE tenant_id = @tenant_id ORDER BY created_at DESC, id DESC";

    /// <summary>
    /// Gets the statement revoking one invitation of one tenant; an already revoked one keeps its first instant but still
    /// matches, so revoking is idempotent. Matching no row means the invitation is not this tenant's.
    /// </summary>
    internal string RevokeSql =>
        $"UPDATE {Invitations} SET revoked_at = COALESCE(revoked_at, @now) WHERE id = @id AND tenant_id = @tenant_id";

    /// <summary>
    /// Gets the statement revoking every live invitation one member of one tenant created (D6) — run in the transaction
    /// that demotes or removes them as an owner, so their invitations die with their ownership.
    /// </summary>
    internal string RevokeCreatedBySql =>
        $"UPDATE {Invitations} SET revoked_at = @now WHERE tenant_id = @tenant_id AND created_by = @user_id AND revoked_at IS NULL";

    /// <summary>
    /// The condition a live invitation <c>i</c>, joined to its tenant <c>t</c>, meets for <c>@code_hash</c> at <c>@now</c>
    /// (<see cref="InvitationAcceptance"/>'s step 3): its tenant a shared one — a personal tenant is never invited into,
    /// which the store enforces here because the table does not — not revoked, not expired, not used up, and its creator
    /// still an owner of the tenant (D6's second line).
    /// </summary>
    private string LiveWhere =>
        $"""
        i.code_hash = @code_hash AND t.kind = '{TenantTables.SharedKind}'
           AND i.revoked_at IS NULL AND i.expires_at > @now AND i.uses < i.max_uses
           AND EXISTS (SELECT 1 FROM {Members} o WHERE o.tenant_id = i.tenant_id AND o.user_id = i.created_by AND o.is_owner = 1)
        """;

    /// <summary>
    /// Gets the statement reading the tenant of the invitation of one code in any state — live or dead — so the acceptance
    /// judges membership before liveness (<see cref="InvitationAcceptance"/>'s step 2); no row when no invitation has the code.
    /// </summary>
    internal string TenantOfSql => $"SELECT tenant_id FROM {Invitations} WHERE code_hash = @code_hash";

    /// <summary>Gets the statement reading the live invitation of one code, in <see cref="ReadLive"/>'s order; no row when it is dead.</summary>
    internal string LiveSql =>
        $"""
        SELECT i.id, i.tenant_id, t.name, i.roles, i.grants_owner, i.email
          FROM {Invitations} i JOIN {Tenants} t ON t.id = i.tenant_id
         WHERE {LiveWhere}
        """;

    /// <summary>
    /// Gets the statement previewing the live invitation of one code, in <see cref="ReadPreview"/>'s order, with its creator's
    /// display name left-joined on — never its email, only whether it has one (D8). No row when it is dead.
    /// </summary>
    internal string PreviewSql =>
        $"""
        SELECT i.tenant_id, t.name, a.display_name, i.roles, i.grants_owner, CASE WHEN i.email IS NULL THEN 0 ELSE 1 END, i.expires_at
          FROM {Invitations} i JOIN {Tenants} t ON t.id = i.tenant_id LEFT JOIN {Accounts} a ON a.id = i.created_by
         WHERE {LiveWhere}
        """;

    /// <summary>
    /// Gets the statement consuming one use of an invitation (D4 step 5, spec §6.4) — the one decision on the use count: it
    /// matches only while the invitation is still unused-up, unrevoked and unexpired at <c>@now</c>, so of two concurrent
    /// consumers of a last use exactly one matches (on PostgreSQL the second waits on the row and re-evaluates).
    /// </summary>
    internal string ConsumeSql =>
        $"UPDATE {Invitations} SET uses = uses + 1 WHERE id = @id AND uses < max_uses AND revoked_at IS NULL AND expires_at > @now";

    /// <summary>Gets the statement inserting an accepted membership, its provenance the invitation's id (D15).</summary>
    internal string InsertInvitedMemberSql =>
        $"""
        INSERT INTO {Members} (tenant_id, user_id, roles, is_owner, joined_at, invitation_id)
        VALUES (@tenant_id, @user_id, @roles, @is_owner, @now, @invitation_id)
        """;

    /// <summary>Maps a row of <see cref="ViewColumns"/>.</summary>
    /// <param name="reader">A reader positioned on a row.</param>
    internal static InvitationView ReadView(DbDataReader reader) => new(
        Guid.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
        new TenantId(Guid.Parse(reader.GetString(1), CultureInfo.InvariantCulture)),
        TenantTables.RolesOf(reader.GetString(2)),
        Flag(reader, 3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        Number(reader, 5),
        Number(reader, 6),
        StoredInstant.Of(reader.GetString(7)),
        new UserId(Guid.Parse(reader.GetString(8), CultureInfo.InvariantCulture)),
        StoredInstant.Of(reader.GetString(9)),
        InstantOrNull(reader, 10));

    /// <summary>Maps a row of <see cref="LiveSql"/>.</summary>
    /// <param name="reader">A reader positioned on a row.</param>
    internal static LiveInvitation ReadLive(DbDataReader reader) => new(
        Guid.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
        new TenantId(Guid.Parse(reader.GetString(1), CultureInfo.InvariantCulture)),
        reader.GetString(2),
        TenantTables.RolesOf(reader.GetString(3)),
        Flag(reader, 4),
        reader.IsDBNull(5) ? null : reader.GetString(5));

    /// <summary>Maps a row of <see cref="PreviewSql"/>.</summary>
    /// <param name="reader">A reader positioned on a row.</param>
    internal static InvitationPreview ReadPreview(DbDataReader reader) => new(
        new TenantId(Guid.Parse(reader.GetString(0), CultureInfo.InvariantCulture)),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        TenantTables.RolesOf(reader.GetString(3)),
        Flag(reader, 4),
        Flag(reader, 5),
        StoredInstant.Of(reader.GetString(6)));

    /// <summary>An <c>INTEGER</c> 0/1 column as a boolean.</summary>
    /// <param name="reader">A reader positioned on a row.</param>
    /// <param name="ordinal">The column.</param>
    internal static bool Flag(DbDataReader reader, int ordinal) =>
        Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture) != 0;

    /// <summary>An <c>INTEGER</c> column as an <see cref="int"/>, whichever width the engine reads it at.</summary>
    /// <param name="reader">A reader positioned on a row.</param>
    /// <param name="ordinal">The column.</param>
    private static int Number(DbDataReader reader, int ordinal) =>
        Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    /// <summary>An optional instant column.</summary>
    /// <param name="reader">A reader positioned on a row.</param>
    /// <param name="ordinal">The column.</param>
    private static DateTimeOffset? InstantOrNull(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : StoredInstant.Of(reader.GetString(ordinal));
}

/// <summary>A live invitation, as the acceptance reads it (<see cref="InvitationAcceptance"/>'s step 3).</summary>
/// <param name="Id">The invitation.</param>
/// <param name="Tenant">The shared tenant it invites into.</param>
/// <param name="TenantName">The tenant's name.</param>
/// <param name="Roles">The roles an accepted membership starts with.</param>
/// <param name="GrantsOwner">Whether an accepted membership is an owner's.</param>
/// <param name="Email">The email it is bound to, if any.</param>
internal sealed record LiveInvitation(
    Guid Id, TenantId Tenant, string TenantName, IReadOnlyList<string> Roles, bool GrantsOwner, string? Email);
