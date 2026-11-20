using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Tenants;
using System.Data.Common;
using System.Globalization;
using Xunit;

#pragma warning disable EF1001

namespace MMLib.Alvo.Tests.Ef;

/// <summary>
/// Test-only rows of the tenant registry that no port member writes the way a fact needs (S3 plan D15): members arrive
/// through invitations (S4) and a personal tenant through a sign-in (Task 5), and an invitation into a personal tenant is
/// one no port member writes at all — so the facts seed them through <see cref="TenantTables"/>' and
/// <see cref="InvitationTables"/>' own statements — the store's SQL, never a copy of it — creating the tables first if no
/// store call has yet.
/// </summary>
public static class TenantSeed
{
    /// <summary>Adds <paramref name="user"/> to <paramref name="tenant"/> with <paramref name="roles"/>.</summary>
    /// <param name="services">The host container.</param>
    /// <param name="tenant">The tenant.</param>
    /// <param name="user">The new member.</param>
    /// <param name="roles">The member's stored roles.</param>
    /// <param name="isOwner">Whether the member is an owner.</param>
    /// <param name="now">When the member joined.</param>
    public static Task AddMemberAsync(
        IServiceProvider services, TenantId tenant, UserId user, IReadOnlyList<string> roles, bool isOwner, DateTimeOffset now) =>
        RunAsync(services, tables => tables.InsertMemberSql, command => BindMember(command, tenant, user, roles, isOwner, now));

    /// <summary>
    /// Adds <paramref name="owner"/>'s personal tenant — kind <c>personal</c>, <c>personal_owner</c> the owner — and its
    /// owner membership with no stored roles (D5).
    /// </summary>
    /// <param name="services">The host container.</param>
    /// <param name="tenant">The personal tenant's id.</param>
    /// <param name="owner">The account it belongs to.</param>
    /// <param name="name">Its name.</param>
    /// <param name="now">When it was created.</param>
    public static async Task AddPersonalTenantAsync(
        IServiceProvider services, TenantId tenant, UserId owner, string name, DateTimeOffset now)
    {
        await RunAsync(services, tables => tables.InsertTenantSql, command => BindPersonal(command, tenant, owner, name, now));
        await AddMemberAsync(services, tenant, owner, [], isOwner: true, now);
    }

    /// <summary>
    /// Inserts the invitation <paramref name="request"/> describes with the store's own statement, bypassing every check
    /// the store makes on creating one — e.g. into a personal tenant (S4 plan D4: the acceptance must refuse it on its own).
    /// </summary>
    /// <param name="services">The host container.</param>
    /// <param name="request">The invitation.</param>
    internal static Task AddInvitationAsync(IServiceProvider services, CreateInvitationRequest request) =>
        RunAsync(services, (_, invitations) => invitations.InsertSql, command => BindInvitation(command, request));

    /// <summary>The <c>invitation_id</c> of <paramref name="user"/>'s membership in <paramref name="tenant"/> (S4 plan D15).</summary>
    /// <param name="services">The host container.</param>
    /// <param name="tenant">The tenant.</param>
    /// <param name="user">The member.</param>
    public static async Task<Guid?> InvitationIdOfAsync(IServiceProvider services, TenantId tenant, UserId user)
    {
        await using var connection = await OpenAsync(services);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT invitation_id FROM {Tables(services).Members} WHERE tenant_id = @tenant_id AND user_id = @user_id";
        Add(command, "@tenant_id", Id(tenant.Value));
        Add(command, "@user_id", Id(user.Value));
        var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return value is string text ? Guid.Parse(text, CultureInfo.InvariantCulture) : null;
    }

    private static Task RunAsync(IServiceProvider services, Func<TenantTables, string> sql, Action<DbCommand> bind) =>
        RunAsync(services, (tables, _) => sql(tables), bind);

    private static async Task RunAsync(
        IServiceProvider services, Func<TenantTables, InvitationTables, string> sql, Action<DbCommand> bind)
    {
        await using var connection = await OpenAsync(services);
        await using var command = connection.CreateCommand();
        command.CommandText = sql(Tables(services), new InvitationTables(Prefix(services)));
        bind(command);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<DbConnection> OpenAsync(IServiceProvider services)
    {
        var connection = services.GetRequiredService<RelationalConnectionFactory>().Create();
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await Tables(services).EnsureAsync(connection, TestContext.Current.CancellationToken);
        await new InvitationTables(Prefix(services)).EnsureAsync(connection, TestContext.Current.CancellationToken);
        return connection;
    }

    private static TenantTables Tables(IServiceProvider services) => new(Prefix(services));

    private static string Prefix(IServiceProvider services) => services.GetRequiredService<IOptions<AlvoOptions>>().Value.SchemaPrefix;

    private static void BindInvitation(DbCommand command, CreateInvitationRequest request)
    {
        Add(command, "@id", Id(request.Id));
        Add(command, "@tenant_id", Id(request.Tenant.Value));
        Add(command, "@code_hash", request.CodeHash);
        Add(command, "@roles", TenantTables.RolesText(request.Roles));
        Add(command, "@grants_owner", request.GrantsOwner ? 1 : 0);
        Add(command, "@email", (object?)request.Email ?? DBNull.Value);
        Add(command, "@max_uses", request.MaxUses);
        Add(command, "@expires_at", StoredInstant.Text(request.ExpiresAt.ToUniversalTime()));
        Add(command, "@created_by", Id(request.Actor.Value));
        Add(command, "@now", StoredInstant.Text(request.Now.ToUniversalTime()));
    }

    private static void BindMember(
        DbCommand command, TenantId tenant, UserId user, IReadOnlyList<string> roles, bool isOwner, DateTimeOffset now)
    {
        Add(command, "@tenant_id", Id(tenant.Value));
        Add(command, "@user_id", Id(user.Value));
        Add(command, "@roles", TenantTables.RolesText(roles));
        Add(command, "@is_owner", isOwner ? 1 : 0);
        Add(command, "@now", StoredInstant.Text(now.ToUniversalTime()));
    }

    private static void BindPersonal(DbCommand command, TenantId tenant, UserId owner, string name, DateTimeOffset now)
    {
        Add(command, "@tenant_id", Id(tenant.Value));
        Add(command, "@kind", TenantTables.PersonalKind);
        Add(command, "@name", name);
        Add(command, "@created_by", Id(owner.Value));
        Add(command, "@now", StoredInstant.Text(now.ToUniversalTime()));
        Add(command, "@personal_owner", Id(owner.Value));
    }

    private static void Add(DbCommand command, string name, object value) => RelationalSqlBatch.AddParameter(command, name, value);

    private static string Id(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);
}
