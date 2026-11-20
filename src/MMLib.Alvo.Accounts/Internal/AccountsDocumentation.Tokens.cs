using Microsoft.OpenApi;
using MMLib.Alvo.Tenants.SelfService;

namespace MMLib.Alvo.Accounts.Internal;

/// <summary>What each app-token operation (spec §7.1, S5 plan D7–D15) publishes.</summary>
/// <remarks>
/// <b>The credential is a member of exactly one answer</b>, the creation's 201 — the registry holds only its hash, and no
/// operation takes it in a URL.
/// </remarks>
internal static partial class AccountsDocumentation
{
    private static Dictionary<string, Operation> WithTokenOperations(Dictionary<string, Operation> operations)
    {
        operations[SelfServiceEndpoints.CreateTokenOperationId] = new(
            "Mint a personal or service token",
            "A signed-in person mints a token for an integration. A `personal` token acts as the person — with the tenant roles "
            + "they hold, read on every request — in one tenant or none; a `service` token acts as a tenant the person owns, "
            + "with roles the person holds there, and only in shared tenants. On the generated Data API and on a command "
            + "bound to an entity, a token reaches only the `scopes` named (`<entity>:read` or `<entity>:write`, never `*`); "
            + "a command's handler then writes as the token's caller under the descriptor's rules, and a host's custom "
            + "endpoints refuse tokens with 403. Every token expires at `expiresAt`. The answer carries the credential "
            + "in `token`, shown this once: present it in the header `header` names. Neither kind ever manages a project or "
            + "mints a token.",
            Bearer: true,
            document => Body("application/json", AccountsSchemas.AppTokenRequestId, "The token to mint.", document),
            CreateTokenResponses,
            SelfServiceEndpoints.TokensTag);
        operations[SelfServiceEndpoints.ListTokensOperationId] = new(
            "List the tokens a person answers for",
            "The person's personal tokens and the service tokens of every shared tenant they own, newest first, each with its "
            + "`status` (`revoked`, else `expired`, else `active`) and when it was last used. Never a credential or a hash: it "
            + "was shown once, at minting.",
            Bearer: true,
            _ => null,
            ListTokensResponses,
            SelfServiceEndpoints.TokensTag);
        operations[SelfServiceEndpoints.RevokeTokenOperationId] = new(
            "Revoke a token",
            "A person revokes a personal token they minted, or a service token they minted or — as an owner — one of a tenant "
            + "they own; it stops working on its next request. Revoking one already revoked is a 204 too. The path names the "
            + "token's key id, never its credential.",
            Bearer: true,
            _ => null,
            RevokeTokenResponses,
            SelfServiceEndpoints.TokensTag);
        return operations;
    }

    private static OpenApiResponses ListTokensResponses(OpenApiDocument document) => Answers(
        document,
        ("200", Response(
            "The person's tokens, newest first.",
            PersonNoStore(),
            Media("application/json", new OpenApiSchema
            {
                Type = JsonSchemaType.Array,
                Items = new OpenApiSchemaReference(AccountsSchemas.AppTokenListItemId, document),
            }))),
        PersonUnauthenticated(document),
        NotAPerson(document),
        Disabled(document, "`feature-disabled`: the descriptor does not declare `auth.accessTokens`; `descriptorKey` names it."));

    private static OpenApiResponses RevokeTokenResponses(OpenApiDocument document) => Answers(
        document,
        ("204", Response("The token is revoked, now or already before.", PersonNoStore())),
        PersonUnauthenticated(document),
        NotAPerson(document),
        Disabled(
            document,
            "`not-found`: no such token, or one the person may not revoke — one answer for both. `feature-disabled`: the "
            + "descriptor does not declare `auth.accessTokens`; `descriptorKey` names it."));

    private static OpenApiResponses CreateTokenResponses(OpenApiDocument document) => Answers(
        document,
        ("201", Response(
            "The token was minted; the body carries its credential, which no later answer repeats.",
            PersonNoStore(),
            Media("application/json", AccountsSchemas.AppTokenId, document))),
        PersonUnauthenticated(document),
        TenantRefusal(
            "403",
            "`forbidden`: an API key or another token was presented — a token never mints one; or a service token was asked "
            + "for a tenant the person is a member of but does not own.",
            document),
        Disabled(
            document,
            "`not-found`: no such tenant, or the person is no member of it — one answer for both. `feature-disabled`: the "
            + "descriptor does not declare `auth.accessTokens`, or the requested kind is off; `descriptorKey` names which "
            + "(`auth.accessTokens`, `auth.accessTokens.personal` or `auth.accessTokens.service`)."),
        TenantRefusal(
            "409",
            "`conflict`: a service token was asked for a personal tenant, or the person already has 100 live tokens.",
            document),
        Unsupported(document),
        TenantRefusal(
            "422",
            "`validation`: `name`, `kind`, `scopes` or `expiresAt` is missing or out of bounds; `tenantId` is missing for a "
            + "service token or not a UUID; `roles` is sent for a personal token, missing or empty for a service token, or "
            + "names a role outside `tenancy.selfService.assignableRoles` or one the person does not hold in the tenant; a "
            + "scope names no entity of the descriptor, is the wildcard or is named twice; `expiresAt` is not an RFC 3339 "
            + "instant with an offset, in the future and at most `auth.accessTokens.maxLifetimeDays` away; or the body "
            + "carries another property.",
            document),
        TokenRateLimited(document));

    private static (string, OpenApiResponse) TokenRateLimited(OpenApiDocument document)
    {
        var (status, response) = TenantRefusal(
            "429",
            "`rate-limited`: this client created tenants and minted tokens more often this minute than "
            + "`Alvo:Api:RateLimits:Create` allows — one budget for both. Wait for `Retry-After`, then send the request again.",
            document);
        response.Headers!["Retry-After"] = Header("Whole seconds until the window admits this client again.", "60");
        return (status, response);
    }
}
