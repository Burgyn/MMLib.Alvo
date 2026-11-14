using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Tenants;

using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// The tenant registry's invitation half: an owner creates, lists and revokes invitations (S4 plan D6, D10). It builds no
/// command of its own and goes through the main file's.
/// </summary>
/// <remarks>
/// <b>Creating and revoking are membership writes</b>: both run through <see cref="MemberWriteAsync"/>, so they bump the
/// tenant's <c>version</c> first and judge the actor's ownership after it. That is what serialises a creation with its
/// creator's demotion or removal, whose sweep (<see cref="InvitationTables.RevokeCreatedBySql"/>) then either sees the new
/// invitation or has already made the creator a non-owner — never an invitation of a non-owner left live.
/// </remarks>
internal sealed partial class EfCoreTenantStore
{
    /// <inheritdoc/>
    public async Task<CreateInvitationOutcome> CreateInvitationAsync(CreateInvitationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var status = await MemberWriteAsync(
            request.Tenant,
            request.Actor,
            (connection, transaction, actor, token) => InvitedAsync(connection, transaction, request, actor, token),
            ct).ConfigureAwait(false);

        return status == MemberChangeStatus.Changed
            ? new CreateInvitationOutcome(status, StoredView(request))
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

    private async Task<MemberChangeStatus> InvitedAsync(
        DbConnection connection, DbTransaction transaction, CreateInvitationRequest request, StoredMembership actor, CancellationToken ct)
    {
        if (actor.Kind == TenantKind.Personal)
        {
            return MemberChangeStatus.PersonalTenant;
        }

        if (!actor.IsOwner)
        {
            return MemberChangeStatus.NotOwner;
        }

        await ExecuteAsync(connection, transaction, _invitations.InsertSql, command => BindInvitation(command, request), ct)
            .ConfigureAwait(false);
        return MemberChangeStatus.Changed;
    }

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

    private static InvitationView StoredView(CreateInvitationRequest request) => new(
        request.Id,
        request.Tenant,
        [.. request.Roles],
        request.GrantsOwner,
        request.Email,
        request.MaxUses,
        Uses: 0,
        request.ExpiresAt.ToUniversalTime(),
        request.Actor,
        request.Now.ToUniversalTime(),
        RevokedAt: null);
}
