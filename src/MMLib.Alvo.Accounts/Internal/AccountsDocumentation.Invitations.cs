using Microsoft.OpenApi;
using MMLib.Alvo.Tenants.SelfService;

namespace MMLib.Alvo.Accounts.Internal;

/// <summary>What each invitation operation (spec §6.3, S4 plan D9–D11, D13) publishes.</summary>
/// <remarks>
/// <b>No operation takes a code in its URL</b> (spec §6.3): a code travels in a request body and comes back in exactly one
/// answer, the creation's 201 — a URL is written to access logs, browser history and <c>Referer</c> headers.
/// </remarks>
internal static partial class AccountsDocumentation
{
    private static Dictionary<string, Operation> WithInvitationOperations(Dictionary<string, Operation> operations)
    {
        operations[SelfServiceEndpoints.CreateInvitationOperationId] = new(
            "Invite someone into a tenant",
            "An owner of a shared tenant creates an invitation: the tenant roles it grants (each from "
            + "`tenancy.selfService.assignableRoles`), whether it makes an owner, an optional email it is bound to, how many "
            + "people may accept it and when it expires. The answer carries its `code` — shown this once, never again.",
            Bearer: true,
            document => Body("application/json", AccountsSchemas.InvitationRequestId, "The invitation.", document),
            CreateInvitationResponses,
            SelfServiceEndpoints.Tag);
        operations[SelfServiceEndpoints.ListInvitationsOperationId] = new(
            "List a tenant's invitations",
            "Every invitation of a tenant the person owns, newest first, with its status. Never a code.",
            Bearer: true,
            _ => null,
            ListInvitationsResponses,
            SelfServiceEndpoints.Tag);
        operations[SelfServiceEndpoints.RevokeInvitationOperationId] = new(
            "Revoke an invitation",
            "An owner revokes any invitation of the tenant; nobody joins through it afterwards. Revoking one already revoked "
            + "is a 204 too.",
            Bearer: true,
            _ => null,
            RevokeInvitationResponses,
            SelfServiceEndpoints.Tag);
        return operations;
    }

    private static OpenApiResponses CreateInvitationResponses(OpenApiDocument document) => Answers(
        document,
        ("201", Response(
            "The invitation was created; the body carries its code, which no later answer repeats.",
            PersonNoStore(),
            Media("application/json", AccountsSchemas.NewInvitationId, document))),
        PersonUnauthenticated(document),
        OwnersOnly(document, "the person is a member but not an owner"),
        NoSuchInvitationTenant(document, string.Empty),
        PersonalTenant(document),
        Unsupported(document),
        TenantRefusal(
            "422",
            "`validation`: `roles` is missing or a role at `/roles/{i}` is not in `tenancy.selfService.assignableRoles` or is "
            + "named twice; `maxUses` is not 1–100; `expiresAt` is not an RFC 3339 instant with an offset, in the future and at "
            + "most 30 days away; `email` is not one address; an owner-making invitation admits more than one person or lives "
            + "longer than 7 days; or the body carries another property.",
            document),
        InvitationsRateLimited(document));

    private static OpenApiResponses ListInvitationsResponses(OpenApiDocument document) => Answers(
        document,
        ("200", Response("The tenant's invitations, newest first.", PersonNoStore(), Media("application/json", Invitations(document)))),
        PersonUnauthenticated(document),
        OwnersOnly(document, "the person is a member but not an owner"),
        NoSuchInvitationTenant(document, string.Empty),
        PersonalTenant(document),
        InvitationsRateLimited(document));

    private static OpenApiResponses RevokeInvitationResponses(OpenApiDocument document) => Answers(
        document,
        ("204", Response("The invitation is revoked.", PersonNoStore())),
        PersonUnauthenticated(document),
        OwnersOnly(document, "the person is a member but not an owner"),
        NoSuchInvitationTenant(document, ", or the invitation is not one of this tenant's"),
        InvitationsRateLimited(document));

    private static (string, OpenApiResponse) NoSuchInvitationTenant(OpenApiDocument document, string invitation) => Disabled(
        document,
        $"`not-found`: no such tenant, or the person is no member of it{invitation} — one answer for all. `feature-disabled`: "
        + "the descriptor does not declare `tenancy.selfService` or `tenancy.selfService.invitations`; `descriptorKey` names which.");

    private static (string, OpenApiResponse) PersonalTenant(OpenApiDocument document) => TenantRefusal(
        "409", "`conflict`: the tenant is a personal one, which nobody is invited into.", document);

    private static (string, OpenApiResponse) InvitationsRateLimited(OpenApiDocument document)
    {
        var (status, response) = TenantRefusal(
            "429",
            "`rate-limited`: this client sent more invitation requests this minute than `Alvo:Api:RateLimits:Invitations` "
            + "allows — one budget for every invitation route. Wait for `Retry-After`, then send the request again.",
            document);
        response.Headers!["Retry-After"] = Header("Whole seconds until the window admits this client again.", "60");
        return (status, response);
    }

    private static OpenApiSchema Invitations(OpenApiDocument document) => new()
    {
        Type = JsonSchemaType.Array,
        Description = "The invitations, newest first.",
        Items = new OpenApiSchemaReference(AccountsSchemas.InvitationId, document),
    };
}
