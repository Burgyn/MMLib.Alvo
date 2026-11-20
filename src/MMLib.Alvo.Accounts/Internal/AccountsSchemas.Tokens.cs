using Microsoft.OpenApi;
using MMLib.Alvo.Tenants.SelfService;

namespace MMLib.Alvo.Accounts.Internal;

/// <summary>The app-token bodies (S5 plan D12–D14): what a person sends to mint one and what comes back.</summary>
/// <remarks>
/// <b><c>token</c> is a member of exactly one schema</b>, <see cref="AppTokenId"/> — the creation's 201.
/// </remarks>
internal static partial class AccountsSchemas
{
    /// <summary>A person's new app token, as minting one sends it.</summary>
    internal const string AppTokenRequestId = "authAppTokenRequest";

    /// <summary>A just-minted app token: its description and its credential, shown this once.</summary>
    internal const string AppTokenId = "authAppToken";

    private static void AddAppTokenComponents(OpenApiDocument document)
    {
        document.AddComponent(AppTokenRequestId, AppTokenRequest());
        document.AddComponent(AppTokenId, AppToken());
    }

    private static OpenApiSchema AppTokenRequest()
    {
        var schema = Object(
            "A new personal or service token. `name`, `kind`, `scopes` and `expiresAt` are always required; no other property is accepted.",
            ["name", "kind", "scopes", "expiresAt"],
            ("name", new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                MinLength = 1,
                MaxLength = SelfServiceName.MaxLength,
                Description = "What the token is for: trimmed, 1–100 Unicode code points, no control character.",
            }),
            ("kind", Enumerated(
                "`personal`: acts as the person. `service`: acts as a tenant the person owns.",
                Tenants.SelfService.TokenResponse.PersonalKind,
                Tenants.SelfService.TokenResponse.ServiceKind)),
            ("tenantId", new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Format = "uuid",
                Description = "The tenant the token acts in: required for `service` (a shared tenant the person owns); for `personal` optional, "
                    + "and fixed at mint — a personal token with no tenant acts in none.",
            }),
            ("roles", new OpenApiSchema
            {
                Type = JsonSchemaType.Array,
                MinItems = 1,
                UniqueItems = true,
                Description = "`service` only, required: the tenant roles the token acts with, each from `tenancy.selfService.assignableRoles` and "
                    + "held by the person in the tenant. Never sent for `personal` — its roles are its person's, read on every request.",
                Items = new OpenApiSchema { Type = JsonSchemaType.String },
            }),
            ("scopes", new OpenApiSchema
            {
                Type = JsonSchemaType.Array,
                MinItems = 1,
                UniqueItems = true,
                Description = "What the token may do: `<entity>:read` or `<entity>:write`, each naming an entity of the applied descriptor. "
                    + "The wildcard `*` is not accepted.",
                Items = new OpenApiSchema { Type = JsonSchemaType.String },
            }),
            ("expiresAt", new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Format = "date-time",
                Description = "When the token expires: RFC 3339 with an offset, in the future, at most `auth.accessTokens.maxLifetimeDays` days away. "
                    + "A token always expires.",
            }));
        schema.AdditionalPropertiesAllowed = false;
        return schema;
    }

    private static OpenApiSchema AppToken() => Object(
        "A just-minted app token, with its credential.",
        ["keyId", "token", "header", "kind", "name", "tenantId", "roles", "scopes", "expiresAt", "createdAt"],
        ("keyId", Text("The token's key id (`alvo_pat_…` or `alvo_svc_…`): not secret.")),
        ("token", Text("The credential, `keyId.secret`. Shown this once, never again; present it in the header `header` names.")),
        ("header", Text("The request header the credential goes in.")),
        ("kind", Enumerated("`personal` or `service`.", Tenants.SelfService.TokenResponse.PersonalKind, Tenants.SelfService.TokenResponse.ServiceKind)),
        ("name", Text("The token's name.")),
        ("tenantId", new OpenApiSchema
        {
            Type = JsonSchemaType.String | JsonSchemaType.Null,
            Format = "uuid",
            Description = "The tenant the token acts in, or null for a personal token with none.",
        }),
        ("roles", Strings("A service token's tenant roles; empty for a personal one.")),
        ("scopes", Strings("What the token may do, as `<entity>:read` or `<entity>:write`.")),
        ("expiresAt", Instant("When the token expires.")),
        ("createdAt", Instant("When it was minted.")));
}
