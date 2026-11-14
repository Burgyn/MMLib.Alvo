using Microsoft.OpenApi;
using MMLib.Alvo.Api.Internal;

namespace MMLib.Alvo.Accounts.Internal;

/// <summary>
/// What each sign-in operation publishes: its summary, body, every status it answers with and the headers on each, and
/// the credential it takes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every response documents <c>Cache-Control</c></b> — <c>no-store</c> (with <c>Pragma: no-cache</c>, RFC 6749 §5.1)
/// on everything about a person or a token, <c>public, max-age=300</c> on the two discovery documents — and every
/// refusal is <c>application/problem+json</c>, the core's one problem component.
/// </para>
/// <para>
/// <b>Only the person's own routes declare the Bearer scheme</b>, and as the only alternative: they answer no anonymous
/// caller. The token endpoint and discovery declare none — they are how a client gets a credential.
/// </para>
/// </remarks>
internal static class AccountsDocumentation
{
    private const string NoStoreValue = "no-store";

    private static readonly Dictionary<string, Operation> _operations = new(StringComparer.Ordinal)
    {
        [AccountsEndpoints.TokenOperationId] = new(
            "Sign in, or refresh a session",
            "Exchanges an identity provider's ID token for an Alvo session (RFC 8693), or a refresh token for the next "
            + "pair (RFC 6749 §6). Refusals carry RFC 6749's `error` and `error_description` beside the problem `type`.",
            Bearer: false,
            document => Body("application/x-www-form-urlencoded", AccountsSchemas.TokenRequestId, "The token request.", document),
            TokenResponses),
        [UserEndpoints.GetUserOperationId] = new(
            "Read the signed-in person",
            "The account the access token names, with its providers and memberships.",
            Bearer: true,
            _ => null,
            document => Answers(document, Ok(document, "The signed-in person.", AccountsSchemas.UserId), Unauthenticated(document))),
        [UserEndpoints.UpdateUserOperationId] = new(
            "Change the signed-in person's display name",
            "Sets the display name; later sign-ins do not overwrite it. Answers the person as changed.",
            Bearer: true,
            document => Body("application/json", AccountsSchemas.UserUpdateId, "The new display name.", document),
            UpdateResponses),
        [UserEndpoints.LogoutOperationId] = new(
            "Sign out",
            "Ends the session the access token belongs to: its refresh token dies now, its access tokens when they expire. "
            + "Signing out of a session that has already ended is a 204 too.",
            Bearer: true,
            _ => null,
            document => Answers(document, ("204", Response("The session has ended.", NoStore())), Unauthenticated(document))),
        [DiscoveryEndpoints.MetadataOperationId] = new(
            "Read the authorization-server metadata",
            "RFC 8414 metadata: the issuer, the token endpoint, the key set and the grants offered. Also served at the RFC "
            + "8414 §3.1 path-inserted URL, `/.well-known/oauth-authorization-server{prefix}`.",
            Bearer: false,
            _ => null,
            document => Answers(document, Cached(document, "The metadata.", AccountsSchemas.MetadataId))),
        [DiscoveryEndpoints.JwksOperationId] = new(
            "Read the keys access tokens are signed with",
            "The JWKS (RFC 7517) a resource server validates an access token against, selecting the key by `kid`.",
            Bearer: false,
            _ => null,
            document => Answers(document, Cached(document, "The public signing keys.", AccountsSchemas.KeySetId))),
    };

    /// <summary>Whether <paramref name="operationId"/> is a sign-in operation this catalog describes.</summary>
    /// <param name="operationId">An endpoint's name.</param>
    internal static bool Describes(string operationId) => _operations.ContainsKey(operationId);

