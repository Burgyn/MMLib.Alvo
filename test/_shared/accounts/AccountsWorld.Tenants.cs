using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Api.Tests;
using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Tests.Accounts;

/// <summary>
/// The world's tenants as a person sees them over HTTP, and a member seeded beside them — on both engines.
/// </summary>
/// <remarks>
/// <b>A member is seeded with the registry's own column list</b> (S3 plan D15), for the facts that are about a member
/// and not about how they joined — joining by invitation is <see cref="InvitationEngineFacts"/>' subject.
/// <c>TenantSeed</c> runs <c>TenantTables</c>' statement, but that type is internal to the EF adapter, which grants neither
/// suite that links this file — and no new grant is opened for a seed. The table is named by its suffix as the framework
/// reserves it (<c>AlvoFrameworkTables.TenantMembersSuffix</c>, internal to Abstractions, which the PostgreSQL suite is not
/// granted); the row matches what the store writes: roles a JSON array of names, ids and the instant as text, the flag 0/1.
/// </remarks>
internal sealed partial class AccountsWorld
{
    private const string TenantMembersSuffix = "_tenant_members";

    private const string TenantInvitationsSuffix = "_tenant_invitations";

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
            INSERT INTO {prefix}{TenantMembersSuffix} (tenant_id, user_id, roles, is_owner, joined_at)
            VALUES (@tenant_id, @user_id, @roles, @is_owner, @now)
            """;
        Add(command, "@tenant_id", tenant.Value.ToString("D", CultureInfo.InvariantCulture));
        Add(command, "@user_id", user.Value.ToString("D", CultureInfo.InvariantCulture));
        Add(command, "@roles", JsonSerializer.Serialize(roles));
        Add(command, "@is_owner", isOwner ? 1 : 0);
        Add(command, "@now", Clock.GetUtcNow().ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Inserts a live, single-use invitation into <paramref name="tenant"/> with the registry's own column list, bypassing
    /// every check the creation route makes — e.g. into a personal tenant (S4 plan D4: the acceptance must refuse it on its
    /// own). The table exists once any invitation route or store call has run; the world's boot creates it too.
    /// </summary>
    /// <param name="tenant">The tenant it invites into.</param>
    /// <param name="createdBy">Its creator.</param>
    /// <param name="codeHash">The SHA-256 of its code, lowercase hex.</param>
    internal async Task AddInvitationAsync(TenantId tenant, UserId createdBy, string codeHash)
    {
        var prefix = Api.Services.GetRequiredService<IOptions<AlvoOptions>>().Value.SchemaPrefix;
        await using var connection = Api.Database.Connect();
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO {prefix}{TenantInvitationsSuffix} (id, tenant_id, code_hash, roles, grants_owner, email, max_uses, uses, expires_at, created_by, created_at, revoked_at)
            VALUES (@id, @tenant_id, @code_hash, @roles, 0, NULL, 1, 0, @expires_at, @created_by, @now, NULL)
            """;
        var now = Clock.GetUtcNow().ToUniversalTime();
        Add(command, "@id", Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture));
        Add(command, "@tenant_id", tenant.Value.ToString("D", CultureInfo.InvariantCulture));
        Add(command, "@code_hash", codeHash);
        Add(command, "@roles", "[]");
        Add(command, "@expires_at", now.AddDays(7).ToString("O", CultureInfo.InvariantCulture));
        Add(command, "@created_by", createdBy.Value.ToString("D", CultureInfo.InvariantCulture));
        Add(command, "@now", now.ToString("O", CultureInfo.InvariantCulture));
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
