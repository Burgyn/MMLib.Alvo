using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using MMLib.Alvo.Accounts;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Tenants;
using MMLib.Alvo.Tenants.SelfService;

namespace MMLib.Alvo.Management.Internal;

/// <summary>
/// The operator's route that makes an end user's account an owner of a shared tenant (#543): how a tenant whose only owner
/// is banned gets an owner again without enabling the banned person.
/// </summary>
internal static partial class ManagementEndpoints
{
    /// <summary>
    /// <c>PUT {prefix}/projects/{project}/tenants/{tenant}/owners/{account}</c> —
    /// <see cref="IAlvoAccountAdministration.GrantOwnershipAsync"/>, mapped beside the account route and only where it is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Gated exactly as the ban</b> (<see cref="ManagementOperation.ManageAccounts"/>, <c>admin</c>): whoever may ban an
    /// owner may give the tenant another (design §9.1, Q2). Idempotent, as a <c>PUT</c>: an owner granted again stays one.
    /// </para>
    /// <para>
    /// <b>The body is optional</b>: <c>{}</c> or none keeps a member's roles and gives a new member none;
    /// <c>{"roles": [...]}</c> sets them, each in the applied <c>tenancy.selfService.assignableRoles</c> — nobody changes
    /// their own membership (S3 D7), so an operator-made owner could otherwise never hold a role in that tenant.
    /// </para>
    /// </remarks>
    /// <param name="group">The group to map into.</param>
    private static void MapOwnership(RouteGroupBuilder group) => Gate(
        group.MapPut(
            "/projects/{project}/tenants/{tenant:guid}/owners/{account:guid}",
            (string project, Guid tenant, Guid account, ManagementOwnerGrant? body, ManagementGate gate,
                IAlvoAccountAdministration accounts, IPolicyCatalogProvider catalog, ILoggerFactory loggers, CancellationToken ct) =>
                    Rendered(() => GrantOwnershipAsync(
                        new OwnershipCall(project, new TenantId(tenant), new UserId(account), body?.Roles),
                        gate, accounts, catalog, loggers.CreateLogger(AccountsLogCategory), ct))),
        new ManagementRoute(nameof(IAlvoAccountAdministration.GrantOwnershipAsync), ManagementOperation.ManageAccounts),
        qualifier: "Accounts");

    /// <summary>
    /// Admits the caller, checks the roles against the applied descriptor, then grants: 200 with the membership, logged as
    /// <see cref="OwnershipGranted"/>; 404 naming the unknown tenant or account; 409 for a disabled account or a personal
    /// tenant; 422 for a role the descriptor does not let a membership carry.
    /// </summary>
    private static async Task<IResult> GrantOwnershipAsync(
        OwnershipCall call,
        ManagementGate gate,
        IAlvoAccountAdministration accounts,
        IPolicyCatalogProvider catalog,
        ILogger logger,
        CancellationToken ct)
    {
        gate.Admit(ManagementOperation.ManageAccounts, call.Project);
        if (RolesRefusal(call.Roles, catalog.Current?.SelfService ?? SelfServicePolicy.None) is { } refused)
        {
            return ProblemResultFactory.ManagementValidation(refused);
        }

        var outcome = await accounts.GrantOwnershipAsync(call.Tenant, call.Account, call.Roles, ct).ConfigureAwait(false);
        if (outcome is not { Status: OwnershipGrantStatus.Granted, Membership: { } membership })
        {
            return GrantRefusal(outcome.Status, call);
        }

        OwnershipGranted(logger, gate.Actor, call.Account.Value, call.Tenant.Value);
        return Results.Ok(new MembershipResponse(
            membership.Tenant.Value,
            membership.Name,
            MembershipResponse.SharedKind,
            [.. membership.Roles.Order(StringComparer.Ordinal)],
            membership.IsOwner));
    }

    /// <summary>The refusal of <paramref name="roles"/>, or <see langword="null"/> when each is assignable and named once.</summary>
    private static string? RolesRefusal(IReadOnlyList<string>? roles, SelfServicePolicy policy)
    {
        if (roles is null)
        {
            return null;
        }

        var assignable = SelfServiceRoles.Assignable(policy);
        var listed = assignable.Count == 0 ? "none" : string.Join(", ", assignable);
        return roles.Distinct(StringComparer.Ordinal).Count() != roles.Count
            ? "A role is named twice. Send each role once."
            : roles.Any(role => !assignable.Contains(role, StringComparer.Ordinal))
                ? $"A role is not one a membership can carry; tenancy.selfService.assignableRoles lists {listed}. "
                    + "Send only those roles, or no roles to keep the member's own."
                : null;
    }

    private static IResult GrantRefusal(OwnershipGrantStatus status, OwnershipCall call) => status switch
    {
        OwnershipGrantStatus.NoSuchTenant => ProblemResultFactory.ManagementNotFound(
            $"There is no tenant '{call.Tenant.Value}'. A ban of an account answers the ids of the tenants it left without an enabled owner."),
        OwnershipGrantStatus.NoSuchAccount => ProblemResultFactory.ManagementNotFound(
            $"There is no account '{call.Account.Value}'. Account ids are the 'sub' of the access tokens Alvo issues."),
        OwnershipGrantStatus.AccountDisabled => ProblemResultFactory.ManagementConflict(
            $"The account '{call.Account.Value}' is disabled, and a disabled account is never made an owner. Enable it first, or choose another account."),
        _ => ProblemResultFactory.ManagementConflict(
            $"The tenant '{call.Tenant.Value}' is a personal tenant, which has exactly one member. Only a shared tenant takes another owner."),
    };

    /// <summary>The one record of an operator's ownership grant, until the audit component (#42) keeps one — ids only.</summary>
    /// <param name="logger">The logger to write through.</param>
    /// <param name="actor">The management caller.</param>
    /// <param name="account">The account made an owner.</param>
    /// <param name="tenant">The tenant.</param>
    [LoggerMessage(Level = LogLevel.Information, Message = "Management: {Actor} made end-user account {Account} an owner of tenant {Tenant}.")]
    private static partial void OwnershipGranted(ILogger logger, string actor, Guid account, Guid tenant);

    /// <summary>One ownership grant as the route read it.</summary>
    /// <param name="Project">The project the path names.</param>
    /// <param name="Tenant">The tenant.</param>
    /// <param name="Account">The account.</param>
    /// <param name="Roles">The roles the body names, or <see langword="null"/>.</param>
    private sealed record OwnershipCall(string Project, TenantId Tenant, UserId Account, IReadOnlyList<string>? Roles);
}
