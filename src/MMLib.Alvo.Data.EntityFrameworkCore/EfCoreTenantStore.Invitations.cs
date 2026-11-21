using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Tenants;

using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// The tenant registry's invitation half: an owner creates, lists and revokes invitations (S4 plan D6, D10), anyone holding
/// a code previews one, and a person accepts one. It builds no command of its own and goes through the main file's and
/// <see cref="InvitationAcceptance"/>'s.
/// </summary>
/// <remarks>
/// <para>
/// <b>Creating and revoking are membership writes</b>: both run through <see cref="MemberWriteAsync"/>, so they bump the
/// tenant's <c>version</c> first and judge the actor's ownership after it. That is what serialises a creation with its
/// creator's demotion or removal, whose sweep (<see cref="InvitationTables.RevokeCreatedBySql"/>) then either sees the new
/// invitation or has already made the creator a non-owner — never an invitation of a non-owner left live.
/// </para>
/// <para>
/// <b>Accepting is <see cref="InvitationAcceptance"/></b>, in a transaction of its own here and inside the sign-in's in the
/// account store — one routine, so the two paths cannot drift.
/// </para>
/// </remarks>
internal sealed partial class EfCoreTenantStore
{
    /// <inheritdoc/>
    public async Task<CreateInvitationOutcome> CreateInvitationAsync(CreateInvitationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        InvitationView? stored = null;
        var status = await MemberWriteAsync(
            request.Tenant,
            request.Actor,
            async (connection, transaction, actor, token) =>
            {
                var (written, view) = await InvitedAsync(connection, transaction, request, actor, token).ConfigureAwait(false);
                stored = view;
                return written;
            },
            ct,
            request.Session is { } session ? (connection, transaction, token) => SessionLiveAsync(connection, transaction, request, session, token) : null)
            .ConfigureAwait(false);

        return status == MemberChangeStatus.Changed
            ? new CreateInvitationOutcome(status, stored)
            : new CreateInvitationOutcome(status, null);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<InvitationView>> ListInvitationsAsync(TenantId tenant, CancellationToken ct) =>
        await ReadAsync(
            _invitations.ListSql,
            command => Bind(command, ("@tenant_id", Id(tenant))),
            InvitationTables.ReadView,
            ct).ConfigureAwait(false);

    /// <inheritdoc/>
    public Task<MemberChangeStatus> RevokeInvitationAsync(InvitationRevocation revocation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(revocation);

        return MemberWriteAsync(
            revocation.Tenant,
            revocation.Actor,
            (connection, transaction, actor, token) => RevokedAsync(connection, transaction, revocation, actor, token),
            ct);
    }

    /// <inheritdoc/>
    public async Task<InvitationPreview?> PreviewInvitationAsync(string codeHash, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(codeHash);

        var found = await ReadAsync(
            _invitations.PreviewSql,
            command => Bind(command, ("@code_hash", codeHash), ("@now", Instant(now))),
            InvitationTables.ReadPreview,
            ct).ConfigureAwait(false);

        return found.Count == 0 ? null : found[0];
    }

    /// <inheritdoc/>
    public Task<AcceptInvitationOutcome> AcceptInvitationAsync(AcceptInvitationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        return InTransactionAsync(
            (connection, transaction, token) => InvitationAcceptance.AcceptAsync(
                connection, transaction, _invitations, _tables, _authTables, request.CodeHash, request.Person, request.Now, token),
            outcome => outcome.Status == AcceptInvitationStatus.Joined,
            ct);
    }

    /// <summary>
    /// Inserts the invitation an owner asked for and reads it back in the same transaction, as the list reads it — so the
    /// answer names its creator exactly as the list does (#542 item 1), and nothing can fail between the durable write and
    /// the one answer that carries the code (the token mint's precedent).
    /// </summary>
    private async Task<(MemberChangeStatus Status, InvitationView? Stored)> InvitedAsync(
        DbConnection connection, DbTransaction transaction, CreateInvitationRequest request, StoredMembership actor, CancellationToken ct)
    {
        if (actor.Kind == TenantKind.Personal)
        {
            return (MemberChangeStatus.PersonalTenant, null);
        }

        if (!actor.IsOwner)
        {
            return (MemberChangeStatus.NotOwner, null);
        }

        await ExecuteAsync(connection, transaction, _invitations.InsertSql, command => BindInvitation(command, request), ct)
            .ConfigureAwait(false);
        var stored = await RelationalSqlBatch.ReadRowsAsync(
            connection, transaction, _invitations.ViewSql, command => Bind(command, ("@id", Id(request.Id))), InvitationTables.ReadView, ct)
            .ConfigureAwait(false);
        return (MemberChangeStatus.Changed, stored.Single());
    }

    /// <summary>
    /// Locks the creator's <paramref name="session"/> if it is still live (stack review A-I1) — before the tenant's bump, the
    /// order a mint and a refresh take a session and a tenant in.
    /// </summary>
    private async Task<bool> SessionLiveAsync(
        DbConnection connection, DbTransaction transaction, CreateInvitationRequest request, Guid session, CancellationToken ct) =>
        await ExecuteAsync(
            connection, transaction, _authTables.LockLiveSessionSql,
            command => Bind(command, ("@session_id", Id(session)), ("@account_id", Id(request.Actor)), ("@now", Instant(request.Now))),
            ct).ConfigureAwait(false) == 1;

    private async Task<MemberChangeStatus> RevokedAsync(
        DbConnection connection, DbTransaction transaction, InvitationRevocation revocation, StoredMembership actor, CancellationToken ct)
    {
        if (!actor.IsOwner)
        {
            return MemberChangeStatus.NotOwner;
        }

        var revoked = await ExecuteAsync(
            connection, transaction, _invitations.RevokeSql,
            command => Bind(
                command,
                ("@id", Id(revocation.Invitation)),
                ("@tenant_id", Id(revocation.Tenant)),
                ("@now", Instant(revocation.Now))),
            ct).ConfigureAwait(false);

        return revoked == 0 ? MemberChangeStatus.NoSuchInvitation : MemberChangeStatus.Changed;
    }

    /// <summary>
    /// Revokes every live invitation <paramref name="member"/> created in <paramref name="tenant"/> — the sweep of a
    /// demotion or removal of an owner, in its transaction (D6) — and answers how many it revoked.
    /// </summary>
    private Task<int> SweepInvitationsAsync(
        DbConnection connection, DbTransaction transaction, TenantId tenant, UserId member, DateTimeOffset now, CancellationToken ct) =>
        ExecuteAsync(
            connection, transaction, _invitations.RevokeCreatedBySql,
            command => BindMemberKey(command, tenant, member, ("@now", Instant(now))),
            ct);

    private static void BindInvitation(DbCommand command, CreateInvitationRequest request) => Bind(
        command,
        ("@id", Id(request.Id)),
        ("@tenant_id", Id(request.Tenant)),
        ("@code_hash", request.CodeHash),
        ("@roles", TenantTables.RolesText(request.Roles)),
        ("@grants_owner", request.GrantsOwner ? 1 : 0),
        ("@email", request.Email),
        ("@max_uses", request.MaxUses),
        ("@expires_at", Instant(request.ExpiresAt)),
        ("@created_by", Id(request.Actor)),
        ("@now", Instant(request.Now)));
}
