using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Api.Tests;
using MMLib.Alvo.Tenants;
using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Tests.Accounts;

/// <summary>
/// The world's tenant registry, for this suite only (the <see cref="Store"/> precedent): <see cref="ITenantStore"/> is
/// internal to Abstractions, which grants this suite.
/// </summary>
/// <remarks>
/// <b>A member is seeded with the registry's own column list</b> (S3 plan D15: members arrive through invitations, in S4).
/// <c>TenantSeed</c> runs <c>TenantTables</c>' statement, but that type is internal to the EF adapter, which grants this
/// suite nothing — and no new grant is opened for a seed. The row matches what the store writes: roles a JSON array of
/// names, ids and the instant as text, the flag 0/1.
/// </remarks>
internal sealed partial class AccountsWorld
{
    /// <summary>Gets the tenant registry the host reads memberships from.</summary>
    internal ITenantStore Tenants => Api.Services.GetRequiredService<ITenantStore>();

    /// <summary>Adds <paramref name="user"/> to <paramref name="tenant"/> with <paramref name="roles"/>.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="user">The new member.</param>
    /// <param name="roles">The member's stored roles, verbatim.</param>
    /// <param name="isOwner">Whether the member is an owner.</param>
    internal async Task AddMemberAsync(TenantId tenant, UserId user, IReadOnlyList<string> roles, bool isOwner = false)
    {
        var prefix = Api.Services.GetRequiredService<IOptions<AlvoOptions>>().Value.SchemaPrefix;
        await using var connection = Api.Database.Connect();
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO {prefix}{AlvoFrameworkTables.TenantMembersSuffix} (tenant_id, user_id, roles, is_owner, joined_at)
            VALUES (@tenant_id, @user_id, @roles, @is_owner, @now)
            """;
        Add(command, "@tenant_id", tenant.Value.ToString("D", CultureInfo.InvariantCulture));
        Add(command, "@user_id", user.Value.ToString("D", CultureInfo.InvariantCulture));
        Add(command, "@roles", JsonSerializer.Serialize(roles));
        Add(command, "@is_owner", isOwner ? 1 : 0);
        Add(command, "@now", Clock.GetUtcNow().ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>The account <paramref name="access"/> names, read from <c>GET /user</c>.</summary>
    /// <param name="access">The access token.</param>
    internal async Task<UserId> UserIdAsync(string access) => new((await UserAsync(access))["id"]!.GetValue<Guid>());

    /// <summary>The <c>memberships</c> <c>GET /user</c> answers <paramref name="access"/>'s person.</summary>
    /// <param name="access">The access token.</param>
    internal async Task<JsonArray> MembershipsAsync(string access) => (await UserAsync(access))["memberships"]!.AsArray();

    /// <summary>The personal tenant <c>GET /user</c> lists for <paramref name="access"/>'s person.</summary>
    /// <param name="access">The access token.</param>
    internal async Task<TenantId> PersonalTenantAsync(string access) => new((await MembershipsAsync(access))
        .Single(membership => membership!["kind"]!.GetValue<string>() == "personal")!["tenantId"]!.GetValue<Guid>());

    private async Task<JsonObject> UserAsync(string access)
    {
        using var user = await AsPersonAsync(HttpMethod.Get, "/auth/v1/user", access);
        user.StatusCode.ShouldBe(HttpStatusCode.OK, await user.ReadTextAsync());
        return await user.ReadJsonObjectAsync();
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
