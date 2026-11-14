using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Tenants;

using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// The tenant registry's membership half: renaming, the members list, and changing or removing a member. It builds no
/// command of its own and goes through the main file's.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every write runs in one order</b> (S3 plan D3): bump the tenant's <c>version</c> (no row: <see cref="MemberChangeStatus.NotFound"/>),
/// read the actor's membership (none: <see cref="MemberChangeStatus.NotFound"/> as well — a non-member learns nothing), and
/// only then judge and write. The bump is the tenant's row lock: two owners demoting or removing each other at once are
/// serialised, the second judges against what the first committed — no longer an owner, or no longer a member — and the
/// tenant keeps an owner.
/// </para>
/// <para>
/// <b>The last-owner check is the invariant's, not only the leaving member's.</b> Sequentially it fires only when the sole
/// owner leaves: nobody changes their own membership, and another owner's demotion leaves the actor. It is still judged on
/// every demotion and removal, so a write that ever ran without the bump would refuse rather than strand the tenant.
/// </para>
/// </remarks>
internal sealed partial class EfCoreTenantStore
{
    private delegate Task<MemberChangeStatus> Judgement(
        DbConnection connection, DbTransaction transaction, StoredMembership actor, CancellationToken ct);

    /// <inheritdoc/>
    public Task<MemberChangeStatus> RenameAsync(TenantId tenant, UserId actor, string name, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(name);

        return MemberWriteAsync(
            tenant,
            actor,
            async (connection, transaction, membership, token) =>
            {
                if (!membership.IsOwner)
                {
                    return MemberChangeStatus.NotOwner;
                }

                await ExecuteAsync(
                    connection, transaction, _tables.RenameSql,
                    command => Bind(command, ("@name", name), ("@tenant_id", Id(tenant))), token).ConfigureAwait(false);
                return MemberChangeStatus.Changed;
            },
            ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<MemberView>?> ListMembersAsync(TenantId tenant, UserId actor, CancellationToken ct)
    {
        var members = await ReadAsync(
            _tables.MembersSql,
            command => Bind(command, ("@tenant_id", Id(tenant)), ("@actor_id", Id(actor))),
            TenantTables.ReadMember,
            ct).ConfigureAwait(false);

        return members.Count == 0 ? null : members;
    }

    /// <inheritdoc/>
    public Task<MemberChangeStatus> UpdateMemberAsync(MemberUpdate update, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(update);

        return MemberWriteAsync(
            update.Tenant,
            update.Actor,
            (connection, transaction, actor, token) => UpdatedAsync(connection, transaction, update, actor, token),
            ct);
    }

    /// <inheritdoc/>
    public Task<MemberChangeStatus> RemoveMemberAsync(MemberRemoval removal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(removal);

        return MemberWriteAsync(
            removal.Tenant,
            removal.Actor,
            (connection, transaction, actor, token) => RemovedAsync(connection, transaction, removal, actor, token),
            ct);
    }

    private async Task<MemberChangeStatus> UpdatedAsync(
        DbConnection connection, DbTransaction transaction, MemberUpdate update, StoredMembership actor, CancellationToken ct)
    {
        if (update.Actor == update.Member)
        {
            return MemberChangeStatus.Self;
        }

        if (OwnersOnly(actor) is { } refused)
        {
            return refused;
        }

        var member = await MembershipAsync(connection, transaction, update.Tenant, update.Member, ct).ConfigureAwait(false);
        if (member is null)
        {
            return MemberChangeStatus.NoSuchMember;
        }

        if (update.IsOwner == false && await IsLastOwnerAsync(connection, transaction, member, ct).ConfigureAwait(false))
        {
            return MemberChangeStatus.LastOwner;
        }

        await WriteUpdateAsync(connection, transaction, update, ct).ConfigureAwait(false);
        return MemberChangeStatus.Changed;
    }

    private async Task<MemberChangeStatus> RemovedAsync(
        DbConnection connection, DbTransaction transaction, MemberRemoval removal, StoredMembership actor, CancellationToken ct)
    {
        var leaving = removal.Actor == removal.Member;
        if (actor.Kind == TenantKind.Personal)
        {
            return MemberChangeStatus.PersonalTenant;
        }

        if (!leaving && !actor.IsOwner)
        {
            return MemberChangeStatus.NotOwner;
        }

        var member = leaving
            ? actor
            : await MembershipAsync(connection, transaction, removal.Tenant, removal.Member, ct).ConfigureAwait(false);
        if (member is null)
        {
            return MemberChangeStatus.NoSuchMember;
        }

        if (await IsLastOwnerAsync(connection, transaction, member, ct).ConfigureAwait(false))
        {
            return MemberChangeStatus.LastOwner;
        }

        await ExecuteAsync(
            connection, transaction, _tables.RemoveMemberSql, command => BindMemberKey(command, removal.Tenant, removal.Member), ct)
            .ConfigureAwait(false);
        return MemberChangeStatus.Changed;
    }

    /// <summary>The refusal of a change only an owner may make, of a shared tenant's membership; <see langword="null"/> to go on.</summary>
    private static MemberChangeStatus? OwnersOnly(StoredMembership actor) =>
        !actor.IsOwner ? MemberChangeStatus.NotOwner
        : actor.Kind == TenantKind.Personal ? MemberChangeStatus.PersonalTenant
        : null;

    private async Task<bool> IsLastOwnerAsync(
        DbConnection connection, DbTransaction transaction, StoredMembership member, CancellationToken ct) =>
        member.IsOwner
        && await CountAsync(
            connection, transaction, _tables.OwnersSql, command => Bind(command, ("@tenant_id", Id(member.Tenant))), ct)
            .ConfigureAwait(false) <= 1;

    private async Task WriteUpdateAsync(DbConnection connection, DbTransaction transaction, MemberUpdate update, CancellationToken ct)
    {
        if (update.Roles is { } roles)
        {
            await ExecuteAsync(
                connection, transaction, _tables.SetRolesSql,
                command => BindMemberKey(command, update.Tenant, update.Member, ("@roles", TenantTables.RolesText(roles))),
                ct).ConfigureAwait(false);
        }

        if (update.IsOwner is { } isOwner)
        {
            await ExecuteAsync(
                connection, transaction, _tables.SetOwnerSql,
                command => BindMemberKey(command, update.Tenant, update.Member, ("@is_owner", isOwner ? 1 : 0)),
                ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One membership write: bump the tenant's version, read the actor's membership, then <paramref name="judge"/> — in one
    /// transaction, committed only on <see cref="MemberChangeStatus.Changed"/>, so a refusal leaves even the bump undone.
    /// </summary>
    private Task<MemberChangeStatus> MemberWriteAsync(TenantId tenant, UserId actor, Judgement judge, CancellationToken ct) =>
        InTransactionAsync(
            async (connection, transaction, token) =>
            {
                if (await ExecuteAsync(
                    connection, transaction, _tables.BumpVersionSql, command => Bind(command, ("@tenant_id", Id(tenant))), token)
                    .ConfigureAwait(false) != 1)
                {
                    return MemberChangeStatus.NotFound;
                }

                return await MembershipAsync(connection, transaction, tenant, actor, token).ConfigureAwait(false) is { } membership
                    ? await judge(connection, transaction, membership, token).ConfigureAwait(false)
                    : MemberChangeStatus.NotFound;
            },
            status => status == MemberChangeStatus.Changed,
            ct);

    private async Task<StoredMembership?> MembershipAsync(
        DbConnection connection, DbTransaction transaction, TenantId tenant, UserId user, CancellationToken ct)
    {
        var found = await RelationalSqlBatch.ReadRowsAsync(
            connection, transaction, _tables.MembershipSql,
            command => Bind(command, ("@user_id", Id(user)), ("@tenant_id", Id(tenant))),
            reader => TenantTables.ReadMembership(reader),
            ct).ConfigureAwait(false);

        return found.Count == 0 ? null : found[0];
    }

    private static void BindMemberKey(DbCommand command, TenantId tenant, UserId member, params (string Name, object? Value)[] values)
    {
        Bind(command, values);
        Bind(command, ("@tenant_id", Id(tenant)), ("@user_id", Id(member)));
    }
}
