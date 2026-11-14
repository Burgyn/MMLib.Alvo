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
/// Test-only rows of the tenant registry that no port member writes yet (S3 plan D15): members arrive through invitations
/// (S4) and a personal tenant through a sign-in (Task 5), so the facts seed both through <see cref="TenantTables"/>' own
/// statements — the store's SQL, never a copy of it — creating the tables first if no store call has yet.
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

    private static async Task RunAsync(IServiceProvider services, Func<TenantTables, string> sql, Action<DbCommand> bind)
    {
        var tables = new TenantTables(services.GetRequiredService<IOptions<AlvoOptions>>().Value.SchemaPrefix);
        await using var connection = services.GetRequiredService<RelationalConnectionFactory>().Create();
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await tables.EnsureAsync(connection, TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql(tables);
        bind(command);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
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
