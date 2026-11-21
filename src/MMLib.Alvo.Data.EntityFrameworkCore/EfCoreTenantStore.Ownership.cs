using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Tenants;

using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// The tenant registry's operator half (#543): making an account an owner of a shared tenant, and listing the tenants a ban
/// left with no enabled owner. It builds no command of its own and goes through the main file's.
/// </summary>
/// <remarks>
/// <para>
/// <b>A grant keeps the lock order every accounts and tenancy write keeps</b> (hardening design §2.3): the account's row
/// first — <see cref="TenantTables.LockAccountSql"/>, which also refuses a disabled account, so a ban racing the grant either
/// commits first and refuses it, or waits for it and leaves an owner it then disables like any other — then the tenant's
/// version bump, which serialises the grant with every member write of the tenant, then the membership.
/// </para>
/// <para>
/// <b>It revokes nothing and records no invitation</b>: <c>invitation_id</c> stays <c>NULL</c>, as for a creator. A member's
/// roles are kept unless the grant names roles; a new member holds the roles it names, or none.
/// </para>
/// </remarks>
internal sealed partial class EfCoreTenantStore
{
    /// <inheritdoc/>
    public Task<OwnershipGrantOutcome> GrantOwnershipAsync(OwnershipGrant grant, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(grant);

        return InTransactionAsync(
            (connection, transaction, token) => GrantedAsync(connection, transaction, grant, token),
            outcome => outcome.Status == OwnershipGrantStatus.Granted,
            ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<OrphanedTenant>> ListOrphanedTenantsAsync(UserId account, CancellationToken ct) =>
        await ReadAsync(
            _tables.OrphanedOwnedSql,
            command => Bind(command, ("@user_id", Id(account))),
            reader => new OrphanedTenant(new TenantId(GuidOf(reader.GetString(0))), reader.GetString(1)),
            ct).ConfigureAwait(false);

    private async Task<OwnershipGrantOutcome> GrantedAsync(
        DbConnection connection, DbTransaction transaction, OwnershipGrant grant, CancellationToken ct)
    {
        if (await AccountRefusalAsync(connection, transaction, grant.Account, ct).ConfigureAwait(false) is { } refused)
        {
            return new OwnershipGrantOutcome(refused, null);
        }

        if (await TenantRefusalAsync(connection, transaction, grant.Tenant, ct).ConfigureAwait(false) is { } absent)
        {
            return new OwnershipGrantOutcome(absent, null);
        }

        await OwnerWrittenAsync(connection, transaction, grant, ct).ConfigureAwait(false);
        return new OwnershipGrantOutcome(
            OwnershipGrantStatus.Granted, await MembershipAsync(connection, transaction, grant.Tenant, grant.Account, ct).ConfigureAwait(false));
    }

    /// <summary>Takes the enabled account's row lock; the refusal when it is disabled or unknown.</summary>
    private async Task<OwnershipGrantStatus?> AccountRefusalAsync(
        DbConnection connection, DbTransaction transaction, UserId account, CancellationToken ct)
    {
        var bind = (DbCommand command) => Bind(command, ("@account_id", Id(account)));
        if (await ExecuteAsync(connection, transaction, _tables.LockAccountSql, bind, ct).ConfigureAwait(false) == 1)
        {
            return null;
        }

        var exists = await RelationalSqlBatch.ReadRowsAsync(connection, transaction, _authTables.AccountExistsSql, bind, static _ => true, ct)
            .ConfigureAwait(false);
        return exists.Count == 1 ? OwnershipGrantStatus.AccountDisabled : OwnershipGrantStatus.NoSuchAccount;
    }

    /// <summary>Bumps the tenant's version; the refusal when it is unknown or personal.</summary>
    private async Task<OwnershipGrantStatus?> TenantRefusalAsync(
        DbConnection connection, DbTransaction transaction, TenantId tenant, CancellationToken ct)
    {
        var bind = (DbCommand command) => Bind(command, ("@tenant_id", Id(tenant)));
        if (await ExecuteAsync(connection, transaction, _tables.BumpVersionSql, bind, ct).ConfigureAwait(false) != 1)
        {
            return OwnershipGrantStatus.NoSuchTenant;
        }

        var kinds = await RelationalSqlBatch.ReadRowsAsync(
            connection, transaction, _tables.KindSql, bind, reader => TenantTables.KindOf(reader.GetString(0)), ct).ConfigureAwait(false);
        return kinds.Single() == TenantKind.Personal ? OwnershipGrantStatus.PersonalTenant : null;
    }

    /// <summary>Makes the account an owner: its membership's flag (and roles, when named), or a new owner membership.</summary>
    private async Task OwnerWrittenAsync(DbConnection connection, DbTransaction transaction, OwnershipGrant grant, CancellationToken ct)
    {
        if (await MembershipAsync(connection, transaction, grant.Tenant, grant.Account, ct).ConfigureAwait(false) is null)
        {
            await ExecuteAsync(
                connection, transaction, _tables.InsertMemberSql,
                command => BindMember(command, grant.Tenant, grant.Account, grant.Roles ?? [], isOwner: true, grant.Now),
                ct).ConfigureAwait(false);
            return;
        }

        await ExecuteAsync(
            connection, transaction, _tables.SetOwnerSql,
            command => BindMemberKey(command, grant.Tenant, grant.Account, ("@is_owner", 1)), ct).ConfigureAwait(false);
        if (grant.Roles is { } roles)
        {
            await ExecuteAsync(
                connection, transaction, _tables.SetRolesSql,
                command => BindMemberKey(command, grant.Tenant, grant.Account, ("@roles", TenantTables.RolesText(roles))), ct)
                .ConfigureAwait(false);
        }
    }
}
