using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Accounts;
using MMLib.Alvo.Api.Internal;

namespace MMLib.Alvo.Management.Internal;

/// <summary>The operator's route over end users' accounts (accounts spec §5.6), gated exactly as the management routes are.</summary>
internal static partial class ManagementEndpoints
{
    private const string AccountDisabledRequired =
        "Send whether the account should be disabled: {\"disabled\": true} disables it and ends every session it has, "
        + "{\"disabled\": false} lets the person sign in again.";

    /// <summary>
    /// <c>PUT {prefix}/projects/{project}/accounts/{account}/disabled</c> —
    /// <see cref="IAlvoAccountAdministration.SetDisabledAsync"/>, mapped only when end-user sign-in is registered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Mapped only when the service exists</b>, the <c>MapUsers</c> precedent: <c>AddAlvoAccounts</c> registers it, and a
    /// deployment without end-user sign-in has no account to disable, so the route is absent (404) rather than present
    /// and answering 500. Asked of the container's registrations, so nothing is constructed at mapping time.
    /// </para>
    /// <para>
    /// <b>The gate is the whole of the authorization</b>, because the port is internal and this route is its only caller —
    /// see <see cref="IAlvoAccountAdministration"/>'s remarks for the shape it takes when that stops being true. The
    /// delegate admits through <see cref="ManagementGate"/> as well, as the operations port does, which is what answers a
    /// project this build does not serve with its named 404.
    /// </para>
    /// <para>
    /// <b>Its endpoint name is qualified</b> (<c>Alvo.Management.Accounts.SetDisabledAsync</c>): the users' route stands for
    /// <c>IAlvoUserAdministration.SetDisabledAsync</c>, a member of the same name, and endpoint names must be unique. The
    /// <see cref="ManagementRoute"/> metadata still names the member with <c>nameof</c>.
    /// </para>
    /// <para>
    /// <b>The body is optional at binding and <c>disabled</c> is required after the gate</b> (the operations routes'
    /// reason): a body of <c>{}</c> must not read as "enable".
    /// </para>
    /// </remarks>
    /// <param name="endpoints">The route builder, for asking whether an implementation is registered.</param>
    /// <param name="group">The group to map into.</param>
    private static void MapAccounts(IEndpointRouteBuilder endpoints, RouteGroupBuilder group)
    {
        if (endpoints.ServiceProvider.GetService<IServiceProviderIsService>() is not { } registrations
            || !registrations.IsService(typeof(IAlvoAccountAdministration)))
        {
            return;
        }

        Gate(
            group.MapPut(
                "/projects/{project}/accounts/{account:guid}/disabled",
                (string project, Guid account, ManagementAccountDisabledFlag? body,
                    ManagementGate gate, IAlvoAccountAdministration accounts, CancellationToken ct) =>
                        Rendered(() => SetAccountDisabledAsync(project, account, body, gate, accounts, ct))),
            new ManagementRoute(nameof(IAlvoAccountAdministration.SetDisabledAsync), ManagementOperation.ManageAccounts),
            qualifier: "Accounts");
    }

    /// <summary>Admits the caller to the project, then disables or enables the account: 204, or a named 404.</summary>
    private static async Task<IResult> SetAccountDisabledAsync(
        string project,
        Guid account,
        ManagementAccountDisabledFlag? body,
        ManagementGate gate,
        IAlvoAccountAdministration accounts,
        CancellationToken ct)
    {
        gate.Admit(ManagementOperation.ManageAccounts, project);
        if (body?.Disabled is not { } disabled)
        {
            return ProblemResultFactory.ManagementValidation(AccountDisabledRequired);
        }

        return await accounts.SetDisabledAsync(new UserId(account), disabled, ct).ConfigureAwait(false)
            ? Results.NoContent()
            : ProblemResultFactory.ManagementNotFound(
                $"There is no account '{account}'. Account ids are the 'sub' of the access tokens Alvo issues.");
    }
}