    /// <summary>Rewrites <paramref name="operation"/> into the contract its route implements.</summary>
    /// <param name="operation">The generated operation.</param>
    /// <param name="operationId">Its endpoint's name, one <see cref="Describes"/> accepts.</param>
    /// <param name="document">The document being built.</param>
    internal static void Describe(OpenApiOperation operation, string operationId, OpenApiDocument document)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var spec = _operations[operationId];
        operation.OperationId = operationId;
        operation.Summary = spec.Summary;
        operation.Description = spec.Description;
        operation.Tags = new HashSet<OpenApiTagReference> { new(AccountsEndpoints.Tag, document) };
        operation.RequestBody = spec.Body(document);
        operation.Responses = spec.Responses(document);
        operation.Security = spec.Bearer
            ? [new() { [new OpenApiSecuritySchemeReference(AlvoDocumentTransformer.BearerScheme, document)] = [] }]
            : null;
    }

    private static OpenApiResponses TokenResponses(OpenApiDocument document) => Answers(
        document,
        Ok(document, "The tokens.", AccountsSchemas.TokenId),
        Refusal(
            "400",
            "`token-refused`: the request or its grant was refused — `error` is `invalid_request` (a malformed form, or an "
            + "ID token that does not verify), `invalid_grant` (a refresh token that is unknown, expired or replayed), "
            + "`unsupported_grant_type` or `invalid_target`.",
            document),
        Refusal(
            "403",
            "`access_denied`: `sign-up-refused` (the descriptor's `auth.signUp` admits no new account for this identity; "
            + "`policy` names the rule) or `sign-in-refused` (the account cannot sign in).",
            document));

    private static OpenApiResponses UpdateResponses(OpenApiDocument document) => Answers(
        document,
        Ok(document, "The person, as changed.", AccountsSchemas.UserId),
        Unauthenticated(document),
        Refusal("415", "`unsupported-media-type`: the body is not `application/json`.", document),
        Refusal("422", "`validation`: the name is missing, too long or not text, or the body names another member.", document));

    private static OpenApiResponses Answers(OpenApiDocument document, params (string Status, OpenApiResponse Response)[] responses)
    {
        ArgumentNullException.ThrowIfNull(document);
        var answers = new OpenApiResponses();
        foreach (var (status, response) in responses)
        {
            answers[status] = response;
        }

        return answers;
    }

    private static (string, OpenApiResponse) Ok(OpenApiDocument document, string description, string schema)
        => ("200", Response(description, NoStore(), Media("application/json", schema, document)));

    private static (string, OpenApiResponse) Cached(OpenApiDocument document, string description, string schema)
        => ("200", Response(description, Cacheable(), Media("application/json", schema, document)));

    private static (string, OpenApiResponse) Unauthenticated(OpenApiDocument document) => Refusal(
        "401",
        "`unauthenticated`: no access token was presented (an API key does not reach this route), or it cannot be used. "
        + "`WWW-Authenticate` is the RFC 6750 challenge: bare `Bearer` when none was presented, otherwise with "
        + "`error=\"invalid_token\"` or `error=\"invalid_request\"`.",
        document,
        challenge: true);

    private static (string, OpenApiResponse) Refusal(string status, string description, OpenApiDocument document, bool challenge = false)
    {
        var headers = NoStore();
        if (challenge)
        {
            headers["WWW-Authenticate"] = Header("The RFC 6750 Bearer challenge.");
        }

        return (status, Response(description, headers, Media(AlvoDocumentTransformer.ProblemMediaType, ProblemComponents.DocumentId, document)));
    }

    private static OpenApiResponse Response(
        string description, Dictionary<string, IOpenApiHeader> headers, Dictionary<string, OpenApiMediaType>? content = null)
        => new() { Description = description, Headers = headers, Content = content };

    private static Dictionary<string, OpenApiMediaType> Media(string mediaType, string schema, OpenApiDocument document)
        => new(StringComparer.Ordinal) { [mediaType] = new OpenApiMediaType { Schema = new OpenApiSchemaReference(schema, document) } };

    private static OpenApiRequestBody Body(string mediaType, string schema, string description, OpenApiDocument document)
        => new() { Required = true, Description = description, Content = Media(mediaType, schema, document) };

    private static Dictionary<string, IOpenApiHeader> NoStore() => new(StringComparer.Ordinal)
    {
        ["Cache-Control"] = Header("Always `no-store` (RFC 6749 §5.1): every answer here is about one person or carries a token.", NoStoreValue),
        ["Pragma"] = Header("Always `no-cache`, for HTTP/1.0 caches.", "no-cache"),
    };

    private static Dictionary<string, IOpenApiHeader> Cacheable() => new(StringComparer.Ordinal)
    {
        ["Cache-Control"] = Header(
            "Always `" + DiscoveryEndpoints.Cacheable + "`: the same for every caller, and a replica picks up a new key "
            + "within the same five minutes.",
            DiscoveryEndpoints.Cacheable),
    };

    private static OpenApiHeader Header(string description, string? example = null) => new()
    {
        Description = description,
        Schema = new OpenApiSchema { Type = JsonSchemaType.String },
        Example = example is null ? null : System.Text.Json.Nodes.JsonValue.Create(example),
    };

    private sealed record Operation(
        string Summary,
        string Description,
        bool Bearer,
        Func<OpenApiDocument, OpenApiRequestBody?> Body,
        Func<OpenApiDocument, OpenApiResponses> Responses);
}
