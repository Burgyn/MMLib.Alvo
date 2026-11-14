using Microsoft.OpenApi;
using MMLib.Alvo.Tenants.SelfService;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Accounts.Internal;

/// <summary>
/// The bodies the sign-in routes read and write, as the document's schema components — written by hand, because every
/// one of them is serialized with its own fixed member names (RFC 6749, RFC 8414, RFC 7517, or the person's own), which
/// no schema ApiExplorer could infer from an <c>IResult</c> would carry.
/// </summary>
/// <remarks>
/// Every id starts with <c>auth</c> and carries an upper-case letter, so it can collide with no component the Data API
/// derives from an entity name (the descriptor's grammar admits only lower case).
/// </remarks>
internal static partial class AccountsSchemas
{
    /// <summary>The token request's form.</summary>
    internal const string TokenRequestId = "authTokenRequest";

    /// <summary>The token endpoint's successful answer.</summary>
    internal const string TokenId = "authToken";

    /// <summary>The signed-in person.</summary>
    internal const string UserId = "authUser";

    /// <summary>The display-name change.</summary>
    internal const string UserUpdateId = "authUserUpdate";

    /// <summary>The authorization-server metadata.</summary>
    internal const string MetadataId = "authServerMetadata";

    /// <summary>The key set.</summary>
    internal const string KeySetId = "authKeySet";

    /// <summary>One key of the key set.</summary>
    internal const string KeyId = "authJsonWebKey";

    /// <summary>One tenant a person belongs to.</summary>
    internal const string MembershipId = "authMembership";

    /// <summary>A tenant's name, as creating or renaming one sends it.</summary>
    internal const string TenantNameId = "authTenantName";

    /// <summary>One member of a tenant.</summary>
    internal const string MemberId = "authMember";

    /// <summary>A change to a member's roles or ownership.</summary>
    internal const string MemberUpdateId = "authMemberUpdate";

    /// <summary>Registers every component, unless the document already carries them.</summary>
    /// <param name="document">The document being built.</param>
    internal static void AddTo(OpenApiDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Components?.Schemas?.ContainsKey(TokenId) == true)
        {
            return;
        }

