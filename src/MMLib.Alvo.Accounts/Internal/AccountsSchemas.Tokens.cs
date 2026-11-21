using Microsoft.OpenApi;
using MMLib.Alvo.Tenants.SelfService;

namespace MMLib.Alvo.Accounts.Internal;

/// <summary>The app-token bodies (S5 plan D12–D14): what a person sends to mint one and what comes back.</summary>
/// <remarks>
/// <b><c>token</c> is a member of exactly one schema</b>, <see cref="AppTokenId"/> — the creation's 201; the list item has none.
/// </remarks>
internal static partial class AccountsSchemas
{
    /// <summary>A person's new app token, as minting one sends it.</summary>
    internal const string AppTokenRequestId = "authAppTokenRequest";

    /// <summary>A just-minted app token: its description and its credential, shown this once.</summary>
    internal const string AppTokenId = "authAppToken";

    /// <summary>One app token as listing shows it: everything but the credential.</summary>
    internal const string AppTokenListItemId = "authAppTokenListItem";

    /// <summary>The members a listed token always carries — and a minted one too, beside its credential (#542 item 1).</summary>
    private static readonly string[] _appTokenRequired =
        ["keyId", "kind", "name", "tenantId", "roles", "scopes", "createdBy", "createdAt", "expiresAt", "lastUsedAt", "revokedAt", "status"];

    private static void AddAppTokenComponents(OpenApiDocument document)
    {
        document.AddComponent(AppTokenRequestId, AppTokenRequestSchema());
        document.AddComponent(AppTokenId, AppTokenSchema(document));
        document.AddComponent(AppTokenListItemId, AppTokenListItemSchema(document));
    }

    private static OpenApiSchema AppTokenRequestSchema()
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
                AppTokenResponse.PersonalKind,
                AppTokenResponse.ServiceKind)),
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

    private static OpenApiSchema AppTokenSchema(OpenApiDocument document)
    {
        var properties = AppTokenProperties(document);
        properties.InsertRange(
            1,
            [
                ("token", Text("The credential, `keyId.secret`. Shown this once, never again; present it in the header `header` names.")),
                ("header", Text("The request header the credential goes in.")),
            ]);
        return Object(
            "A just-minted app token: its list item, with the credential and the header it goes in.",
            [.. _appTokenRequired, "token", "header"],
            [.. properties]);
    }

    private static OpenApiSchema AppTokenListItemSchema(OpenApiDocument document) => Object(
        "An app token as listed: never its credential or a hash.",
        _appTokenRequired,
        [.. AppTokenProperties(document)]);

    private static List<(string Name, IOpenApiSchema Schema)> AppTokenProperties(OpenApiDocument document) =>
    [
        ("keyId", Text("The token's key id (`alvo_pat_…` or `alvo_svc_…`): not secret, the one revoking takes.")),
        ("kind", Enumerated("`personal` or `service`.", AppTokenResponse.PersonalKind, AppTokenResponse.ServiceKind)),
        ("name", Text("The token's name.")),
        ("tenantId", new OpenApiSchema
        {
            Type = JsonSchemaType.String | JsonSchemaType.Null,
            Format = "uuid",
            Description = "The tenant the token acts in, or null for a personal token with none.",
        }),
        ("roles", Strings("A service token's tenant roles; empty for a personal one.")),
        ("scopes", Strings("What the token may do, as `<entity>:read` or `<entity>:write`.")),
        ("createdBy", CreatedBy(document)),
        ("createdAt", Instant("When it was minted.")),
        ("expiresAt", Instant("When it expires.")),
        ("lastUsedAt", NullableInstant("When it last authenticated a request, to the minute; null if never.")),
        ("revokedAt", NullableInstant("When it was revoked; null while it is not.")),
        ("status", Enumerated(
            "`revoked`, else `expired`, else `active` — the first that holds, judged when the answer was made.",
            AppTokenListItem.ActiveStatus,
            AppTokenListItem.ExpiredStatus,
            AppTokenListItem.RevokedStatus)),
    ];

    private static OpenApiSchema NullableInstant(string description)
        => new() { Type = JsonSchemaType.String | JsonSchemaType.Null, Format = "date-time", Description = description };
}