        document.AddComponent(TokenRequestId, TokenRequest());
        document.AddComponent(TokenId, Token());
        document.AddComponent(MembershipId, Membership());
        document.AddComponent(UserId, User(document));
        document.AddComponent(TenantNameId, TenantName());
        document.AddComponent(MemberId, Member());
        document.AddComponent(MemberUpdateId, MemberUpdate());
        document.AddComponent(UserUpdateId, UserUpdate());
        document.AddComponent(MetadataId, Metadata());
        document.AddComponent(KeyId, Key());
        document.AddComponent(KeySetId, KeySet(document));
        AddInvitationComponents(document);
    }

    private static OpenApiSchema TokenRequest() => Object(
        "A token request (RFC 6749 §3.2), form-encoded. An exchange (RFC 8693) sends `subject_token` and "
        + "`subject_token_type`; a refresh (RFC 6749 §6) sends `refresh_token`. Each parameter at most once.",
        ["grant_type"],
        ("grant_type", Enumerated("Which grant: exchange a provider's ID token, or refresh a session.", ExchangeGrant.GrantType, RefreshGrant.GrantType)),
        ("subject_token", Text("Exchange only: the identity provider's ID token (a JWT, e.g. from Google Identity Services).")),
        ("subject_token_type", Enumerated("Exchange only: the type of `subject_token`.", ExchangeGrant.IdTokenType)),
        ("requested_token_type", Enumerated("Exchange only, optional: the token wanted back.", TokenIssuance.AccessTokenType)),
        ("invitation_code", Text(
            "Exchange only, optional: an invitation code to accept in the same transaction (RFC 8693 extension parameter). "
            + "Under `signUp.mode: invitationOnly` it is the only way a new account is created.")),
        ("refresh_token", Text("Refresh only: the refresh token the previous answer carried. It is rotated on use.")));

    private static OpenApiSchema Token() => Object(
        "A successful token answer (RFC 6749 §5.1). Keep the refresh token out of reach of scripts; it is rotated on every "
        + "refresh, and the one it replaced dies a few seconds later.",
        ["access_token", "token_type", "expires_in", "refresh_token"],
        ("access_token", Text("The access token: an ES256 JWT (RFC 9068) to present as `Authorization: Bearer`.")),
        ("issued_token_type", Text("On an exchange only (RFC 8693 §2.2.1): `" + TokenIssuance.AccessTokenType + "`.")),
        ("token_type", Enumerated("Always `Bearer` (RFC 6750).", TokenResponse.Bearer)),
        ("expires_in", new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int32", Description = "The access token's lifetime, in seconds." }),
        ("refresh_token", Text("The refresh token for the next refresh.")));

    private static OpenApiSchema User(OpenApiDocument document) => Object(
        "The signed-in person: their account, the providers it signs in with, and the tenants they belong to.",
        ["id", "email", "emailTrusted", "name", "avatarUrl", "providers", "memberships"],
        ("id", new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid", Description = "The account id: `@user.id` in rules." }),
        ("email", Nullable("The address the provider last reported, or null.")),
        ("emailTrusted", new OpenApiSchema { Type = JsonSchemaType.Boolean, Description = "Whether the provider vouches for `email`." }),
        ("name", Nullable("The display name, or null.")),
        ("avatarUrl", Nullable("A picture the provider reported, or null.")),
        ("providers", Strings("The identity providers this account signs in with, e.g. `google`.")),
        ("memberships", new OpenApiSchema
        {
            Type = JsonSchemaType.Array,
            Description = "The tenants this person belongs to, the personal one first. Empty unless the descriptor declares "
                + "`tenancy.selfService`.",
            Items = new OpenApiSchemaReference(MembershipId, document),
        }));

    private static OpenApiSchema Membership() => Object(
        "One tenant the person belongs to.",
        ["tenantId", "name", "kind", "roles", "isOwner"],
        ("tenantId", new OpenApiSchema
        {
            Type = JsonSchemaType.String,
            Format = "uuid",
            Description = "The tenant's id: send it as `X-Alvo-Tenant` to act in this tenant.",
        }),
        ("name", Text("The tenant's name.")),
        ("kind", Enumerated(
            "`personal`: the person's own tenant, used when no tenant is named. `shared`: one others can join.",
            MembershipResponse.PersonalKind,
            MembershipResponse.SharedKind)),
        ("roles", Strings("The tenant roles the person holds here, as `@user.roles` counts them in this tenant's scoped entities.")),
        ("isOwner", new OpenApiSchema { Type = JsonSchemaType.Boolean, Description = "Whether the person manages this tenant. Not a role." }));

    private static OpenApiSchema TenantName()
    {
        var schema = Object(
            "A tenant's name. No other member is accepted.",
            ["name"],
            ("name", new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                MinLength = 1,
                MaxLength = Tenants.SelfService.TenantName.MaxLength,
                Description = "The name: trimmed, 1–100 Unicode code points, no control character.",
            }));
        schema.AdditionalPropertiesAllowed = false;
        return schema;
    }

    private static OpenApiSchema Member() => Object(
        "One member of a tenant, as the other members see them. Never an email.",
        ["userId", "name", "avatarUrl", "roles", "isOwner"],
        ("userId", new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid", Description = "The member's account id." }),
        ("name", Nullable("The member's display name, or null.")),
        ("avatarUrl", Nullable("The member's avatar, or null.")),
        ("roles", Strings("The tenant roles the member holds here, as `@user.roles` counts them in this tenant's scoped entities.")),
        ("isOwner", new OpenApiSchema { Type = JsonSchemaType.Boolean, Description = "Whether the member manages this tenant. Not a role." }));

    private static OpenApiSchema MemberUpdate()
    {
        var schema = Object(
            "A change to another member: `roles`, `isOwner`, or both. A part left out is kept; no other property is accepted.",
            [],
            ("roles", new OpenApiSchema
            {
                Type = JsonSchemaType.Array,
                UniqueItems = true,
                Description = "The member's new tenant roles, each from `tenancy.selfService.assignableRoles`; `[]` takes them all away.",
                Items = new OpenApiSchema { Type = JsonSchemaType.String },
            }),
            ("isOwner", new OpenApiSchema { Type = JsonSchemaType.Boolean, Description = "Whether the member manages the tenant." }));
        schema.AdditionalPropertiesAllowed = false;
        schema.MinProperties = 1;
        return schema;
    }

    private static OpenApiSchema UserUpdate()
    {
        var schema = Object(
            "The one change a person makes to their own account. No other member is accepted.",
            ["name"],
            ("name", new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                MinLength = 1,
                MaxLength = DisplayName.MaxLength,
                Description = "The display name: trimmed, 1–100 Unicode code points, no control character.",
            }));
        schema.AdditionalPropertiesAllowed = false;
        return schema;
    }

    private static OpenApiSchema Metadata() => Object(
        "OAuth 2.0 authorization-server metadata (RFC 8414 §2).",
        ["issuer", "token_endpoint", "jwks_uri", "grant_types_supported", "token_endpoint_auth_methods_supported", "response_types_supported"],
        ("issuer", Uri("The issuer: the `iss` of every access token.")),
        ("token_endpoint", Uri("Where tokens are exchanged and refreshed.")),
        ("jwks_uri", Uri("Where the keys access tokens are signed with are published.")),
        ("grant_types_supported", Strings("The token exchange (RFC 8693) and refresh grants.")),
        ("token_endpoint_auth_methods_supported", Strings("`none`: the token endpoint takes no client authentication.")),
        ("response_types_supported", Strings("Empty: there is no authorization endpoint.")));

    private static OpenApiSchema Key() => Object(
        "One public signing key (RFC 7517 §4, RFC 7518 §6.2.1).",
        ["kty", "crv", "x", "y", "kid", "use", "alg"],
        ("kty", Enumerated("The key type.", "EC")),
        ("crv", Enumerated("The curve.", "P-256")),
        ("x", Text("The point's x coordinate, base64url.")),
        ("y", Text("The point's y coordinate, base64url.")),
        ("kid", Text("The key id an access token's header names: the key's RFC 7638 thumbprint.")),
        ("use", Enumerated("What the key is for.", "sig")),
        ("alg", Enumerated("The algorithm.", "ES256")));

    private static OpenApiSchema KeySet(OpenApiDocument document) => Object(
        "The public keys access tokens are signed with (RFC 7517 §5). Select one by the token header's `kid`.",
        ["keys"],
        ("keys", new OpenApiSchema
        {
            Type = JsonSchemaType.Array,
            Description = "Every key a live access token may name.",
            Items = new OpenApiSchemaReference(KeyId, document),
        }));

    private static OpenApiSchema Object(string description, string[] required, params (string Name, IOpenApiSchema Schema)[] properties) => new()
    {
        Type = JsonSchemaType.Object,
        Description = description,
        Properties = properties.ToDictionary(property => property.Name, property => property.Schema, StringComparer.Ordinal),
        Required = new HashSet<string>(required, StringComparer.Ordinal),
    };

    private static OpenApiSchema Text(string description) => new() { Type = JsonSchemaType.String, Description = description };

    private static OpenApiSchema Nullable(string description)
        => new() { Type = JsonSchemaType.String | JsonSchemaType.Null, Description = description };

    private static OpenApiSchema Uri(string description)
        => new() { Type = JsonSchemaType.String, Format = "uri", Description = description };

    private static OpenApiSchema Strings(string description) => new()
    {
        Type = JsonSchemaType.Array,
        Description = description,
        Items = new OpenApiSchema { Type = JsonSchemaType.String },
    };

    private static OpenApiSchema Enumerated(string description, params string[] values) => new()
    {
        Type = JsonSchemaType.String,
        Description = description,
        Enum = [.. values.Select(value => (JsonNode)JsonValue.Create(value))],
    };
}
